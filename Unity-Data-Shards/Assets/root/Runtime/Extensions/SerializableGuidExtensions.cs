using System;
using System.Runtime.CompilerServices;
using Persistence.Core;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Persistence
{
	public static class SerializableGuidExtensions
	{
		/// <summary>
		/// Deterministic way of generating Guid. The same string outputs the same Guid.
		/// </summary>
		/// <param name="key">Input string.</param>
		/// <returns>Deterministic Guid.</returns>
		public static SerializableGuid Compute(string key)
		{
			var hash = Hash128.Compute(key);
			
			return Unsafe.As<Hash128, SerializableGuid>(ref hash);
		}

		/// <summary>
		/// Deterministic way of generating Guid. The same span outputs the same Guid.
		/// </summary>
		/// <param name="span">Input span.</param>
		/// <returns>Deterministic Guid.</returns>
		public static unsafe SerializableGuid Compute(ReadOnlySpan<char> span)
		{
			Hash128 hash;

			fixed (char* charsPtr = span)
			{
				var array = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<char>(charsPtr, span.Length, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
				// Aliased arrays carry no safety handle; without one, any read throws
				// under collections checks (editor / development builds).
				NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref array, AtomicSafetyHandle.GetTempMemoryHandle());
#endif
				hash = Hash128.Compute(array);
			}

			return Unsafe.As<Hash128, SerializableGuid>(ref hash);
		}
	}
}