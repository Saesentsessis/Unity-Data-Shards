using System;

namespace Saesentsessis.Persistence.Utils
{
	/// <summary>
	/// Platform-specific enumeration of PlayerPrefs key names. Unity exposes no API for this, so
	/// each implementation reads the store Unity itself writes to.
	/// </summary>
	/// <remarks>
	/// Enumeration only. Values still go through <c>PlayerPrefs</c>, which stays the source of
	/// truth — a reader that also read values would be reimplementing Unity's storage format
	/// rather than just its directory.
	/// </remarks>
	internal interface IPlayerPrefsReader : IDisposable
	{
		/// <summary>
		/// Keys in the store whose name ends with <paramref name="postfix"/>, returned with the
		/// postfix removed.
		/// </summary>
		/// <param name="postfix">
		/// Suffix identifying this storage's keys, matched ordinally. Empty matches everything the
		/// store holds — for PlayerPrefs that includes Unity's own entries and every unrelated
		/// setting the game has written.
		/// </param>
		/// <returns>
		/// A slice of a buffer the reader reuses across calls, so it is valid only until the next
		/// call and must never be stored. Empty when the store does not exist yet.
		/// </returns>
		/// <remarks>
		/// The filter is a parameter rather than something the caller applies afterwards for one
		/// reason: it lets an implementation test a candidate <i>before</i> materializing it as a
		/// string. On Windows that means a prefs store of a thousand unrelated settings costs no
		/// managed allocation beyond the handful of keys that actually match.
		/// </remarks>
		ReadOnlySpan<string> EnumerateKeys(string postfix);
	}
}
