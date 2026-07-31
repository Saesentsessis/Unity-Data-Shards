using System;

namespace Saesentsessis.Persistence.Core
{
	/// <summary>
	/// Converts a tick count that came from outside the process into a <see cref="DateTime"/>
	/// without throwing.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <c>new DateTime(ticks)</c> throws <see cref="ArgumentOutOfRangeException"/> for anything
	/// outside <c>[0, DateTime.MaxValue.Ticks]</c>, and a save file's timestamp is an eight-byte
	/// field a hostile or damaged file can set to anything at all. The envelope checksum is no
	/// defence — it is unkeyed, so whoever edited the save simply recomputes it.
	/// </para>
	/// <para>
	/// Reading a header must never blow up a load-game screen over a display field, so a value that
	/// cannot be a date reads as "no timestamp" instead. The rest of the header is still meaningful.
	/// </para>
	/// </remarks>
	internal static class UtcTicks
	{
		/// <summary>True when <paramref name="ticks"/> is a value <see cref="DateTime"/> accepts.</summary>
		public static bool IsValid(long ticks) => ticks > 0 && ticks <= DateTime.MaxValue.Ticks;

		/// <summary>
		/// <paramref name="ticks"/> as a UTC <see cref="DateTime"/>, or <see cref="DateTime.MinValue"/>
		/// when it is zero (meaning "not reported") or out of range (meaning corrupt).
		/// </summary>
		public static DateTime ToDateTime(long ticks)
			=> IsValid(ticks) ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
	}
}
