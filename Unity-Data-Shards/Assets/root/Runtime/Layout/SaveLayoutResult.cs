using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections;

namespace Persistence.Layout
{
	/// <summary>
	/// Everything a layout read produces: the decoded envelope, one contiguous
	/// payload arena holding every shard's bytes, and the ranges indexing into it.
	/// Two native buffers total, regardless of shard count.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct SaveLayoutResult : IDisposable
	{
		public SaveEnvelope Envelope;
		public NativeArray<byte> Payload;
		public NativeArray<ShardBlobRange> Ranges;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public SaveLayoutResult(in SaveEnvelope envelope, NativeArray<byte> payload, NativeArray<ShardBlobRange> ranges)
		{
			Envelope = envelope;
			Payload = payload;
			Ranges = ranges;
		}

		public void Dispose()
		{
			if (Payload.IsCreated)
				Payload.Dispose();

			if (Ranges.IsCreated)
				Ranges.Dispose();
		}
	}
}
