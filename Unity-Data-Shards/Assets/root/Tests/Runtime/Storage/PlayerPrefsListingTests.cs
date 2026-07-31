using System;
using System.Collections.Generic;
using NUnit.Framework;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Storage;

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>
	/// <see cref="PlayerPrefsStorage"/>'s own listing behaviour — the decisions it makes before any
	/// platform reader is involved, so these hold everywhere.
	/// </summary>
	/// <remarks>
	/// Per-platform reader tests live beside this file, each under its own define:
	/// <c>WindowsPlayerPrefsReaderTests</c>, <c>MacPlayerPrefsReaderTests</c>,
	/// <c>LinuxPlayerPrefsReaderTests</c>. The prefs <i>formats</i> are covered by
	/// <c>PrefsParserTests</c>, which is deliberately ungated — the parsers have no platform
	/// dependency, and gating them would make them untestable by anyone not on that platform.
	/// </remarks>
	public class PlayerPrefsListingTests
	{
		[Test]
		public void ListingWithoutAPostfix_IsRejected()
		{
			// Without a postfix every unrelated pref would come back as a save slot, so this fails
			// loudly rather than handing a load-game screen Unity's own bookkeeping entries.
			using var storage = new PlayerPrefsStorage();

			Assert.Throws<InvalidOperationException>(
				() => storage.PopulateAsync(new List<StorageKeyInfo>()));
		}

		[Test]
		public void SlotMapping_IsIdentity()
		{
			using var storage = new PlayerPrefsStorage(".shard");

			Assert.IsTrue(storage.TryGetSlot("save1".AsSpan(), out var slot));
			Assert.AreEqual("save1", slot.ToString());
			Assert.AreEqual("save1".Length, slot.Length, "Length equality is what marks a key as its slot's envelope.");

			Assert.IsFalse(storage.TryGetSlot(ReadOnlySpan<char>.Empty, out _));
		}
	}
}
