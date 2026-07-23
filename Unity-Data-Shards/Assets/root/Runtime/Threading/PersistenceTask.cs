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
			=> UniTask.RunOnThreadPool(() => func(state), cancellationToken: cancellation);

		public static UniTask RunOnThreadPool<TState>(Action<TState> action, TState state, CancellationToken cancellation = default)
			=> UniTask.RunOnThreadPool(() => action(state), cancellationToken: cancellation);

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
			=> Task.Run(() => func(state), cancellation);

		public static Task RunOnThreadPool<TState>(Action<TState> action, TState state, CancellationToken cancellation = default)
			=> Task.Run(() => action(state), cancellation);

		/// <summary>Joins previously scheduled work. Faulted entries surface on await.</summary>
		public static Task WhenAll(Task[] tasks) => Task.WhenAll(tasks);

		public static SwitchToThreadPoolAwaitable SwitchToThreadPool() => default;

		public static SwitchToMainThreadAwaitable SwitchToMainThread(CancellationToken cancellation = default) => new(cancellation);

		public static bool IsMainThread => PersistenceMainThreadDispatcher.IsMainThread;

		public static System.Runtime.CompilerServices.YieldAwaitable Yield() => Task.Yield();
#endif
	}
}
