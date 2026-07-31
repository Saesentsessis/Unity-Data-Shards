using System;
using System.Buffers.Binary;
using NUnit.Framework;
using Saesentsessis.Persistence.Buffers;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Layout;
using Unity.Collections.LowLevel.Unsafe;

namespace Saesentsessis.Persistence.Tests
{
	public class CodecTests
	{
		// Field offsets inside the 32-byte v4 header. Hardcoded on purpose: these ARE the wire
		// format, so a test that derived them from the struct could never catch it shifting.
		private const int VersionOffset = 8;
		private const int MagicOffset = 12;
		private const int TypeCountOffset = 24;
		private const int RecordCountOffset = 28;

		private static SaveEnvelope BuildEnvelope()
		{
			return SaveEnvelope.Create(
				2, new[]
				{
					new SerializedType("Some.Namespace.TypeA", "Assembly.A", 1),
					new SerializedType("Some.Namespace.TypeB", "Assembly.B", 3)
				},
				3, new[]
				{
					new ShardRecord { Id = new SerializableGuid(1, 2), TypeIndex = 0 },
					new ShardRecord { Id = new SerializableGuid(3, 4), TypeIndex = 1 },
					new ShardRecord { Id = new SerializableGuid(5, 6), TypeIndex = 0 }
				});
		}

		private static byte[] Encode(in SaveEnvelope envelope)
		{
			using var writer = new PooledArrayBufferWriter();
			EnvelopeCodec.Write(envelope, writer);
			return writer.WrittenSpan.ToArray();
		}

		private static SaveCorruptedExceptionReason ReasonOf(byte[] bytes)
		{
			var exception = Assert.Throws<SaveCorruptedException>(() => EnvelopeCodec.Read(bytes, out _));
			return exception.Reason;
		}

		[Test]
		public void MaxEncodedSize_BoundsTheActualEncoding([Values(0, 1, 3, 47, 500)] int recordCount)
		{
			// Layouts size their assembly buffer with this, so it has to be an upper bound at every
			// record count — the record block is 20 bytes each, which is what a flat allowance misses.
			var types = new[]
			{
				new SerializedType("Some.Namespace.TypeA", "Assembly.A", 1),
				new SerializedType("Ünïcödé.Namespace.TypeB", "Assembly.B", 3)
			};

			var records = new ShardRecord[recordCount];

			for (var i = 0; i < recordCount; i++)
				records[i] = new ShardRecord { Id = new SerializableGuid((ulong)i, 7), TypeIndex = i % 2 };

			var envelope = SaveEnvelope.Create(types.Length, types, recordCount, records);
			var bound = EnvelopeCodec.MaxEncodedSize(envelope);
			var actual = Encode(envelope).Length;

			Assert.GreaterOrEqual(bound, actual,
				"MaxEncodedSize must never under-report, or the buffer it sized reallocates mid-encode.");

			// Non-ASCII type names encode to more bytes than they have chars, so the slack must come
			// from the string allowance rather than from the record block being over-counted.
			Assert.LessOrEqual(bound - actual, 4 * types.Length * 64,
				"The bound is meant to be tight enough to size a buffer with, not a wild over-estimate.");
		}

		[Test]
		public void WireLayout_MatchesFormatSpecification()
		{
			// The header and the record block are transferred as raw struct memory, so the runtime's
			// layout IS the format. If Mono/IL2CPP ever disagreed with this, saves written by one
			// would be unreadable by the other — so pin both sizes down explicitly.
			Assert.AreEqual(20, UnsafeUtility.SizeOf<ShardRecord>(), "ShardRecord must stay 20 bytes on the wire.");
			Assert.AreEqual(32, UnsafeUtility.SizeOf<SaveEnvelopeHeader>(), "The envelope header must stay 32 bytes.");
		}

		[Test]
		public void Codec_WritesMagicAsAsciiTag()
		{
			var bytes = Encode(BuildEnvelope());

			// Spells "SHRD" at a fixed offset, so the format is identifiable in a hex dump.
			Assert.AreEqual((byte)'S', bytes[MagicOffset + 0]);
			Assert.AreEqual((byte)'H', bytes[MagicOffset + 1]);
			Assert.AreEqual((byte)'R', bytes[MagicOffset + 2]);
			Assert.AreEqual((byte)'D', bytes[MagicOffset + 3]);
		}

		[Test]
		public void Codec_RoundTrips()
		{
			var envelope = BuildEnvelope();
			envelope.TimestampUtc = DateTime.UtcNow.Ticks;

			var bytes = Encode(envelope);
			var decoded = EnvelopeCodec.Read(bytes, out var consumed);

			Assert.AreEqual(bytes.Length, consumed);
			Assert.AreEqual(SaveEnvelope.CurrentFormatVersion, decoded.FormatVersion);
			Assert.AreEqual(envelope.TimestampUtc, decoded.TimestampUtc);
			Assert.AreEqual(envelope.TypeCount, decoded.TypeCount);
			Assert.AreEqual(envelope.RecordCount, decoded.RecordCount);

			for (var i = 0; i < envelope.TypeCount; i++)
				Assert.AreEqual(envelope.Types[i], decoded.Types[i]);

			for (var i = 0; i < envelope.RecordCount; i++)
			{
				Assert.AreEqual(envelope.Records[i].Id, decoded.Records[i].Id);
				Assert.AreEqual(envelope.Records[i].TypeIndex, decoded.Records[i].TypeIndex);
			}
		}

		[Test]
		public void Codec_EmptyEnvelope_RoundTrips()
		{
			var envelope = SaveEnvelope.Create(0, Array.Empty<SerializedType>(), 0, Array.Empty<ShardRecord>());
			var bytes = Encode(envelope);

			// Header only: Write must early-out of the record block without emitting anything.
			Assert.AreEqual(32, bytes.Length);

			var decoded = EnvelopeCodec.Read(bytes, out var consumed);

			Assert.AreEqual(32, consumed);
			Assert.AreEqual(0, decoded.TypeCount);
			Assert.AreEqual(0, decoded.RecordCount);
		}

		[Test]
		public void Codec_TruncatedAtEveryOffset_ThrowsCorrupted()
		{
			var bytes = Encode(BuildEnvelope());

			for (var length = 0; length < bytes.Length; length++)
			{
				var truncated = new byte[length];
				Array.Copy(bytes, truncated, length);

				Assert.Throws<SaveCorruptedException>(
					() => EnvelopeCodec.Read(truncated, out _),
					$"Truncation to {length}/{bytes.Length} bytes must throw.");
			}
		}

		[Test]
		public void Codec_ForeignData_RejectedAsInvalidMagic()
		{
			var bytes = Encode(BuildEnvelope());
			bytes[MagicOffset] ^= 0xFF;

			Assert.AreEqual(SaveCorruptedExceptionReason.InvalidMagic, ReasonOf(bytes),
				"Data that is not ours must be reported as such, not as an unsupported version.");
		}

		[Test]
		public void Codec_ArbitraryBuffer_RejectedAsInvalidMagic()
		{
			// A file of the right size but entirely foreign content must not be mistaken for a save.
			var bytes = new byte[128];

			for (var i = 0; i < bytes.Length; i++)
				bytes[i] = (byte)i;

			Assert.AreEqual(SaveCorruptedExceptionReason.InvalidMagic, ReasonOf(bytes));
		}

		[Test]
		public void Codec_WrongVersion_RejectedAsUnsupportedVersion()
		{
			var bytes = Encode(BuildEnvelope());
			bytes[VersionOffset] = 0xFF;

			Assert.AreEqual(SaveCorruptedExceptionReason.UnsupportedVersion, ReasonOf(bytes),
				"A v3 (or any non-v4) envelope must be refused explicitly — there is no compatibility path.");
		}

		[Test]
		public void Codec_HostileCounts_RejectedBeforeAllocating()
		{
			var bytes = Encode(BuildEnvelope());

			// Both counts sit in the fixed header, so a body far smaller than they describe is
			// detectable up front — without first allocating arrays for a million entries.
			BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(TypeCountOffset), 900_000);
			BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(RecordCountOffset), 900_000);

			Assert.AreEqual(SaveCorruptedExceptionReason.EnvelopeTruncated, ReasonOf(bytes));
		}

		[Test]
		public void Codec_CountOverCap_RejectedAsOverflow()
		{
			var bytes = Encode(BuildEnvelope());
			BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(TypeCountOffset), int.MaxValue);

			Assert.AreEqual(SaveCorruptedExceptionReason.TypeCountOverflow, ReasonOf(bytes));
		}

		[Test]
		public void Codec_InvalidTypeIndex_ThrowsCorrupted()
		{
			var envelope = BuildEnvelope();
			ref var record = ref envelope.RecordsArray[1];
			record.TypeIndex = 7;

			var bytes = Encode(envelope);

			Assert.AreEqual(SaveCorruptedExceptionReason.TypeIndexOutOfRange, ReasonOf(bytes));
		}

		[Test]
		public void Checksum_DetectsEveryBitFlipInHashedRegion()
		{
			using var writer = new PooledArrayBufferWriter();
			EnvelopeCodec.Write(BuildEnvelope(), writer);
			var bytes = writer.WrittenSpan.ToArray();
			EnvelopeCodec.PatchChecksum(bytes);

			Assert.DoesNotThrow(() => EnvelopeCodec.ValidateChecksum(bytes));

			// Flip one bit per byte across checksum field + hashed region.
			for (var i = 0; i < bytes.Length; i++)
			{
				bytes[i] ^= 0x10;
				Assert.Throws<SaveCorruptedException>(
					() => EnvelopeCodec.ValidateChecksum(bytes),
					$"Bit flip at offset {i} must fail validation.");
				bytes[i] ^= 0x10;
			}
		}
	}
}
