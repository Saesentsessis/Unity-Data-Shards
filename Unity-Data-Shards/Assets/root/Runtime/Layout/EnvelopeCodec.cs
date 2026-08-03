using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Saesentsessis.Persistence.Core;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Saesentsessis.Persistence.Layout
{
	/// <summary>
	/// Fixed binary codec for <see cref="SaveEnvelope"/>, format v4:
	/// <code>
	/// [Checksum:8] | hashed region: [FormatVersion:4][Magic:4][TimestampUtc:8][TypeCount:4][RecordCount:4]
	///   per type:   [nameLen:4][utf8 name][asmLen:4][utf8 asm][schemaVersion:4]
	///   per record: [guid:16][typeIndex:4]        (one memcpy for the whole block)
	///   (single-file layouts append [ranges][payload] here)
	/// </code>
	/// The checksum (xxHash3-64) covers everything from <see cref="HashedRegionOffset"/> to the end
	/// of the buffer the layout hands in — single-file layouts append ranges and payload after the
	/// envelope, so those are hashed too. Only the checksum field itself sits outside the hash: the
	/// version and magic are inside it, so a flipped version bit cannot silently steer the reader
	/// to a different decoder. Both counts live in the fixed header, which lets a decoder size and
	/// bounds-check the entire body before parsing any of it.
	/// <para>
	/// <b>The format is little-endian only.</b> The 32-byte header and the record block are
	/// transferred as raw struct memory, so their in-memory layout is the wire layout. Every Unity
	/// target is little-endian; <see cref="AssertLittleEndian"/> fails loudly if that ever stops
	/// being true rather than writing files nothing can read back. Variable-length fields (string
	/// lengths, schema versions) still go through <see cref="BinaryPrimitives"/>, which is safe on
	/// unaligned addresses.
	/// </para>
	/// <para>
	/// Format v3 is <b>not</b> supported and cannot be read. It left the version outside the hashed
	/// region and split the two counts across the variable-length type table; both were corrected
	/// by a clean break rather than a compatibility shim.
	/// </para>
	/// </summary>
	public static class EnvelopeCodec
	{
		/// <summary>Start of the checksummed region: everything past the 8-byte checksum field.</summary>
		internal const int HashedRegionOffset = 8;

		// Sanity caps: corrupted counts must fail fast instead of allocating gigabytes.
		private const int MaxCount = 1_000_000;
		private const int MaxStringBytes = 64 * 1024;

		// Smallest possible encoded type entry: two empty strings plus a schema version.
		private const int MinTypeEntryBytes = 4 + 4 + 4;

		private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

		/// <summary>
		/// The <b>exact</b> number of bytes <see cref="Write"/> will append for this envelope.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Needed wherever the envelope has to be followed immediately by other data — a layout that
		/// reserves room for it at the head of the payload arena cannot use a bound, because the
		/// slack would become a gap between the header and the payload, and the file format has no
		/// way to express one.
		/// </para>
		/// <para>
		/// Costs a UTF-8 length pass per <i>type</i>, not per record. That is what makes it
		/// affordable: a save with ten thousand shards still has a handful of distinct types, while
		/// the record block — the part that scales — is a multiplication.
		/// </para>
		/// </remarks>
		public static int ExactEncodedSize(in SaveEnvelope envelope)
		{
			long size = UnsafeUtility.SizeOf<SaveEnvelopeHeader>()
				+ (long)envelope.RecordCount * UnsafeUtility.SizeOf<ShardRecord>();

			for (var i = 0; i < envelope.TypeCount; i++)
			{
				ref readonly var type = ref envelope.Types[i];

				// [nameLen:4][utf8 name][asmLen:4][utf8 asm][schemaVersion:4]
				size += MinTypeEntryBytes + Utf8.GetByteCount(type.TypeName) + Utf8.GetByteCount(type.AssemblyName);
			}

			if (size > int.MaxValue)
				throw new InvalidOperationException(
					$"Envelope encodes to {size} bytes, past the {int.MaxValue}-byte buffer limit.");

			return (int)size;
		}

		/// <summary>
		/// Upper bound on the bytes <see cref="Write"/> appends for this envelope, and on the
		/// largest reservation it makes along the way. Size a growable assembly buffer with this and
		/// the encode cannot trigger a reallocation; use <see cref="ExactEncodedSize"/> when the
		/// region is fixed or something must follow the envelope contiguously.
		/// </summary>
		/// <remarks>
		/// A cheap ceiling: strings are counted at three bytes per UTF-16 char, so this needs no
		/// UTF-8 pass. Use it to pre-size a <i>growable</i> buffer that should not have to grow.
		/// Anything that must be laid out exactly — a fixed region, or data placed immediately after
		/// the envelope — needs <see cref="ExactEncodedSize"/> instead; the slack here would become a
		/// gap the format cannot express.
		/// </remarks>
		public static int MaxEncodedSize(in SaveEnvelope envelope)
		{
			long size = UnsafeUtility.SizeOf<SaveEnvelopeHeader>()
				+ (long)envelope.RecordCount * UnsafeUtility.SizeOf<ShardRecord>();

			for (var i = 0; i < envelope.TypeCount; i++)
			{
				ref readonly var type = ref envelope.Types[i];

				// [nameLen:4][utf8 name][asmLen:4][utf8 asm][schemaVersion:4]
				size += MinTypeEntryBytes + 3L * type.TypeName.Length + 3L * type.AssemblyName.Length;
			}

			if (size > int.MaxValue)
				throw new InvalidOperationException(
					$"Envelope needs up to {size} bytes to encode, past the {int.MaxValue}-byte buffer limit.");

			return (int)size;
		}

		/// <summary>
		/// Appends the encoded envelope to the writer, single pass (no pre-sizing scan).
		/// The checksum field is written as zero — the layout patches it via
		/// <see cref="PatchChecksum"/> once the full buffer (envelope + any appended
		/// payload) is assembled.
		/// </summary>
		public static void Write(in SaveEnvelope envelope, IBufferWriter<byte> writer)
		{
			AssertLittleEndian();

			// Flush the header directly into the stream memory.
			var headerSize = UnsafeUtility.SizeOf<SaveEnvelopeHeader>();
			var headerSpan = writer.GetSpan(headerSize)[..headerSize];

			var header = envelope.Header;
			MemoryMarshal.Write(headerSpan, ref header);

			writer.Advance(headerSize);

			for (var i = 0; i < envelope.TypeCount; i++)
			{
				ref readonly var type = ref envelope.Types[i];
				WriteString(writer, type.TypeName);
				WriteString(writer, type.AssemblyName);
				WriteInt(writer, type.SchemaVersion);
			}

			if (envelope.RecordCount == 0)
				return;

			// Records are fixed-size unmanaged structs: one reservation and one memcpy for the whole
			// block. Records is already sliced to RecordCount, so a pooled tail can never leak out.
			var recordsAsBytes = MemoryMarshal.AsBytes(envelope.Records);
			var span = writer.GetSpan(recordsAsBytes.Length)[..recordsAsBytes.Length];

			recordsAsBytes.CopyTo(span);
			writer.Advance(recordsAsBytes.Length);
		}

		/// <summary>
		/// Decodes an envelope. The magic and version are checked first, then every count is
		/// sanity-capped and weighed against the bytes actually remaining, so truncated or corrupted
		/// data throws <see cref="SaveCorruptedException"/> instead of reading wild or allocating on
		/// a hostile count. <paramref name="bytesConsumed"/> is where appended data (ranges/payload)
		/// begins for single-file layouts.
		/// </summary>
		public static SaveEnvelope Read(ReadOnlySpan<byte> data, out int bytesConsumed)
		{
			AssertLittleEndian();

			var offset = data.ParseEnvelopeHeader(out var header);

			// Cheapest rejection first: is this one of ours at all?
			if (header.Magic != SaveEnvelopeHeader.ExpectedMagic)
				throw new SaveCorruptedException(
					$"Envelope magic {header.Magic:x8} does not match {SaveEnvelopeHeader.ExpectedMagic:x8} — this data was not written by Unity Data Shards.",
					SaveCorruptedExceptionReason.InvalidMagic);

			if (header.FormatVersion != SaveEnvelope.CurrentFormatVersion)
				throw new SaveCorruptedException(
					$"Unsupported envelope version {header.FormatVersion}, expected {SaveEnvelope.CurrentFormatVersion}.",
					SaveCorruptedExceptionReason.UnsupportedVersion);

			if ((uint)header.TypeCount > MaxCount)
				throw new SaveCorruptedException($"Envelope type count {header.TypeCount} is out of range.",
					SaveCorruptedExceptionReason.TypeCountOverflow);

			if ((uint)header.RecordCount > MaxCount)
				throw new SaveCorruptedException($"Envelope record count {header.RecordCount} is out of range.",
					SaveCorruptedExceptionReason.RecordCountOverflow);

			var recordSize = UnsafeUtility.SizeOf<ShardRecord>();

			// Both counts sit in the fixed header, so the smallest body they could possibly describe
			// is known before a single byte of it is parsed. Checking that here means a hostile count
			// cannot drive a multi-megabyte allocation out of a few dozen bytes of input.
			var minimumBody = (long)header.TypeCount * MinTypeEntryBytes + (long)header.RecordCount * recordSize;

			if (minimumBody > data.Length - offset)
				throw new SaveCorruptedException(
					$"Envelope truncated: {header.TypeCount} types and {header.RecordCount} records need at least {minimumBody} bytes, {data.Length - offset} remain.",
					SaveCorruptedExceptionReason.EnvelopeTruncated);

			var types = new SerializedType[header.TypeCount];

			for (var i = 0; i < header.TypeCount; i++)
			{
				var typeName = ReadString(data, ref offset);
				var assemblyName = ReadString(data, ref offset);
				var schemaVersion = ReadInt(data, ref offset);
				types[i] = new SerializedType(typeName, assemblyName, schemaVersion);
			}

			var recordsByteSize = header.RecordCount * recordSize;

			// Re-checked against the true offset: the type table above consumed a variable amount.
			if (data.Length - offset < recordsByteSize)
				throw new SaveCorruptedException(
					$"Envelope truncated: {header.RecordCount} records need {recordsByteSize} bytes, {data.Length - offset} remain.",
					SaveCorruptedExceptionReason.EnvelopeTruncated);

			var records = new ShardRecord[header.RecordCount];

			for (var i = 0; i < header.RecordCount; i++)
			{
				ref var record = ref records[i];
				record = MemoryMarshal.Read<ShardRecord>(data.Slice(offset, recordSize));

				// Every type index must point inside the type table.
				if (math.asuint(record.TypeIndex) >= header.TypeCount)
					throw new SaveCorruptedException(
						$"Record {i} references type index {record.TypeIndex}, but only {header.TypeCount} types are stored.",
						SaveCorruptedExceptionReason.TypeIndexOutOfRange);

				offset += recordSize;
			}

			bytesConsumed = offset;
			return SaveEnvelope.Create(header, types, records);
		}

		/// <summary>xxHash3-64 over the hashed region: everything past the checksum field.</summary>
		public static unsafe ulong ComputeChecksum(ReadOnlySpan<byte> encoded)
		{
			if (encoded.Length < HashedRegionOffset)
				throw new SaveCorruptedException($"Buffer too small for an envelope header ({encoded.Length} bytes).",
					SaveCorruptedExceptionReason.EnvelopeTruncated);

			var region = encoded[HashedRegionOffset..];

			fixed (byte* ptr = region)
			{
				var hash = xxHash3.Hash64(ptr, region.Length);
				return ((ulong)hash.y << 32) | hash.x;
			}
		}

		/// <summary>Computes the checksum of the assembled buffer and writes it into the checksum slot.</summary>
		public static void PatchChecksum(Span<byte> encoded)
		{
			var checksum = ComputeChecksum(encoded);
			BinaryPrimitives.WriteUInt64LittleEndian(encoded, checksum);
		}

		/// <summary>
		/// Verifies the stored checksum against the buffer content. Layouts call this
		/// BEFORE <see cref="Read"/> parses anything — the checksum is the primary
		/// corruption gate; Read's bounds checks are defense in depth.
		/// </summary>
		public static void ValidateChecksum(ReadOnlySpan<byte> encoded)
		{
			var computed = ComputeChecksum(encoded);
			var stored = BinaryPrimitives.ReadUInt64LittleEndian(encoded);

			if (computed != stored)
				throw new SaveCorruptedException($"Checksum mismatch: stored {stored:x16}, computed {computed:x16}. The save data is corrupted.",
					SaveCorruptedExceptionReason.ChecksumMismatch);
		}

		/// <summary>
		/// The header and record block are raw struct memory, so a big-endian host would emit files
		/// no little-endian host could read back. No Unity target is big-endian; this exists so that
		/// if one ever appears the failure is immediate and obvious instead of silent data loss.
		/// </summary>
		private static void AssertLittleEndian()
		{
			if (BitConverter.IsLittleEndian == false)
				throw new PlatformNotSupportedException(
					"The Unity Data Shards envelope format is little-endian only; this platform is big-endian.");
		}

		private static void WriteInt(IBufferWriter<byte> writer, int value)
		{
			var span = writer.GetSpan(4);
			BinaryPrimitives.WriteInt32LittleEndian(span, value);
			writer.Advance(4);
		}

		/// <summary>
		/// Writes <c>[byteLength:4][utf8]</c>, reserving <b>exactly</b> what it writes.
		/// </summary>
		/// <remarks>
		/// The reservation used to be the 3-bytes-per-UTF-16-char worst case, which a growable
		/// writer absorbed silently. It is not free against a fixed region: a caller that sized the
		/// region with <see cref="ExactEncodedSize"/> would see the last string demand more than
		/// remains, even though the total fits. That is only visible when nothing follows to soak up
		/// the slack — an envelope with a single record — which is why it survived every multi-shard
		/// test. Counting first costs one pass over a short name and makes the reservation match the
		/// accounting.
		/// </remarks>
		private static void WriteString(IBufferWriter<byte> writer, string value)
		{
			var byteCount = Utf8.GetByteCount(value);

			// Gated: this validates OUR OWN data, so it is a development-time contract check, not a
			// corruption gate. A type or assembly name this long is a bug in the caller — and the
			// save it would produce is one ReadString refuses to decode.
#if ENABLE_PERSISTENCE_INTEGRITY_CHECKS
			if ((uint)byteCount > MaxStringBytes)
				throw new ArgumentException(
					$"Envelope string encodes to {byteCount} bytes, over the {MaxStringBytes} limit.", nameof(value));
#endif

			var span = writer.GetSpan(4 + byteCount);

			BinaryPrimitives.WriteInt32LittleEndian(span, byteCount);
			Utf8.GetBytes(value.AsSpan(), span[4..]);

			writer.Advance(4 + byteCount);
		}

		private static int ReadInt(ReadOnlySpan<byte> data, ref int offset)
		{
			if (BinaryPrimitives.TryReadInt32LittleEndian(data[offset..], out var value) == false)
				throw new SaveCorruptedException($"Envelope truncated at offset {offset} (need 4 bytes, {data.Length - offset} remain).",
						SaveCorruptedExceptionReason.EnvelopeTruncated);

			offset += 4;
			return value;
		}

		private static string ReadString(ReadOnlySpan<byte> data, ref int offset)
		{
			var byteCount = ReadInt(data, ref offset);

			// Never gated. The unsigned cast is the only rejection of a NEGATIVE length, which the
			// signed truncation check below passes straight through — leaving data.Slice to throw
			// ArgumentOutOfRangeException instead of SaveCorruptedException, so a caller's
			// restore-from-backup path would never see it. Untrusted bytes get identical
			// validation in every build; only the checks on our own data are optional.
			if ((uint)byteCount > MaxStringBytes)
				throw new SaveCorruptedException($"Envelope string length {byteCount} at offset {offset} is out of range.",
					SaveCorruptedExceptionReason.EnvelopeTruncated);

			if (data.Length - offset < byteCount)
				throw new SaveCorruptedException($"Envelope truncated at offset {offset} (need {byteCount} bytes, {data.Length - offset} remain).",
					SaveCorruptedExceptionReason.EnvelopeTruncated);

			var value = Utf8.GetString(data.Slice(offset, byteCount));
			offset += byteCount;
			return value;
		}
	}
}
