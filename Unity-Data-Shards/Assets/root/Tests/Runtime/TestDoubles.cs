using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Serialization;
using Saesentsessis.Persistence.Threading;
using Unity.Collections;
using UnityEngine;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using BoolTask = Cysharp.Threading.Tasks.UniTask<bool>;
using StorageReadTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.Core.StorageReadResult>;
using SaveLayoutTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.Layout.SaveLayoutResult>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using StorageReadTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Core.StorageReadResult>;
using SaveLayoutTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Layout.SaveLayoutResult>;
#endif

namespace Saesentsessis.Persistence.Tests
{
	[Serializable]
	[ShardSchema(1)]
	public class TestShard : IDataShard
	{
		[SerializeField] private SerializableGuid id;
		[SerializeField] public int value;
		[SerializeField] public string text;

		[NonSerialized] private bool _dirty = true;

		public TestShard() { }

		public TestShard(Guid guid, int value, string text = "")
		{
			id = guid;
			this.value = value;
			this.text = text;
		}

		public SerializableGuid Identifier => id;
		public bool IsDirty => _dirty;
		public void ClearDirty() => _dirty = false;
		public void MarkDirty() => _dirty = true;
	}

	[Serializable]
	[ShardSchema(1)]
	public class LegacyShard : IDataShard
	{
		[SerializeField] private SerializableGuid id;
		[SerializeField] public int value;

		public LegacyShard() { }

		public LegacyShard(Guid guid, int value)
		{
			id = guid;
			this.value = value;
		}

		public SerializableGuid Identifier => id;
	}

	[Serializable]
	[ShardSchema(2)]
	public class ModernShard : IDataShard
	{
		[SerializeField] private SerializableGuid id;
		[SerializeField] public int points;

		public ModernShard() { }

		public ModernShard(Guid guid, int points)
		{
			id = guid;
			this.points = points;
		}

		public SerializableGuid Identifier => id;
	}

	/// <summary>Blob-level migration: renames LegacyShard's json field "value" to "points".</summary>
	public sealed class LegacyToModernMigration : IShardMigration
	{
		private readonly int _toVersion;

		public LegacyToModernMigration(int toVersion = 2)
		{
			_toVersion = toVersion;
		}

		public string FromTypeName => typeof(LegacyShard).FullName;
		public int FromVersion => 1;
		public Type ToType => typeof(ModernShard);
		public int ToVersion => _toVersion;

		public void Migrate(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
		{
			var json = System.Text.Encoding.UTF8.GetString(src).Replace("\"value\"", "\"points\"");
			var bytes = System.Text.Encoding.UTF8.GetBytes(json);
			dst.Write(bytes);
		}
	}

	/// <summary>Typed counterpart of <see cref="LegacyToModernMigration"/>: converts via plain C#
	/// instead of touching serialized bytes. Identity is carried over explicitly.</summary>
	public sealed class TypedLegacyToModern : TypedShardMigration<LegacyShard, ModernShard>
	{
		public TypedLegacyToModern() : base(fromVersion: 1, toVersion: 2) { }

		protected override ModernShard Convert(LegacyShard old)
			=> new ModernShard(old.Identifier, old.value);
	}

	/// <summary>Reverse of <see cref="LegacyToModernMigration"/>; registered together they form a cycle.</summary>
	public sealed class ModernToLegacyCycleMigration : IShardMigration
	{
		public string FromTypeName => typeof(ModernShard).FullName;
		public int FromVersion => 1;
		public Type ToType => typeof(LegacyShard);
		public int ToVersion => 1;

		public void Migrate(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
		{
			var json = System.Text.Encoding.UTF8.GetString(src).Replace("\"points\"", "\"value\"");
			dst.Write(System.Text.Encoding.UTF8.GetBytes(json));
		}
	}

	/// <summary>In-memory IStorage; copies in and out, so buffer lifetime bugs surface as garbage data.</summary>
	public sealed class MemoryStorage : IStorage
	{
		public readonly Dictionary<string, byte[]> Data = new();
		public readonly Dictionary<string, int> WriteCounts = new();

		public StorageReadTask TryReadAsync(string key, Allocator allocator, CancellationToken cancellation = default)
		{
			if (!Data.TryGetValue(key, out var bytes))
				return PersistenceTask.FromResult(StorageReadResult.NotFound);

			var result = new NativeArray<byte>(bytes.Length, allocator, NativeArrayOptions.UninitializedMemory);
			result.CopyFrom(bytes);
			return PersistenceTask.FromResult(new StorageReadResult(result));
		}

		public TaskType WriteAsync(string key, NativeArray<byte> data, CancellationToken cancellation = default)
		{
			Data[key] = data.ToArray();
			WriteCounts[key] = WriteCounts.TryGetValue(key, out var count) ? count + 1 : 1;
			return PersistenceTask.CompletedTask;
		}

		public BoolTask ExistsAsync(string key, CancellationToken cancellation = default)
			=> PersistenceTask.FromResult(Data.ContainsKey(key));

		public TaskType DeleteAsync(string key, CancellationToken cancellation = default)
		{
			Data.Remove(key);
			return PersistenceTask.CompletedTask;
		}
	}

	/// <summary>Counts Serialize calls; background flag is configurable for deterministic tests.</summary>
	public sealed class CountingSerializer : ISerializer
	{
		private readonly UnityJsonSerializer _inner = new();

		public int SerializeCalls;
		public int DeserializeCalls;

		public CountingSerializer(bool background = false)
		{
			SupportsBackgroundSerialization = background;
		}

		public bool SupportsBackgroundSerialization { get; }

		public void Serialize(object value, Type type, IBufferWriter<byte> writer)
		{
			SerializeCalls++;
			_inner.Serialize(value, type, writer);
		}

		public object Deserialize(ReadOnlySpan<byte> data, Type type)
		{
			DeserializeCalls++;
			return _inner.Deserialize(data, type);
		}
	}

	/// <summary>Write-capturing ISaveLayout for incremental-save and envelope-cache assertions.</summary>
	public sealed class CapturingLayout : ISaveLayout
	{
		public bool FullSnapshot;
		public int WriteCalls;
		public int LastBlobCount;
		public int LastPayloadLength;
		public SerializedType[] LastTypesArray;
		public ShardRecord[] LastRecordsArray;
		public List<SerializableGuid> LastBlobIds = new();

		public bool RequiresFullSnapshot => FullSnapshot;

		public TaskType WriteAsync(string slot, SaveEnvelope envelope, NativeArray<byte> payload,
			NativeArray<ShardBlobRange> ranges, CancellationToken cancellation = default)
		{
			WriteCalls++;
			LastBlobCount = ranges.Length;
			LastPayloadLength = payload.Length;
			LastTypesArray = envelope.Types;
			LastRecordsArray = envelope.Records;
			LastBlobIds.Clear();

			for (var i = 0; i < ranges.Length; i++)
				LastBlobIds.Add(ranges[i].Id);

			return PersistenceTask.CompletedTask;
		}

		public SaveLayoutTask ReadAsync(string slot, Allocator allocator, CancellationToken cancellation = default)
			=> throw new NotSupportedException();

		public BoolTask ExistsAsync(string slot, CancellationToken cancellation = default)
			=> PersistenceTask.FromResult(false);

		public TaskType DeleteAsync(string slot, CancellationToken cancellation = default)
			=> PersistenceTask.CompletedTask;
	}
}
