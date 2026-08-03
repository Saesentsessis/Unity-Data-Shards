using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Saesentsessis.Persistence.Buffers;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Threading;
using Unity.Collections;
using UnityEngine.Pool;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using BoolTask = Cysharp.Threading.Tasks.UniTask<bool>;
using LoadResultTask = Cysharp.Threading.Tasks.UniTask<System.Collections.Generic.IReadOnlyList<Saesentsessis.Persistence.Core.IDataShard>>;
using ShardArrayTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.Core.IDataShard[]>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using LoadResultTask = System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Saesentsessis.Persistence.Core.IDataShard>>;
using ShardArrayTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Core.IDataShard[]>;
#endif

namespace Saesentsessis.Persistence
{
	public sealed class SaveManager : IDisposable
	{
		private const int MinArenaCapacity = 16 * 1024;

		private readonly IPipeline _pipeline;
		private readonly MigrationRegistry _migrations;

		// A5: per-slot envelope cache. As long as the same ShardStore instance saves
		// to the slot and its Generation is unchanged, the type table and records are
		// reused verbatim — an incremental save of one dirty shard skips the whole
		// type-dedup/record-build pass. The store is held weakly so the cache never
		// extends shard lifetimes; entries are evicted on DeleteAsync or replacement.
		private readonly Dictionary<string, EnvelopeCacheEntry> _envelopeCache = new();

		// The cache above is read-modify-written across awaits (look up, build, evict, store), which
		// no thread-safe dictionary can make atomic on its own: two savers can both miss, both
		// build, and one can then release pooled arrays the other has already handed to the
		// pipeline. Serialising per slot is the fix; SlotGate documents what each build pays.
		private readonly SlotGate _slotGate = new();

		public SaveManager(ISerializer serializer, ISaveLayout layout, MigrationRegistry migrations = null)
		{
			_pipeline = new UnmanagedPipeline(serializer, layout);
			_migrations = migrations;
			migrations?.BindSerializer(serializer);
		}

		public SaveManager(ISerializer serializer, IManagedSaveLayout layout, MigrationRegistry migrations = null)
		{
			_pipeline = new ManagedPipeline(serializer, layout);
			_migrations = migrations;
			migrations?.BindSerializer(serializer);
		}

		/// <summary>
		/// Serializes and persists the given shards. Only dirty shards are written unless the layout
		/// requires a full snapshot or does not already hold the shard's blob.
		/// </summary>
		/// <remarks>
		/// CONTRACT: shards must not be mutated between the call and the task's completion.
		/// When the serializer supports background serialization the shard data is read on
		/// a thread-pool thread, so a mid-save mutation is a data race, and its dirty flag
		/// would be lost by the post-save <see cref="IDataShard.ClearDirty"/> pass.
		/// </remarks>
		public async TaskType SaveAsync(string slot, IReadOnlyList<IDataShard> shards, CancellationToken cancellation = default)
		{
			EnsureSlotIsValid(slot);

			var count = shards.Count;

			// Capture the blob set synchronously, before any await or thread hop.
			// An empty shard set skips the scan and the bit array entirely.
			var snapshot = count > 0 ? new NativeBitArray(count, Allocator.Persistent) : default;
			var envelope = default(SaveEnvelope);
			var releaseEnvelope = false;

			try
			{
				await _slotGate.EnterAsync(slot, cancellation);

				// Inside the gate: the layout's view of what it holds is only stable while nothing
				// else is committing to this slot.
				var blobCount = CaptureBlobSet(slot, shards, snapshot,
					_pipeline.RequiresFullSnapshot, _pipeline.Incremental);

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

				_slotGate.Exit(slot);
			}
		}

		public async LoadResultTask LoadAsync(string slot, CancellationToken cancellation = default)
		{
			EnsureSlotIsValid(slot);

			await _slotGate.EnterAsync(slot, cancellation);

			try
			{
				var shards = await _pipeline.LoadAsync(slot, _migrations, cancellation);

				for (var i = shards.Length - 1; i >= 0; i--)
					shards[i].ClearDirty();

				return shards;
			}
			finally
			{
				_slotGate.Exit(slot);
			}
		}

		public BoolTask ExistsAsync(string slot, CancellationToken cancellation = default)
		{
			EnsureSlotIsValid(slot);
			
			return _pipeline.ExistsAsync(slot, cancellation);
		}

		public async TaskType DeleteAsync(string slot, CancellationToken cancellation = default)
		{
			EnsureSlotIsValid(slot);

			// async, unlike the other delegating members: the cache eviction below and the pipeline
			// delete have to sit inside one gated section, or a save running concurrently can
			// repopulate the cache for a slot that is being deleted.
			await _slotGate.EnterAsync(slot, cancellation);

			try
			{
				// The slot's persisted state is gone; drop its cached envelope with it.
				if (_envelopeCache.Remove(slot, out var stale))
					ReleaseEnvelope(stale.Envelope);

				await _pipeline.DeleteAsync(slot, cancellation);
			}
			finally
			{
				_slotGate.Exit(slot);
			}
		}

		#region Blob capture

		/// <summary>
		/// Sets a bit for every shard whose blob this save has to write, and returns how many.
		/// </summary>
		/// <remarks>
		/// Dirtiness is not the whole rule. An incremental layout is handed dirty blobs only and is
		/// expected to still hold the rest, so a clean shard whose blob is <i>not</i> on storage has
		/// to be written too — otherwise the envelope commits a record pointing at nothing. Two
		/// ordinary sequences reach that state: removing a shard and later restoring it unmodified
		/// (the removal deleted the blob), and loading one slot to save it into another (loading
		/// clears every dirty flag, so the new slot would get an envelope and no blobs at all).
		/// <para>
		/// Span locals are forbidden in async methods, so this stays synchronous and
		/// <see cref="SaveAsync"/> calls it between the gate and the first await.
		/// </para>
		/// </remarks>
		private static int CaptureBlobSet(string slot, IReadOnlyList<IDataShard> shards, NativeBitArray snapshot,
			bool fullSnapshot, IIncrementalSaveLayout incremental)
		{
			var count = shards.Count;

			if (count == 0)
				return 0;

			if (fullSnapshot)
			{
				for (var i = 0; i < count; i++)
					snapshot.Set(i, true);

				return count;
			}

			// A layout that reports nothing — including one that does not implement the capability at
			// all — is taken at its word only for the shards it does list. Everything else is written.
			var persisted = incremental == null ? default : incremental.GetPersistedIds(slot);

			// Nothing on storage to match against, so every shard is written and the set logic below
			// could only reach that answer the slow way. This is not a rare path: it is the first save
			// of every session, plus every save through a layout without the capability.
			if (persisted.IsEmpty)
			{
				for (var i = 0; i < count; i++)
					snapshot.Set(i, true);

				return count;
			}

			// Cheap gate first, mirroring MultiFileSaveLayout's orphan diff: the same ids in the same
			// order means every blob is already on storage, so dirtiness alone decides. This is the
			// steady state — save the same store over and over — and it rents nothing.
			if (persisted.Length == count && MatchesInOrder(persisted, shards))
				return CaptureDirty(shards, snapshot);

			var known = HashSetPool<SerializableGuid>.Get();

			try
			{
				for (var i = 0; i < persisted.Length; i++)
					known.Add(persisted[i]);

				var blobCount = 0;

				for (var i = 0; i < count; i++)
				{
					var shard = shards[i];

					if (!shard.IsDirty && known.Contains(shard.Identifier))
						continue;

					snapshot.Set(i, true);
					blobCount++;
				}

				return blobCount;
			}
			finally
			{
				HashSetPool<SerializableGuid>.Release(known);
			}
		}

		private static int CaptureDirty(IReadOnlyList<IDataShard> shards, NativeBitArray snapshot)
		{
			var blobCount = 0;
			var count = shards.Count;

			for (var i = 0; i < count; i++)
			{
				if (!shards[i].IsDirty)
					continue;

				snapshot.Set(i, true);
				blobCount++;
			}

			return blobCount;
		}

		/// <summary>Ordered comparison — record order is stable for an unchanged store.</summary>
		private static bool MatchesInOrder(ReadOnlySpan<SerializableGuid> persisted, IReadOnlyList<IDataShard> shards)
		{
			for (var i = 0; i < persisted.Length; i++)
				if (persisted[i].Equals(shards[i].Identifier) == false)
					return false;

			return true;
		}

		// A layout that cannot report its membership is assumed to hold nothing, which is safe but
		// costs a full write on every save — silently, and only for layouts outside this package.
		// Trusting it instead would trade that cost for saves that fail to load, so the choice is
		// made for correctness and announced once, while the author can still act on it.
		[Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
		private static void WarnIfIncrementalWithoutMembership(object layout, bool requiresFullSnapshot)
		{
			if (requiresFullSnapshot || layout is IIncrementalSaveLayout)
				return;

			UnityEngine.Debug.LogWarning(
				$"{layout.GetType().Name} saves incrementally but does not implement IIncrementalSaveLayout, so " +
				"SaveManager cannot tell whether a clean shard's blob is still on storage and has to write every " +
				"shard on every save. Implementing it restores incremental writes; leaving it unimplemented is " +
				"safe, only slower.");
		}

		#endregion

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

				return SaveEnvelope.Create(types.Count, typeArray, count, records);
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
			if (envelope.RecordsArray != null)
				ArrayPool<ShardRecord>.Shared.Return(envelope.RecordsArray);

			// SerializedType holds string references — clear so the pool doesn't pin them.
			if (envelope.TypesArray != null)
				ArrayPool<SerializedType>.Shared.Return(envelope.TypesArray, clearArray: true);
		}

		#endregion

		#region Serialize/Deserialize cores (shared by both pipelines)

		// Span locals are forbidden in async methods, so the hot loops live in these
		// sync helpers and the async pipelines call them between thread switches.
		/// <summary>
		/// Serializes the captured shards into the arena, leaving the layout's reservations empty.
		/// </summary>
		/// <remarks>
		/// The gaps are advanced over rather than written, so the shard lands at its final address
		/// and the layout fills the space in front of it afterwards. Recorded offsets stay
		/// <b>absolute</b> within the arena, which is what lets a layout reach a blob's prefix by
		/// subtracting from its offset; a layout that writes offsets into a file where they are
		/// payload-relative subtracts the header itself.
		/// </remarks>
		private static void SerializeBlobs(ISerializer serializer, IReadOnlyList<IDataShard> shards,
			NativeBitArray snapshot, IArenaWriter arena, Span<ShardBlobRange> ranges,
			int header, int blobPrefix, CancellationToken cancellation)
		{
			var index = 0;
			var count = shards.Count;

			Reserve(arena, header);

			for (var i = 0; i < count; i++)
			{
				if (!snapshot.IsSet(i))
					continue;

				cancellation.ThrowIfCancellationRequested();

				Reserve(arena, blobPrefix);

				var shard = shards[i];
				var before = arena.WrittenLength;
				serializer.Serialize(shard, shard.GetType(), arena);
				ranges[index++] = new ShardBlobRange(shard.Identifier, before, arena.WrittenLength - before);
			}
		}

		/// <summary>Advances the arena over <paramref name="bytes"/> without writing them.</summary>
		private static void Reserve(IArenaWriter arena, int bytes)
		{
			if (bytes <= 0)
				return;

			arena.GetSpan(bytes);
			arena.Advance(bytes);
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
				ref readonly var stored = ref envelope.Types[i];

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

				ref readonly var record = ref envelope.Records[i];
				ref readonly var range = ref ranges[i];
				var blob = payload.Slice(range.Offset, range.Length);
				var typeIndex = record.TypeIndex;
				
				if (record.Id != range.Id)
					throw new SaveCorruptedException($"Layout is corrupted. Expected {range.Id}({envelope.Types[typeIndex]}), got {record.Id}({envelope.Types[record.TypeIndex]}).",
						SaveCorruptedExceptionReason.CorruptedLayout);

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

		// Deliberately NOT [Conditional]: this validates caller input on a public entry point. A null
		// slot that survives to the storage layer fails there in a backend-specific way that says
		// nothing about the actual mistake. One null check per save is not a cost worth stripping.
		private static void EnsureSlotIsValid(string slot)
		{
			if (string.IsNullOrEmpty(slot))
				throw new ArgumentNullException(nameof(slot));
		}
		
		private interface IPipeline : IDisposable
		{
			bool RequiresFullSnapshot { get; }

			/// <summary>The layout's membership capability, or null when it has none.</summary>
			IIncrementalSaveLayout Incremental { get; }

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

			// Per-slot range arrays, reused across saves. Unmanaged, so every exit path — Dispose,
			// DeleteAsync, growth — has to release the old one.
			private readonly Dictionary<string, NativeArray<ShardBlobRange>> _rangeBuffers = new();

			// Resolved once: the cast is a type test per save otherwise, and the answer cannot change.
			private readonly IIncrementalSaveLayout _incremental;

			public UnmanagedPipeline(ISerializer serializer, ISaveLayout layout)
			{
				_serializer = serializer;
				_layout = layout;
				_incremental = layout as IIncrementalSaveLayout;

				WarnIfIncrementalWithoutMembership(layout, layout.RequiresFullSnapshot);
			}

			public bool RequiresFullSnapshot => _layout.RequiresFullSnapshot;

			public IIncrementalSaveLayout Incremental => _incremental;

			public async TaskType SaveAsync(string slot, SaveEnvelope envelope, IReadOnlyList<IDataShard> shards,
				NativeBitArray snapshot, int blobCount, CancellationToken cancellation)
			{
				var background = _serializer.SupportsBackgroundSerialization;

				// Space the layout wants in front of the data, asked for before anything is
				// serialized so the shards can be written into their final position. This is what
				// removes the payload copy both layouts used to pay.
				var header = _layout.HeaderReservation(envelope, blobCount);
				var blobPrefix = _layout.BlobReservation;
				var reserved = header + blobCount * blobPrefix;

				var capacity = Math.Max(ArenaCapacity(slot, blobCount), reserved + 1);

				// One arena per save. The range array is kept per slot and reused.
				var arena = new NativeListBufferWriter(capacity, Allocator.Persistent);
				var ranges = RentRanges(slot, blobCount);

				try
				{
					if (background)
						await PersistenceTask.SwitchToThreadPool();

					Serialize(_serializer, shards, snapshot, arena, ranges, header, blobPrefix, cancellation);

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

					// `ranges` is owned by _rangeBuffers, not by this save.
					arena.Dispose();
				}
			}

			/// <summary>
			/// A range array for this slot, sized to <paramref name="blobCount"/>.
			/// </summary>
			/// <remarks>
			/// Kept per slot and grown on demand rather than allocated per save. The returned value
			/// is a <c>GetSubArray</c> view trimmed to the exact count, because layouts read
			/// <c>ranges.Length</c> as the blob count — handing back the whole capacity would make a
			/// shrinking save write stale ranges. Released in <see cref="Dispose"/> and whenever the
			/// slot is deleted.
			/// </remarks>
			private NativeArray<ShardBlobRange> RentRanges(string slot, int blobCount)
			{
				if (_rangeBuffers.TryGetValue(slot, out var buffer) && buffer.Length >= blobCount)
					return buffer.GetSubArray(0, blobCount);

				if (buffer.IsCreated)
					buffer.Dispose();

				buffer = new NativeArray<ShardBlobRange>(Math.Max(blobCount, 4), Allocator.Persistent);
				_rangeBuffers[slot] = buffer;

				return buffer.GetSubArray(0, blobCount);
			}

			private void ReleaseRanges(string slot)
			{
				if (_rangeBuffers.Remove(slot, out var buffer) && buffer.IsCreated)
					buffer.Dispose();
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
				ReleaseRanges(slot);

				return _layout.DeleteAsync(slot, cancellation);
			}

			private int ArenaCapacity(string slot, int blobCount)
			{
				if (blobCount == 0)
					return 1;

				return _arenaSizeHints.TryGetValue(slot, out var hint) ? Math.Max(hint, MinArenaCapacity) : MinArenaCapacity;
			}

			private static void Serialize(ISerializer serializer, IReadOnlyList<IDataShard> shards,
				NativeBitArray snapshot, NativeListBufferWriter arena, NativeArray<ShardBlobRange> ranges,
				int header, int blobPrefix, CancellationToken cancellation)
			{
				SerializeBlobs(serializer, shards, snapshot, arena, ranges.AsSpan(), header, blobPrefix, cancellation);
			}

			private static IDataShard[] Deserialize(ISerializer serializer, MigrationRegistry migrations,
				in SaveLayoutResult result, Type[] types, int[] currentVersions, bool[] needsMigration, CancellationToken cancellation)
			{
				return DeserializeCore(serializer, migrations, result.Envelope,
					result.Payload.AsReadOnlySpan(), result.Ranges.AsReadOnlySpan(),
					types, currentVersions, needsMigration, cancellation);
			}

			public void Dispose()
			{
				_arenaSizeHints.Clear();

				// Unmanaged and reused across saves, so nothing else will collect them.
				foreach (var buffer in _rangeBuffers.Values)
					if (buffer.IsCreated)
						buffer.Dispose();

				_rangeBuffers.Clear();
				_layout.Dispose();
			}
		}

		private sealed class ManagedPipeline : IPipeline
		{
			private readonly ISerializer _serializer;
			private readonly IManagedSaveLayout _layout;
			private readonly Dictionary<string, int> _arenaSizeHints = new();

			private readonly IIncrementalSaveLayout _incremental;

			public ManagedPipeline(ISerializer serializer, IManagedSaveLayout layout)
			{
				_serializer = serializer;
				_layout = layout;
				_incremental = layout as IIncrementalSaveLayout;

				WarnIfIncrementalWithoutMembership(layout, layout.RequiresFullSnapshot);
			}

			public bool RequiresFullSnapshot => _layout.RequiresFullSnapshot;

			public IIncrementalSaveLayout Incremental => _incremental;

			public async TaskType SaveAsync(string slot, SaveEnvelope envelope, IReadOnlyList<IDataShard> shards,
				NativeBitArray snapshot, int blobCount, CancellationToken cancellation)
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
				// IManagedSaveLayout has no reservation contract yet — no shipped implementation to
				// need one — so the managed path serializes with no gaps.
				SerializeBlobs(serializer, shards, snapshot, arena, ranges.AsSpan(0, blobCount),
					header: 0, blobPrefix: 0, cancellation);
			}

			private static IDataShard[] Deserialize(ISerializer serializer, MigrationRegistry migrations,
				in ManagedSaveLayoutResult result, Type[] types, int[] currentVersions, bool[] needsMigration, CancellationToken cancellation)
			{
				return DeserializeCore(serializer, migrations, result.Envelope,
					result.Payload.AsSpan(0, result.PayloadLength),
					result.Ranges.AsSpan(0, result.RangeCount),
					types, currentVersions, needsMigration, cancellation);
			}

			public void Dispose()
			{
				_arenaSizeHints.Clear();
				_layout.Dispose();
			}
		}

		public void Dispose()
		{
			foreach (var entry in _envelopeCache.Values)
				ReleaseEnvelope(entry.Envelope);

			_envelopeCache.Clear();
			_slotGate.Dispose();
			_pipeline.Dispose();
		}
	}
}
