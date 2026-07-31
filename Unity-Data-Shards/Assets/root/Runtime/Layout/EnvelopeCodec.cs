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
		/// Upper bound on the bytes <see cref="Write"/> appends for this envelope, and on the
		/// largest reservation it makes along the way. Size an assembly buffer with this and the
		/// encode cannot trigger a reallocation.
		/// </summary>
		/// <remarks>
		/// Matches <see cref="Write"/>'s own worst case rather than the exact encoded length: strings
		/// are counted at three bytes per UTF-16 char, which is what <c>WriteString</c> reserves
		/// before it encodes. Counting exactly would mean a UTF-8 pass over every type name to save a
		/// few hundred bytes of unused <i>capacity</i> — the encoded output is unaffected either way.
		/// The record block, which is the part that actually scales, is counted exactly.
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

		// Single pass — reserve worst case (3 bytes per UTF-16 char), encode once,
		// patch the length prefix, advance by what was actually written.
		private static void WriteString(IBufferWriter<byte> writer, string value)
		{
			// Gated: this validates OUR OWN data, so it is a development-time contract check, not a
			// corruption gate. A type or assembly name this long is a bug in the caller — and the
			// save it would produce is one ReadString refuses to decode.
			// UTF-8 never emits fewer bytes than there are UTF-16 chars, so testing the char count
			// first is a sound fail-fast before the worst-case span reservation below; it uses the
			// same '>' as the byte-count test so it can never reject a string ReadString accepts.
#if ENABLE_PERSISTENCE_INTEGRITY_CHECKS
			if ((uint)value.Length > MaxStringBytes)
				throw new ArgumentException(
					$"Envelope string is {value.Length} chars, over the {MaxStringBytes} limit.", nameof(value));
#endif

			var span = writer.GetSpan(4 + value.Length * 3);
			var byteCount = Utf8.GetBytes(value.AsSpan(), span[4..]);

#if ENABLE_PERSISTENCE_INTEGRITY_CHECKS
			if ((uint)byteCount > MaxStringBytes)
				throw new ArgumentException(
					$"Envelope string encodes to {byteCount} bytes, over the {MaxStringBytes} limit.", nameof(value));
#endif

			BinaryPrimitives.WriteInt32LittleEndian(span, byteCount);
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
