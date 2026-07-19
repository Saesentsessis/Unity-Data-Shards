using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Persistence.Layout
{
	/// <summary>
	/// Managed counterpart of <see cref="SaveLayoutResult"/>. Arrays may be rented
	/// from <see cref="ArrayPool{T}"/> (they can be longer than the logical counts);
	/// <see cref="Dispose"/> returns pooled arrays when <c>pooled</c> was set.
	/// </summary>
	public struct ManagedSaveLayoutResult : IDisposable
	{
		public SaveEnvelope Envelope;
		public byte[] Payload;
		public int PayloadLength;
		public ShardBlobRange[] Ranges;
		public int RangeCount;

		private readonly bool _pooled;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public ManagedSaveLayoutResult(in SaveEnvelope envelope, byte[] payload, int payloadLength,
			ShardBlobRange[] ranges, int rangeCount, bool pooled)
		{
			Envelope = envelope;
			Payload = payload;
			PayloadLength = payloadLength;
			Ranges = ranges;
			RangeCount = rangeCount;
			_pooled = pooled;
		}

		public void Dispose()
		{
			if (!_pooled)
				return;

			if (Payload != null)
				ArrayPool<byte>.Shared.Return(Payload);

			if (Ranges != null)
				ArrayPool<ShardBlobRange>.Shared.Return(Ranges);

			Payload = null;
			Ranges = null;
		}
	}
}
