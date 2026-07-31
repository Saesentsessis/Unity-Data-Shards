using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Saesentsessis.Persistence.Utils;

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>
	/// The Unity preferences XML parser behind the Linux reader. Ungated for the same reason as
	/// <see cref="PlistKeyReaderTests"/>: the format has no Linux dependency, so gating it would put
	/// it beyond reach of anyone not building on Linux.
	/// </summary>
	public class UnityPrefsXmlReaderTests
	{
		private const string Postfix = ".shard";

		private static List<string> Keys(ReadOnlySpan<byte> file, string postfix = Postfix)
		{
			var buffer = new PrefsKeyBuffer();
			buffer.Reset(Array.Empty<string>());

			UnityPrefsXmlReader.Read(file, postfix, ref buffer);

			var result = new List<string>();

			foreach (var key in buffer.Keys)
				result.Add(key);

			return result;
		}

		[Test]
		public void UnityPrefs_ReadsMatchingKeysOnly()
		{
			var file = Encoding.UTF8.GetBytes(
				"<unity_prefs version_major=\"1\" version_minor=\"1\">\n" +
				"<pref name=\"Screenmanager Resolution Width\" type=\"int\">1920</pref>\n" +
				"<pref name=\"save1.shard\" type=\"string\">QUJD</pref>\n" +
				"<pref name=\"save2.shard\" type=\"string\">REVG</pref>\n" +
				"</unity_prefs>");

			CollectionAssert.AreEqual(new[] { "save1", "save2" }, Keys(file));
		}

		[Test]
		public void UnityPrefs_UnescapesEntitiesInNames()
		{
			var file = Encoding.UTF8.GetBytes(
				"<unity_prefs>" +
				"<pref name=\"a&amp;b.shard\" type=\"string\">x</pref>" +
				"<pref name=\"&lt;tag&gt;.shard\" type=\"string\">y</pref>" +
				"<pref name=\"say&quot;hi&quot;.shard\" type=\"string\">z</pref>" +
				"</unity_prefs>");

			CollectionAssert.AreEqual(new[] { "a&b", "<tag>", "say\"hi\"" }, Keys(file));
		}

		[Test]
		public void UnityPrefs_NonAsciiKeyIsScannedByteWise()
		{
			// The scanner works on raw UTF-8, so a multi-byte name is the case that would break if
			// a marker search ever matched inside a character. It cannot — no byte of a multi-byte
			// sequence is below 0x80 — and this pins that.
			var file = Encoding.UTF8.GetBytes(
				"<unity_prefs><pref name=\"sävé.shard\" type=\"string\">x</pref>" +
				"<pref name=\"日本語.shard\" type=\"string\">y</pref></unity_prefs>");

			CollectionAssert.AreEqual(new[] { "sävé", "日本語" }, Keys(file));
		}

		[Test]
		public void UnityPrefs_ValueContainingTheMarkerIsNotMistakenForAKey()
		{
			// A base64 payload cannot contain '<', but an escaped value can spell the marker out.
			// Escaped, it is not the marker, and must not be read as one.
			var file = Encoding.UTF8.GetBytes(
				"<unity_prefs><pref name=\"real.shard\" type=\"string\">&lt;pref name=&quot;fake.shard&quot;</pref>" +
				"</unity_prefs>");

			CollectionAssert.AreEqual(new[] { "real" }, Keys(file));
		}

		[Test]
		public void UnityPrefs_TruncatedFileKeepsWhatItRead()
		{
			var file = Encoding.UTF8.GetBytes(
				"<unity_prefs><pref name=\"save1.shard\" type=\"string\">x</pref><pref name=\"save2");

			CollectionAssert.AreEqual(new[] { "save1" }, Keys(file));
		}

		[Test]
		public void UnityPrefs_EmptyFileYieldsNothing()
		{
			Assert.IsEmpty(Keys(Array.Empty<byte>()));
		}
	}
}
