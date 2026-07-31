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
	/// <summary>In-memory IStorage; copies in and out, so buffer lifetime bugs surface as garbage data.</summary>
	public sealed class MemoryStorage : IStorage, IListableStorage
	{
		public readonly Dictionary<string, byte[]> Data = new();
		public readonly Dictionary<string, int> WriteCounts = new();

		/// <summary>Set by <see cref="Dispose"/>, so a decorator's cascade can be asserted.</summary>
		public bool Disposed;

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

		/// <summary>
		/// Enumerates in dictionary order — deliberately not sorted, so any consumer that depends on
		/// ordering has to establish it for itself.
		/// </summary>
		public IntTask PopulateAsync(IList<StorageKeyInfo> destination, CancellationToken cancellation = default)
		{
			foreach (var pair in Data)
				destination.Add(new StorageKeyInfo(pair.Key, pair.Value.Length, ModifiedTicks));

			return PersistenceTask.FromResult(Data.Count);
		}

		/// <summary>Reported for every key, so tests can assert the value survives grouping.</summary>
		public long ModifiedTicks;

		/// <summary>Counts every delete, so "the sweep did nothing" can be asserted directly.</summary>
		public int DeleteCount;

		public TaskType DeleteAsync(string key, CancellationToken cancellation = default)
		{
			DeleteCount++;
			Data.Remove(key);
			return PersistenceTask.CompletedTask;
		}

		public void Dispose()
		{
			Disposed = true;
		}
	}

}
