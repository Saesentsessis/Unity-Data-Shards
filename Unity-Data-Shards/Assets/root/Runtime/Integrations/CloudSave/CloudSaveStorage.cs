#if PERSISTENCE_HAS_CLOUDSAVE
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Persistence.Core;
using Unity.Collections;
using Unity.Services.Authentication;
using Unity.Services.CloudSave;
using Unity.Services.CloudSave.Models;

namespace Persistence.Storage.CloudSave
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
	public sealed class CloudSaveStorage : IStorage
	{
		private readonly char _reservedChar;
		private readonly Dictionary<string, string> _keyCache = new();

		/// <param name="reservedChar">
		/// Replaces <c>/</c> in incoming keys to satisfy Cloud Save's key rules. Must not appear in
		/// your slot names — an incoming key already containing it is rejected. Default <c>.</c>.
		/// </param>
		public CloudSaveStorage(char reservedChar = '.')
		{
			_reservedChar = reservedChar;
		}

		public async UniTask<StorageReadResult> TryReadAsync(string key, Allocator allocator, CancellationToken cancellation = default)
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

		public async UniTask WriteAsync(string key, NativeArray<byte> data, CancellationToken cancellation = default)
		{
			cancellation.ThrowIfCancellationRequested();
			RequireSignedIn();

			// UGS takes a managed byte[]; the NativeArray cannot be handed over directly.
			await CloudSaveService.Instance.Files.Player.SaveAsync(ResolveKey(key), data.ToArray());
		}

		public async UniTask<bool> ExistsAsync(string key, CancellationToken cancellation = default)
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

		public async UniTask DeleteAsync(string key, CancellationToken cancellation = default)
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

			// Fresh copy — never mutate the caller's string in place (it may be a dictionary key
			// elsewhere; in-place mutation would poison that hash bucket).
			var resolved = key.Replace('/', _reservedChar);
			_keyCache[key] = resolved;
			return resolved;
		}
	}
}
#endif
