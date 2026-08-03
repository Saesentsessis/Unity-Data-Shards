#if PERSISTENCE_HAS_CLOUDSAVE
using System;
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY
using System.Collections.Concurrent;
#endif
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Saesentsessis.Persistence.Core;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Services.Authentication;
using Unity.Services.CloudSave;
using Unity.Services.CloudSave.Models;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using BoolTask = Cysharp.Threading.Tasks.UniTask<bool>;
using IntTask = Cysharp.Threading.Tasks.UniTask<int>;
using StorageReadTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.Core.StorageReadResult>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using IntTask = System.Threading.Tasks.Task<int>;
using StorageReadTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Core.StorageReadResult>;
#endif

namespace Saesentsessis.Persistence.Storage.CloudSave
{
	/// <summary>
	/// <see cref="IStorage"/> backed by Unity Gaming Services Cloud Save (player Files API),
	/// which is designed for binary blobs. Pairs with any layout, including
	/// <see cref="Layout.MultiFileSaveLayout"/> — the <c>/</c> in its <c>slot/&lt;hex&gt;</c> keys
	/// is remapped to a Cloud-Save-valid reserved character.
	/// </summary>
	/// <remarks>
	/// CALLER-INITIALIZED: this storage does not touch authentication. The app must call
	/// <c>UnityServices.InitializeAsync()</c> and sign the player in (e.g.
	/// <c>AuthenticationService.Instance.SignInAnonymouslyAsync()</c>) before use; every operation
	/// throws if <see cref="AuthenticationService.IsSignedIn"/> is false. UGS Task APIs carry no
	/// cancellation token, so cancellation is honored only up to the point each call is dispatched.
	/// </remarks>
	/// <remarks>
	/// <b>Cloud Save Files quotas, per player</b> (Unity documents these; there is no limit on the
	/// number of players): 1 GiB of total file storage, up to 1 GiB in any single file, at most
	/// <b>200 files</b>, and a filename of at most 255 characters.
	/// <para>
	/// The file <i>count</i> is the one that bites, and it interacts badly with
	/// <see cref="Layout.MultiFileSaveLayout"/>: that layout spends one file per shard plus one for
	/// the envelope, so a slot of N shards costs N+1 of the player's 200 — across every slot they
	/// own. Anything past roughly 199 shards in total cannot be stored this way at all, and this
	/// package is comfortable with shard counts far above that.
	/// <see cref="Layout.SingleFileSaveLayout"/> spends exactly one file per slot regardless of
	/// shard count and is the right pairing for cloud storage; the size quotas are generous enough
	/// that a single-file save will not approach them.
	/// </para>
	/// <para>
	/// The two quotas a single call can actually check — file size and filename length — are
	/// enforced below. The 200-file and 1 GiB-total quotas are per-player running totals that
	/// nothing in a write call can see without an extra network round trip, so they are documented
	/// rather than guarded; UGS reports them as a <c>CloudSaveException</c> at the point of failure.
	/// </para>
	/// </remarks>
	public sealed class CloudSaveStorage : IStorage, IListableStorage
	{
		/// <summary>Cloud Save's per-file ceiling, and also the per-player total.</summary>
		private const long MaxFileBytes = 1L * 1024 * 1024 * 1024;

		/// <summary>Cloud Save's filename limit, applied to the key after separator remapping.</summary>
		private const int MaxKeyChars = 255;

		private readonly char _reservedChar;
#if ENABLE_PERSISTENCE_SAFE_CONCURRENCY
		private readonly ConcurrentDictionary<string, string> _keyCache = new();
#else
		private readonly Dictionary<string, string> _keyCache = new();
#endif

		/// <param name="reservedChar">
		/// Replaces <c>/</c> in incoming keys to satisfy Cloud Save's key rules. Must not appear in
		/// your slot names — an incoming key already containing it is rejected. Default <c>.</c>.
		/// </param>
		public CloudSaveStorage(char reservedChar = '.')
		{
			_reservedChar = reservedChar;
		}

		public async StorageReadTask TryReadAsync(string key, Allocator allocator, CancellationToken cancellation = default)
		{
			cancellation.ThrowIfCancellationRequested();
			RequireSignedIn();

			byte[] bytes;

			try
			{
				bytes = await CloudSaveService.Instance.Files.Player.LoadBytesAsync(ResolveKey(key));
			}
			catch (CloudSaveException e) when (e.Reason == CloudSaveExceptionReason.NotFound)
			{
				return StorageReadResult.NotFound;
			}

			return new StorageReadResult(new NativeArray<byte>(bytes, allocator));
		}

		public async TaskType WriteAsync(string key, NativeArray<byte> data, CancellationToken cancellation = default)
		{
			cancellation.ThrowIfCancellationRequested();
			RequireSignedIn();

			if (data.Length > MaxFileBytes)
				throw new IOException(
					$"[CloudSaveStorage] Save for key '{key}' is {data.Length} bytes, over Cloud Save's " +
					$"{MaxFileBytes}-byte per-file limit. Caught here rather than after the upload has already " +
					"spent the player's bandwidth.");

			// The Stream overload rather than the byte[] one: ToArray() would copy the whole save
			// into managed memory purely to hand it over. An UnmanagedMemoryStream reads the
			// NativeArray in place, and the caller's buffer-lifetime contract already guarantees it
			// stays valid until this task completes.
			await WriteStreamAsync(ResolveKey(key), data);
		}

		/// <summary>
		/// Uploads the buffer through an <see cref="UnmanagedMemoryStream"/> over its own memory.
		/// </summary>
		/// <remarks>
		/// Split out because the pointer and the <c>unsafe</c> block cannot live in an async method
		/// alongside the await. The stream is disposed before the upload is awaited only in the
		/// sense that it is created here and handed over — UGS reads it during the returned task,
		/// which is why the <c>using</c> spans the await.
		/// </remarks>
		private static async TaskType WriteStreamAsync(string resolvedKey, NativeArray<byte> data)
		{
			using var stream = CreateStream(data);

			await CloudSaveService.Instance.Files.Player.SaveAsync(resolvedKey, stream);
		}

		private static unsafe UnmanagedMemoryStream CreateStream(NativeArray<byte> data)
			=> new((byte*)data.GetUnsafeReadOnlyPtr(), data.Length);

		public async BoolTask ExistsAsync(string key, CancellationToken cancellation = default)
		{
			cancellation.ThrowIfCancellationRequested();
			RequireSignedIn();

			try
			{
				await CloudSaveService.Instance.Files.Player.GetMetadataAsync(ResolveKey(key));
				return true;
			}
			catch (CloudSaveException e) when (e.Reason == CloudSaveExceptionReason.NotFound)
			{
				return false;
			}
		}

		public async TaskType DeleteAsync(string key, CancellationToken cancellation = default)
		{
			cancellation.ThrowIfCancellationRequested();
			RequireSignedIn();

			try
			{
				await CloudSaveService.Instance.Files.Player.DeleteAsync(ResolveKey(key));
			}
			catch (CloudSaveException e) when (e.Reason == CloudSaveExceptionReason.NotFound)
			{
				// Delete is idempotent — a missing key is success.
			}
		}

		/// <inheritdoc />
		/// <remarks>
		/// <para>
		/// Cloud Save's listing already carries each file's size, so this needs no downloads —
		/// which is the whole point of listing before reading.
		/// </para>
		/// <para>
		/// Keys are mapped back through the reserved character, undoing the <c>/</c> substitution
		/// <see cref="ResolveKey"/> applies, so what comes out is a key this storage accepts and a
		/// slot mapper understands.
		/// </para>
		/// <para>
		/// <b>Not compile-verified.</b> This file only builds where the Cloud Save package is
		/// installed, so it is reviewed rather than compiled here — see the notes in the method body
		/// about the one member left unread.
		/// </para>
		/// </remarks>
		public async IntTask PopulateAsync(IList<StorageKeyInfo> destination, CancellationToken cancellation = default)
		{
			if (destination == null)
				throw new ArgumentNullException(nameof(destination));

			cancellation.ThrowIfCancellationRequested();
			RequireSignedIn();

			var files = await CloudSaveService.Instance.Files.Player.ListAllAsync();

			if (files == null)
				return 0;

			foreach (var file in files)
			{
				// FileItem also reports a last-modified time, deliberately not read here: its member
				// type could not be confirmed without the package installed, and a wrong guess would
				// break the build for exactly the projects that do have it. Zero means "backend
				// supplied no time", which StorageKeyInfo already models. Worth filling in once
				// somebody can compile against the real assembly.
				destination.Add(new StorageKeyInfo(RestoreKey(file.Key), file.Size));
			}

			return files.Count;
		}

		/// <summary>Inverse of <see cref="ResolveKey"/>: puts the layout's separator back.</summary>
		private string RestoreKey(string cloudKey)
		{
			return cloudKey.IndexOf(_reservedChar) < 0 ? cloudKey : cloudKey.Replace(_reservedChar, '/');
		}

		private static void RequireSignedIn()
		{
			if (!AuthenticationService.Instance.IsSignedIn)
				throw new InvalidOperationException(
					"[CloudSaveStorage] The player is not signed in. Initialize UGS and sign in before using cloud storage.");
		}

		private string ResolveKey(string key)
		{
			if (_keyCache.TryGetValue(key, out var cached))
				return cached;

			if (key.IndexOf(_reservedChar) >= 0)
				throw new ArgumentException(
					$"[CloudSaveStorage] Key '{key}' contains the reserved character '{_reservedChar}'. " +
					"Slot names must not use it — it encodes the layout's key separator.", nameof(key));

			if (key.Length > MaxKeyChars)
				throw new ArgumentException(
					$"[CloudSaveStorage] Key '{key}' is {key.Length} characters, over Cloud Save's " +
					$"{MaxKeyChars}-character filename limit. Remapping the separator does not change the " +
					"length, so this is checked on the incoming key.", nameof(key));

			// Fresh copy — never mutate the caller's string in place (it may be a dictionary key
			// elsewhere; in-place mutation would poison that hash bucket).
			var resolved = key.Replace('/', _reservedChar);
			_keyCache[key] = resolved;
			return resolved;
		}

		public void Dispose()
		{
			_keyCache.Clear();
		}
	}
}
#endif
