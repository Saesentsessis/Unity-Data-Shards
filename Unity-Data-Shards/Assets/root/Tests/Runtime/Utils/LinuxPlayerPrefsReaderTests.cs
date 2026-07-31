#if UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
using System;
using System.IO;
using NUnit.Framework;
using Saesentsessis.Persistence.Utils;
using UnityEngine;

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>
	/// The Linux reader against a real Unity preferences file.
	/// </summary>
	/// <remarks>
	/// The XML <i>format</i> is covered by <c>PrefsParserTests</c>, which runs everywhere because
	/// <see cref="UnityPrefsXmlReader"/> is deliberately not platform-gated. What is left here is
	/// the part that needs a Linux box: that <c>~/.config/unity3d/&lt;company&gt;/&lt;product&gt;</c>
	/// — or the <c>XDG_CONFIG_HOME</c> equivalent — is where Unity actually put the file.
	/// </remarks>
	public class LinuxPlayerPrefsReaderTests
	{
		private const string Postfix = ".udsreadertest";

		[Test]
		public void EnumeratesAKeyItJustWrote()
		{
			PlayerPrefsRoundTrip.Run(new LinuxPlayerPrefsReader(), Postfix);
		}

		[Test]
		public void ResolvesToAPrefsFileThatExists()
		{
			// Separated from the round trip so a failure distinguishes a wrong path from a wrong
			// parse. Honours XDG_CONFIG_HOME the same way the reader does, so a relocated config
			// directory is not reported as a bug in the reader.
			PlayerPrefs.SetString("uds-probe" + Postfix, "eA==");
			PlayerPrefs.Save();

			try
			{
				var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

				if (string.IsNullOrEmpty(config))
					config = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? string.Empty, ".config");

				var path = Path.Combine(config, "unity3d",
					Application.companyName, Application.productName, "prefs");

				Assert.IsTrue(File.Exists(path),
					$"No prefs file at '{path}' after PlayerPrefs wrote a key. Unity's layout has changed and " +
					"LinuxPlayerPrefsReader's path rule needs updating.");
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
			Assert.IsInstanceOf<LinuxPlayerPrefsReader>(PlayerPrefsUtils.Shared);
		}
	}
}
#endif
