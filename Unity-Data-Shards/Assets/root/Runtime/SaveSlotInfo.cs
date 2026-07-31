using System;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence
{
	/// <summary>
	/// A save slot as seen from a key listing alone — identity and footprint, with nothing read.
	/// </summary>
	/// <remarks>
	/// This is the cheap half of browsing. Every field here comes from
	/// <see cref="Core.IListableStorage"/>, so a list of slots can be built, sized and sorted
	/// without opening a single save. <see cref="SaveSlotHeader"/> is the expensive half.
	/// </remarks>
	public readonly struct SaveSlotInfo
	{
		/// <summary>Slot name, as <see cref="SaveManager"/> accepts it.</summary>
		public readonly string Slot;

		/// <summary>
		/// Bytes the slot occupies across every key it owns, as stored — so still compressed and
		/// encrypted if a transform chain is in play.
		/// </summary>
		public readonly long TotalBytes;

		/// <summary>Most recent write time across the slot's keys, in UTC ticks. <c>0</c> if unknown.</summary>
		public readonly long ModifiedUtcTicks;

		/// <summary>
		/// How many storage keys the slot occupies — 1 under a single-file layout, and
		/// 1 + shard count under a multi-file one.
		/// </summary>
		public readonly int KeyCount;

		public SaveSlotInfo(string slot, long totalBytes, long modifiedUtcTicks, int keyCount)
		{
			Slot = slot;
			TotalBytes = totalBytes;
			ModifiedUtcTicks = modifiedUtcTicks;
			KeyCount = keyCount;
		}

		/// <summary>True when at least one of the slot's keys reported a usable write time.</summary>
		public bool HasModifiedTime => UtcTicks.IsValid(ModifiedUtcTicks);

		/// <summary><see cref="ModifiedUtcTicks"/> as a <see cref="DateTime"/>, computed on demand.</summary>
		public DateTime ModifiedUtc => UtcTicks.ToDateTime(ModifiedUtcTicks);
	}

	/// <summary>Outcome of decoding a slot's envelope header.</summary>
	public enum SaveSlotStatus
	{
		/// <summary>Header decoded and its checksum verified.</summary>
		Ok = 0,

		/// <summary>No data under the slot key.</summary>
		Missing,

		/// <summary>Checksum failed, the data was truncated, or the counts were implausible.</summary>
		Corrupted,

		/// <summary>Not written by this package — the format marker is absent.</summary>
		Foreign,

		/// <summary>A Data Shards save, but from a format version this build cannot read.</summary>
		UnsupportedVersion,

		/// <summary>
		/// The bytes could not be obtained at all — an I/O failure, a permission error, a decryption
		/// chain that rejected the payload. Distinct from <see cref="Corrupted"/>, which means the
		/// bytes arrived and did not decode.
		/// </summary>
		Unreadable,
	}

	/// <summary>
	/// A slot's envelope header. The expensive half of browsing: obtaining one means reading the
	/// slot's bytes, and through a transform chain that means reversing the whole chain.
	/// </summary>
	/// <remarks>
	/// Deliberately separate from <see cref="SaveSlotInfo"/> so a browser can list first and decode
	/// only what the user actually looks at. Every field except <see cref="Status"/> is meaningless
	/// unless that reads <see cref="SaveSlotStatus.Ok"/>.
	/// </remarks>
	public readonly struct SaveSlotHeader
	{
		/// <summary>Why the read succeeded or failed. Check this before anything else.</summary>
		public readonly SaveSlotStatus Status;

		/// <summary>Envelope format version the save was written with.</summary>
		public readonly int FormatVersion;

		/// <summary>When the save was written, in UTC ticks, as recorded inside the envelope.</summary>
		public readonly long TimestampUtc;

		/// <summary>Distinct shard types in the save.</summary>
		public readonly int TypeCount;

		/// <summary>Shards in the save.</summary>
		public readonly int RecordCount;

		/// <summary>The envelope's xxHash3-64 checksum, as stored.</summary>
		public readonly ulong Checksum;

		public SaveSlotHeader(SaveSlotStatus status, int formatVersion = 0, long timestampUtc = 0,
			int typeCount = 0, int recordCount = 0, ulong checksum = 0)
		{
			Status = status;
			FormatVersion = formatVersion;
			TimestampUtc = timestampUtc;
			TypeCount = typeCount;
			RecordCount = recordCount;
			Checksum = checksum;
		}

		/// <summary>True when the envelope carried a timestamp that can be read as a date.</summary>
		/// <remarks>
		/// False for a save written before the field was stamped — and for one whose eight-byte
		/// timestamp holds something no date could, which a damaged or edited file is free to do.
		/// </remarks>
		public bool HasTimestamp => UtcTicks.IsValid(TimestampUtc);

		/// <summary>
		/// <see cref="TimestampUtc"/> as a <see cref="DateTime"/>, computed on demand and never
		/// throwing — see <see cref="HasTimestamp"/> for why that matters.
		/// </summary>
		public DateTime WrittenUtc => UtcTicks.ToDateTime(TimestampUtc);
	}
}
