#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using NUnit.Framework;
using Saesentsessis.Persistence.Utils;

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>
	/// Registry value name → storage key. This is the step that silently drops saves when it is
	/// wrong: a key that fails to parse simply never appears in the browser.
	/// </summary>
	public class WindowsPlayerPrefsKeyTests
	{
		private const string Postfix = ".shard";

		private static string Extract(string valueName, string postfix = Postfix)
			=> WindowsPlayerPrefsReader.TryExtractKey(valueName, postfix, out var key) ? key.ToString() : null;

		[Test]
		public void StripsUnityHashAndPostfix()
		{
			Assert.AreEqual("save1", Extract("save1.shard_h3837386411"));
		}

		[Test]
		public void KeyContainingUnderscoreH_TrimsOnlyUnitysSuffix()
		{
			// Unity always appends its hash last, so the LAST "_h" is always the one to cut — a key
			// the game itself named "my_hero" must survive intact.
			Assert.AreEqual("my_hero", Extract("my_hero.shard_h12"));
			Assert.AreEqual("save_h1", Extract("save_h1.shard_h999"));
		}

		[Test]
		public void RejectsNamesThatAreNotPlayerPrefsValues()
		{
			Assert.IsNull(Extract("save1.shard"), "No hash suffix at all.");
			Assert.IsNull(Extract("save1.shard_h"), "Hash marker with no digits.");
			Assert.IsNull(Extract("save1.shard_habc"), "Suffix must be digits.");
		}

		[Test]
		public void RejectsKeysBelongingToAnotherStorage()
		{
			Assert.IsNull(Extract("unity.player_session_count_h1234"), "No postfix.");
			Assert.IsNull(Extract("save1.other_h1234"), "Different postfix.");
		}

		[Test]
		public void RejectsAnEmptyKey()
		{
			// ".shard_h1" would strip to nothing; PlayerPrefsStorage never writes an empty key.
			Assert.IsNull(Extract(".shard_h1"));
		}

		[Test]
		public void EmptyPostfixStillRequiresTheHash()
		{
			Assert.AreEqual("anything", Extract("anything_h5", string.Empty));
			Assert.IsNull(Extract("anything", string.Empty));
		}
	}
}
#endif
