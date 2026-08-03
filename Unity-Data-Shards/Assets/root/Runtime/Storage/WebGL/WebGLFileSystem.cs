#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AOT;
using UnityEngine;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
#else
using TaskType = System.Threading.Tasks.Task;
#endif

namespace Saesentsessis.Persistence.Storage.WebGL
{
	/// <summary>
	/// Makes files written on WebGL actually survive the tab closing.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Emscripten's filesystem is in memory. A write lands in MEMFS immediately and is gone on
	/// reload; IDBFS is the layer that mirrors a mounted directory into IndexedDB, and it moves
	/// nothing until <c>FS.syncfs</c> is called. Unity syncs on application quit — which a browser
	/// has no reliable event for, so a player closing the tab loses whatever was not flushed. This
	/// flushes after every write instead, so a completed <c>WriteAsync</c> means durable rather than
	/// "in RAM".
	/// </para>
	/// <para>
	/// <b>The mount path decides whether saves survive a redeploy.</b>
	/// <c>Application.persistentDataPath</c> is <c>/idbfs/&lt;md5 of the page's directory URL&gt;</c>,
	/// so serving the build from a new URL — which is what itch.io and most CI deploys do on every
	/// upload — produces a different path and orphans every existing save. Passing `FileStorage` a
	/// fixed root under <c>/idbfs/</c> pins it: this class mounts that path itself, since Unity only
	/// mounts the one it computed.
	/// </para>
	/// </remarks>
	internal static class WebGLFileSystem
	{
		/// <summary>Emscripten mount point below which a directory can be IndexedDB-backed.</summary>
		public const string IdbfsRoot = "/idbfs/";

		private static readonly Dictionary<int, TaskCompletionSource<bool>> Pending = new();
		private static readonly HashSet<string> Mounted = new(StringComparer.Ordinal);

		private static int _nextToken;
		private static bool _warnedAboutVolatileRoot;

		/// <summary>
		/// Mounts and populates <paramref name="root"/> if it needs it. Safe to call before every
		/// operation; the work happens once per path.
		/// </summary>
		public static async TaskType EnsureMountedAsync(string root)
		{
			if (string.IsNullOrEmpty(root) || Mounted.Contains(root))
				return;

			// Unity already mounted persistentDataPath and populated it during startup. Mounting it
			// again throws inside Emscripten, so this only handles roots Unity does not know about.
			if (IsUnityManaged(root))
			{
				Mounted.Add(root);
				return;
			}

			if (root.StartsWith(IdbfsRoot, StringComparison.Ordinal) == false)
			{
				WarnAboutVolatileRoot(root);
				Mounted.Add(root);
				return;
			}

			// Added before awaiting: a second call for the same root while the first is in flight
			// must not start a second mount. Single-threaded, so this is a complete guard.
			Mounted.Add(root);

			var tcs = new TaskCompletionSource<bool>();
			PersistenceIdbfsMount(root, Register(tcs), Completed);

			await tcs.Task;
		}

		/// <summary>Pushes everything written so far into IndexedDB.</summary>
		public static async TaskType FlushAsync()
		{
			var tcs = new TaskCompletionSource<bool>();
			PersistenceIdbfsFlush(Register(tcs), Completed);

			await tcs.Task;
		}

		/// <summary>True when Unity mounted this path itself, so it is already IndexedDB-backed.</summary>
		private static bool IsUnityManaged(string root)
		{
			var managed = Application.persistentDataPath;

			return string.IsNullOrEmpty(managed) == false
				&& root.StartsWith(managed, StringComparison.Ordinal);
		}

		private static void WarnAboutVolatileRoot(string root)
		{
			if (_warnedAboutVolatileRoot)
				return;

			_warnedAboutVolatileRoot = true;

			Debug.LogWarning(
				$"[Persistence] WebGL save root '{root}' is neither Application.persistentDataPath nor under " +
				$"'{IdbfsRoot}', so it lives in memory only and every save is lost when the tab closes. Use " +
				$"Application.persistentDataPath, or a fixed path under '{IdbfsRoot}' to also survive a redeploy.");
		}

		private static int Register(TaskCompletionSource<bool> completion)
		{
			// WebGL is single-threaded and these callbacks arrive on the browser's event loop, so no
			// synchronisation is needed — but the token must be unique for the lifetime of the page.
			var token = ++_nextToken;
			Pending[token] = completion;

			return token;
		}

		[MonoPInvokeCallback(typeof(Action<int, int>))]
		private static void Completed(int token, int error)
		{
			if (Pending.Remove(token, out var completion) == false)
				return;

			// A failed sync is reported, never thrown: the bytes are still in memory and the save
			// itself succeeded. Throwing here would turn a durability warning into a lost save.
			if (error != 0)
				Debug.LogError("[Persistence] IDBFS synchronisation failed — see the browser console. " +
					"Data written this session may not survive a reload.");

			completion.TrySetResult(error == 0);
		}

		[DllImport("__Internal")]
		private static extern void PersistenceIdbfsMount(string path, int token, Action<int, int> callback);

		[DllImport("__Internal")]
		private static extern void PersistenceIdbfsFlush(int token, Action<int, int> callback);
	}
}
#endif
