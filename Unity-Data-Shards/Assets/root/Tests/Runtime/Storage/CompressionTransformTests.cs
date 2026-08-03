using NUnit.Framework;
using Saesentsessis.Persistence.Buffers;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Storage.Transforms;
#if PERSISTENCE_HAS_LZ4
using K4os.Compression.LZ4;
using Saesentsessis.Persistence.Storage.Transforms.LZ4;
#endif
#if PERSISTENCE_HAS_ZSTD
using Saesentsessis.Persistence.Storage.Transforms.Zstd;
#endif

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>The dependency-free <see cref="DeflateTransform"/> that ships in the box.</summary>
	public sealed class DeflateTransformTests : CompressionTransformContractTests
	{
		protected override string TransformName => "Deflate";

		protected override IStorageTransform CreateTransform() => new DeflateTransform();
	}

#if PERSISTENCE_HAS_LZ4
	/// <summary>
	/// The LZ4 sample, backed by K4os.Compression.LZ4. Fast and pure-managed; the ratio is the
	/// trade-off, so the shared <c>RepetitivePayload_IsActuallyCompressed</c> bar is the right one
	/// to hold it to rather than anything tighter.
	/// </summary>
	public sealed class LZ4TransformTests : CompressionTransformContractTests
	{
		protected override string TransformName => "LZ4";

		protected override IStorageTransform CreateTransform() => new LZ4Transform();

		[Test]
		public void HigherLevel_CompressesAtLeastAsWell()
		{
			// Pins that the level argument is actually threaded through to the codec rather than
			// ignored — a silent default would otherwise look identical in every other test.
			var payload = Payload(16384, seed: 5);

			Assert.LessOrEqual(CompressedLength(new LZ4Transform(LZ4Level.L09_HC), payload),
				CompressedLength(new LZ4Transform(LZ4Level.L00_FAST), payload),
				"L09_HC must not produce more bytes than L00_FAST.");
		}

		[Test]
		public void EveryLevel_RoundTrips([Values(LZ4Level.L00_FAST, LZ4Level.L03_HC, LZ4Level.L12_MAX)] LZ4Level level)
		{
			// The decoder takes no level, so a mismatch would be silent: all levels must produce
			// blocks the same Reverse can read.
			var payload = Payload(4096, seed: (int)level);

			using var applied = new PooledArrayBufferWriter();
			new LZ4Transform(level).Apply(payload, applied);

			using var reversed = new PooledArrayBufferWriter();
			new LZ4Transform().Reverse(applied.WrittenSpan, reversed);

			CollectionAssert.AreEqual(payload, reversed.WrittenSpan.ToArray(),
				$"A block written at {level} must decode with a default-constructed transform.");
		}

		private static int CompressedLength(IStorageTransform transform, byte[] payload)
		{
			using var writer = new PooledArrayBufferWriter();
			transform.Apply(payload, writer);

			return writer.WrittenLength;
		}
	}
#endif

#if PERSISTENCE_HAS_ZSTD
	/// <summary>
	/// The Zstandard sample, backed by ZstdSharp. Better ratio than LZ4 and slower both ways, so the
	/// pairing worth pinning is that it beats Deflate on the same input.
	/// </summary>
	public sealed class ZstdTransformTests : CompressionTransformContractTests
	{
		protected override string TransformName => "Zstd";

		protected override IStorageTransform CreateTransform() => new ZstdTransform();

		[Test]
		public void EveryLevel_RoundTrips([Values(1, 3, 9, 19)] int level)
		{
			// A zstd frame is self-describing, so any level must decode through the default
			// decompressor — this is what lets the level be a deployment decision, not a format one.
			var payload = Payload(4096, seed: level);

			using var applied = new PooledArrayBufferWriter();
			new ZstdTransform(level).Apply(payload, applied);

			using var reversed = new PooledArrayBufferWriter();
			new ZstdTransform().Reverse(applied.WrittenSpan, reversed);

			CollectionAssert.AreEqual(payload, reversed.WrittenSpan.ToArray(),
				$"A frame written at level {level} must decode with a default-constructed transform.");
		}

		[Test]
		public void CompressesRepetitiveDataAtLeastAsWellAsDeflate()
		{
			// The reason to pay for this sample over the built-in transform. Deliberately "at least
			// as well" rather than a fixed ratio: the margin depends on the data, the claim does not.
			var payload = Payload(16384, seed: 9);

			using var zstd = new PooledArrayBufferWriter();
			new ZstdTransform().Apply(payload, zstd);

			using var deflate = new PooledArrayBufferWriter();
			new DeflateTransform().Apply(payload, deflate);

			Assert.LessOrEqual(zstd.WrittenLength, deflate.WrittenLength,
				"Zstd is chosen over Deflate for ratio; if it loses here the sample is not earning its dependency.");
		}
	}
#endif
}
