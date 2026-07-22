using System;
using System.Collections;

namespace Persistence.Tests
{
	/// <summary>
	/// Bridges an async test body to the <c>[UnityTest]</c> <see cref="IEnumerator"/> contract for
	/// both async backends: UniTask via <c>ToCoroutine</c>, or plain <see cref="System.Threading.Tasks.Task"/>
	/// polled frame-by-frame so the editor/player loop keeps pumping (which drains the fallback
	/// main-thread dispatcher) while the task is in flight.
	/// </summary>
	internal static class AsyncTest
	{
#if PERSISTENCE_HAS_UNITASK
		public static IEnumerator Run(Func<Cysharp.Threading.Tasks.UniTask> testBody)
			=> Cysharp.Threading.Tasks.UniTask.ToCoroutine(testBody);
#else
		public static IEnumerator Run(Func<System.Threading.Tasks.Task> testBody)
		{
			var task = testBody();

			while (!task.IsCompleted)
				yield return null;

			if (!task.IsFaulted)
				yield break;

			var exception = task.Exception is { InnerExceptions: { Count: 1 } inner }
				? inner[0]
				: task.Exception;
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
		}
#endif
	}
}
