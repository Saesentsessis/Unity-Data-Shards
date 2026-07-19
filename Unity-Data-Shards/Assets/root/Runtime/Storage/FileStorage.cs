using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Persistence.Core;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using UnityEngine;

namespace Persistence.Storage
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
        private readonly Dictionary<string, string> _pathCache = new();

        public FileStorage(string rootDirectory = null, string fileExtension = null)
        {
            _fileExtension = fileExtension ?? "save";
            _rootDirectory = rootDirectory ?? Application.persistentDataPath;
        }

        public async UniTask<StorageReadResult> TryReadAsync(string key, Allocator allocator, CancellationToken cancellation = default)
        {
            var path = ResolvePath(key);

            // .bak restore + stat touch the filesystem — keep them off the caller thread.
            var length = await UniTask.RunOnThreadPool(static state => PrepareRead((string)state), path, cancellationToken: cancellation);

            if (length < 0)
                return StorageReadResult.NotFound;

            if (length == 0)
                return new StorageReadResult(new NativeArray<byte>(0, allocator));

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

            if (status != ReadStatus.Complete || cancellation.IsCancellationRequested)
            {
                result.Dispose();
                cancellation.ThrowIfCancellationRequested();
                throw new IOException($"[FileStorage] AsyncReadManager failed reading '{path}' ({status}).");
            }

            return new StorageReadResult(result);
        }

        public unsafe UniTask WriteAsync(string key, NativeArray<byte> data, CancellationToken cancellation = default)
        {
            var path = ResolvePath(key);

            // Zero-copy by contract: the caller guarantees `data` stays valid until this
            // task completes (see IStorage.WriteAsync remarks), so only the pointer and
            // length cross the thread hop — no defensive TempJob duplicate.
            var state = (path, (IntPtr)data.GetUnsafeReadOnlyPtr(), data.Length);

            return UniTask.RunOnThreadPool(static boxed =>
            {
                var (p, ptr, length) = ((string, IntPtr, int))boxed;
                WriteSync(p, ptr, length);
            }, state, cancellationToken: cancellation);
        }

        public UniTask<bool> ExistsAsync(string key, CancellationToken cancellation = default)
        {
            var path = ResolvePath(key);
            return UniTask.FromResult(File.Exists(path) || File.Exists(path + ".bak"));
        }

        public UniTask DeleteAsync(string key, CancellationToken cancellation = default)
        {
            var path = ResolvePath(key);

            // File.Delete is a no-op for missing files.
            File.Delete(path);
            File.Delete(path + ".bak");
            File.Delete(path + ".tmp");

            return UniTask.CompletedTask;
        }

        private static async UniTask<ReadStatus> AwaitCompletion(ReadHandle handle)
        {
            ReadStatus status;

            while ((status = handle.Status) == ReadStatus.InProgress)
                await UniTask.Yield();

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
            lock (_pathCache)
            {
                if (_pathCache.TryGetValue(key, out var cached))
                    return cached;

                var path = BuildPath(key);
                _pathCache[key] = path;
                return path;
            }
        }

        // Runs once per key ever (see _pathCache) — plain concat is the right tool.
        private string BuildPath(string key)
        {
            return Path.Combine(_rootDirectory, key + "." + _fileExtension);
        }
    }
}
