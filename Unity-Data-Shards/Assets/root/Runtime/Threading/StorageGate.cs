using System.Diagnostics;
using System.Threading;
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY || UNITY_EDITOR
using System;
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
	/// Process-wide lock over a storage resource, keyed by the resource's own identity rather than
	/// by the object that reached it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Why this is static, unlike <see cref="SlotGate"/>.</b> A gate's scope has to match the
	/// scope of the thing it guards. <see cref="SlotGate"/> protects a <c>SaveManager</c>'s envelope
	/// cache, which belongs to that manager, so a per-manager gate is exactly right. A file path
	/// belongs to the <i>process</i>: two <c>FileStorage</c> instances built over one directory name
	/// the same files, and a per-instance gate leaves them completely uncoordinated.
	/// </para>
	/// <para>
	/// They cannot be handed a shared gate either, because in the case that matters there is nothing
	/// to share. The Save Viewer builds its own storage from a descriptor and has no way to reach a
	/// running game's <c>SaveManager</c>. So coordination goes through the <b>resource identity</b>
	/// instead: both sides resolve the same absolute path, look the semaphore up by that string, and
	/// meet on it without ever holding a reference to each other.
	/// </para>
	/// <para>
	/// <b>When it is compiled in.</b> Under <c>ENABLE_PERSISTENCE_SAFE_CONCURRENCY</c>, and always in
	/// the editor — the Save Viewer can refresh while Play Mode is saving, and that is not a race the
	/// user can be asked to avoid. In a player build without the define there is no viewer, so the
	/// only remaining caller is the game itself, which is what the define governs. Everything here
	/// then compiles down to empty calls.
	/// </para>
	/// <para>
	/// Scope is one process. Two editor instances, or an editor plus a standalone build, over one
	/// save directory stay uncoordinated — a named mutex is Windows-shaped and the mobile story is
	/// worse, so that remains explicitly out of scope.
	/// </para>
	/// </remarks>
	internal static class StorageGate
	{
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY || UNITY_EDITOR
		/// <summary>
		/// Case-insensitive on every platform, deliberately. Windows and macOS treat
		/// <c>a.save</c> and <c>A.save</c> as one file, so an ordinal key would silently fail to
		/// protect them. On a case-sensitive filesystem this over-locks two genuinely distinct
		/// files — costing a little contention and no correctness — which is the safe direction to
		/// be wrong in, and cheaper than a per-platform comparer matrix.
		/// </summary>
		private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
			new(StringComparer.OrdinalIgnoreCase);
#endif

		/// <summary>Claims <paramref name="resource"/>, awaiting whoever holds it.</summary>
		/// <param name="resource">
		/// Canonical identity of the thing being locked — for a file backend, the resolved absolute
		/// path. Two callers naming the same resource must produce the same string, which is what
		/// makes them meet.
		/// </param>
		public static TaskType EnterAsync(string resource, CancellationToken cancellation = default)
		{
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY || UNITY_EDITOR
			var semaphore = Gates.GetOrAdd(resource, static _ => new SemaphoreSlim(1, 1));
#if PERSISTENCE_HAS_UNITASK
			return semaphore.WaitAsync(cancellation).AsUniTask();
#else
			return semaphore.WaitAsync(cancellation);
#endif // PERSISTENCE_HAS_UNITASK
#else
			return PersistenceTask.CompletedTask;
#endif // ENABLE_PERSISTENCE_SAFE_CONCURRENCY || UNITY_EDITOR
		}

		/// <summary>
		/// Blocking counterpart, for a critical section already off the caller thread that contains
		/// no awaits.
		/// </summary>
		public static void Enter(string resource)
		{
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY || UNITY_EDITOR
			Gates.GetOrAdd(resource, static _ => new SemaphoreSlim(1, 1)).Wait();
#endif
		}

		/// <summary>
		/// Releases <paramref name="resource"/>. Pair with every successful Enter/EnterAsync.
		/// </summary>
		/// <remarks>
		/// <see cref="SemaphoreSlim"/> is not thread-affine, so releasing on a different thread from
		/// the one that took it is legal — which the read path relies on, since it acquires before a
		/// thread-pool hop and releases after coming back.
		/// </remarks>
		public static void Exit(string resource)
		{
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY || UNITY_EDITOR
			if (Gates.TryGetValue(resource, out var semaphore))
				semaphore.Release();
#endif
		}

		/// <summary>
		/// Number of resources currently tracked. Test-only: the growth bound is the point of it.
		/// </summary>
		/// <remarks>
		/// Entries are never removed. The key space is bounded by save structure rather than user
		/// input — one per slot plus one per shard file — so a large multi-file project reaches a
		/// few thousand semaphores and stops. Reference-counted removal is the usual answer if that
		/// ever stops being true, but it carries an acquire/remove race not worth taking on until a
		/// real project needs it.
		/// </remarks>
		internal static int TrackedResources
		{
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY || UNITY_EDITOR
			get => Gates.Count;
#else
			get => 0;
#endif
		}
	}
}
