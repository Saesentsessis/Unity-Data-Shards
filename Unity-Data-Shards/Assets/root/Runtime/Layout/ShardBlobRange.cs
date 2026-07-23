using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Layout
{
	/// <summary>
	/// Locates one shard's serialized bytes inside the contiguous payload arena:
	/// <c>payload[Offset .. Offset + Length]</c>. Fully blittable — an array of
	/// these plus the arena replaces per-shard buffers, so a save performs two
	/// native allocations total instead of one per shard.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct ShardBlobRange
	{
		public SerializableGuid Id;
		public int Offset;
		public int Length;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public ShardBlobRange(SerializableGuid id, int offset, int length)
		{
			Id = id;
			Offset = offset;
			Length = length;
		}
	}
}
