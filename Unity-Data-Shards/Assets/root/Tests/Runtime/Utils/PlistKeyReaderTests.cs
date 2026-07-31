using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Saesentsessis.Persistence.Utils;

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>
	/// The Apple property-list parser behind the macOS reader, exercised on whatever machine is
	/// running the tests. The format is pure bytes with no Apple dependency, which is exactly why
	/// <see cref="PlistKeyReader"/> lives outside the <c>UNITY_STANDALONE_OSX</c> gate — see
	/// <c>MacPlayerPrefsReaderTests</c> for the half that does need a Mac.
	/// </summary>
	public class PlistKeyReaderTests
	{
		private const string Postfix = ".shard";

		private static List<string> Keys(ReadOnlySpan<byte> file, string postfix = Postfix)
		{
			var buffer = new PrefsKeyBuffer();
			buffer.Reset(Array.Empty<string>());

			PlistKeyReader.Read(file, postfix, ref buffer);

			var result = new List<string>();

			foreach (var key in buffer.Keys)
				result.Add(key);

			return result;
		}
		/// <summary>
		/// Builds a real <c>bplist00</c> dictionary of ASCII string keys mapped to empty-string
		/// values. Hand-assembled rather than mocked: the point of the test is the byte layout.
		/// </summary>
		private static byte[] BuildBinaryPlist(params string[] keys)
		{
			var bytes = new List<byte>();
			bytes.AddRange(Encoding.ASCII.GetBytes("bplist00"));

			var offsets = new List<long>();

			// Object 0: the root dictionary. Keys are objects 1..n, values all point at object n+1
			// (a single shared empty string), which is legal and keeps the file small.
			offsets.Add(bytes.Count);
			bytes.Add((byte)(0xD0 | keys.Length));

			for (var i = 0; i < keys.Length; i++)
				bytes.Add((byte)(i + 1));

			for (var i = 0; i < keys.Length; i++)
				bytes.Add((byte)(keys.Length + 1));

			foreach (var key in keys)
			{
				offsets.Add(bytes.Count);

				// 0x5n ASCII string. Every key here is short enough to avoid the 0x5F escape.
				Assert.Less(key.Length, 15, "Test keys must fit the short-form length nibble.");
				bytes.Add((byte)(0x50 | key.Length));
				bytes.AddRange(Encoding.ASCII.GetBytes(key));
			}

			// The shared empty-string value object.
			offsets.Add(bytes.Count);
			bytes.Add(0x50);

			var offsetTable = bytes.Count;

			foreach (var offset in offsets)
				bytes.Add((byte)offset);

			// Trailer: 6 unused/sortVersion bytes, offsetIntSize, objectRefSize, then three
			// 8-byte big-endian values.
			bytes.AddRange(new byte[6]);
			bytes.Add(1);
			bytes.Add(1);
			AppendBigEndian(bytes, offsets.Count);
			AppendBigEndian(bytes, 0);
			AppendBigEndian(bytes, offsetTable);

			return bytes.ToArray();
		}

		private static void AppendBigEndian(List<byte> bytes, long value)
		{
			for (var shift = 56; shift >= 0; shift -= 8)
				bytes.Add((byte)(value >> shift));
		}

		[Test]
		public void BinaryPlist_ReadsMatchingKeysOnly()
		{
			var file = BuildBinaryPlist("save1.shard", "AppleLanguages", "save2.shard", "volume");

			CollectionAssert.AreEquivalent(new[] { "save1", "save2" }, Keys(file));
		}

		[Test]
		public void BinaryPlist_Utf16KeyIsByteSwapped()
		{
			// A key outside ASCII forces the 0x6n branch, where every code unit is big-endian and
			// has to be swapped on the little-endian host this runs on.
			const string key = "sävé.shard";

			var bytes = new List<byte>();
			bytes.AddRange(Encoding.ASCII.GetBytes("bplist00"));

			var offsets = new List<long> { bytes.Count };
			bytes.Add(0xD1);
			bytes.Add(1);
			bytes.Add(2);

			offsets.Add(bytes.Count);
			bytes.Add((byte)(0x60 | key.Length));

			foreach (var c in key)
			{
				bytes.Add((byte)(c >> 8));
				bytes.Add((byte)(c & 0xFF));
			}

			offsets.Add(bytes.Count);
			bytes.Add(0x50);

			var offsetTable = bytes.Count;

			foreach (var offset in offsets)
				bytes.Add((byte)offset);

			bytes.AddRange(new byte[6]);
			bytes.Add(1);
			bytes.Add(1);
			AppendBigEndian(bytes, offsets.Count);
			AppendBigEndian(bytes, 0);
			AppendBigEndian(bytes, offsetTable);

			CollectionAssert.AreEqual(new[] { "sävé" }, Keys(bytes.ToArray()));
		}

		[Test]
		public void BinaryPlist_MalformedFileYieldsNothing()
		{
			// Every one of these used to be a way to walk off the end of the buffer.
			Assert.IsEmpty(Keys(Encoding.ASCII.GetBytes("bplist00")), "Header with no body.");
			Assert.IsEmpty(Keys(new byte[8]), "Too short for a trailer.");

			var truncated = BuildBinaryPlist("save1.shard");
			Assert.IsEmpty(Keys(truncated.AsSpan(0, truncated.Length - 10)), "Trailer cut off.");

			var corrupted = BuildBinaryPlist("save1.shard");
			corrupted[8] = 0xA1; // Root is an array, not a dictionary.
			Assert.IsEmpty(Keys(corrupted), "Root is not a dictionary.");
		}

		[Test]
		public void XmlPlist_IsAcceptedToo()
		{
			var xml = Encoding.UTF8.GetBytes(
				"<?xml version=\"1.0\"?><plist version=\"1.0\"><dict>" +
				"<key>save1.shard</key><string>abc</string>" +
				"<key>volume</key><real>0.5</real>" +
				"</dict></plist>");

			CollectionAssert.AreEqual(new[] { "save1" }, Keys(xml));
		}

		[Test]
		public void KeyThatIsOnlyThePostfix_IsRejected()
		{
			// Stripping would leave an empty storage key, which PlayerPrefsStorage never writes.
			Assert.IsEmpty(Keys(BuildBinaryPlist(".shard")));
		}
	}
}
