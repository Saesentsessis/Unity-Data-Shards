#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
using System;
using System.IO;
using NUnit.Framework;
using Saesentsessis.Persistence.Utils;
using UnityEngine;

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>
	/// The macOS reader against a real <c>NSUserDefaults</c> store.
	/// </summary>
	/// <remarks>
	/// The plist <i>format</i> is covered by <c>PrefsParserTests</c>, which runs everywhere because
	/// <see cref="PlistKeyReader"/> is deliberately not platform-gated. What is left for this file
	/// is the half that can only be answered on a Mac: whether the file the reader opens is the one
	/// Unity writes. The name is probed across three candidates precisely because published sources
	/// disagree on it, so the round trip below is the only evidence the probe lands.
	/// </remarks>
	public class MacPlayerPrefsReaderTests
	{
		private const string Postfix = ".udsreadertest";

		[Test]
		public void EnumeratesAKeyItJustWrote()
		{
			PlayerPrefsRoundTrip.Run(new MacPlayerPrefsReader(), Postfix);
		}

		[Test]
		public void ResolvesToAPreferencesFileThatExists()
		{
			// Named separately from the round trip so a failure says *which* half broke: a missing
			// plist means the path rule is wrong, whereas a present plist that yields no keys means
			// the parser or the filter is.
			PlayerPrefs.SetString("uds-probe" + Postfix, "eA==");
			PlayerPrefs.Save();

			try
			{
				var directory = Path.Combine(
					Environment.GetEnvironmentVariable("HOME") ?? string.Empty, "Library", "Preferences");

				Assert.IsTrue(Directory.Exists(directory), $"No preferences directory at '{directory}'.");

				var candidates = new[]
				{
					Path.Combine(directory, $"unity.{Application.companyName}.{Application.productName}.plist"),
					Path.Combine(directory, $"com.{Application.companyName}.{Application.productName}.plist"),
					Path.Combine(directory, $"{Application.identifier}.plist")
				};

				var matched = Array.FindIndex(candidates, File.Exists);

				Assert.GreaterOrEqual(matched, 0,
					"None of the probed plist names exist after PlayerPrefs wrote a key. Unity's naming has " +
					"changed and MacPlayerPrefsReader's candidate list needs the new shape:\n  " +
					string.Join("\n  ", candidates));

				// Not an assertion — a note in the log, so the name that actually works on this
				// version is recorded rather than rediscovered.
				Debug.Log($"[MacPlayerPrefsReaderTests] PlayerPrefs resolved to '{candidates[matched]}'.");
			}
			finally
			{
				PlayerPrefs.DeleteKey("uds-probe" + Postfix);
				PlayerPrefs.Save();
			}
		}

		[Test]
		public void IsTheReaderThisPlatformResolvesTo()
		{
			Assert.IsInstanceOf<MacPlayerPrefsReader>(PlayerPrefsUtils.Shared);
		}
	}
}
#endif
