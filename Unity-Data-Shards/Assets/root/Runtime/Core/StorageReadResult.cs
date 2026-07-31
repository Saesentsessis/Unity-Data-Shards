using System;
using System.Runtime.CompilerServices;
using Unity.Collections;

namespace Saesentsessis.Persistence.Core
{
	/// <summary>
	/// Result of <see cref="IStorage.TryReadAsync"/>. <see cref="Found"/> is false when
	/// the key has no persisted data — no exception, no extra Exists round trip.
	/// When found, the caller owns <see cref="Data"/> and must dispose it.
	/// </summary>
	/// <remarks>
	/// <see cref="Found"/> is derived from the buffer rather than stored alongside it, so the two
	/// cannot disagree. A backend that reported a hit while handing back an uncreated
	/// <see cref="NativeArray{T}"/> — which is what an empty stored payload used to do — produced a
	/// result that threw the moment a caller honoured the ownership contract above.
	/// </remarks>
	public struct StorageReadResult : IDisposable
	{
		public NativeArray<byte> Data;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public StorageReadResult(NativeArray<byte> data)
		{
			Data = data;
		}

		public static StorageReadResult NotFound => default;

		public readonly bool Found => Data.IsCreated;

		/// <summary>Safe on a not-found result, so callers can dispose unconditionally.</summary>
		public void Dispose()
		{
			if (Data.IsCreated)
				Data.Dispose();
		}
	}

	/// <summary>
	/// Managed counterpart of <see cref="StorageReadResult"/> for <see cref="IManagedStorage.TryReadAsync"/>.
	/// </summary>
	public readonly struct ManagedStorageReadResult
	{
		public readonly bool Found;
		public readonly Memory<byte> Data;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public ManagedStorageReadResult(Memory<byte> data)
		{
			Found = true;
			Data = data;
		}

		public static ManagedStorageReadResult NotFound => default;
	}
}
