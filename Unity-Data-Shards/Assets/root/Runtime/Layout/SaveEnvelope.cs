using System;
using System.Runtime.InteropServices;

namespace Persistence.Layout
{
	/// <summary>
	/// Save metadata: format version, timestamp, integrity checksum, the deduplicated
	/// type table and one record per shard. <see cref="Types"/>/<see cref="Records"/>
	/// may be rented from ArrayPool and longer than the logical counts — always index
	/// through <see cref="TypeCount"/>/<see cref="RecordCount"/>, never <c>.Length</c>.
	/// </summary>
	[Serializable]
	[StructLayout(LayoutKind.Sequential)]
	public struct SaveEnvelope
	{
		public const int CurrentFormatVersion = 3;

		public int FormatVersion;
		public long TimestampUtc;

		/// <summary>
		/// xxHash3 over the encoded envelope body and shard payload. Computed and
		/// verified by the layout layer; zero while the envelope is in memory.
		/// </summary>
		public ulong Checksum;

		public SerializedType[] Types;
		public int TypeCount;

		public ShardRecord[] Records;
		public int RecordCount;
	}
}
