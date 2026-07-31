using NUnit.Framework;
using Saesentsessis.Persistence.Utils;
using UnityEngine;

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>
	/// The one assertion every platform reader must satisfy: a key written through PlayerPrefs is
	/// found again by the reader.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Shared because the claim is identical on every platform, and because the thing it proves —
	/// that the store the reader opened is the store PlayerPrefs writes to — is precisely what no
	/// unit test can establish. Every reader resolves a location from a rule that could be wrong:
	/// the Windows editor and player registry branches differ, the macOS plist name is probed
	/// across three candidates because sources disagree, and Linux depends on
	/// <c>XDG_CONFIG_HOME</c>. Each of those failures looks exactly like "there are no saves",
	/// which is why it has to be tested on the machine rather than reasoned about.
	/// </para>
	/// <para>
	/// Takes ownership of the reader and disposes it, and removes its own keys afterwards so a test
	/// run leaves no residue in the developer's real preferences store.
	/// </para>
	/// </remarks>
	internal static class PlayerPrefsRoundTrip
	{
		public static void Run(IPlayerPrefsReader reader, string postfix)
		{
			const string slot = "uds-roundtrip-slot";
			const string decoy = "uds-roundtrip-decoy";

			var stored = slot + postfix;

			PlayerPrefs.SetString(stored, "dGVzdA==");

			// Same prefix, no postfix: proves the filter is the postfix rather than a name guess.
			PlayerPrefs.SetString(decoy, "ignored");
			PlayerPrefs.Save();

			try
			{
				var keys = reader.EnumerateKeys(postfix);

				var found = false;
				var leaked = false;

				for (var i = 0; i < keys.Length; i++)
				{
					if (keys[i] == slot)
						found = true;

					if (keys[i] == decoy || keys[i] == stored)
						leaked = true;
				}

				Assert.IsTrue(found,
					$"{reader.GetType().Name} did not find '{stored}' after PlayerPrefs wrote it. The store it " +
					"opened is not the one PlayerPrefs uses on this platform.");

				Assert.IsFalse(leaked,
					"A key without the postfix was returned, or the postfix was not stripped.");

				// A postfix nothing uses must come back empty rather than falling back to everything.
				Assert.AreEqual(0, reader.EnumerateKeys(".uds-nothing-uses-this").Length);
			}
			finally
			{
				PlayerPrefs.DeleteKey(stored);
				PlayerPrefs.DeleteKey(decoy);
				PlayerPrefs.Save();

				reader.Dispose();
			}
		}
	}
}
