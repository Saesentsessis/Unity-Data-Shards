using System;
using System.Buffers;
using System.Text;

namespace Saesentsessis.Persistence.Utils
{
	/// <summary>
	/// Extracts the root dictionary's key names from an Apple property list — the format
	/// <c>NSUserDefaults</c> writes on macOS, iOS and tvOS.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Deliberately not platform-gated.</b> This is pure byte manipulation with no Apple
	/// dependency, so keeping it out of the <c>UNITY_STANDALONE_OSX</c> branch is what makes it
	/// testable on a machine that is not a Mac — which matters more here than usual, since the
	/// code cannot otherwise be exercised by whoever is most likely to change it.
	/// </para>
	/// <para>
	/// Only keys are decoded. Values are skipped entirely: the object table is random-access, so
	/// reaching the key objects never requires touching a value, which is what keeps listing a
	/// store of large saves cheap.
	/// </para>
	/// </remarks>
	internal static class PlistKeyReader
	{
		private const int TrailerSize = 32;
		private const int HeaderSize = 8;

		/// <summary>
		/// Appends every root-level key matching <paramref name="postfix"/> (postfix removed) to
		/// <paramref name="destination"/>. Silently reads nothing from a file it cannot parse: a
		/// prefs store is not save data, and a browser must survive one it does not understand.
		/// </summary>
		public static void Read(ReadOnlySpan<byte> file, string postfix, ref PrefsKeyBuffer destination)
		{
			try
			{
				// "bplist00". An XML plist starts with '<' or a BOM, so the magic is the only test
				// needed to tell the two apart.
				if (file.Length > HeaderSize && file[0] == (byte)'b' && file[1] == (byte)'p' && file[2] == (byte)'l')
					ReadBinary(file, postfix, ref destination);
				else
					ReadXml(file, postfix, ref destination);
			}
			catch (Exception)
			{
				// Bounds arithmetic on a malformed file. The checks below cover the shapes seen in
				// practice; this is the backstop that keeps a corrupt plist from reaching a caller.
			}
		}

		#region Binary (bplist00)

		private static void ReadBinary(ReadOnlySpan<byte> file, string postfix, ref PrefsKeyBuffer destination)
		{
			if (file.Length < HeaderSize + TrailerSize)
				return;

			var trailer = file[^TrailerSize..];

			// Trailer: 5 unused bytes, sortVersion, offsetIntSize, objectRefSize, then three
			// 8-byte big-endian values — object count, root index, offset-table position.
			int offsetSize = trailer[6];
			int refSize = trailer[7];
			var objectCount = (long)ReadBigEndian(trailer[8..], 8);
			var rootIndex = (long)ReadBigEndian(trailer[16..], 8);
			var offsetTable = (long)ReadBigEndian(trailer[24..], 8);

			if (offsetSize is < 1 or > 8 || refSize is < 1 or > 8)
				return;

			if (objectCount <= 0 || rootIndex >= objectCount || offsetTable < HeaderSize)
				return;

			if (offsetTable + objectCount * offsetSize > file.Length)
				return;

			var root = ObjectOffset(file, offsetTable, offsetSize, rootIndex);

			if (root < 0 || root >= file.Length)
				return;

			// A defaults file's root is a dictionary: marker 0xDn, then n key references followed
			// by n value references. Only the first half is walked.
			if ((file[(int)root] & 0xF0) != 0xD0)
				return;

			var cursor = root;
			var count = ReadCount(file, ref cursor);

			if (count < 0 || cursor + count * 2L * refSize > file.Length)
				return;

			Span<byte> postfixBytes = stackalloc byte[Math.Max(postfix.Length * 3, 1)];
			var encoded = PrefsKeyBuffer.EncodePostfix(postfix, postfixBytes);

			// Rented lazily: an all-ASCII defaults file — the overwhelmingly common case — never
			// needs it, because ASCII keys are matched and decoded straight from their file bytes.
			char[] scratch = null;

			try
			{
				for (long i = 0; i < count; i++)
				{
					var reference = (long)ReadBigEndian(file[(int)(cursor + i * refSize)..], refSize);

					if (reference >= objectCount)
						continue;

					var offset = ObjectOffset(file, offsetTable, offsetSize, reference);

					if (offset < 0 || offset >= file.Length)
						continue;

					ReadKey(file, offset, encoded, postfix, ref scratch, ref destination);
				}
			}
			finally
			{
				if (scratch != null)
					ArrayPool<char>.Shared.Return(scratch);
			}
		}

		private static long ObjectOffset(ReadOnlySpan<byte> file, long offsetTable, int offsetSize, long index)
		{
			var at = offsetTable + index * offsetSize;

			return at + offsetSize > file.Length ? -1 : (long)ReadBigEndian(file[(int)at..], offsetSize);
		}

		/// <summary>
		/// Reads a marker's element count, following the <c>0xnF</c> escape into a trailing integer
		/// when the count does not fit in the low nibble. Advances past marker and count.
		/// </summary>
		private static long ReadCount(ReadOnlySpan<byte> file, ref long cursor)
		{
			var marker = file[(int)cursor++];
			var low = marker & 0x0F;

			if (low != 0x0F)
				return low;

			if (cursor >= file.Length || (file[(int)cursor] & 0xF0) != 0x10)
				return -1;

			var width = 1 << (file[(int)cursor] & 0x0F);
			cursor++;

			if (cursor + width > file.Length)
				return -1;

			var value = (long)ReadBigEndian(file[(int)cursor..], width);
			cursor += width;
			return value;
		}

		/// <summary>
		/// Matches one key object against the postfix and, on a match, adds it.
		/// </summary>
		/// <remarks>
		/// The two string encodings are handled asymmetrically on purpose. An ASCII object
		/// (<c>0x5n</c>) is already valid UTF-8, so it is compared and decoded directly from the
		/// mapped file — no intermediate buffer, and a rejected key costs nothing but a memcmp.
		/// UTF-16 (<c>0x6n</c>) has to be byte-swapped into chars before it means anything, so it
		/// pays for a scratch buffer; it is also the rare case, since defaults keys are ASCII
		/// almost without exception.
		/// </remarks>
		private static void ReadKey(ReadOnlySpan<byte> file, long offset, ReadOnlySpan<byte> postfixBytes,
			string postfix, ref char[] scratch, ref PrefsKeyBuffer destination)
		{
			var kind = file[(int)offset] & 0xF0;

			if (kind != 0x50 && kind != 0x60)
				return;

			var cursor = offset;
			var count = ReadCount(file, ref cursor);

			if (count < 0)
				return;

			// A UTF-16 count is in code units, so its byte length doubles.
			var bytes = kind == 0x50 ? count : count * 2;

			if (cursor + bytes > file.Length)
				return;

			var source = file.Slice((int)cursor, (int)bytes);

			if (kind == 0x50)
			{
				var matched = PrefsKeyBuffer.MatchLength(source, postfixBytes);

				if (matched > 0)
					destination.Add(source[..matched]);

				return;
			}

			scratch ??= ArrayPool<char>.Shared.Rent(256);

			if (scratch.Length < count)
			{
				ArrayPool<char>.Shared.Return(scratch);
				scratch = ArrayPool<char>.Shared.Rent((int)count);
			}

			// UTF-16 **big-endian**, so every unit is byte-swapped on a little-endian host — which
			// is all of them.
			for (var i = 0; i < count; i++)
				scratch[i] = (char)((source[i * 2] << 8) | source[i * 2 + 1]);

			if (PrefsKeyBuffer.TryMatch(new ReadOnlySpan<char>(scratch, 0, (int)count), postfix, out var key))
				destination.Add(key);
		}

		private static ulong ReadBigEndian(ReadOnlySpan<byte> source, int width)
		{
			ulong value = 0;

			for (var i = 0; i < width; i++)
				value = (value << 8) | source[i];

			return value;
		}

		#endregion

		/// <summary>
		/// XML plist fallback — dictionary keys are <c>&lt;key&gt;name&lt;/key&gt;</c>. Rare from
		/// <c>NSUserDefaults</c> itself, but hand-edited and older files exist.
		/// </summary>
		private static void ReadXml(ReadOnlySpan<byte> file, string postfix, ref PrefsKeyBuffer destination)
		{
			Span<byte> postfixBytes = stackalloc byte[Math.Max(postfix.Length * 3, 1)];
			var encoded = PrefsKeyBuffer.EncodePostfix(postfix, postfixBytes);

			// Byte-scanned like the Linux format, and for the same reason: an XML plist carries its
			// values inline, so decoding the document to find key elements would decode every save
			// blob in it.
			while (true)
			{
				var start = file.IndexOf(KeyOpen);

				if (start < 0)
					return;

				file = file[(start + KeyOpen.Length)..];
				var end = file.IndexOf(KeyClose);

				if (end < 0)
					return;

				var matched = PrefsKeyBuffer.MatchLength(file[..end], encoded);

				if (matched > 0)
					destination.Add(file[..matched]);

				file = file[(end + KeyClose.Length)..];
			}
		}

		private static ReadOnlySpan<byte> KeyOpen => new[] { (byte)'<', (byte)'k', (byte)'e', (byte)'y', (byte)'>' };

		private static ReadOnlySpan<byte> KeyClose =>
			new[] { (byte)'<', (byte)'/', (byte)'k', (byte)'e', (byte)'y', (byte)'>' };
	}
}
