using System;
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY
using System.Collections.Concurrent;
#endif
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Threading;
using Saesentsessis.Persistence.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using UnityEngine;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using BoolTask = Cysharp.Threading.Tasks.UniTask<bool>;
using StorageReadTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.Core.StorageReadResult>;
using ReadStatusTask = Cysharp.Threading.Tasks.UniTask<Unity.IO.LowLevel.Unsafe.ReadStatus>;
using IntTask = Cysharp.Threading.Tasks.UniTask<int>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using StorageReadTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Core.StorageReadResult>;
using ReadStatusTask = System.Threading.Tasks.Task<Unity.IO.LowLevel.Unsafe.ReadStatus>;
using IntTask = System.Threading.Tasks.Task<int>;
#endif

namespace Saesentsessis.Persistence.Storage
{
	/// <summary>
	/// Local file storage. Writes are crash-safe (tmp + bak dance); reads go through
	/// <see cref="AsyncReadManager"/> straight into the target unmanaged buffer, so no
	/// thread-pool thread is blocked on I/O and no managed intermediate exists.
	/// </summary>
	public sealed class FileStorage : IStorage, IListableStorage
	{
		private const string BackupSuffix = ".bak";
		private const string DefaultExtension = "save";

		private readonly string _rootDirectory;
		private readonly string _fileExtension;

		// Slots are few and hot; resolving key -> full path once per key keeps the
		// steady-state path allocation-free.
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY
		private readonly ConcurrentDictionary<string, string> _pathCache = new();
#else
		private readonly Dictionary<string, string> _pathCache = new();
#endif

		// The tmp/bak/final dance is a multi-step rename over fixed names derived from the key, and
		// two writers — or a writer and a reader restoring a .bak — interleaving through it can lose
		// an update. The lock for that lives in StorageGate rather than here, keyed by the resolved
		// path: a second FileStorage over the same directory names the same files, and a field on
		// this instance could never coordinate with it. See StorageGate for when it compiles in.

		public FileStorage(string rootDirectory = null, string fileExtension = null)
		{
			_fileExtension = ValidateExtension(fileExtension);

			// GetFullPath, not GetPathRoot: the root is the directory saves live in, and it is
			// normally absolute (persistentDataPath is). GetFullPath also applies the platform's own
			// canonicalisation — separator flavour, "..", Windows' trailing-dot stripping — so the
			// confinement test in BuildPath compares two strings the OS already agrees on.
			var root = Path.GetFullPath(string.IsNullOrEmpty(rootDirectory)
				? Application.persistentDataPath
				: rootDirectory);

			// The trailing separator turns the prefix test into a directory-boundary test. Without
			// it a root of "<...>/saves" would also accept everything under "<...>/saves2".
			_rootDirectory = root[^1] == Path.DirectorySeparatorChar
				? root
				: root + Path.DirectorySeparatorChar;
		}

		/// <summary>
		/// Normalises the file extension, rejecting the values that would make a key's path depend
		/// on which runtime resolves it.
		/// </summary>
		/// <remarks>
		/// An <b>empty</b> extension is refused rather than accepted: it turns a key into
		/// <c>&lt;key&gt;.</c>, and a trailing dot is where runtimes stop agreeing — Mono keeps it
		/// verbatim, CoreCLR strips it, and the Windows filesystem strips it again underneath both.
		/// A key would then resolve to one path and be stored at another, and the <c>.tmp</c>/
		/// <c>.bak</c> names in the write dance would collide with ordinary keys. The extension is
		/// what keeps those three namespaces apart, so it has to be a real one.
		/// </remarks>
		private static string ValidateExtension(string fileExtension)
		{
			// null means "unspecified", which is not the same as "none".
			if (fileExtension == null)
				return DefaultExtension;

			var trimmed = fileExtension.TrimStart('.');

			if (string.IsNullOrWhiteSpace(trimmed))
				throw new InvalidPathException(
					$"[FileStorage] File extension '{fileExtension}' is empty. Pass null for the default " +
					$"('{DefaultExtension}'); an extensionless storage cannot separate saves from their " +
					"own .tmp and .bak files.");

			// The extension lands inside a file name, so a separator in it would silently move keys
			// into a subdirectory — and anything the platform rejects would fail only at write time.
			if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
				throw new InvalidPathException(
					$"[FileStorage] File extension '{fileExtension}' contains characters that are not " +
					"valid in a file name on this platform.");

			return trimmed;
		}

		public async StorageReadTask TryReadAsync(string key, Allocator allocator, CancellationToken cancellation = default)
		{
#if UNITY_WEBGL && !UNITY_EDITOR
			// A fresh page starts with an empty in-memory filesystem: without pulling IndexedDB in
			// first, every previously saved slot reads as "not found".
			await Storage.WebGL.WebGLFileSystem.EnsureMountedAsync(_rootDirectory);
#endif
			var path = ResolvePath(key);

			// Held across the whole read, not just the .bak restore. The write dance renames the
			// live file, so a content read overlapping it can have the file move out from under the
			// handle. Acquired here rather than inside PrepareRead because SemaphoreSlim is NOT
			// reentrant — taking it at both levels would deadlock against itself.
			await StorageGate.EnterAsync(path, cancellation);

			try
			{
				// .bak restore + stat touch the filesystem — keep them off the caller thread.
				var length = await PersistenceTask.RunOnThreadPool(static state => PrepareRead(state), path, cancellation);

				if (length <= 0)
					return StorageReadResult.NotFound;

				if (length > int.MaxValue)
					throw new IOException($"[FileStorage] Save file too large ({length} bytes): '{path}'.");

				var result = new NativeArray<byte>((int)length, allocator, NativeArrayOptions.UninitializedMemory);
				var command = new NativeArray<ReadCommand>(1, Allocator.Persistent);
				ReadStatus status;

				try
				{
					var handle = IssueRead(path, result, command, length);

					try
					{
						status = await AwaitCompletion(handle);
					}
					finally
					{
						handle.Dispose();
					}
				}
				finally
				{
					command.Dispose();
				}

				if (status == ReadStatus.Complete && !cancellation.IsCancellationRequested)
					return new StorageReadResult(result);

				result.Dispose();
				cancellation.ThrowIfCancellationRequested();
				throw new IOException($"[FileStorage] AsyncReadManager failed reading '{path}' ({status}).");
			}
			finally
			{
				StorageGate.Exit(path);
			}
		}

		public unsafe TaskType WriteAsync(string key, NativeArray<byte> data, CancellationToken cancellation = default)
		{
#if UNITY_WEBGL && !UNITY_EDITOR
			// WebGL writes reach a filesystem that lives in RAM; only an explicit IDBFS sync makes
			// them survive the tab. Completing before that sync would report a save as durable when
			// it is one page reload away from being gone.
			return WriteWebGLAsync(key, data, cancellation);
#else
			var path = ResolvePath(key);

			// Zero-copy by contract: the caller guarantees `data` stays valid until this
			// task completes (see IStorage.WriteAsync remarks), so only the pointer and
			// length cross the thread hop — no defensive TempJob duplicate.
			var state = (path, (IntPtr)data.GetUnsafeReadOnlyPtr(), data.Length);

			return PersistenceTask.RunOnThreadPool(static boxed =>
			{
				var (p, ptr, length) = boxed;

				// Blocking rather than async: the whole dance is synchronous and already off the
				// caller thread, so there is nothing to yield to.
				StorageGate.Enter(p);

				try
				{
					WriteSync(p, ptr, length);
				}
				finally
				{
					StorageGate.Exit(p);
				}
			}, state, cancellation);
#endif
		}

#if UNITY_WEBGL && !UNITY_EDITOR
		private async TaskType WriteWebGLAsync(string key, NativeArray<byte> data, CancellationToken cancellation)
		{
			await Storage.WebGL.WebGLFileSystem.EnsureMountedAsync(_rootDirectory);

			cancellation.ThrowIfCancellationRequested();

			var path = ResolvePath(key);

			// Single-threaded platform: the gate is uncontended, and the write is a memcpy into
			// MEMFS rather than a disk seek.
			StorageGate.Enter(path);

			try
			{
				WriteInPlace(path, data);
			}
			finally
			{
				StorageGate.Exit(path);
			}

			await Storage.WebGL.WebGLFileSystem.FlushAsync();
		}

		/// <summary>Pointer extraction split out: a raw pointer cannot live across an await.</summary>
		private static unsafe void WriteInPlace(string path, NativeArray<byte> data)
			=> WriteSync(path, (IntPtr)data.GetUnsafeReadOnlyPtr(), data.Length);
#endif

		public BoolTask ExistsAsync(string key, CancellationToken cancellation = default)
		{
			var path = ResolvePath(key);

			return PersistenceTask.RunOnThreadPool(
				static boxed => File.Exists(boxed) || File.Exists(boxed + ".bak"),
				path,
				cancellation
			);
		}

		public TaskType DeleteAsync(string key, CancellationToken cancellation = default)
		{
#if UNITY_WEBGL && !UNITY_EDITOR
			// A delete that is not synced comes back on the next reload, which is worse than a
			// delete that fails: the player sees a slot they removed reappear.
			return DeleteWebGLAsync(key, cancellation);
#else
			var path = ResolvePath(key);

			// Takes the same lock as the write: deleting a slot while its tmp/bak dance is mid-flight
			// is the nastier half of the race, since the delete can remove the .bak the writer is
			// about to move the live file into.
			return PersistenceTask.RunOnThreadPool(static p =>
				{
					StorageGate.Enter(p);

					try
					{
						// Allocate a temporary string.
						var extraPath = p + ".bak";

						File.Delete(p);
						File.Delete(extraPath);

						// Saving one unnecessary string allocation
						UnsafeStringUtils.Write(extraPath, ".tmp", p.Length);

						File.Delete(extraPath);
					}
					finally
					{
						StorageGate.Exit(p);
					}
				},
				path,
				cancellation
			);
#endif
		}

#if UNITY_WEBGL && !UNITY_EDITOR
		private async TaskType DeleteWebGLAsync(string key, CancellationToken cancellation)
		{
			await Storage.WebGL.WebGLFileSystem.EnsureMountedAsync(_rootDirectory);

			cancellation.ThrowIfCancellationRequested();

			var path = ResolvePath(key);

			StorageGate.Enter(path);

			try
			{
				File.Delete(path);
				File.Delete(path + BackupSuffix);
				File.Delete(path + ".tmp");
			}
			finally
			{
				StorageGate.Exit(path);
			}

			await Storage.WebGL.WebGLFileSystem.FlushAsync();
		}
#endif

		private static async ReadStatusTask AwaitCompletion(ReadHandle handle)
		{
			ReadStatus status;

			while ((status = handle.Status) == ReadStatus.InProgress)
				await PersistenceTask.Yield();

			return status;
		}

		/// <summary>Restores a .bak if the main file is missing; returns the byte length, or -1 if no data.</summary>
		/// <remarks>
		/// This is a <b>mutation</b> despite living on the read path — it renames the backup over the
		/// live name — so it must not race the write dance, which moves the live file to .bak and
		/// back. The lock is held by <see cref="TryReadAsync"/> for the whole read rather than taken
		/// here: <see cref="SemaphoreSlim"/> is not reentrant, so acquiring at both levels would
		/// deadlock. Never call this without the gate held for <paramref name="path"/>.
		/// </remarks>
		private static long PrepareRead(string path)
		{
			var bakPath = path + ".bak";

			if (File.Exists(path) == false && File.Exists(bakPath))
				File.Move(bakPath, path);

			var info = new FileInfo(path);
			return info.Exists ? info.Length : -1L;
		}

		// Unsafe code is not allowed inside async methods; the read is issued here and
		// polled from TryReadAsync. The command buffer must stay alive until completion.
		private static unsafe ReadHandle IssueRead(string path, NativeArray<byte> target, NativeArray<ReadCommand> command, long length)
		{
			command[0] = new ReadCommand
			{
				Buffer = target.GetUnsafePtr(),
				Offset = 0,
				Size = length
			};

			return AsyncReadManager.Read(path, (ReadCommand*)command.GetUnsafePtr(), 1);
		}

		private static unsafe void WriteSync(string path, IntPtr dataPtr, int length)
		{
			var directory = Path.GetDirectoryName(path);

			if (!Directory.Exists(directory))
				Directory.CreateDirectory(directory!);
			
			var tmpPath = path + ".tmp";
			var bakPath = path + ".bak";

			// bufferSize: 1 disables FileStream's internal buffer (useless for one big
			// Write). No FileOptions.WriteThrough: the single Flush(flushToDisk: true)
			// below gives the same durability without disabling the OS write cache.
			using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1))
			{
				stream.Write(new ReadOnlySpan<byte>((byte*)dataPtr, length));
				stream.Flush(flushToDisk: true);
			}

			// Delete first: a stale .bak left behind by a crash between the two moves
			// must not brick the slot forever (File.Move throws if dest exists, and
			// the overwrite overload is not part of .NET Standard 2.1).
			if (File.Exists(path))
			{
				File.Delete(bakPath);
				File.Move(path, bakPath);
			}

			File.Move(tmpPath, path);
			File.Delete(bakPath);
		}

		#region Enumeration

		/// <inheritdoc />
		public IntTask PopulateAsync(IList<StorageKeyInfo> destination, CancellationToken cancellation = default)
		{
			if (destination == null)
				throw new ArgumentNullException(nameof(destination));

			// A directory walk is blocking I/O, so it goes to the pool like every other operation on
			// this backend. `destination` is appended to from that thread; the caller is awaiting and
			// must not touch the list until it completes, which is the same contract as WriteAsync's.
			return PersistenceTask.RunOnThreadPool(
				static state => Enumerate(state._rootDirectory, state._fileExtension, state.destination),
				(_rootDirectory, _fileExtension, destination),
				cancellation);
		}

		private static int Enumerate(string rootDirectory, string fileExtension, IList<StorageKeyInfo> destination)
		{
			var directory = new DirectoryInfo(rootDirectory);

			// A fresh install has no save folder. That is an empty listing, not a failure.
			if (directory.Exists == false)
				return 0;

			var suffix = "." + fileExtension;
			var backupSuffix = suffix + BackupSuffix;
			var added = 0;

			// EnumerateFiles yields FileInfo, so Length and LastWriteTimeUtc come from the walk
			// itself rather than costing a second stat per file. AllDirectories because
			// MultiFileSaveLayout puts a slot's shard files in a "slot/" subdirectory.
			foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
			{
				var name = file.Name;
				int suffixLength;

				// Matched against the name rather than through the search pattern on purpose:
				// Windows pattern matching also matches extensions LONGER than the pattern (the
				// "*.htm" matches ".html" quirk), which would let the crash-safe dance's
				// "<key>.save.tmp" and "<key>.save.bak" through as if they were saves.
				if (name.EndsWith(suffix, StringComparison.Ordinal))
				{
					suffixLength = suffix.Length;
				}
				else if (name.EndsWith(backupSuffix, StringComparison.Ordinal))
				{
					// A backup with no live file is a slot that crashed mid-write. TryReadAsync
					// restores it transparently, so it is genuinely loadable — hiding it would make
					// a save vanish at exactly the moment someone goes looking for it.
					if (File.Exists(file.FullName[..^BackupSuffix.Length]))
						continue;

					suffixLength = backupSuffix.Length;
				}
				else
				{
					continue;
				}

				var key = ToKey(file.FullName, rootDirectory, suffixLength);

				if (key == null)
					continue;

				destination.Add(new StorageKeyInfo(key, file.Length, file.LastWriteTimeUtc.Ticks));
				added++;
			}

			return added;
		}

		/// <summary>
		/// Inverts <see cref="BuildPath"/>: strips the root and the extension, and normalises the
		/// platform separator back to '/', so the result can be handed straight back to
		/// <see cref="TryReadAsync"/>.
		/// </summary>
		private static string ToKey(string fullPath, string rootDirectory, int suffixLength)
		{
			// The walk is rooted at rootDirectory so this holds, but a junction or symlink inside it
			// can surface a path that is not underneath — skip rather than produce a bogus key.
			if (fullPath.StartsWith(rootDirectory, StringComparison.Ordinal) == false)
				return null;

			var start = rootDirectory.Length;
			var length = fullPath.Length - start - suffixLength;

			if (length <= 0)
				return null;

			return string.Create(length, (fullPath, start), static (span, state) =>
			{
				state.fullPath.AsSpan(state.start, span.Length).CopyTo(span);

				// No-op on platforms where the separator already is '/'.
				for (var i = 0; i < span.Length; i++)
					if (span[i] == Path.DirectorySeparatorChar)
						span[i] = '/';
			});
		}

		#endregion

		private string ResolvePath(string key)
		{
			// Dictionary throws System.ArgumentNullException on null/empty string.
			if (string.IsNullOrEmpty(key))
				throw new InvalidPathException("Key is null or empty. Unable to build path.");
			
			if (_pathCache.TryGetValue(key, out var cached))
				return cached;
			
			var path = BuildPath(key);
			_pathCache[key] = path;
			return path;
		}

		// Runs once per key ever (see _pathCache) — plain concat is the right tool.
		private string BuildPath(string key)
		{
			var localPath = string.Create(key.Length + _fileExtension.Length + 1, (key, _fileExtension),
				static (span, state) =>
				{
					state.key.AsSpan().CopyTo(span);
					var offset = state.key.Length - 1;
					span[++offset] = '.';
					state._fileExtension.AsSpan().CopyTo(span[++offset..]);
				});

			// Confinement is decided on the NORMALIZED result, which is the whole point:
			//   * Path.Combine performs no normalization at all, so "a/../../evil" survives it
			//     verbatim and would sail through a prefix test on the combined string while
			//     resolving somewhere else entirely;
			//   * Combine also discards the root outright when the key is itself rooted, so
			//     "C:\evil" simply *becomes* the path.
			// GetFullPath collapses both cases into an absolute path that either starts with the
			// root or does not. Ordinal, because the default StartsWith overload is culture-aware
			// and a linguistic comparison has no business deciding a security boundary.
			var fullPath = Path.GetFullPath(Path.Combine(_rootDirectory, localPath));

			if (fullPath.StartsWith(_rootDirectory, StringComparison.Ordinal) == false)
				throw new InvalidPathException(
					$"Key '{key}' resolves to '{fullPath}', which is outside the save root '{_rootDirectory}'.");

			// Confined but not usable: a key can still normalise to a directory rather than a file.
			// Whether it does is runtime-dependent — CoreCLR strips trailing dots, so "..." collapses
			// to its parent directory, while Mono keeps the component verbatim — so this fires on
			// some runtimes and not others for the same key. It stays because where it does fire the
			// alternative is an access-denied IOException from the write, which says nothing about
			// the key that caused it. The extension check in the constructor is what makes the
			// common route into this deterministic.
			if (Path.GetFileName(fullPath.AsSpan()).IsEmpty)
				throw new InvalidPathException(
					$"Key '{key}' resolves to the directory '{fullPath}' rather than to a file.");

			return fullPath;
		}

		/// <remarks>
		/// The path lock is deliberately not released here: it is process-wide and shared with any
		/// other storage over the same files, so one instance closing must not disturb it.
		/// </remarks>
		public void Dispose()
		{
			_pathCache.Clear();
		}
	}
}