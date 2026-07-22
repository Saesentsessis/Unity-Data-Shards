#if !PERSISTENCE_HAS_UNITASK
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using UnityEngine.LowLevel;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Persistence.Threading
{
	/// <summary>
	/// PlayerLoop-driven main-thread dispatcher used when UniTask is absent. Continuations that
	/// need the Unity main thread are queued here and drained once per player-loop tick (and per
	/// editor update in the editor), reproducing UniTask's main-thread return without depending on
	/// <see cref="SynchronizationContext"/>. The main-thread id is captured on load.
	/// </summary>
	internal static class PersistenceMainThreadDispatcher
	{
		private static readonly ConcurrentQueue<Action> Continuations = new();
		private static int _mainThreadId = -1;

		public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

		public static void Post(Action continuation) => Continuations.Enqueue(continuation);

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void InitializeRuntime()
		{
			CaptureMainThread();
			InstallPlayerLoop();
		}

#if UNITY_EDITOR
		[InitializeOnLoadMethod]
		private static void InitializeEditor()
		{
			CaptureMainThread();
			EditorApplication.update -= Drain;
			EditorApplication.update += Drain;
		}
#endif

		private static void CaptureMainThread()
		{
			if (_mainThreadId == -1)
				_mainThreadId = Thread.CurrentThread.ManagedThreadId;
		}

		private static void InstallPlayerLoop()
		{
			var loop = PlayerLoop.GetCurrentPlayerLoop();
			var subsystems = new List<PlayerLoopSystem>(loop.subSystemList);

			// Idempotent across domain reloads: drop a prior copy before re-adding.
			subsystems.RemoveAll(system => system.type == typeof(PersistenceMainThreadDispatcher));
			subsystems.Add(new PlayerLoopSystem
			{
				type = typeof(PersistenceMainThreadDispatcher),
				updateDelegate = Drain
			});

			loop.subSystemList = subsystems.ToArray();
			PlayerLoop.SetPlayerLoop(loop);
		}

		private static void Drain()
		{
			// Snapshot the count so a continuation that re-posts cannot spin this loop forever.
			var count = Continuations.Count;

			while (count-- > 0 && Continuations.TryDequeue(out var continuation))
				continuation();
		}
	}

	/// <summary>Awaitable that resumes its continuation on a thread-pool thread.</summary>
	public readonly struct SwitchToThreadPoolAwaitable
	{
		public Awaiter GetAwaiter() => default;

		public readonly struct Awaiter : ICriticalNotifyCompletion
		{
			public bool IsCompleted => false;
			public void GetResult() { }
			public void OnCompleted(Action continuation) => ThreadPool.QueueUserWorkItem(Run, continuation);
			public void UnsafeOnCompleted(Action continuation) => ThreadPool.UnsafeQueueUserWorkItem(Run, continuation);
			private static void Run(object state) => ((Action)state)();
		}
	}

	/// <summary>Awaitable that resumes its continuation on the Unity main thread via the dispatcher.</summary>
	public readonly struct SwitchToMainThreadAwaitable
	{
		private readonly CancellationToken _cancellation;

		public SwitchToMainThreadAwaitable(CancellationToken cancellation) => _cancellation = cancellation;

		public Awaiter GetAwaiter() => new(_cancellation);

		public readonly struct Awaiter : INotifyCompletion
		{
			private readonly CancellationToken _cancellation;

			public Awaiter(CancellationToken cancellation) => _cancellation = cancellation;

			public bool IsCompleted => PersistenceMainThreadDispatcher.IsMainThread;
			public void GetResult() => _cancellation.ThrowIfCancellationRequested();
			public void OnCompleted(Action continuation) => PersistenceMainThreadDispatcher.Post(continuation);
		}
	}
}
#endif
