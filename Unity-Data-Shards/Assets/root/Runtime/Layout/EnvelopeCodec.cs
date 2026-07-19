using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Persistence.Core;
using Unity.Collections;

namespace Persistence.Layout
{
	/// <summary>
	/// Fixed binary codec for <see cref="SaveEnvelope"/>, format v3 (little-endian):
	/// <code>
	/// [Version:4][Checksum:8] | hashed region: [Timestamp:8][TypeCount:4]
	///   per type: [nameLen:4][utf8 name][asmLen:4][utf8 asm][schemaVersion:4]
	///   [RecordCount:4]  per record: [guid:16][typeIndex:4]
	/// </code>
	/// The checksum (xxHash3-64) covers everything from <see cref="HashedRegionOffset"/>
	/// to the end of the buffer the layout hands in — single-file layouts append ranges
	/// and payload after the envelope, so those are hashed too. <see cref="Version"/>
	/// stays outside the hash so it can always be parsed to pick a decoder.
	/// All primitive access goes through <see cref="BinaryPrimitives"/>: safe on
	/// unaligned addresses and endian-stable across platforms.
	/// </summary>
	public static class EnvelopeCodec
	{
		public const int ChecksumOffset = 4;
		public const int HashedRegionOffset = 12;

		// version(4) + checksum(8) + timestamp(8) + typeCount(4)
		private const int HeaderSize = 24;

		// guid(16) + typeIndex(4)
		private const int RecordSize = 20;

		// Sanity caps: corrupted counts must fail fast instead of allocating gigabytes.
		private const int MaxCount = 1_000_000;
		private const int MaxStringBytes = 64 * 1024;

		private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

		/// <summary>
		/// Appends the encoded envelope to the writer, single pass (no pre-sizing scan).
		/// The checksum field is written as zero — the layout patches it via
		/// <see cref="PatchChecksum"/> once the full buffer (envelope + any appended
		/// payload) is assembled.
		/// </summary>
		public static void Write(in SaveEnvelope envelope, IBufferWriter<byte> writer)
		{
			var header = writer.GetSpan(HeaderSize);
			BinaryPrimitives.WriteInt32LittleEndian(header, envelope.FormatVersion);
			BinaryPrimitives.WriteUInt64LittleEndian(header[ChecksumOffset..], 0UL);
			BinaryPrimitives.WriteInt64LittleEndian(header[HashedRegionOffset..], envelope.TimestampUtc);
			BinaryPrimitives.WriteInt32LittleEndian(header[20..], envelope.TypeCount);
			writer.Advance(HeaderSize);

			for (var i = 0; i < envelope.TypeCount; i++)
			{
				ref readonly var type = ref envelope.Types[i];
				WriteString(writer, type.TypeName);
				WriteString(writer, type.AssemblyName);
				WriteInt(writer, type.SchemaVersion);
			}

			WriteInt(writer, envelope.RecordCount);

			// Records are fixed-size: one reservation for the whole block.
			var recordBytes = envelope.RecordCount * RecordSize;
			var span = writer.GetSpan(recordBytes);
			var offset = 0;

			for (var i = 0; i < envelope.RecordCount; i++)
			{
				ref readonly var record = ref envelope.Records[i];
				BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], record.Id.Head);
				BinaryPrimitives.WriteUInt64LittleEndian(span[(offset + 8)..], record.Id.Tail);
				BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 16)..], record.TypeIndex);
				offset += RecordSize;
			}

			writer.Advance(recordBytes);
		}

		/// <summary>
		/// Decodes an envelope. Every advance is bounds-checked against the buffer and
		/// counts are sanity-capped, so truncated or corrupted data throws
		/// <see cref="SaveCorruptedException"/> instead of reading wild.
		/// <paramref name="bytesConsumed"/> is where appended data (ranges/payload)
		/// begins for single-file layouts.
		/// </summary>
		public static SaveEnvelope Read(ReadOnlySpan<byte> data, out int bytesConsumed)
		{
			var offset = 0;
			var version = ReadInt(data, ref offset);

			if (version != SaveEnvelope.CurrentFormatVersion)
				throw new InvalidOperationException($"Unsupported envelope version {version}, expected {SaveEnvelope.CurrentFormatVersion}.");

			var envelope = new SaveEnvelope
			{
				FormatVersion = version,
				Checksum = ReadULong(data, ref offset),
				TimestampUtc = ReadLong(data, ref offset)
			};

			var typeCount = ReadInt(data, ref offset);

			if ((uint)typeCount > MaxCount)
				throw new SaveCorruptedException($"Envelope type count {typeCount} is out of range.");

			envelope.Types = new SerializedType[typeCount];
			envelope.TypeCount = typeCount;

			for (var i = 0; i < typeCount; i++)
			{
				var typeName = ReadString(data, ref offset);
				var assemblyName = ReadString(data, ref offset);
				var schemaVersion = ReadInt(data, ref offset);
				envelope.Types[i] = new SerializedType(typeName, assemblyName, schemaVersion);
			}

			var recordCount = ReadInt(data, ref offset);

			if ((uint)recordCount > MaxCount)
				throw new SaveCorruptedException($"Envelope record count {recordCount} is out of range.");

			// C4: the full record block size is known here — verify it fits before
			// touching a single record.
			if (data.Length - offset < recordCount * RecordSize)
				throw new SaveCorruptedException($"Envelope truncated: {recordCount} records need {recordCount * RecordSize} bytes, {data.Length - offset} remain.");

			envelope.Records = new ShardRecord[recordCount];
			envelope.RecordCount = recordCount;

			for (var i = 0; i < recordCount; i++)
			{
				var head = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
				var tail = BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 8)..]);
				envelope.Records[i].Id = new SerializableGuid(head, tail);
				envelope.Records[i].TypeIndex = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 16)..]);
				offset += RecordSize;
			}

			// Every type index must point inside the type table.
			for (var i = 0; i < recordCount; i++)
				if ((uint)envelope.Records[i].TypeIndex >= (uint)typeCount)
					throw new SaveCorruptedException($"Record {i} references type index {envelope.Records[i].TypeIndex}, but only {typeCount} types are stored.");

			bytesConsumed = offset;
			return envelope;
		}

		/// <summary>xxHash3-64 over the hashed region: everything past the checksum field.</summary>
		public static unsafe ulong ComputeChecksum(ReadOnlySpan<byte> encoded)
		{
			if (encoded.Length < HashedRegionOffset)
				throw new SaveCorruptedException($"Buffer too small for an envelope header ({encoded.Length} bytes).");

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
			BinaryPrimitives.WriteUInt64LittleEndian(encoded[ChecksumOffset..], checksum);
		}

		/// <summary>
		/// Verifies the stored checksum against the buffer content. Layouts call this
		/// BEFORE <see cref="Read"/> parses anything — the checksum is the primary
		/// corruption gate; Read's bounds checks are defense in depth.
		/// </summary>
		public static void ValidateChecksum(ReadOnlySpan<byte> encoded)
		{
			var computed = ComputeChecksum(encoded);
			var stored = BinaryPrimitives.ReadUInt64LittleEndian(encoded[ChecksumOffset..]);

			if (computed != stored)
				throw new SaveCorruptedException($"Checksum mismatch: stored {stored:x16}, computed {computed:x16}. The save data is corrupted.");
		}

		private static void WriteInt(IBufferWriter<byte> writer, int value)
		{
			var span = writer.GetSpan(4);
			BinaryPrimitives.WriteInt32LittleEndian(span, value);
			writer.Advance(4);
		}

		// D3: single pass — reserve worst case (3 bytes per UTF-16 char), encode once,
		// patch the length prefix, advance by what was actually written.
		private static void WriteString(IBufferWriter<byte> writer, string value)
		{
			var span = writer.GetSpan(4 + value.Length * 3);
			var byteCount = Utf8.GetBytes(value.AsSpan(), span[4..]);
			BinaryPrimitives.WriteInt32LittleEndian(span, byteCount);
			writer.Advance(4 + byteCount);
		}

		private static int ReadInt(ReadOnlySpan<byte> data, ref int offset)
		{
			if (data.Length - offset < 4)
				throw new SaveCorruptedException($"Envelope truncated at offset {offset} (need 4 bytes, {data.Length - offset} remain).");

			var value = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
			offset += 4;
			return value;
		}

		private static long ReadLong(ReadOnlySpan<byte> data, ref int offset)
		{
			if (data.Length - offset < 8)
				throw new SaveCorruptedException($"Envelope truncated at offset {offset} (need 8 bytes, {data.Length - offset} remain).");

			var value = BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
			offset += 8;
			return value;
		}

		private static ulong ReadULong(ReadOnlySpan<byte> data, ref int offset)
		{
			if (data.Length - offset < 8)
				throw new SaveCorruptedException($"Envelope truncated at offset {offset} (need 8 bytes, {data.Length - offset} remain).");

			var value = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
			offset += 8;
			return value;
		}

		private static string ReadString(ReadOnlySpan<byte> data, ref int offset)
		{
			var byteCount = ReadInt(data, ref offset);

			if ((uint)byteCount > MaxStringBytes)
				throw new SaveCorruptedException($"Envelope string length {byteCount} at offset {offset} is out of range.");

			if (data.Length - offset < byteCount)
				throw new SaveCorruptedException($"Envelope truncated at offset {offset} (need {byteCount} bytes, {data.Length - offset} remain).");

			var value = Utf8.GetString(data.Slice(offset, byteCount));
			offset += byteCount;
			return value;
		}
	}
}
