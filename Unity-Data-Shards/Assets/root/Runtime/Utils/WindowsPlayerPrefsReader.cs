#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using System;
using System.Buffers;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Saesentsessis.Persistence.Utils
{
	/// <summary>
	/// Enumerates PlayerPrefs keys from the Windows registry, where Unity stores them as values
	/// under <c>HKCU\Software\&lt;company&gt;\&lt;product&gt;</c> (players) or
	/// <c>HKCU\Software\Unity\UnityEditor\&lt;company&gt;\&lt;product&gt;</c> (the editor).
	/// </summary>
	/// <remarks>
	/// <para>
	/// Uses <c>advapi32</c> directly rather than <c>Microsoft.Win32.Registry</c>, for allocations
	/// rather than dependencies: <c>RegistryKey.GetValueNames</c> returns a <c>string[]</c> with one
	/// string per value — every unrelated setting the game has ever written included — and stripping
	/// the hash suffix then allocates a second string per key. <c>RegEnumValueW</c> writes each name
	/// into a buffer we own, so a candidate can be matched and trimmed as a span and only the keys
	/// that actually belong to this storage are ever materialized.
	/// </para>
	/// <para>
	/// Unity appends <c>_h&lt;hash&gt;</c> to every value name. The hash algorithm is undocumented,
	/// so the suffix is identified structurally — the last <c>_h</c> followed only by digits — which
	/// is sound because Unity always appends it last: a key literally named <c>save_h1</c> is stored
	/// as <c>save_h1_h&lt;hash&gt;</c> and still trims back correctly.
	/// </para>
	/// </remarks>
	internal sealed class WindowsPlayerPrefsReader : IPlayerPrefsReader
	{
		private const int ErrorSuccess = 0;
		private const int ErrorMoreData = 234;
		private const int ErrorNoMoreItems = 259;

		private const int KeyRead = 0x20019;

		// Registry value names cap at 16,383 characters, so growth terminates well before this.
		private const int MaxNameChars = 16 * 1024;
		private const int InitialNameChars = 256;

		private static readonly IntPtr HkeyCurrentUser = new(unchecked((int)0x80000001));

		private readonly string _subKey;

		// Reused across calls: the returned span points into this, which is why the contract
		// forbids storing it.
		private string[] _keys = Array.Empty<string>();

		public WindowsPlayerPrefsReader()
		{
			// Built once. The editor writes to a different branch than a player does — a
			// long-standing source of "my keys are missing" confusion.
#if UNITY_EDITOR_WIN
			_subKey = $@"Software\Unity\UnityEditor\{Application.companyName}\{Application.productName}";
#else
			_subKey = $@"Software\{Application.companyName}\{Application.productName}";
#endif
		}

		public ReadOnlySpan<string> EnumerateKeys(string postfix)
		{
			if (RegOpenKeyExW(HkeyCurrentUser, _subKey, 0, KeyRead, out var handle) != ErrorSuccess)
				return ReadOnlySpan<string>.Empty; // No key yet: nothing has been saved on this machine.

			var buffer = ArrayPool<char>.Shared.Rent(InitialNameChars);
			var count = 0;

			try
			{
				for (var index = 0; ; index++)
				{
					if (TryReadName(handle, index, ref buffer, out var name) == false)
						break;

					if (TryExtractKey(name, postfix, out var key) == false)
						continue;

					Append(ref count, new string(key));
				}
			}
			finally
			{
				ArrayPool<char>.Shared.Return(buffer);
				RegCloseKey(handle);
			}

			return new ReadOnlySpan<string>(_keys, 0, count);
		}

		/// <summary>
		/// Reads value <paramref name="index"/> into <paramref name="buffer"/>, growing it if the
		/// name does not fit. False once the enumeration is exhausted.
		/// </summary>
		private static unsafe bool TryReadName(IntPtr handle, int index, ref char[] buffer, out ReadOnlySpan<char> name)
		{
			while (true)
			{
				// In: capacity including the null terminator. Out: length excluding it. Reset every
				// attempt — the API overwrites it, so a stale value would truncate the next name.
				var length = buffer.Length;
				int status;

				fixed (char* pointer = buffer)
					status = RegEnumValueW(handle, index, pointer, ref length,
						IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

				if (status == ErrorSuccess)
				{
					name = new ReadOnlySpan<char>(buffer, 0, length);
					return true;
				}

				if (status != ErrorMoreData || buffer.Length >= MaxNameChars)
				{
					// ErrorNoMoreItems is the normal exit; anything else is a permission or
					// concurrent-modification failure, and an unreadable store lists as empty
					// rather than throwing out of a browser refresh.
					name = default;
					return false;
				}

				// Same index retried against a larger buffer — RegEnumValue is positional, so
				// growing does not skip or repeat an entry.
				ArrayPool<char>.Shared.Return(buffer);
				buffer = ArrayPool<char>.Shared.Rent(Math.Min(buffer.Length * 2, MaxNameChars));
			}
		}

		/// <summary>
		/// Turns a registry value name into the storage key it represents: strips Unity's
		/// <c>_h&lt;hash&gt;</c> suffix, then requires and removes <paramref name="postfix"/>.
		/// </summary>
		internal static bool TryExtractKey(ReadOnlySpan<char> valueName, string postfix, out ReadOnlySpan<char> key)
		{
			key = default;

			var separator = valueName.LastIndexOf("_h".AsSpan());

			if (separator < 0)
				return false;

			var hash = valueName[(separator + 2)..];

			if (hash.IsEmpty)
				return false;

			foreach (var c in hash)
				if ((ushort)(c - '0') > 9)
					return false;

			var candidate = valueName[..separator];

			if (candidate.EndsWith(postfix.AsSpan(), StringComparison.Ordinal) == false)
				return false;

			key = candidate[..^postfix.Length];

			// A key that is nothing but the postfix is not one of ours; PlayerPrefsStorage never
			// writes an empty storage key.
			return key.IsEmpty == false;
		}

		private void Append(ref int count, string key)
		{
			if (count == _keys.Length)
				Array.Resize(ref _keys, count == 0 ? 8 : count * 2);

			_keys[count++] = key;
		}

		public void Dispose()
		{
			// Drop the string references so a large one-off listing does not stay resident for the
			// lifetime of the storage.
			Array.Clear(_keys, 0, _keys.Length);
			_keys = Array.Empty<string>();
		}

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
		private static extern int RegOpenKeyExW(IntPtr key, string subKey, int options, int desired, out IntPtr result);

		[DllImport("advapi32.dll", ExactSpelling = true)]
		private static extern int RegCloseKey(IntPtr key);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
		private static extern unsafe int RegEnumValueW(IntPtr key, int index, char* name, ref int nameLength,
			IntPtr reserved, IntPtr type, IntPtr data, IntPtr dataLength);
	}
}
#endif
