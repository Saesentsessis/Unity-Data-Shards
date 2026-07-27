using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Saesentsessis.Persistence.Layout
{
	/// <summary>
	/// Save metadata: format version, timestamp, integrity checksum, the deduplicated
	/// type table and one record per shard. <see cref="types"/>/<see cref="records"/>
	/// may be rented from ArrayPool and longer than the logical counts — always index
	/// through <see cref="TypeCount"/>/<see cref="RecordCount"/>, never <c>.Length</c>.
	/// </summary>
	[Serializable]
	[StructLayout(LayoutKind.Sequential)]
	public struct SaveEnvelope
	{
		public const int CurrentFormatVersion = 3;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static SaveEnvelope Create(int typeCount, SerializedType[] types, int recordCount, ShardRecord[] records)
		{
			SaveEnvelope result = default;

			result.FormatVersion = CurrentFormatVersion;
			result.TimestampUtc = DateTime.UtcNow.Ticks;
			result.types = types;
			result.TypeCount = typeCount;
			result.records = records;
			result.RecordCount = recordCount;

			return result;
		}
		
		public int FormatVersion;
		public long TimestampUtc;

		/// <summary>
		/// xxHash3 over the encoded envelope body and shard payload. Computed and
		/// verified by the layout layer; zero while the envelope is in memory.
		/// </summary>
		public ulong Checksum;

		private SerializedType[] types;
		public int TypeCount;

		private ShardRecord[] records;
		public int RecordCount;
		
		public ReadOnlySpan<SerializedType> Types => types;
		internal SerializedType[] TypesArray
		{
			readonly get => types;
			set => types = value;
		}

		public ReadOnlySpan<ShardRecord> Records => records;
		internal ShardRecord[] RecordsArray
		{
			readonly get => records;
			set => records = value;
		}
	}
}
