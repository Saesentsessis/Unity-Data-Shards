using System;
using System.Buffers;
using System.Text;

namespace Saesentsessis.Persistence.Utils
{
	/// <summary>
	/// Extracts key names from the XML preferences file Unity writes on Linux: a
	/// <c>&lt;unity_prefs&gt;</c> root holding one <c>&lt;pref name="…" type="…"&gt;</c> element
	/// per key.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Not platform-gated, for the same reason as <see cref="PlistKeyReader"/> — the format has no
	/// Linux dependency, and gating it would put it beyond reach of anyone not building on Linux.
	/// </para>
	/// <para>
	/// <b>Scans the raw UTF-8, never decoding the file.</b> That matters far more here than the
	/// choice not to build a DOM: this file holds the prefs <i>values</i> too, and with base64 save
	/// blobs among them it is dominated by data no key scan cares about. Decoding it whole would
	/// rent a char buffer twice the file's size to read a few dozen attribute names. Byte scanning
	/// is exact rather than approximate — the markers are ASCII, and UTF-8 never places a byte
	/// below 0x80 inside a multi-byte sequence, so a byte-level search cannot match inside a
	/// character.
	/// </para>
	/// <para>
	/// The result: a non-matching key costs no decoding at all, and a matching one costs exactly
	/// the string it becomes.
	/// </para>
	/// </remarks>
	internal static class UnityPrefsXmlReader
	{
		// UTF-8 for `<pref name="`, the only element this format uses for a key.
		private static ReadOnlySpan<byte> ElementPrefix => new[]
		{
			(byte)'<', (byte)'p', (byte)'r', (byte)'e', (byte)'f', (byte)' ',
			(byte)'n', (byte)'a', (byte)'m', (byte)'e', (byte)'=', (byte)'"'
		};

		private const byte Quote = (byte)'"';
		private const byte Ampersand = (byte)'&';

		public static void Read(ReadOnlySpan<byte> file, string postfix, ref PrefsKeyBuffer destination)
		{
			// Encoded once per enumeration onto the stack. A storage postfix is a handful of
			// characters; the ×3 covers the widest UTF-8 expansion of any of them.
			Span<byte> postfixBytes = stackalloc byte[Math.Max(postfix.Length * 3, 1)];
			var encoded = PrefsKeyBuffer.EncodePostfix(postfix, postfixBytes);

			while (true)
			{
				var start = file.IndexOf(ElementPrefix);

				if (start < 0)
					return;

				file = file[(start + ElementPrefix.Length)..];

				var end = file.IndexOf(Quote);

				if (end < 0)
					return; // Truncated file: keep whatever was already collected.

				Accept(file[..end], encoded, postfix, ref destination);
				file = file[(end + 1)..];
			}
		}

		private static void Accept(ReadOnlySpan<byte> name, ReadOnlySpan<byte> postfixBytes, string postfix,
			ref PrefsKeyBuffer destination)
		{
			// Fast path: no entity, so the bytes are the name and the comparison is a memcmp.
			if (name.IndexOf(Ampersand) < 0)
			{
				var matched = PrefsKeyBuffer.MatchLength(name, postfixBytes);

				if (matched > 0)
					destination.Add(name[..matched]);

				return;
			}

			// An escaped name has to be decoded before the postfix can be tested — `&amp;` is five
			// bytes standing for one character, so the byte tail is not the character tail. Rare
			// enough that renting here costs nothing in practice.
			var chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(name.Length));

			try
			{
				var decoded = Encoding.UTF8.GetChars(name, chars);
				var length = Unescape(new ReadOnlySpan<char>(chars, 0, decoded), chars);

				if (PrefsKeyBuffer.TryMatch(new ReadOnlySpan<char>(chars, 0, length), postfix, out var key))
					destination.Add(key);
			}
			finally
			{
				ArrayPool<char>.Shared.Return(chars);
			}
		}

		/// <summary>
		/// Resolves the five predefined XML entities. Anything else passes through verbatim: a
		/// numeric character reference is not something Unity writes into a pref name, and copying
		/// it makes the key fail to match rather than silently corrupting it.
		/// </summary>
		/// <remarks>
		/// Safe to run in place — every entity is longer than its replacement, so the write cursor
		/// can never overtake the read cursor.
		/// </remarks>
		internal static int Unescape(ReadOnlySpan<char> source, Span<char> destination)
		{
			var written = 0;

			for (var i = 0; i < source.Length;)
			{
				if (source[i] != '&')
				{
					destination[written++] = source[i++];
					continue;
				}

				var rest = source[i..];

				if (Consume(rest, "&amp;", '&', destination, ref written, ref i)) continue;
				if (Consume(rest, "&lt;", '<', destination, ref written, ref i)) continue;
				if (Consume(rest, "&gt;", '>', destination, ref written, ref i)) continue;
				if (Consume(rest, "&quot;", '"', destination, ref written, ref i)) continue;
				if (Consume(rest, "&apos;", '\'', destination, ref written, ref i)) continue;

				destination[written++] = source[i++];
			}

			return written;
		}

		private static bool Consume(ReadOnlySpan<char> source, string entity, char replacement,
			Span<char> destination, ref int written, ref int index)
		{
			if (source.StartsWith(entity.AsSpan(), StringComparison.Ordinal) == false)
				return false;

			destination[written++] = replacement;
			index += entity.Length;
			return true;
		}
	}
}
