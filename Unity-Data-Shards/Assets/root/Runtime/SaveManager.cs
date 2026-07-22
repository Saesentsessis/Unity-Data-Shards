using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Persistence.Buffers;
using Persistence.Core;
using Persistence.Layout;
using Persistence.Threading;
using Unity.Collections;
using UnityEngine.Pool;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using BoolTask = Cysharp.Threading.Tasks.UniTask<bool>;
using LoadResultTask = Cysharp.Threading.Tasks.UniTask<System.Collections.Generic.IReadOnlyList<Persistence.Core.IDataShard>>;
using ShardArrayTask = Cysharp.Threading.Tasks.UniTask<Persistence.Core.IDataShard[]>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using LoadResultTask = System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Persistence.Core.IDataShard>>;
using ShardArrayTask = System.Threading.Tasks.Task<Persistence.Core.IDataShard[]>;
#endif

namespace Persistence
{
	public sealed class SaveManager
	{
		private const int MinArenaCapacity = 16 * 1024;

		private readonly IPipeline _pipeline;
		private readonly MigrationRegistry _migrations;

		// A5: per-slot envelope cache. As long as the same ShardStore instance saves to
		// the slot and its Generation is unchanged, the type table and records are
		// reused verbatim — an incremental save of one dirty shard skips the whole
		// type-dedup/record-build pass. The store is held weakly so the cache never
		// extends shard lifetimes; entries are evicted on DeleteAsync or replacement.
		private readonly Dictionary<string, EnvelopeCacheEntry> _envelopeCache = new();

		public SaveManager(ISerializer serializer, ISaveLayout layout, MigrationRegistry migrations = null)
		{
			_pipeline = new UnmanagedPipeline(serializer, layout);
			_migrations = migrations;
		}

		public SaveManager(ISerializer serializer, IManagedSaveLayout layout, MigrationRegistry migrations = null)
		{
			_pipeline = new ManagedPipeline(serializer, layout);
			_migrations = migrations;
		}

		/// <summary>
		/// Serializes and persists the given shards. Only dirty shards are written unless
		/// the layout requires a full snapshot.
		/// </summary>
		/// <remarks>
		/// CONTRACT: shards must not be mutated between the call and the task's completion.
		/// When the serializer supports background serialization the shard data is read on
		/// a thread-pool thread, so a mid-save mutation is a data race, and its dirty flag
		/// would be lost by the post-save <see cref="IDataShard.ClearDirty"/> pass.
		/// </remarks>
		public async TaskType SaveAsync(string slot, IReadOnlyList<IDataShard> shards, CancellationToken cancellation = default)
		{
			var count = shards.Count;
			var fullSnapshot = _pipeline.RequiresFullSnapshot;

			// C2: capture the dirty set synchronously, before any await or thread hop.
			// An empty shard set skips the scan and the bit array entirely.
			var snapshot = count > 0 ? new NativeBitArray(count, Allocator.Persistent) : default;
			var envelope = default(SaveEnvelope);
			var releaseEnvelope = false;

			try
			{
				var blobCount = 0;

				for (var i = 0; i < count; i++)
				{
					if (!fullSnapshot && !shards[i].IsDirty)
						continue;

					snapshot.Set(i, true);
					blobCount++;
				}

				envelope = GetOrBuildEnvelope(slot, shards, out releaseEnvelope);
				envelope.TimestampUtc = DateTime.UtcNow.Ticks;

				await _pipeline.SaveAsync(slot, envelope, shards, snapshot, blobCount, cancellation);

				// Success: clear dirty state only for shards that were actually captured.
				for (var i = 0; i < count; i++)
					if (snapshot.IsSet(i))
						shards[i].ClearDirty();
			}
			finally
			{
				if (snapshot.IsCreated)
					snapshot.Dispose();

				if (releaseEnvelope)
					ReleaseEnvelope(envelope);
			}
		}

		public async LoadResultTask LoadAsync(string slot, CancellationToken cancellation = default)
		{
			var shards = await _pipeline.LoadAsync(slot, _migrations, cancellation);

			for (var i = 0; i < shards.Length; i++)
				shards[i].ClearDirty();

			return shards;
		}

		public BoolTask ExistsAsync(string slot, CancellationToken cancellation = default)
		{
			return _pipeline.ExistsAsync(slot, cancellation);
		}

		public TaskType DeleteAsync(string slot, CancellationToken cancellation = default)
		{
			// The slot's persisted state is gone; drop its cached envelope with it.
			if (_envelopeCache.Remove(slot, out var stale))
				ReleaseEnvelope(stale.Envelope);

			return _pipeline.DeleteAsync(slot, cancellation);
		}

		#region Envelope

		private readonly struct EnvelopeCacheEntry
		{
			public readonly WeakReference<ShardStore> Store;
			public readonly int Generation;
			public readonly SaveEnvelope Envelope;

			public EnvelopeCacheEntry(ShardStore store, int generation, in SaveEnvelope envelope)
			{
				Store = new WeakReference<ShardStore>(store);
				Generation = generation;
				Envelope = envelope;
			}
		}

		/// <summary>
		/// Returns a valid envelope for the shard set. ShardStore inputs are cached per
		/// slot (the cache owns the pooled arrays); other inputs are rebuilt every save
		/// and <paramref name="releaseAfterSave"/> tells the caller to return the arrays.
		/// </summary>
		private SaveEnvelope GetOrBuildEnvelope(string slot, IReadOnlyList<IDataShard> shards, out bool releaseAfterSave)
		{
			if (shards is ShardStore store)
			{
				releaseAfterSave = false;

				if (_envelopeCache.TryGetValue(slot, out var entry)
					&& entry.Store.TryGetTarget(out var cachedStore)
					&& ReferenceEquals(cachedStore, store)
					&& entry.Generation == store.Generation)
					return entry.Envelope;

				var envelope = BuildEnvelope(shards);

				if (_envelopeCache.Remove(slot, out var stale))
					ReleaseEnvelope(stale.Envelope);

				_envelopeCache[slot] = new EnvelopeCacheEntry(store, store.Generation, envelope);
				return envelope;
			}

			releaseAfterSave = true;
			return BuildEnvelope(shards);
		}

		private static SaveEnvelope BuildEnvelope(IReadOnlyList<IDataShard> shards)
		{
			var count = shards.Count;
			var typeLookup = DictionaryPool<Type, int>.Get();
			var types = ListPool<SerializedType>.Get();
			var records = ArrayPool<ShardRecord>.Shared.Rent(count);

			try
			{
				for (var i = 0; i < count; i++)
				{
					var shard = shards[i];
					var type = shard.GetType();

					if (!typeLookup.TryGetValue(type, out var typeIndex))
					{
						typeIndex = types.Count;
						typeLookup[type] = typeIndex;
						types.Add(SerializedTypeHelper.Describe(type));
					}

					records[i] = new ShardRecord
					{
						Id = shard.Identifier,
						TypeIndex = typeIndex
					};
				}

				var typeArray = ArrayPool<SerializedType>.Shared.Rent(types.Count);

				for (var i = 0; i < types.Count; i++)
					typeArray[i] = types[i];

				return new SaveEnvelope
				{
					FormatVersion = SaveEnvelope.CurrentFormatVersion,
					Types = typeArray,
					TypeCount = types.Count,
					Records = records,
					RecordCount = count
				};
			}
			catch
			{
				ArrayPool<ShardRecord>.Shared.Return(records);
				throw;
			}
			finally
			{
				DictionaryPool<Type, int>.Release(typeLookup);
				ListPool<SerializedType>.Release(types);
			}
		}

		private static void ReleaseEnvelope(in SaveEnvelope envelope)
		{
			if (envelope.Records != null)
				ArrayPool<ShardRecord>.Shared.Return(envelope.Records);

			// SerializedType holds string references — clear so the pool doesn't pin them.
			if (envelope.Types != null)
				ArrayPool<SerializedType>.Shared.Return(envelope.Types, clearArray: true);
		}

		#endregion

		#region Serialize/Deserialize cores (shared by both pipelines)

		// Span locals are forbidden in async methods, so the hot loops live in these
		// sync helpers and the async pipelines call them between thread switches.
		private static void SerializeBlobs(ISerializer serializer, IReadOnlyList<IDataShard> shards,
			NativeBitArray snapshot, IArenaWriter arena, Span<ShardBlobRange> ranges, CancellationToken cancellation)
		{
			var index = 0;
			var count = shards.Count;

			for (var i = 0; i < count; i++)
			{
				if (!snapshot.IsSet(i))
					continue;

				cancellation.ThrowIfCancellationRequested();

				var shard = shards[i];
				var before = arena.WrittenLength;
				serializer.Serialize(shard, shard.GetType(), arena);
				ranges[index++] = new ShardBlobRange(shard.Identifier, before, arena.WrittenLength - before);
			}
		}

		/// <summary>
		/// Per envelope type: either resolve the CLR type + current schema version, or —
		/// when a blob migration chain starts at the stored state — defer resolution
		/// entirely to the chain (the stored CLR type may no longer exist). E4: one
		/// GetVersion per type, not per record. All arrays are pooled; release via
		/// <see cref="ReleaseResolved"/>.
		/// </summary>
		private static void ResolveTypes(in SaveEnvelope envelope, MigrationRegistry migrations,
			out Type[] types, out int[] currentVersions, out bool[] needsMigration)
		{
			var count = envelope.TypeCount;
			types = ArrayPool<Type>.Shared.Rent(count);
			currentVersions = ArrayPool<int>.Shared.Rent(count);
			needsMigration = ArrayPool<bool>.Shared.Rent(count);

			for (var i = 0; i < count; i++)
			{
				var stored = envelope.Types[i];

				if (migrations != null && migrations.HasMigration(stored.TypeName, stored.SchemaVersion))
				{
					needsMigration[i] = true;
					types[i] = null;
					currentVersions[i] = 0;
					continue;
				}

				needsMigration[i] = false;
				types[i] = SerializedTypeHelper.Resolve(stored);
				currentVersions[i] = ShardSchemaHelper.GetVersion(types[i]);
			}
		}

		private static void ReleaseResolved(Type[] types, int[] currentVersions, bool[] needsMigration)
		{
			ArrayPool<Type>.Shared.Return(types, clearArray: true);
			ArrayPool<int>.Shared.Return(currentVersions);
			ArrayPool<bool>.Shared.Return(needsMigration);
		}

		private static IDataShard[] DeserializeCore(ISerializer serializer, MigrationRegistry migrations,
			in SaveEnvelope envelope, ReadOnlySpan<byte> payload, ReadOnlySpan<ShardBlobRange> ranges,
			Type[] types, int[] currentVersions, bool[] needsMigration, CancellationToken cancellation)
		{
			var count = envelope.RecordCount;

			if (ranges.Length < count)
				throw new InvalidOperationException($"Layout returned {ranges.Length} blob ranges for {count} envelope records.");

			var shards = new IDataShard[count];

			for (var i = 0; i < count; i++)
			{
				cancellation.ThrowIfCancellationRequested();

				var record = envelope.Records[i];
				var range = ranges[i];
				var blob = payload.Slice(range.Offset, range.Length);
				var typeIndex = record.TypeIndex;

				if (needsMigration[typeIndex])
				{
					var stored = envelope.Types[typeIndex];
					using var migrated = migrations.MigrateToLatest(blob, stored.TypeName, stored.SchemaVersion, out var finalType);
					shards[i] = (IDataShard)serializer.Deserialize(migrated.WrittenSpan, finalType);
				}
				else
				{
					var storedVersion = envelope.Types[typeIndex].SchemaVersion;
					var currentVersion = currentVersions[typeIndex];

					if (storedVersion > currentVersion)
						throw new InvalidOperationException($"Data version ({storedVersion}) exceeds schema version ({currentVersion}) for {types[typeIndex].Name}.");

					shards[i] = (IDataShard)serializer.Deserialize(blob, types[typeIndex]);
				}
			}

			return shards;
		}

		#endregion

		private interface IPipeline
		{
			bool RequiresFullSnapshot { get; }
			TaskType SaveAsync(string slot, SaveEnvelope envelope, IReadOnlyList<IDataShard> shards, NativeBitArray snapshot, int blobCount, CancellationToken cancellation);
			ShardArrayTask LoadAsync(string slot, MigrationRegistry migrations, CancellationToken cancellation);
			BoolTask ExistsAsync(string slot, CancellationToken cancellation);
			TaskType DeleteAsync(string slot, CancellationToken cancellation);
		}

		private sealed class UnmanagedPipeline : IPipeline
		{
			private readonly ISerializer _serializer;
			private readonly ISaveLayout _layout;

			// Per-slot arena sizing: start each save at the previous payload size so the
			// steady state never grows mid-serialization. Main-thread-affine — only
			// touched after SwitchToMainThread.
			private readonly Dictionary<string, int> _arenaSizeHints = new();

			public UnmanagedPipeline(ISerializer serializer, ISaveLayout layout)
			{
				_serializer = serializer;
				_layout = layout;
			}

			public bool RequiresFullSnapshot => _layout.RequiresFullSnapshot;

			public async TaskType SaveAsync(string slot, SaveEnvelope envelope, IReadOnlyList<IDataShard> shards, NativeBitArray snapshot, int blobCount, CancellationToken cancellation)
			{
				var background = _serializer.SupportsBackgroundSerialization;
				var capacity = ArenaCapacity(slot, blobCount);

				// A1: one arena + one blittable range array per save, regardless of shard count.
				var arena = new NativeListBufferWriter(capacity, Allocator.Persistent);
				var ranges = new NativeArray<ShardBlobRange>(blobCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);

				try
				{
					if (background)
						await PersistenceTask.SwitchToThreadPool();

					Serialize(_serializer, shards, snapshot, arena, ranges, cancellation);

					// Layouts/storages may touch Unity APIs — hand off from the main thread.
					if (background)
						await PersistenceTask.SwitchToMainThread(cancellation);

					_arenaSizeHints[slot] = arena.WrittenLength;

					await _layout.WriteAsync(slot, envelope, arena.AsArray(), ranges, cancellation);
				}
				finally
				{
					// Exception-safe affinity restore: the caller must never resume on a
					// pool thread, whatever the failure path was. No cancellation token —
					// the restore has to run even when the save was cancelled.
					if (background && !PersistenceTask.IsMainThread)
						await PersistenceTask.SwitchToMainThread();

					arena.Dispose();
					ranges.Dispose();
				}
			}

			public async ShardArrayTask LoadAsync(string slot, MigrationRegistry migrations, CancellationToken cancellation)
			{
				var result = await _layout.ReadAsync(slot, Allocator.Persistent, cancellation);

				try
				{
					ResolveTypes(result.Envelope, migrations, out var types, out var currentVersions, out var needsMigration);
					var background = _serializer.SupportsBackgroundSerialization;

					try
					{
						if (background)
							await PersistenceTask.SwitchToThreadPool();

						return Deserialize(_serializer, migrations, result, types, currentVersions, needsMigration, cancellation);
					}
					finally
					{
						if (background && !PersistenceTask.IsMainThread)
							await PersistenceTask.SwitchToMainThread();

						ReleaseResolved(types, currentVersions, needsMigration);
					}
				}
				finally
				{
					result.Dispose();
				}
			}

			public BoolTask ExistsAsync(string slot, CancellationToken cancellation)
			{
				return _layout.ExistsAsync(slot, cancellation);
			}

			public TaskType DeleteAsync(string slot, CancellationToken cancellation)
			{
				_arenaSizeHints.Remove(slot);
				return _layout.DeleteAsync(slot, cancellation);
			}

			private int ArenaCapacity(string slot, int blobCount)
			{
				if (blobCount == 0)
					return 1;

				return _arenaSizeHints.TryGetValue(slot, out var hint) ? Math.Max(hint, MinArenaCapacity) : MinArenaCapacity;
			}

			private static void Serialize(ISerializer serializer, IReadOnlyList<IDataShard> shards,
				NativeBitArray snapshot, NativeListBufferWriter arena, NativeArray<ShardBlobRange> ranges, CancellationToken cancellation)
			{
				SerializeBlobs(serializer, shards, snapshot, arena, ranges.AsSpan(), cancellation);
			}

			private static IDataShard[] Deserialize(ISerializer serializer, MigrationRegistry migrations,
				in SaveLayoutResult result, Type[] types, int[] currentVersions, bool[] needsMigration, CancellationToken cancellation)
			{
				return DeserializeCore(serializer, migrations, result.Envelope,
					result.Payload.AsReadOnlySpan(), result.Ranges.AsReadOnlySpan(),
					types, currentVersions, needsMigration, cancellation);
			}
		}

		private sealed class ManagedPipeline : IPipeline
		{
			private readonly ISerializer _serializer;
			private readonly IManagedSaveLayout _layout;
			private readonly Dictionary<string, int> _arenaSizeHints = new();

			public ManagedPipeline(ISerializer serializer, IManagedSaveLayout layout)
			{
				_serializer = serializer;
				_layout = layout;
			}

			public bool RequiresFullSnapshot => _layout.RequiresFullSnapshot;

			public async TaskType SaveAsync(string slot, SaveEnvelope envelope, IReadOnlyList<IDataShard> shards, NativeBitArray snapshot, int blobCount, CancellationToken cancellation)
			{
				var background = _serializer.SupportsBackgroundSerialization;
				var capacity = ArenaCapacity(slot, blobCount);

				var arena = new PooledArrayBufferWriter(capacity);
				var ranges = ArrayPool<ShardBlobRange>.Shared.Rent(blobCount);

				try
				{
					if (background)
						await PersistenceTask.SwitchToThreadPool();

					Serialize(_serializer, shards, snapshot, arena, ranges, blobCount, cancellation);

					if (background)
						await PersistenceTask.SwitchToMainThread(cancellation);

					_arenaSizeHints[slot] = arena.WrittenLength;

					await _layout.WriteAsync(slot, envelope, arena.WrittenMemory,
						ranges.AsMemory(0, blobCount), cancellation);
				}
				finally
				{
					if (background && !PersistenceTask.IsMainThread)
						await PersistenceTask.SwitchToMainThread();

					arena.Dispose();
					ArrayPool<ShardBlobRange>.Shared.Return(ranges);
				}
			}

			public async ShardArrayTask LoadAsync(string slot, MigrationRegistry migrations, CancellationToken cancellation)
			{
				var result = await _layout.ReadAsync(slot, cancellation);

				try
				{
					ResolveTypes(result.Envelope, migrations, out var types, out var currentVersions, out var needsMigration);
					var background = _serializer.SupportsBackgroundSerialization;

					try
					{
						if (background)
							await PersistenceTask.SwitchToThreadPool();

						return Deserialize(_serializer, migrations, result, types, currentVersions, needsMigration, cancellation);
					}
					finally
					{
						if (background && !PersistenceTask.IsMainThread)
							await PersistenceTask.SwitchToMainThread();

						ReleaseResolved(types, currentVersions, needsMigration);
					}
				}
				finally
				{
					result.Dispose();
				}
			}

			public BoolTask ExistsAsync(string slot, CancellationToken cancellation)
			{
				return _layout.ExistsAsync(slot, cancellation);
			}

			public TaskType DeleteAsync(string slot, CancellationToken cancellation)
			{
				_arenaSizeHints.Remove(slot);
				return _layout.DeleteAsync(slot, cancellation);
			}

			private int ArenaCapacity(string slot, int blobCount)
			{
				if (blobCount == 0)
					return 1;

				return _arenaSizeHints.TryGetValue(slot, out var hint) ? Math.Max(hint, MinArenaCapacity) : MinArenaCapacity;
			}

			private static void Serialize(ISerializer serializer, IReadOnlyList<IDataShard> shards,
				NativeBitArray snapshot, PooledArrayBufferWriter arena, ShardBlobRange[] ranges, int blobCount, CancellationToken cancellation)
			{
				SerializeBlobs(serializer, shards, snapshot, arena, ranges.AsSpan(0, blobCount), cancellation);
			}

			private static IDataShard[] Deserialize(ISerializer serializer, MigrationRegistry migrations,
				in ManagedSaveLayoutResult result, Type[] types, int[] currentVersions, bool[] needsMigration, CancellationToken cancellation)
			{
				return DeserializeCore(serializer, migrations, result.Envelope,
					result.Payload.AsSpan(0, result.PayloadLength),
					result.Ranges.AsSpan(0, result.RangeCount),
					types, currentVersions, needsMigration, cancellation);
			}
		}
	}
}
