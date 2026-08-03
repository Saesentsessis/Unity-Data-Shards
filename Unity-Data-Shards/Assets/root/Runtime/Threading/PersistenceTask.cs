using System;
using System.Threading;
#if PERSISTENCE_HAS_UNITASK
using Cysharp.Threading.Tasks;
#else
using System.Threading.Tasks;
#endif

namespace Saesentsessis.Persistence.Threading
{
	/// <summary>
	/// Backend-agnostic async primitives used by the save pipeline. When
	/// <c>PERSISTENCE_HAS_UNITASK</c> is defined (the Cysharp UniTask package is present)
	/// these forward to UniTask; otherwise they map onto <see cref="System.Threading.Tasks.Task"/>
	/// plus a PlayerLoop-driven main-thread dispatcher (see the fallback file). The type used in
	/// signatures is the <c>TaskType</c> / <c>TaskType&lt;T&gt;</c> alias each file declares.
	/// </summary>
	public static class PersistenceTask
	{
#if PERSISTENCE_HAS_UNITASK
		public static UniTask CompletedTask => UniTask.CompletedTask;

		public static UniTask<T> FromResult<T>(T value) => UniTask.FromResult(value);

		public static UniTask<TResult> RunOnThreadPool<TState, TResult>(Func<TState, TResult> func, TState state, CancellationToken cancellation = default)
		{
#if UNITY_WEBGL
			return UniTask.FromResult(RunInline(func, state, cancellation));
#else
			return UniTask.RunOnThreadPool(() => func(state), cancellationToken: cancellation);
#endif
		}

		public static UniTask RunOnThreadPool<TState>(Action<TState> action, TState state, CancellationToken cancellation = default)
		{
#if UNITY_WEBGL
			RunInline(action, state, cancellation);
			return UniTask.CompletedTask;
#else
			return UniTask.RunOnThreadPool(() => action(state), cancellationToken: cancellation);
#endif
		}

		/// <summary>Joins previously scheduled work. Faulted entries surface on await.</summary>
		public static UniTask WhenAll(UniTask[] tasks) => UniTask.WhenAll(tasks);

		public static SwitchToThreadPoolAwaitable SwitchToThreadPool() => UniTask.SwitchToThreadPool();

		public static SwitchToMainThreadAwaitable SwitchToMainThread(CancellationToken cancellation = default) => UniTask.SwitchToMainThread(cancellation);

		public static bool IsMainThread => PlayerLoopHelper.IsMainThread;

		public static YieldAwaitable Yield() => UniTask.Yield();
#else
		public static Task CompletedTask => Task.CompletedTask;

		public static Task<T> FromResult<T>(T value) => Task.FromResult(value);

		public static Task<TResult> RunOnThreadPool<TState, TResult>(Func<TState, TResult> func, TState state, CancellationToken cancellation = default)
		{
#if UNITY_WEBGL
			return Task.FromResult(RunInline(func, state, cancellation));
#else
			return Task.Run(() => func(state), cancellation);
#endif
		}

		public static Task RunOnThreadPool<TState>(Action<TState> action, TState state, CancellationToken cancellation = default)
		{
#if UNITY_WEBGL
			RunInline(action, state, cancellation);
			return Task.CompletedTask;
#else
			return Task.Run(() => action(state), cancellation);
#endif
		}

		/// <summary>Joins previously scheduled work. Faulted entries surface on await.</summary>
		public static Task WhenAll(Task[] tasks) => Task.WhenAll(tasks);

		public static SwitchToThreadPoolAwaitable SwitchToThreadPool() => default;

		public static SwitchToMainThreadAwaitable SwitchToMainThread(CancellationToken cancellation = default) => new(cancellation);

		public static bool IsMainThread => PersistenceMainThreadDispatcher.IsMainThread;

		public static System.Runtime.CompilerServices.YieldAwaitable Yield() => Task.Yield();
#endif

#if UNITY_WEBGL
		/// <summary>
		/// Runs the work on the calling thread, because on WebGL there is no other one.
		/// </summary>
		/// <remarks>
		/// WebGL builds are single-threaded. `Task.Run` / `UniTask.RunOnThreadPool` do not fail
		/// there — they queue work that only runs when the main thread next yields, which turns a
		/// storage call into something that completes an unpredictable number of frames later, or
		/// not at all if the caller is waiting on it. Running inline keeps the operation ordered
		/// with respect to its caller. It does mean file I/O happens on the main thread, which on
		/// WebGL is a memory copy rather than a disk seek — the filesystem is in RAM.
		/// </remarks>
		private static TResult RunInline<TState, TResult>(Func<TState, TResult> func, TState state, CancellationToken cancellation)
		{
			cancellation.ThrowIfCancellationRequested();

			return func(state);
		}

		private static void RunInline<TState>(Action<TState> action, TState state, CancellationToken cancellation)
		{
			cancellation.ThrowIfCancellationRequested();

			action(state);
		}
#endif
	}
}
