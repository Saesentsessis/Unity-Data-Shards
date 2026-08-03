using System;
using System.Collections;
using System.Threading;
using NUnit.Framework;
using Saesentsessis.Persistence.Buffers;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Storage;
using Saesentsessis.Persistence.Storage.Transforms;
using Unity.Collections;
using UnityEngine.TestTools;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using ByteArrayTask = Cysharp.Threading.Tasks.UniTask<byte[]>;
#else
using TaskType = System.Threading.Tasks.Task;
using ByteArrayTask = System.Threading.Tasks.Task<byte[]>;
#endif

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>
	/// What every <see cref="IStorageTransform"/> must do, regardless of what it actually does to the
	/// bytes, written once and run against each implementation.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The properties here are the ones <c>TransformStorage</c> and the pipeline rely on and that no
	/// individual transform's own tests tend to cover: that <see cref="IStorageTransform.Apply"/>
	/// appends to the writer rather than assuming it owns it, that an instance reused across calls
	/// carries no state between them, and that a payload larger than one buffer reservation still
	/// round-trips. A transform that fails any of these works fine in isolation and corrupts saves
	/// in a chain.
	/// </para>
	/// <para>
	/// Deriving fixtures supply a factory rather than an instance: several of these tests need two
	/// independent transforms, and <c>TransformStorage</c> takes ownership of whatever it is handed.
	/// </para>
	/// </remarks>
	public abstract class StorageTransformContractTests
	{
		protected const string Key = "transform-contract-slot";

		/// <summary>Names the implementation in assertion messages, since the failures are shared.</summary>
		protected abstract string TransformName { get; }

		/// <summary>A fresh instance every call — never a cached one, see the class remarks.</summary>
		protected abstract IStorageTransform CreateTransform();

		/// <summary>Deterministic pseudo-random bytes: tests must not depend on an RNG seed.</summary>
		protected static byte[] Payload(int length, int seed = 0)
		{
			var bytes = new byte[length];

			for (var i = 0; i < length; i++)
				bytes[i] = (byte)((i * 31 + seed) % 251);

			return bytes;
		}

		/// <summary>Bytes with no exploitable structure, so a compressor hits its expansion path.</summary>
		protected static byte[] IncompressiblePayload(int length, int seed = 1)
		{
			var bytes = new byte[length];
			var state = (uint)(seed * 2654435761u + 1);

			for (var i = 0; i < length; i++)
			{
				// xorshift32 — deterministic, and far less compressible than a modular ramp.
				state ^= state << 13;
				state ^= state >> 17;
				state ^= state << 5;
				bytes[i] = (byte)state;
			}

			return bytes;
		}

		protected byte[] RoundTrip(byte[] payload)
		{
			var transform = CreateTransform();

			try
			{
				using var applied = new PooledArrayBufferWriter();
				transform.Apply(payload, applied);

				using var reversed = new PooledArrayBufferWriter();
				transform.Reverse(applied.WrittenSpan, reversed);

				return reversed.WrittenSpan.ToArray();
			}
			finally
			{
				(transform as IDisposable)?.Dispose();
			}
		}

		private static async TaskType WriteAsync(IStorage storage, string key, byte[] bytes)
		{
			var data = new NativeArray<byte>(bytes, Allocator.Persistent);

			try
			{
				await storage.WriteAsync(key, data);
			}
			finally
			{
				data.Dispose();
			}
		}

		private static async ByteArrayTask ReadAsync(IStorage storage, string key)
		{
			var result = await storage.TryReadAsync(key, Allocator.Persistent);

			if (result.Found == false)
				return null;

			try
			{
				return result.Data.ToArray();
			}
			finally
			{
				result.Data.Dispose();
			}
		}

		#region Round trip

		[Test]
		public void Reverse_UndoesApply([Values(0, 1, 2, 15, 16, 17, 255, 1023, 4096)] int length)
		{
			// The odd sizes around block boundaries are where a padding or alignment mistake in a
			// block cipher or a compressor's tail handling shows up, and nowhere else.
			var payload = Payload(length, seed: length);

			CollectionAssert.AreEqual(payload, RoundTrip(payload),
				$"{TransformName}: a {length}-byte payload did not survive the round trip.");
		}

		[Test]
		public void EmptyInput_RoundTripsToEmpty()
		{
			// Empty is a real case: a save with no shards still writes an envelope through the chain.
			Assert.AreEqual(0, RoundTrip(Array.Empty<byte>()).Length,
				$"{TransformName}: empty input must reverse back to empty, not to garbage.");
		}

		[Test]
		public void IncompressibleData_RoundTrips()
		{
			// For a compressor this is the expansion path, where output is LARGER than input and the
			// worst-case reservation is what stops it overrunning.
			var payload = IncompressiblePayload(8192);

			CollectionAssert.AreEqual(payload, RoundTrip(payload),
				$"{TransformName}: incompressible data must still round-trip.");
		}

		[Test]
		public void PayloadLargerThanOneReservation_RoundTrips()
		{
			// Past TransformLimits.MaxReservation, so any implementation that reserves in one shot
			// has to loop or clamp instead.
			var payload = Payload(TransformLimits.MaxReservation + 4096, seed: 7);

			CollectionAssert.AreEqual(payload, RoundTrip(payload),
				$"{TransformName}: a payload larger than one reservation did not round-trip.");
		}

		#endregion

		#region Buffer contract

		[Test]
		public void Apply_AppendsToTheWriter_RatherThanResettingIt()
		{
			// TransformStorage chains transforms through shared ping-pong buffers, so a transform
			// that rewinds the writer or assumes it starts empty destroys whatever ran before it.
			var transform = CreateTransform();

			try
			{
				var prefix = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
				var payload = Payload(512, seed: 3);

				using var writer = new PooledArrayBufferWriter();

				prefix.CopyTo(writer.GetSpan(prefix.Length));
				writer.Advance(prefix.Length);

				transform.Apply(payload, writer);

				var written = writer.WrittenSpan.ToArray();

				Assert.Greater(written.Length, prefix.Length, $"{TransformName}: nothing was appended.");
				CollectionAssert.AreEqual(prefix, written[..prefix.Length],
					$"{TransformName}: the bytes already in the writer were overwritten.");

				using var reversed = new PooledArrayBufferWriter();
				transform.Reverse(new ReadOnlySpan<byte>(written, prefix.Length, written.Length - prefix.Length), reversed);

				CollectionAssert.AreEqual(payload, reversed.WrittenSpan.ToArray(),
					$"{TransformName}: the appended region must reverse on its own.");
			}
			finally
			{
				(transform as IDisposable)?.Dispose();
			}
		}

		[Test]
		public void ReusedInstance_LargeThenSmall_DoesNotBleedStaleBytes()
		{
			// One transform instance serves every key of every save. A scratch buffer that is sized
			// once and not re-cleared leaks the tail of the previous, larger payload into this one.
			var transform = CreateTransform();

			try
			{
				var large = Payload(8192, seed: 1);
				var small = Payload(24, seed: 2);

				foreach (var payload in new[] { large, small, large, small })
				{
					using var applied = new PooledArrayBufferWriter();
					transform.Apply(payload, applied);

					using var reversed = new PooledArrayBufferWriter();
					transform.Reverse(applied.WrittenSpan, reversed);

					CollectionAssert.AreEqual(payload, reversed.WrittenSpan.ToArray(),
						$"{TransformName}: a {payload.Length}-byte payload was corrupted by the previous call.");
				}
			}
			finally
			{
				(transform as IDisposable)?.Dispose();
			}
		}

		#endregion

		#region Through TransformStorage

		[UnityTest]
		public IEnumerator RoundTripsThroughTransformStorage() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner, CreateTransform());

			var payload = Payload(4096, seed: 11);
			await WriteAsync(storage, Key, payload);

			CollectionAssert.AreEqual(payload, await ReadAsync(storage, Key),
				$"{TransformName}: the round trip broke once driven by TransformStorage.");
		});

		[UnityTest]
		public IEnumerator RepeatedWritesThroughStorage_StayCorrect() => AsyncTest.Run(async () =>
		{
			// Storage reuses its ping-pong buffers across writes, so this is the decorator-level
			// mirror of ReusedInstance_LargeThenSmall.
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner, CreateTransform());

			foreach (var length in new[] { 4096, 32, 4096, 1 })
			{
				var payload = Payload(length, seed: length);
				await WriteAsync(storage, Key, payload);

				CollectionAssert.AreEqual(payload, await ReadAsync(storage, Key),
					$"{TransformName}: a {length}-byte payload failed on a repeated write.");
			}
		});

		#endregion
	}

	/// <summary>
	/// Adds what a length-prefixed <i>compression</i> transform owes on top of the shared contract:
	/// it must actually compress, and it must treat the declared output length as hostile input.
	/// </summary>
	/// <remarks>
	/// The length prefix comes off disk, which means it comes from whoever last edited the save. A
	/// decoder that reserves from it turns a 40-byte file into a multi-gigabyte allocation, so every
	/// compressing transform is expected to reject an implausible prefix on the ratio check
	/// <i>before</i> reserving anything, and to surface any other damage as
	/// <see cref="SaveCorruptedException"/> rather than as whatever its backing library throws.
	/// </remarks>
	public abstract class CompressionTransformContractTests : StorageTransformContractTests
	{
		/// <summary>Bytes the prefix occupies; every shipped compression transform uses 4, little-endian.</summary>
		protected virtual int LengthPrefixSize => 4;

		[Test]
		public void RepetitivePayload_IsActuallyCompressed()
		{
			// A run of zeroes is the easy case for every algorithm here. If this does not shrink, the
			// transform is passing bytes through and the whole point of it is gone.
			var payload = new byte[8192];
			var transform = CreateTransform();

			try
			{
				using var applied = new PooledArrayBufferWriter();
				transform.Apply(payload, applied);

				Assert.Less(applied.WrittenLength, payload.Length / 4,
					$"{TransformName}: a run of zeroes must compress to a fraction of its size.");
			}
			finally
			{
				(transform as IDisposable)?.Dispose();
			}
		}

		[Test]
		public void HostileLengthPrefix_IsRejectedOnTheRatioCheck()
		{
			// The decompression bomb: a small body claiming ~2 GB of output.
			var transform = CreateTransform();

			try
			{
				using var compressed = new PooledArrayBufferWriter();
				transform.Apply(Payload(64), compressed);

				var tampered = compressed.WrittenSpan.ToArray();
				System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(tampered, int.MaxValue);

				using var output = new PooledArrayBufferWriter();

				var thrown = Assert.Throws<SaveCorruptedException>(() => transform.Reverse(tampered, output),
					$"{TransformName}: an implausible declared length must be rejected.");

				Assert.AreEqual(SaveCorruptedExceptionReason.EnvelopeIsTooLarge, thrown.Reason,
					$"{TransformName}: rejection must happen on the ratio check, before anything is reserved.");
			}
			finally
			{
				(transform as IDisposable)?.Dispose();
			}
		}

		[Test]
		public void ShortenedLengthPrefix_IsRejected()
		{
			// Plausible enough to pass the ratio check, but smaller than what the body decodes to.
			// Must fail loudly rather than silently truncate the save.
			var transform = CreateTransform();

			try
			{
				using var compressed = new PooledArrayBufferWriter();
				transform.Apply(Payload(2048), compressed);

				var tampered = compressed.WrittenSpan.ToArray();
				System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(tampered, 16);

				using var output = new PooledArrayBufferWriter();

				Assert.Throws<SaveCorruptedException>(() => transform.Reverse(tampered, output),
					$"{TransformName}: a declared length shorter than the real output must be rejected.");
			}
			finally
			{
				(transform as IDisposable)?.Dispose();
			}
		}

		[Test]
		public void TruncatedBody_IsRejected()
		{
			// Honest prefix, missing bytes — the case a half-written file actually produces.
			var transform = CreateTransform();

			try
			{
				using var compressed = new PooledArrayBufferWriter();
				transform.Apply(Payload(4096), compressed);

				var truncated = compressed.WrittenSpan[..(compressed.WrittenLength / 2)].ToArray();

				using var output = new PooledArrayBufferWriter();

				Assert.Throws<SaveCorruptedException>(() => transform.Reverse(truncated, output),
					$"{TransformName}: a truncated body must surface as SaveCorruptedException, " +
					"not as whatever the backing library throws.");
			}
			finally
			{
				(transform as IDisposable)?.Dispose();
			}
		}

		[Test]
		public void PayloadShorterThanTheLengthPrefix_IsRejected()
		{
			// The degenerate truncation: not even enough bytes to read the prefix itself.
			var transform = CreateTransform();

			try
			{
				using var output = new PooledArrayBufferWriter();

				Assert.Throws<SaveCorruptedException>(
					() => transform.Reverse(new byte[LengthPrefixSize - 1], output),
					$"{TransformName}: a payload too short to hold its own prefix must be rejected.");
			}
			finally
			{
				(transform as IDisposable)?.Dispose();
			}
		}
	}
}
