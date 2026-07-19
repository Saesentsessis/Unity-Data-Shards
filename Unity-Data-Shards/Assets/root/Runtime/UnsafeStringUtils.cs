using System.Runtime.CompilerServices;
using Persistence.Core;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Persistence
{
	[BurstCompile]
	public static partial class UnsafeStringUtils
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static unsafe void Write(string dst, string src, int offset = 0)
		{
			fixed (char* dstPtr = dst)
			fixed (char* srcPtr = src)
				UnsafeUtility.MemCpy(dstPtr + offset, srcPtr, (long)src.Length * sizeof(char));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static unsafe void Write(string dst, char src, int offset = 0)
		{
			fixed (char* dstPtr = dst)
				*(dstPtr + offset) = src;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static unsafe void Write(string dst, SerializableGuid guid, int offset = 0)
		{
			fixed (char* dstPtr = dst)
				WriteInternal((ushort*)dstPtr + offset, in guid);
		}
		
		[BurstCompile(DisableSafetyChecks = true)]
		public static unsafe void WriteInternal(ushort* dst, in SerializableGuid src)
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
	}
}