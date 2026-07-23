using System;
using System.Threading;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using BoolTask = Cysharp.Threading.Tasks.UniTask<bool>;
using ManagedStorageReadTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.Core.ManagedStorageReadResult>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using ManagedStorageReadTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Core.ManagedStorageReadResult>;
#endif

namespace Saesentsessis.Persistence.Core
{
	/// <summary>
	/// Managed-memory counterpart of <see cref="IStorage"/>. Uses GC-tracked buffers,
	/// suitable for platforms or backends where native allocation is impractical.
	/// </summary>
	public interface IManagedStorage
	{
		/// <summary>
		/// Reads raw bytes for the key. Returns <c>Found == false</c> when the key has
		/// no persisted data.
		/// </summary>
		ManagedStorageReadTask TryReadAsync(string key, CancellationToken cancellation = default);

		/// <summary>Persists raw bytes under the key.</summary>
		/// <remarks>
		/// OWNERSHIP CONTRACT: the storage does NOT copy <paramref name="data"/>. The
		/// caller guarantees the buffer stays valid and unmodified until the returned
		/// task completes.
		/// </remarks>
		TaskType WriteAsync(string key, ReadOnlyMemory<byte> data, CancellationToken cancellation = default);

		/// <summary>Returns true if the key has persisted data.</summary>
		BoolTask ExistsAsync(string key, CancellationToken cancellation = default);

		/// <summary>Removes persisted data for the key. No-op if key does not exist.</summary>
		TaskType DeleteAsync(string key, CancellationToken cancellation = default);
	}
}
