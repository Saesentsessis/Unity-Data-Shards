using System;
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY
using System.Collections.Concurrent;
#endif
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Threading;
using Saesentsessis.Persistence.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using IntTask = System.Threading.Tasks.Task<int>;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using BoolTask = Cysharp.Threading.Tasks.UniTask<bool>;
using StorageReadTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.Core.StorageReadResult>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using StorageReadTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Core.StorageReadResult>;
#endif

namespace Saesentsessis.Persistence.Storage
{
	/// <summary>
	/// PlayerPrefs storage. PlayerPrefs is main-thread only and string-based, so the
	/// payload round-trips through base64; both directions are single-allocation.
	/// </summary>
	/// <remarks>
	/// <b>This is the small-payload backend.</b> Unity's own guidance is to keep a PlayerPrefs
	/// value at 2 KB or smaller and to write anything larger to a file under
	/// <c>Application.persistentDataPath</c> — which is exactly what <see cref="FileStorage"/>
	/// does. Base64 costs another third on top, so a 2 KB stored value carries ~1.5 KB of save
	/// data. See the platform budgets below for what each platform actually enforces.
	/// </remarks>
	public sealed class PlayerPrefsStorage : IStorage, IListableStorage, ISlotKeyMapper
	{
		internal const Options DefaultOptions = Options.FlushOnWrite;

		#region Platform value budgets

		// Both numbers are measured against the BASE64 STRING, since that is what PlayerPrefs
		// stores; the usable payload is three quarters of them.
		//
		// They are deliberately two different kinds of number:
		//
		//   HardValueChars    exceeding it loses the write or kills the process. Always thrown on,
		//                     never gated — the alternative is a save the player silently loses.
		//
		//   AdvisedValueChars platform guidance about performance, not a ceiling. Logged once per
		//                     key under integrity checks and never thrown on: shipped games exceed
		//                     it today and the cost is a slower preferences load, not data loss.

#if UNITY_TVOS
		// tvOS is the strict one: Apple posts a warning notification when the user-defaults store
		// reaches 512 KB and TERMINATES the app at 1 MB. That ceiling covers the whole store
		// rather than one value, so a single value at Apple's warning mark has already spent the
		// entire budget — which is why the hard cap here is the warning number, not the fatal one.
		private const int HardValueChars = 512 * 1024 - 1;
#elif UNITY_IOS
		// iOS 13+ refuses the write and logs "Attempting to store >= 4194304 bytes of data in
		// CFPreferences/NSUserDefaults on this platform is invalid". Also a whole-store limit.
		private const int HardValueChars = 4 * 1024 * 1024 - 1;
#else
		// No documented ceiling to enforce, and inventing one would break projects that work
		// today: the Windows registry is bounded by available memory (Microsoft's 2048-byte
		// figure is a performance recommendation, not a limit), Android SharedPreferences by the
		// size of a Java String, WebGL by whatever quota the browser grants IndexedDB. The 1 MB
		// number often quoted for Web builds belonged to the long-removed Unity Web Player.
		private const int HardValueChars = int.MaxValue;
#endif

		// Unity's documented recommendation, and the same figure Microsoft gives for registry
		// values. Android's whole preferences XML is parsed into memory on first access, which is
		// the practical reason to care there.
		private const int AdvisedValueChars = 2 * 1024;

		#endregion

		private readonly string _postfix;
		private readonly Options _options;
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY
		private readonly ConcurrentDictionary<string, string> _keyCache;
#else
		private readonly Dictionary<string, string> _keyCache;
#endif


		public PlayerPrefsStorage(string postfix = null, Options options = DefaultOptions)
		{
			_postfix = postfix ?? string.Empty;
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY
			_keyCache = _postfix.Length > 0 ? new ConcurrentDictionary<string, string>() : null;
#else
			_keyCache = _postfix.Length > 0 ? new Dictionary<string, string>() : null;
#endif
			_options = options;
		}

		public StorageReadTask TryReadAsync(string key, Allocator allocator, CancellationToken cancellation = default)
		{
			var resolvedKey = ResolveKey(key);

			if (PlayerPrefs.HasKey(resolvedKey) == false)
				return PersistenceTask.FromResult(StorageReadResult.NotFound);
			
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

				if (Convert.TryFromBase64String(base64, new Span<byte>((byte*)result.GetUnsafePtr(), decodedLength), out _))
					return PersistenceTask.FromResult(new StorageReadResult(result));
				
				result.Dispose();
				throw new InvalidOperationException($"[PlayerPrefsStorage] Corrupted base64 payload for key '{key}'.");
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
						var span = new ReadOnlySpan<byte>((byte*)state.Item1, state.Length);
						Convert.TryToBase64Chars(span, chars, out _);
					});
			}

			EnsureWithinPlatformLimit(key, base64.Length, data.Length);
			WarnIfOversized(key, base64.Length, data.Length);

			PlayerPrefs.SetString(ResolveKey(key), base64);

			if ((_options & Options.FlushOnWrite) != 0)
				PlayerPrefs.Save();

			return PersistenceTask.CompletedTask;
		}

		/// <summary>
		/// Rejects a value the platform would refuse to store, before PlayerPrefs is asked to.
		/// </summary>
		/// <remarks>
		/// Only iOS and tvOS have a real ceiling, and on both it applies to the whole defaults
		/// store rather than to one value — so passing this check is necessary, not sufficient.
		/// Several values under the cap can still add up past it, and nothing readable from
		/// PlayerPrefs would let this method know. Use <see cref="FileStorage"/> for save data of
		/// any size on those platforms.
		/// </remarks>
		[Conditional("UNITY_IOS"), Conditional("UNITY_TVOS")]
		private static void EnsureWithinPlatformLimit(string key, int encodedLength, int payloadLength)
		{
			if (encodedLength <= HardValueChars)
				return;

			throw new IOException(
				$"[PlayerPrefsStorage] Save for key '{key}' is {payloadLength} bytes, which base64 expands to " +
				$"{encodedLength} — past this platform's {HardValueChars}-character PlayerPrefs ceiling. The " +
				"platform would drop the write or terminate the app rather than store it. Use FileStorage, or " +
				"add a compression transform if the payload is close to the line.");
		}

#if ENABLE_PERSISTENCE_INTEGRITY_CHECKS
		// Dev-build only, so the allocation is irrelevant; per key rather than global so a second
		// oversized slot is not hidden by the first.
		private static readonly HashSet<string> WarnedKeys = new();
#endif

		/// <summary>
		/// Notes a value past Unity's 2 KB guidance. Advisory: this costs load time, not data.
		/// </summary>
		[Conditional("ENABLE_PERSISTENCE_INTEGRITY_CHECKS")]
		private static void WarnIfOversized(string key, int encodedLength, int payloadLength)
		{
#if ENABLE_PERSISTENCE_INTEGRITY_CHECKS
			if (encodedLength <= AdvisedValueChars || WarnedKeys.Add(key) == false)
				return;

			// Qualified: System.Diagnostics is in scope here for [Conditional].
			UnityEngine.Debug.LogWarning(
				$"[PlayerPrefsStorage] Save for key '{key}' is {payloadLength} bytes, base64-expanded to " +
				$"{encodedLength}, over the {AdvisedValueChars}-character value size Unity recommends for " +
				"PlayerPrefs. It will still be stored. PlayerPrefs is the small-payload backend — every platform " +
				"reads the whole preferences store into memory — so FileStorage is the better home for a save " +
				"this size. This warning appears once per key and only under ENABLE_PERSISTENCE_INTEGRITY_CHECKS.");
#endif
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
		
		/// <inheritdoc />
		/// <remarks>
		/// <para>
		/// PlayerPrefs has no enumeration API, so this reads the store Unity itself writes to — the
		/// registry on Windows, <c>SharedPreferences</c> on Android. Platforms without a reader
		/// throw <see cref="NotSupportedException"/>: silently returning zero would be
		/// indistinguishable from "no saves exist", which is the one answer a load-game screen must
		/// never get wrong.
		/// </para>
		/// <para>
		/// <b>Sizes are reported as 0.</b> Measuring one would mean a <c>GetString</c> per key,
		/// materializing every save's full base64 payload just to draw a list — exactly the cost the
		/// two-phase browse design exists to avoid. Timestamps are 0 for a simpler reason: no
		/// platform's prefs store records a per-key modification time.
		/// </para>
		/// <para>
		/// <b>An empty postfix cannot be listed.</b> Without one there is nothing distinguishing a
		/// save from Unity's own <c>unity.player_session_count</c> or the game's audio settings, and
		/// every one of them would be handed back as a slot.
		/// </para>
		/// </remarks>
		public IntTask PopulateAsync(IList<StorageKeyInfo> destination, CancellationToken cancellation = default)
		{
			if (destination == null)
				throw new ArgumentNullException(nameof(destination));

			if (_postfix.Length == 0)
				throw new InvalidOperationException(
					"[PlayerPrefsStorage] Listing requires a non-empty postfix. PlayerPrefs is a shared " +
					"namespace: without a postfix nothing separates this storage's saves from Unity's own " +
					"entries and every other setting the game has written, so every key would list as a slot.");

			// Process-wide and NOT owned here: the prefs store is one resource however many storages
			// address it, so Dispose must leave it alone. Same rule as the storage gate.
			var reader = PlayerPrefsUtils.Shared;

			if (reader == null)
				throw new NotSupportedException(
					$"[PlayerPrefsStorage] Listing PlayerPrefs keys is not implemented for {Application.platform}. " +
					"Windows (player and editor) and Android are supported; use FileStorage where a slot browser " +
					"is required on other platforms.");

			// The span aliases the reader's reused buffer, so it is consumed before this method
			// yields — and it never does yield, which is what keeps a second storage sharing the
			// same reader from overwriting it mid-loop.
			var keys = reader.EnumerateKeys(_postfix);

			for (var i = 0; i < keys.Length; i++)
			{
				cancellation.ThrowIfCancellationRequested();

				// Size and timestamp are both unavailable without reading the value; see the remarks.
				destination.Add(new StorageKeyInfo(keys[i], 0, 0));
			}

			return PersistenceTask.FromResult(keys.Length);
		}

		/// <inheritdoc />
		/// <remarks>
		/// A PlayerPrefs key is flat — there is no separator and no nesting — so a key is its own
		/// slot, exactly as under <c>SingleFileSaveLayout</c>. The postfix is already stripped by
		/// the time a key reaches here.
		/// </remarks>
		public bool TryGetSlot(ReadOnlySpan<char> storageKey, out ReadOnlySpan<char> slot)
		{
			slot = storageKey;

			return storageKey.IsEmpty == false;
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

		public void Dispose()
		{
			_keyCache?.Clear();

			// PlayerPrefsUtils.Shared is deliberately not disposed here: it is process-wide, and one
			// storage releasing it would break every other one still listing.
			PlayerPrefs.Save();
		}

		[Flags]
		public enum Options
		{
			FlushOnWrite = 1 << 0,
		}
	}
}
