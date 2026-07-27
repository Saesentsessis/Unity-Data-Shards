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
	public struct StorageReadResult : IDisposable
	{
		public readonly bool Found;
		public NativeArray<byte> Data;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public StorageReadResult(NativeArray<byte> data)
		{
			Found = true;
			Data = data;
		}

		public static StorageReadResult NotFound => default;

		public void Dispose()
		{
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
