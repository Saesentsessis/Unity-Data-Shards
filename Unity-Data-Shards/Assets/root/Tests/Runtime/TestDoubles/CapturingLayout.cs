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
using IntTask = Cysharp.Threading.Tasks.UniTask<int>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using StorageReadTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Core.StorageReadResult>;
using SaveLayoutTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Layout.SaveLayoutResult>;
using IntTask = System.Threading.Tasks.Task<int>;
#endif


namespace Saesentsessis.Persistence.Tests
{
	/// <summary>Write-capturing ISaveLayout for incremental-save and envelope-cache assertions.</summary>
	/// <remarks>
	/// Reports membership like a real incremental layout would, so the manager's "clean but not on
	/// storage" rule is exercised rather than bypassed. A double that reported nothing would be
	/// treated as holding nothing and see every shard on every save.
	/// </remarks>
	public sealed class CapturingLayout : ISaveLayout, IIncrementalSaveLayout
	{
		public bool FullSnapshot;
		public int WriteCalls;
		public int LastBlobCount;
		public int LastPayloadLength;
		public SerializedType[] LastTypesArray;
		public ShardRecord[] LastRecordsArray;
		public List<SerializableGuid> LastBlobIds = new();

		/// <summary>Ids the last committed envelope claimed, per slot — in record order.</summary>
		private readonly Dictionary<string, SerializableGuid[]> _persisted = new();

		public bool RequiresFullSnapshot => FullSnapshot;

		public ReadOnlySpan<SerializableGuid> GetPersistedIds(string slot)
			=> _persisted.TryGetValue(slot, out var ids) ? ids : default;

		public TaskType WriteAsync(string slot, SaveEnvelope envelope, NativeArray<byte> payload,
			NativeArray<ShardBlobRange> ranges, CancellationToken cancellation = default)
		{
			WriteCalls++;
			LastBlobCount = ranges.Length;
			LastPayloadLength = payload.Length;
			LastTypesArray = envelope.TypesArray;
			LastRecordsArray = envelope.RecordsArray;
			LastBlobIds.Clear();

			for (var i = 0; i < ranges.Length; i++)
				LastBlobIds.Add(ranges[i].Id);

			var records = envelope.Records;
			var persisted = new SerializableGuid[records.Length];

			for (var i = 0; i < records.Length; i++)
				persisted[i] = records[i].Id;

			_persisted[slot] = persisted;

			return PersistenceTask.CompletedTask;
		}

		public SaveLayoutTask ReadAsync(string slot, Allocator allocator, CancellationToken cancellation = default)
			=> throw new NotSupportedException();

		public BoolTask ExistsAsync(string slot, CancellationToken cancellation = default)
			=> PersistenceTask.FromResult(false);

		public TaskType DeleteAsync(string slot, CancellationToken cancellation = default)
			=> PersistenceTask.CompletedTask;

		public void Dispose()
		{
			LastBlobIds.Clear();
			_persisted.Clear();
		}
	}
}
