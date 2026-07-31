using System;
using System.Diagnostics;
using System.Threading;
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY || ENABLE_PERSISTENCE_INTEGRITY_CHECKS
using System.Collections.Concurrent;
#endif
#if PERSISTENCE_HAS_UNITASK
using Cysharp.Threading.Tasks;
using TaskType = Cysharp.Threading.Tasks.UniTask;
#else
using TaskType = System.Threading.Tasks.Task;
#endif

namespace Saesentsessis.Persistence.Threading
{
	/// <summary>
	/// Serializes operations that share a key — a save slot, a storage path — for the code paths
	/// whose critical section is a read-modify-write that cannot be made atomic any other way.
	/// <para>
	/// The package's baseline contract is <b>one operation in flight per key</b>. Almost everything
	/// downstream assumes it: the pipeline arenas, <see cref="Saesentsessis.Persistence.Buffers.PooledArrayBufferWriter"/>,
	/// and <c>TransformStorage</c>'s ping-pong buffers are all documented single-operation. So the
	/// default build spends nothing enforcing it:
	/// </para>
	/// <list type="bullet">
	/// <item><description>
	/// <c>ENABLE_PERSISTENCE_SAFE_CONCURRENCY</c> — a real per-key mutex. Overlapping callers queue
	/// instead of racing, and concurrent save/delete of one slot becomes well-defined.
	/// </description></item>
	/// <item><description>
	/// Otherwise — nothing at runtime. Under <c>ENABLE_PERSISTENCE_INTEGRITY_CHECKS</c> an overlap
	/// throws on the spot, so a contract violation surfaces during development as a stack trace
	/// pointing at the second caller rather than as corruption weeks later.
	/// </description></item>
	/// </list>
	/// <para>
	/// Scope is one process. Two processes writing the same save directory is not defended against
	/// here and cannot be, portably — a named mutex is Windows-shaped and the mobile story is worse.
	/// </para>
	/// </summary>
	internal sealed class SlotGate : IDisposable
	{
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY
		private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
#elif ENABLE_PERSISTENCE_INTEGRITY_CHECKS
		private readonly ConcurrentDictionary<string, byte> _inFlight = new();
#endif

		/// <summary>Claims <paramref name="key"/>, awaiting the holder when locking is compiled in.</summary>
		public TaskType EnterAsync(string key, CancellationToken cancellation = default)
		{
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY
			var semaphore = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
#if PERSISTENCE_HAS_UNITASK
			return semaphore.WaitAsync(cancellation).AsUniTask();
#else
			return semaphore.WaitAsync(cancellation);
#endif // PERSISTENCE_HAS_UNITASK
#else
			DetectOverlap(key);
			return PersistenceTask.CompletedTask;
#endif // ENABLE_PERSISTENCE_SAFE_CONCURRENCY
		}

		/// <summary>
		/// Blocking counterpart for critical sections that are already off the caller thread and
		/// contain no awaits — the file write dance, for instance.
		/// </summary>
		public void Enter(string key)
		{
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY
			_gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1)).Wait();
#else
			DetectOverlap(key);
#endif
		}

		/// <summary>Releases <paramref name="key"/>. Pair with every successful Enter/EnterAsync.</summary>
		public void Exit(string key)
		{
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY
			if (_gates.TryGetValue(key, out var semaphore))
				semaphore.Release();
#else
			ClearOverlap(key);
#endif
		}

#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY == false
		[Conditional("ENABLE_PERSISTENCE_INTEGRITY_CHECKS")]
		private void DetectOverlap(string key)
		{
#if ENABLE_PERSISTENCE_INTEGRITY_CHECKS
			if (_inFlight.TryAdd(key, 0) == false)
				throw new InvalidOperationException(
					$"Another operation is already in flight for '{key}'. One operation per key at a time is the " +
					"contract; the arenas underneath are single-operation by design. Await the first call before " +
					"starting the second, or build with ENABLE_PERSISTENCE_SAFE_CONCURRENCY to serialise them.");
#endif // ENABLE_PERSISTENCE_INTEGRITY_CHECKS
		}

		[Conditional("ENABLE_PERSISTENCE_INTEGRITY_CHECKS")]
		private void ClearOverlap(string key)
		{
#if ENABLE_PERSISTENCE_INTEGRITY_CHECKS
			_inFlight.TryRemove(key, out _);
#endif // ENABLE_PERSISTENCE_INTEGRITY_CHECKS
		}
#endif // ENABLE_PERSISTENCE_SAFE_CONCURRENCY == false

		public void Dispose()
		{
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY
			foreach (var semaphore in _gates.Values)
				semaphore.Dispose();

			_gates.Clear();
#elif ENABLE_PERSISTENCE_INTEGRITY_CHECKS
			_inFlight.Clear();
#endif
		}
	}
}
