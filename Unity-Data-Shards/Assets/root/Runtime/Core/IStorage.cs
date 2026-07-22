using System.Threading;
using Unity.Collections;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using BoolTask = Cysharp.Threading.Tasks.UniTask<bool>;
using StorageReadTask = Cysharp.Threading.Tasks.UniTask<Persistence.Core.StorageReadResult>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using StorageReadTask = System.Threading.Tasks.Task<Persistence.Core.StorageReadResult>;
#endif

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
        StorageReadTask TryReadAsync(string key, Allocator allocator, CancellationToken cancellation = default);

        /// <summary>Persists raw bytes under the key.</summary>
        /// <remarks>
        /// OWNERSHIP CONTRACT: the storage does NOT copy <paramref name="data"/>. The
        /// caller guarantees the buffer stays valid and unmodified until the returned
        /// task completes. This makes large writes zero-copy; violating it is a
        /// use-after-free on a background thread.
        /// </remarks>
        TaskType WriteAsync(string key, NativeArray<byte> data, CancellationToken cancellation = default);

        /// <summary>Returns true if the key has persisted data.</summary>
        BoolTask ExistsAsync(string key, CancellationToken cancellation = default);

        /// <summary>Removes persisted data for the key. No-op if key does not exist.</summary>
        TaskType DeleteAsync(string key, CancellationToken cancellation = default);
    }
}
