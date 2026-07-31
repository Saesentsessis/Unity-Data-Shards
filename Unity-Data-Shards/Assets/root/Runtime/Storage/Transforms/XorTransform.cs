using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Saesentsessis.Persistence.Core;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Saesentsessis.Persistence.Storage.Transforms
{
	/// <summary>
	/// Masks every byte with a repeating single-byte pattern.
	/// <para>
	/// <b>This is obfuscation, not encryption — it has no security value.</b> A single-byte XOR is
	/// recovered instantly from a known-plaintext guess, and save files are full of known plaintext
	/// (the <c>SHRD</c> magic sits at a fixed offset in every envelope). Its purpose is to stop a
	/// player from editing a save in a text editor, and to serve as the smallest possible worked
	/// example of the <see cref="IStorageTransform"/> contract. Reach for
	/// <see cref="AesCbcHmacTransform"/> when tamper *detection* matters.
	/// </para>
	/// <para>
	/// XOR is an involution, so <see cref="Apply"/> and <see cref="Reverse"/> are literally the same
	/// operation — which also makes this the cheapest transform to reason about: output length always
	/// equals input length, and no framing is added.
	/// </para>
	/// </summary>
	[BurstCompile]
	public class XorTransform : IStorageTransform, IDisposable
	{
		private NativeArray<byte> _pattern;
		
		/// <param name="pattern">Byte XORed into every position. Zero is a no-op mask.</param>
		public XorTransform(byte pattern) => ConstructPattern(pattern, out _pattern);
		
		/// <param name="pattern">Uint XORed into every 4 positions. Zero is a no-op mask.</param>
		public XorTransform(uint pattern) => ConstructPattern(pattern, out _pattern);
		
		public XorTransform(uint2 pattern) => ConstructPattern(pattern, out _pattern);
		
		public XorTransform(uint3 pattern) => ConstructPattern(pattern, out _pattern);
		
		public XorTransform(uint4 pattern) => ConstructPattern(pattern, out _pattern);
		
		/// <param name="pattern">Int XORed into every 4 positions. Zero is a no-op mask.</param>
		public XorTransform(int pattern) => ConstructPattern(pattern, out _pattern);
		
		public XorTransform(int2 pattern) => ConstructPattern(pattern, out _pattern);
		
		public XorTransform(int3 pattern) => ConstructPattern(pattern, out _pattern);
		
		public XorTransform(int4 pattern) => ConstructPattern(pattern, out _pattern);

		/// <param name="pattern">Long XORed into every 8 positions. Zero is a no-op mask.</param>
		public XorTransform(long pattern) => ConstructPattern(pattern, out _pattern);
		
		/// <param name="pattern">Ulong XORed into every 8 positions. Zero is a no-op mask.</param>
		public XorTransform(ulong pattern) => ConstructPattern(pattern, out _pattern);
		
		public XorTransform(v128 pattern) => ConstructPattern(pattern, out _pattern);
		
		public XorTransform(v256 pattern) => ConstructPattern(pattern, out _pattern);

		/// <param name="pattern">Byte array XORed into every N positions. Empty or zeroed-out span is a no-op mask.</param>
		public XorTransform(ReadOnlySpan<byte> pattern)
		{
			if (pattern.Length == 0 || IsZeroedOut(pattern))
				throw new ArgumentException("An array of bytes should is zero length or it's content zeroed out.", nameof(pattern));
			
			_pattern = new NativeArray<byte>(pattern.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			pattern.CopyTo(_pattern.AsSpan());
		}

		/// <inheritdoc />
		public void Apply(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
			=> Process(src, dst, _pattern);

		/// <inheritdoc />
		/// <remarks>Identical to <see cref="Apply"/>: XOR undoes itself.</remarks>
		public void Reverse(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
			=> Process(src, dst, _pattern);

		/// <summary>
		/// Pins both buffers and hands raw pointers to the Burst kernel — Burst cannot take spans, so
		/// the managed/native boundary is drawn here.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static unsafe void Process(ReadOnlySpan<byte> src, IBufferWriter<byte> dst, NativeArray<byte> pattern)
		{
			fixed (byte* srcPtr = src, dstPtr = dst.GetSpan(src.Length))
				ProcessInternal(srcPtr, dstPtr, src.Length, (byte*)pattern.GetUnsafeReadOnlyPtr(), pattern.Length);
			
			dst.Advance(src.Length);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static bool IsZeroedOut(ReadOnlySpan<byte> src)
		{
			byte result = 0;
			
			for (var i = 0; i < src.Length; i++)
				result |= src[i];

			return result == 0;
		}
		
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static unsafe void ConstructPattern<T>(T pattern, out NativeArray<byte> array) where T : unmanaged
		{
			var size = UnsafeUtility.SizeOf<T>();
			var dataPtr = (byte*)&pattern;
			
			var zeroesPtr = stackalloc byte[size];
			UnsafeUtility.MemClear(zeroesPtr, size);

			if (UnsafeUtility.MemCmp(zeroesPtr, dataPtr, size) == 0)
				throw new ArgumentException("Pattern content must not be zeroed out.", nameof(pattern));
			
			array = new NativeArray<byte>(size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
			
			var dstPtr = (byte*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(array);
			UnsafeUtility.MemCpy(dstPtr, dataPtr, size);
		}

		[BurstCompile(DisableSafetyChecks = true)]
		private static unsafe void ProcessInternal([NoAlias] byte* srcPtr, [NoAlias] byte* dstPtr, int length,
			byte* patternPtr, int patternLength)
		{
			if (patternLength <= 0 || length <= 0)
				return;
			
			switch (patternLength)
			{
				case 1:
					ProcessConstantLength(srcPtr, dstPtr, length, patternPtr, 1);
					return;
				case 2:
					ProcessConstantLength(srcPtr, dstPtr, length, patternPtr, 2);
					return;
				case 4:
					ProcessConstantLength(srcPtr, dstPtr, length, patternPtr, 4);
					return;
				case 8:
					ProcessConstantLength(srcPtr, dstPtr, length, patternPtr, 8);
					return;
				case 16:
					ProcessConstantLength(srcPtr, dstPtr, length, patternPtr, 16);
					return;
				case 32:
					ProcessConstantLength(srcPtr, dstPtr, length, patternPtr, 32);
					return;
			}

			// Fallback for arbitrary lengths
			var offset = 0;
			var fullPasses = length / patternLength;

			for (var i = 0; i < fullPasses; i++)
				for (var p = 0; p < patternLength; p++)
				{
					dstPtr[offset] = (byte)(srcPtr[offset] ^ patternPtr[p]);
					offset++;
				}

			for (var p = 0; offset < length; p++, offset++)
				dstPtr[offset] = (byte)(srcPtr[offset] ^ patternPtr[p]);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static unsafe void ProcessConstantLength([NoAlias] byte* srcPtr, [NoAlias] byte* dstPtr, int length, byte* patternPtr, int constantLength)
		{
			// Because constantLength is evaluated at compile time due to the explicit branches above,
			// we can use a bitwise AND mask instead of modulo.
			// This allows LLVM to process the entire stream in a single continuous loop,
			// which it will aggressively unroll into 128-byte/256-bit SIMD instructions.

			var i = -1;
			var mask = constantLength - 1;
			var endPtr = dstPtr + length;

			while (dstPtr < endPtr)
				*dstPtr++ = (byte)(*srcPtr++ ^ patternPtr[++i & mask]);
		}

		public void Dispose()
		{
			if (_pattern.IsCreated)
				_pattern.Dispose();
		}
	}
}