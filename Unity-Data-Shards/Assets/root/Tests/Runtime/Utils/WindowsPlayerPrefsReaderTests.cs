#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using NUnit.Framework;
using Saesentsessis.Persistence.Utils;

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>
	/// End-to-end against the real registry: write through PlayerPrefs, then read the key back out
	/// through the reader.
	/// </summary>
	/// <remarks>
	/// This is the test that covers what <c>WindowsPlayerPrefsKeyTests</c> cannot — that the
	/// registry branch actually resolved is the one PlayerPrefs writes to. The editor and player
	/// branches differ (<c>Software\Unity\UnityEditor\…</c> versus <c>Software\…</c>), and picking
	/// the wrong one fails as "no saves exist", which looks exactly like the truth.
	/// </remarks>
	public class WindowsPlayerPrefsRoundTripTests
	{
		private const string Postfix = ".udsreadertest";

		[Test]
		public void EnumeratesAKeyItJustWrote()
		{
			PlayerPrefsRoundTrip.Run(new WindowsPlayerPrefsReader(), Postfix);
		}
	}
}
#endif
