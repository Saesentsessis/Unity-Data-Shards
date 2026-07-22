using System;
using System.Collections.Generic;
using System.Threading;
using Persistence.Core;
using Persistence.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using BoolTask = Cysharp.Threading.Tasks.UniTask<bool>;
using StorageReadTask = Cysharp.Threading.Tasks.UniTask<Persistence.Core.StorageReadResult>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using StorageReadTask = System.Threading.Tasks.Task<Persistence.Core.StorageReadResult>;
#endif

namespace Persistence.Storage
{
	/// <summary>
	/// PlayerPrefs storage. PlayerPrefs is main-thread only and string-based, so the
	/// payload round-trips through base64; both directions are single-allocation.
	/// </summary>
	public sealed class PlayerPrefsStorage : IStorage
	{
		private readonly string _postfix;
		private readonly Dictionary<string, string> _keyCache;

		public PlayerPrefsStorage(string postfix = null)
		{
			_postfix = postfix ?? string.Empty;
			_keyCache = _postfix.Length > 0 ? new Dictionary<string, string>() : null;
		}

		public StorageReadTask TryReadAsync(string key, Allocator allocator, CancellationToken cancellation = default)
		{
			var base64 = PlayerPrefs.GetString(ResolveKey(key));

			if (string.IsNullOrEmpty(base64))
				return PersistenceTask.FromResult(StorageReadResult.NotFound);

			unsafe
			{
				// Exact decoded length from the padding, so we can decode straight
				// into the final buffer with no Temp array and no MemCpy.
				var padding = 0;
				if (base64[^1] == '=') padding++;
				if (base64.Length > 1 && base64[^2] == '=') padding++;
				var decodedLength = base64.Length / 4 * 3 - padding;

				var result = new NativeArray<byte>(decodedLength, allocator, NativeArrayOptions.UninitializedMemory);

				if (!Convert.TryFromBase64String(base64, new Span<byte>((byte*)result.GetUnsafePtr(), decodedLength), out _))
				{
					result.Dispose();
					throw new InvalidOperationException($"[PlayerPrefsStorage] Corrupted base64 payload for key '{key}'.");
				}

				return PersistenceTask.FromResult(new StorageReadResult(result));
			}
		}

		public TaskType WriteAsync(string key, NativeArray<byte> data, CancellationToken cancellation = default)
		{
			string base64;

			unsafe
			{
				// string.Create + TryToBase64Chars: single allocation, encoded in place.
				var encodedLength = (data.Length + 2) / 3 * 4;
				base64 = string.Create(encodedLength,
					((IntPtr)data.GetUnsafeReadOnlyPtr(), data.Length),
					static (chars, state) =>
					{
						var span = new ReadOnlySpan<byte>((byte*)state.Item1, state.Item2);
						Convert.TryToBase64Chars(span, chars, out _);
					});
			}

			PlayerPrefs.SetString(ResolveKey(key), base64);
			return PersistenceTask.CompletedTask;
		}

		public BoolTask ExistsAsync(string key, CancellationToken cancellation = default)
		{
			return PersistenceTask.FromResult(PlayerPrefs.HasKey(ResolveKey(key)));
		}

		public TaskType DeleteAsync(string key, CancellationToken cancellation = default)
		{
			PlayerPrefs.DeleteKey(ResolveKey(key));
			return PersistenceTask.CompletedTask;
		}

		private string ResolveKey(string key)
		{
			if (_keyCache == null)
				return key;

			if (_keyCache.TryGetValue(key, out var cached))
				return cached;

			var resolved = BuildKey(key);
			_keyCache[key] = resolved;
			return resolved;
		}

		// Runs once per key ever (see _keyCache) — plain concat is the right tool.
		private string BuildKey(string key)
		{
			return key + _postfix;
		}
	}
}
