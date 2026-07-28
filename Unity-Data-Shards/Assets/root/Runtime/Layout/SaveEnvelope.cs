using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Saesentsessis.Persistence.Layout
{
	/// <summary>
	/// Fixed 32-byte envelope header, written to disk verbatim — this layout IS the wire format.
	/// <code>
	/// [Checksum:8][FormatVersion:4][Magic:4][TimestampUtc:8][TypeCount:4][RecordCount:4]
	/// </code>
	/// The checksum occupies the first 8 bytes and is therefore the only field outside the hashed
	/// region; everything after it — version and magic included — is covered, so a flipped version
	/// bit is caught rather than silently selecting a different decoder. Every field also lands on
	/// its natural alignment, so a decoder reading an aligned buffer needs no unaligned loads.
	/// </summary>
	[Serializable]
	[StructLayout(LayoutKind.Sequential, Size = HeaderSize)]
	internal struct SaveEnvelopeHeader
	{
		/// <summary>Wire size in bytes. Asserted against the runtime's own layout by the codec tests.</summary>
		internal const int HeaderSize = 32;

		/// <summary>
		/// File-format marker, spelling <c>SHRD</c> in a little-endian hex dump. Validated before
		/// the version, so a file that is not ours at all is rejected as such instead of being
		/// reported as an unsupported version.
		/// </summary>
		internal const uint ExpectedMagic = 0x44524853;

		/// <summary>
		/// xxHash3-64 over everything that follows this field, including any payload the layout
		/// appends. Patched in by the layout once the full buffer is assembled; zero in memory.
		/// </summary>
		public ulong Checksum;

		public int FormatVersion;

		/// <summary><see cref="ExpectedMagic"/>. Inside the hashed region, so corruption is caught twice.</summary>
		public uint Magic;

		public long TimestampUtc;
		public int TypeCount;
		public int RecordCount;

		/// <summary>
		/// Builds a header for a new save. <see cref="TimestampUtc"/> is deliberately left at zero —
		/// the pipeline stamps it at the moment of writing, which is the only correct time given
		/// envelopes are cached and reused across saves.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static SaveEnvelopeHeader Create(int typeCount, int recordCount)
		{
			SaveEnvelopeHeader header = default;

			header.Checksum = 0UL;
			header.FormatVersion = SaveEnvelope.CurrentFormatVersion;
			header.Magic = ExpectedMagic;
			header.TypeCount = typeCount;
			header.RecordCount = recordCount;

			return header;
		}
	}

	/// <summary>
	/// Save metadata: header, the deduplicated type table and one record per shard.
	/// <see cref="TypesArray"/>/<see cref="RecordsArray"/> may be rented from ArrayPool and longer
	/// than the logical counts, so <see cref="Types"/>/<see cref="Records"/> always slice to
	/// <see cref="TypeCount"/>/<see cref="RecordCount"/> — never expose the pooled tail.
	/// </summary>
	[Serializable]
	[StructLayout(LayoutKind.Sequential)]
	public struct SaveEnvelope
	{
		public const int CurrentFormatVersion = 4;

		#region Static Methods

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static SaveEnvelope Create(SaveEnvelopeHeader header, SerializedType[] types, ShardRecord[] records)
		{
			SaveEnvelope result = default;

			result.header = header;
			result.types = types;
			result.records = records;

			return result;
		}

		/// <summary>
		/// Builds an envelope over caller-owned (possibly pooled) arrays. The arrays may be longer
		/// than the counts; everything downstream indexes through the counts.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static SaveEnvelope Create(int typeCount, SerializedType[] types, int recordCount, ShardRecord[] records)
		{
			CheckCounts(typeCount, types, recordCount, records);

			SaveEnvelope result = default;

			result.header = SaveEnvelopeHeader.Create(typeCount, recordCount);
			result.types = types;
			result.records = records;

			return result;
		}

		/// <summary>
		/// A count larger than its backing array would publish adjacent pool memory into the save
		/// file, so this guards our own data — a caller bug, not corruption. Gated accordingly.
		/// </summary>
		[Conditional("ENABLE_PERSISTENCE_INTEGRITY_CHECKS")]
		private static void CheckCounts(int typeCount, SerializedType[] types, int recordCount, ShardRecord[] records)
		{
			if (typeCount < 0)
				throw new ArgumentOutOfRangeException(nameof(typeCount), typeCount, "Type count cannot be negative.");

			if (recordCount < 0)
				throw new ArgumentOutOfRangeException(nameof(recordCount), recordCount, "Record count cannot be negative.");

			if (typeCount > (types?.Length ?? 0))
				throw new ArgumentException(
					$"Type count {typeCount} exceeds the backing array ({types?.Length ?? 0}).", nameof(typeCount));

			if (recordCount > (records?.Length ?? 0))
				throw new ArgumentException(
					$"Record count {recordCount} exceeds the backing array ({records?.Length ?? 0}).", nameof(recordCount));
		}

		#endregion

		private SaveEnvelopeHeader header;
		private SerializedType[] types;
		private ShardRecord[] records;

		public ReadOnlySpan<SerializedType> Types => types.AsSpan(0, TypeCount);
		public ReadOnlySpan<ShardRecord> Records => records.AsSpan(0, RecordCount);

		internal readonly SaveEnvelopeHeader Header
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get => header;
		}

		internal SerializedType[] TypesArray => types;
		internal ShardRecord[] RecordsArray => records;

		public readonly ulong Checksum
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get => header.Checksum;
		}

		public readonly int FormatVersion
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get => header.FormatVersion;
		}

		public long TimestampUtc
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			readonly get => header.TimestampUtc;
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			set => header.TimestampUtc = value;
		}

		public int TypeCount
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			readonly get => header.TypeCount;
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			set => header.TypeCount = value;
		}

		public int RecordCount
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			readonly get => header.RecordCount;
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			set => header.RecordCount = value;
		}
	}
}
