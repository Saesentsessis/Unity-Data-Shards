using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Saesentsessis.Persistence.Core;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Saesentsessis.Persistence.Utils
{
	[BurstCompile]
	internal static partial class UnsafeStringUtils
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static unsafe void Write(string dst, string src, int offset = 0)
		{
			CheckSufficientCapacity(dst.Length - offset, src.Length);
			
			fixed (char* dstPtr = dst, srcPtr = src)
				UnsafeUtility.MemCpy(dstPtr + offset, srcPtr, (long)src.Length * sizeof(char));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static unsafe void Write(string dst, char src, int offset = 0)
		{
			CheckSufficientCapacity(dst.Length - offset, 1);
			
			fixed (char* dstPtr = dst)
				*(dstPtr + offset) = src;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static unsafe void Write(string dst, SerializableGuid guid, int offset = 0)
		{
			CheckSufficientCapacity(dst.Length - offset, 32);
			
			fixed (char* dstPtr = dst)
				WriteInternal((ushort*)dstPtr + offset, in guid);
		}
		
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static unsafe void Write(Span<char> dst, SerializableGuid guid, int offset)
		{
			fixed (char* dstPtr = dst)
				WriteInternal((ushort*)dstPtr + offset, guid);
		}
		
		[BurstCompile(DisableSafetyChecks = true)]
		private static unsafe void WriteInternal(ushort* dst, in SerializableGuid src)
		{
			// The 'in' modifier passes the struct by readonly reference.
			// Pinning it with 'fixed' allows us to safely extract a raw byte pointer.
			fixed (SerializableGuid* srcPtr = &src)
			{
				var dataPtr = (byte*)srcPtr;
       
				for (var i = 0; i < 16; i++)
				{
					var b = *dataPtr++;

					// Keep variables strictly as 8-bit to prevent dword promotion
					var high = (byte)(b >> 4);
					var low = (byte)(b & 0xF);
					
					// LLVM lowers these ternaries into SIMD blends/masked operations.
					// 48 is '0', 87 is 'a' - 10
					*dst++ = (ushort)(87 + high + (((high - 10) >> 31) & -39));
					*dst++ = (ushort)(87 + low + (((low - 10) >> 31) & -39));
				}
			}
		}

		[Conditional("ENABLE_PERSISTENCE_INTEGRITY_CHECKS")]
		private static void CheckSufficientCapacity(int capacity, int length)
		{
			if (capacity < length)
				throw new InvalidOperationException($"Length {length} exceeds Capacity {capacity}.");
		}
	}
}