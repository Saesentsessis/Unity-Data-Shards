using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Persistence.Core
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
		UniTask<ManagedStorageReadResult> TryReadAsync(string key, CancellationToken cancellation = default);

		/// <summary>Persists raw bytes under the key.</summary>
		/// <remarks>
		/// OWNERSHIP CONTRACT: the storage does NOT copy <paramref name="data"/>. The
		/// caller guarantees the buffer stays valid and unmodified until the returned
		/// task completes.
		/// </remarks>
		UniTask WriteAsync(string key, ReadOnlyMemory<byte> data, CancellationToken cancellation = default);

		/// <summary>Returns true if the key has persisted data.</summary>
		UniTask<bool> ExistsAsync(string key, CancellationToken cancellation = default);

		/// <summary>Removes persisted data for the key. No-op if key does not exist.</summary>
		UniTask DeleteAsync(string key, CancellationToken cancellation = default);
	}
}
