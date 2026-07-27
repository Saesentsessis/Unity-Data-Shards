using System;
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY
using System.Collections.Concurrent;
#endif
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
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
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using StorageReadTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Core.StorageReadResult>;
using ReadStatusTask = System.Threading.Tasks.Task<Unity.IO.LowLevel.Unsafe.ReadStatus>;
#endif

namespace Saesentsessis.Persistence.Storage
{
	/// <summary>
	/// Local file storage. Writes are crash-safe (tmp + bak dance); reads go through
	/// <see cref="AsyncReadManager"/> straight into the target unmanaged buffer, so no
	/// thread-pool thread is blocked on I/O and no managed intermediate exists.
	/// </summary>
	public sealed class FileStorage : IStorage
	{
		private readonly string _rootDirectory;
		private readonly string _fileExtension;

		// Slots are few and hot; resolving key -> full path once per key keeps the
		// steady-state path allocation-free.
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY
		private readonly ConcurrentDictionary<string, string> _pathCache = new();
#else
		private readonly Dictionary<string, string> _pathCache = new();
#endif

		public FileStorage(string rootDirectory = null, string fileExtension = null)
		{
			ValidatePath(rootDirectory);
			ValidatePath(fileExtension);
			
			_fileExtension = fileExtension ?? "save";
			_rootDirectory = rootDirectory ?? Application.persistentDataPath;
		}

		public async StorageReadTask TryReadAsync(string key, Allocator allocator, CancellationToken cancellation = default)
		{
			var path = ResolvePath(key);

			// .bak restore + stat touch the filesystem — keep them off the caller thread.
			var length = await PersistenceTask.RunOnThreadPool(static state => PrepareRead(state), path, cancellation);

			if (length < 0)
				return StorageReadResult.NotFound;

			if (length == 0)
				return new StorageReadResult(default);

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

		public unsafe TaskType WriteAsync(string key, NativeArray<byte> data, CancellationToken cancellation = default)
		{
			var path = ResolvePath(key);

			// Zero-copy by contract: the caller guarantees `data` stays valid until this
			// task completes (see IStorage.WriteAsync remarks), so only the pointer and
			// length cross the thread hop — no defensive TempJob duplicate.
			var state = (path, (IntPtr)data.GetUnsafeReadOnlyPtr(), data.Length);

			return PersistenceTask.RunOnThreadPool(static boxed =>
			{
				var (p, ptr, length) = boxed;
				WriteSync(p, ptr, length);
			}, state, cancellation);
		}

		public BoolTask ExistsAsync(string key, CancellationToken cancellation = default)
		{
			var path = ResolvePath(key);
			return PersistenceTask.FromResult(File.Exists(path) || File.Exists(path + ".bak"));
		}

		public TaskType DeleteAsync(string key, CancellationToken cancellation = default)
		{
			var path = ResolvePath(key);

			// Allocate a temporary string.
			var extraPath = path + ".bak";
			
			// File.Delete is a no-op for missing files.
			File.Delete(path);
			File.Delete(extraPath);
			
			// Saving one unnecessary string allocation
			UnsafeStringUtils.Write(extraPath, ".tmp", path.Length);
			
			File.Delete(extraPath);

			return PersistenceTask.CompletedTask;
		}

		private static async ReadStatusTask AwaitCompletion(ReadHandle handle)
		{
			ReadStatus status;

			while ((status = handle.Status) == ReadStatus.InProgress)
				await PersistenceTask.Yield();

			return status;
		}

		/// <summary>Restores a .bak if the main file is missing; returns the byte length, or -1 if no data.</summary>
		private static long PrepareRead(string path)
		{
			var bakPath = path + ".bak";

			if (!File.Exists(path) && File.Exists(bakPath))
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

		private string ResolvePath(string key)
		{
			if (_pathCache.TryGetValue(key, out var cached))
				return cached;
			
			var path = BuildPath(key);
			_pathCache[key] = path;
			return path;
		}

		// Runs once per key ever (see _pathCache) — plain concat is the right tool.
		private string BuildPath(string key)
		{
			if (string.IsNullOrEmpty(key))
				throw new InvalidPathException("Key is null or empty. Unable to build path.");
			
			ValidatePath(key);

			var localPath = string.Create(key.Length + _fileExtension.Length + 1, (key, _fileExtension),
				static (span, state) =>
				{
					state.key.AsSpan().CopyTo(span);
					var offset = state.key.Length - 1;
					span[++offset] = '.';
					state._fileExtension.AsSpan().CopyTo(span[++offset..]);
				});
			
			return Path.Combine(_rootDirectory, localPath);
		}
		
		[Conditional("ENABLE_PERSISTENCE_INTEGRITY_CHECKS")]
		private static unsafe void ValidatePath(string path)
		{
			if (string.IsNullOrEmpty(path))
				return;
			
			fixed (char* pathPtr = path)
				if (ContainsPreviousDirectory((ushort*)pathPtr, path.Length))
					throw new InvalidPathException("Provided key contains \'../\'(previous directory) symbols. That's prohibited.");
		}
		
		// "../" UTF-16: '.' (0x002E), '.' (0x002E), '/' (0x002F) -> 0x0000002F002E002E
		private const ulong TargetForward = 0x0000002F002E002Eul;
		// "..\" UTF-16: '.' (0x002E), '.' (0x002E), '\' (0x005C) -> 0x0000005C002E002E
		private const ulong TargetBackward = 0x0000005C002E002Eul;
		
		private const ulong Mask3Chars = 0x0000FFFFFFFFFFFFul;
		
		/// <summary>
		/// Evaluates if the string contains the "../" or "..\" sequence using a 64-bit SWAR approach.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static unsafe bool ContainsPreviousDirectory(ushort* pathPtr, int length)
		{
			// We search up to the point where 3 characters remain.
			var endPtr = pathPtr + length;
			var limitPtr = endPtr - 3;

			// Unroll by 4 to utilize instruction-level parallelism.
			// Using bitwise OR (|) instead of logical OR (||) avoids branch mispredictions.
			while (pathPtr < limitPtr)
			{
				var v0 = *(ulong*)pathPtr++ & Mask3Chars;
				var v1 = *(ulong*)pathPtr++ & Mask3Chars;
				var v2 = *(ulong*)pathPtr++ & Mask3Chars;
				var v3 = *(ulong*)pathPtr++ & Mask3Chars;

				var match0 = (v0 == TargetForward) | (v0 == TargetBackward);
				var match1 = (v1 == TargetForward) | (v1 == TargetBackward);
				var match2 = (v2 == TargetForward) | (v2 == TargetBackward);
				var match3 = (v3 == TargetForward) | (v3 == TargetBackward);
				
				if (match0 | match1 | match2 | match3)
					return true;
			}
			
			// Tail loop for remaining characters (0 to 3 iterations)
			while (pathPtr < endPtr)
			{
				var v = *(ulong*)pathPtr++ & Mask3Chars;
				
				if ((v == TargetForward) | (v == TargetBackward))
					return true;
			}

			return false;
		}

		public void Dispose()
		{
			_pathCache.Clear();
		}
	}
}