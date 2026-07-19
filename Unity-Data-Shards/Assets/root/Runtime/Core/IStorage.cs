using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Collections;

namespace Persistence.Core
{
    /// <summary>
    /// Async key-value byte storage backed by unmanaged NativeArrays.
    /// Implementations handle the physical medium (filesystem, PlayerPrefs, cloud).
    /// </summary>
    public interface IStorage
    {
        /// <summary>
        /// Reads raw bytes for the key. Returns <c>Found == false</c> when the key has
        /// no persisted data. When found, the caller owns the returned NativeArray.
        /// </summary>
        UniTask<StorageReadResult> TryReadAsync(string key, Allocator allocator, CancellationToken cancellation = default);

        /// <summary>Persists raw bytes under the key.</summary>
        /// <remarks>
        /// OWNERSHIP CONTRACT: the storage does NOT copy <paramref name="data"/>. The
        /// caller guarantees the buffer stays valid and unmodified until the returned
        /// task completes. This makes large writes zero-copy; violating it is a
        /// use-after-free on a background thread.
        /// </remarks>
        UniTask WriteAsync(string key, NativeArray<byte> data, CancellationToken cancellation = default);

        /// <summary>Returns true if the key has persisted data.</summary>
        UniTask<bool> ExistsAsync(string key, CancellationToken cancellation = default);

        /// <summary>Removes persisted data for the key. No-op if key does not exist.</summary>
        UniTask DeleteAsync(string key, CancellationToken cancellation = default);
    }
}
