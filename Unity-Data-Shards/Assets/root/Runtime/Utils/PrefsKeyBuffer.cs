using System;
using System.Buffers;
using System.IO;
using System.Text;

namespace Saesentsessis.Persistence.Utils
{
	/// <summary>
	/// Shared plumbing for the readers that parse a preferences <i>file</i> — loading it into a
	/// pooled buffer, and collecting the keys that survive filtering.
	/// </summary>
	/// <remarks>
	/// The file readers all follow the Windows reader's shape rather than the Android one: the whole
	/// store is scanned as spans and a managed string is allocated only for a key that matches, so a
	/// prefs file holding hundreds of unrelated settings costs nothing to skip past.
	/// </remarks>
	internal struct PrefsKeyBuffer
	{
		private string[] _keys;
		private int _count;

		public readonly ReadOnlySpan<string> Keys => new(_keys, 0, _count);

		public void Reset(string[] storage)
		{
			_keys = storage ?? Array.Empty<string>();
			_count = 0;
		}

		public void Add(ReadOnlySpan<char> key)
		{
			Grow();
			_keys[_count++] = new string(key);
		}

		/// <summary>
		/// Adds a key still in its UTF-8 source encoding, decoding straight into the final string.
		/// </summary>
		/// <remarks>
		/// The point of this overload is what it lets a parser skip: a scanner working on raw file
		/// bytes never has to decode anything it is about to reject, so the whole-file char buffer
		/// disappears and only matched keys are ever converted.
		/// </remarks>
		public void Add(ReadOnlySpan<byte> utf8Key)
		{
			Grow();
			_keys[_count++] = Encoding.UTF8.GetString(utf8Key);
		}

		private void Grow()
		{
			if (_count == _keys.Length)
				Array.Resize(ref _keys, _count == 0 ? 8 : _count * 2);
		}

		public readonly string[] Storage => _keys;

		/// <summary>
		/// Reads a whole file into a pooled buffer. Null when it does not exist or cannot be read —
		/// a store that is unreadable lists as empty rather than throwing out of a browser refresh.
		/// </summary>
		public static byte[] TryReadFile(string path, out int length)
		{
			length = 0;

			try
			{
				if (File.Exists(path) == false)
					return null;

				// bufferSize 1 disables FileStream's own 4 KB staging buffer: the whole file goes
				// into the pooled array in one read, so an intermediate copy would be pure waste.
				using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
					bufferSize: 1, FileOptions.SequentialScan);

				var size = stream.Length;

				if (size is 0 or > int.MaxValue)
					return null;

				var buffer = ArrayPool<byte>.Shared.Rent((int)size);
				var total = 0;

				while (total < size)
				{
					var read = stream.Read(buffer, total, (int)size - total);

					if (read <= 0)
						break;

					total += read;
				}

				length = total;
				return buffer;
			}
			catch (IOException)
			{
				return null;
			}
			catch (UnauthorizedAccessException)
			{
				return null;
			}
		}

		/// <summary>The user's home directory, or null when the environment does not name one.</summary>
		public static string HomeDirectory()
		{
			var home = Environment.GetEnvironmentVariable("HOME");

			return string.IsNullOrEmpty(home)
				? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
				: home;
		}

		/// <summary>
		/// Applies the storage postfix to a candidate key, yielding the storage key it represents.
		/// </summary>
		public static bool TryMatch(ReadOnlySpan<char> candidate, string postfix, out ReadOnlySpan<char> key)
		{
			key = default;

			if (candidate.EndsWith(postfix.AsSpan(), StringComparison.Ordinal) == false)
				return false;

			key = candidate[..^postfix.Length];

			return key.IsEmpty == false;
		}

		/// <summary>
		/// The same match, performed on the UTF-8 bytes as they sit in the file. Returns the length
		/// of the key once the postfix is removed, or -1 when the candidate does not match.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Valid because UTF-8 is self-synchronising: a multi-byte sequence never contains a byte
		/// below 0x80, so a suffix comparison on bytes can never split a character or match one
		/// partially. A postfix outside ASCII simply encodes to bytes an ASCII key cannot end with,
		/// which is the correct answer anyway.
		/// </para>
		/// <para>
		/// A length rather than an <c>out</c> span deliberately: callers encode the postfix with
		/// <c>stackalloc</c>, and the compiler cannot tell that a returned span derives from
		/// <paramref name="candidate"/> rather than from <paramref name="postfix"/>, so an out span
		/// would be rejected as escaping its scope. The caller slices its own buffer instead.
		/// </para>
		/// </remarks>
		public static int MatchLength(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> postfix)
		{
			if (candidate.Length <= postfix.Length || candidate.EndsWith(postfix) == false)
				return -1;

			return candidate.Length - postfix.Length;
		}

		/// <summary>Encodes the postfix once per enumeration, into caller-provided (stack) space.</summary>
		public static ReadOnlySpan<byte> EncodePostfix(string postfix, Span<byte> destination)
			=> destination[..Encoding.UTF8.GetBytes(postfix.AsSpan(), destination)];
	}
}
