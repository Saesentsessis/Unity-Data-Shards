using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Saesentsessis.Persistence.Layout
{
	/// <summary>
	/// Managed counterpart of <see cref="SaveLayoutResult"/>. Arrays may be rented
	/// from <see cref="ArrayPool{T}"/> (they can be longer than the logical counts);
	/// <see cref="Dispose"/> returns pooled arrays when <c>pooled</c> was set.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct ManagedSaveLayoutResult : IDisposable
	{
		public SaveEnvelope Envelope;
		public byte[] Payload;
		public ShardBlobRange[] Ranges;
		public int PayloadLength;
		private uint _rangeCount;
		
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public ManagedSaveLayoutResult(in SaveEnvelope envelope, byte[] payload, int payloadLength,
			ShardBlobRange[] ranges, int rangeCount, bool pooled)
		{
			Envelope = envelope;
			Payload = payload;
			PayloadLength = payloadLength;
			Ranges = ranges;
			_rangeCount = (uint)rangeCount;
			
			if (pooled)
				_rangeCount |= 0x80000000;
			else
				_rangeCount &= 0x7FFFFFFF;
		}
		
		public int RangeCount
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get => (int)_rangeCount & 0x7FFFFFFF;
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			set
			{
				var pooled = IsPooled;
				_rangeCount = (uint)value;
				
				if (pooled)
					_rangeCount |= 0x80000000;
				else
					_rangeCount &= 0x7FFFFFFF;
			}
		}

		private bool IsPooled => _rangeCount >> 31 > 0;

		public void Dispose()
		{
			if (!IsPooled)
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
