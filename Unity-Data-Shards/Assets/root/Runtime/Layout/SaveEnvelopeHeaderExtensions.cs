using System;
using System.Runtime.InteropServices;
using Saesentsessis.Persistence.Core;
using Unity.Collections.LowLevel.Unsafe;

namespace Saesentsessis.Persistence.Layout
{
	internal static class SaveEnvelopeHeaderExtensions
	{
		/// <summary>
		/// Reads the fixed-size header off the front of <paramref name="data"/> and returns its
		/// size, i.e. the offset at which the type table begins. Reading is unaligned-safe.
		/// </summary>
		public static int ParseEnvelopeHeader(this ReadOnlySpan<byte> data, out SaveEnvelopeHeader result)
		{
			var size = UnsafeUtility.SizeOf<SaveEnvelopeHeader>();

			if (data.Length < size)
				throw new SaveCorruptedException(
					$"Buffer of {data.Length} bytes is too short for a {size}-byte envelope header.",
					SaveCorruptedExceptionReason.EnvelopeTruncated);

			result = MemoryMarshal.Read<SaveEnvelopeHeader>(data[..size]);
			return size;
		}
	}
}
