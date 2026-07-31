using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Storage.Transforms
{
	/// <summary>
	/// Deflate compression over <see cref="System.IO.Compression.DeflateStream"/> — the only
	/// compressor available without a third-party dependency.
	/// <para>
	/// Wire format is <c>[originalLength:4 LE][deflate stream]</c>. Deflate is self-terminating, so
	/// the length is not required to decode; it is stored to size the output buffer and to verify the
	/// result, and it is the same framing the LZ4 and Zstd samples use (LZ4 genuinely requires it).
	/// </para>
	/// <para>
	/// <see cref="Reverse"/> treats that prefix as untrusted. Decompression is driven by the stream
	/// and each reservation is capped by <see cref="TransformLimits"/>, so a doctored prefix can
	/// neither force an oversized allocation nor let a truncated payload pass as complete.
	/// </para>
	/// <para>
	/// Neither direction allocates an intermediate buffer: the compressor writes straight into the
	/// pipeline's arena through <see cref="BufferWriterStream"/>, and the decompressor reads straight
	/// out of the source span through an <see cref="UnmanagedMemoryStream"/> over pinned memory.
	/// </para>
	/// <para>
	/// <b>IL2CPP caveat:</b> Unity has a historical report of <c>DeflateStream</c> dropping bytes
	/// under IL2CPP. Verify a round-trip on your target platform before shipping this; the LZ4 sample
	/// is pure managed C# and carries no such risk.
	/// </para>
	/// </summary>
	public sealed class DeflateTransform : IStorageTransform
	{
		private const int LengthPrefixSize = 4;

		private readonly CompressionLevel _level;

		// Re-pointed at each call's writer instead of reallocated. TransformStorage runs one
		// operation at a time per instance, so a single adapter is enough.
		private readonly BufferWriterStream _adapter = new(null);

		public DeflateTransform(CompressionLevel level = CompressionLevel.Optimal)
		{
			_level = level;
		}

		public void Apply(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
		{
			BinaryPrimitives.WriteInt32LittleEndian(dst.GetSpan(LengthPrefixSize), src.Length);
			dst.Advance(LengthPrefixSize);

			if (src.Length == 0)
				return;

			_adapter.Reset(dst);

			// leaveOpen: the adapter wraps the caller's writer and must outlive the compressor.
			using var deflate = new DeflateStream(_adapter, _level, leaveOpen: true);
			
			deflate.Write(src);
		}

		public unsafe void Reverse(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
		{
			if (src.Length < LengthPrefixSize)
				throw new SaveCorruptedException(
					$"Deflate payload of {src.Length} bytes is too short for its {LengthPrefixSize}-byte length prefix.",
					SaveCorruptedExceptionReason.EnvelopeTruncated);

			var originalLength = BinaryPrimitives.ReadInt32LittleEndian(src);
			var body = src[LengthPrefixSize..];

			TransformLimits.ValidateDeclaredLength(originalLength, body.Length, TransformLimits.DeflateMaxRatio, "Deflate");

			if (originalLength == 0)
				return;

			var written = 0;

			fixed (byte* bodyPtr = body)
			{
				using var source = new UnmanagedMemoryStream(bodyPtr, body.Length);
				using var deflate = new DeflateStream(source, CompressionMode.Decompress);

				// The loop is driven by the stream, never by the prefix: the prefix only bounds the
				// result from above. Deflate is self-terminating, so a truncated or hostile payload
				// simply stops producing bytes, and each reservation is capped — a prefix claiming
				// 2 GB from a 40-byte body can no longer force a 2 GB allocation before we find out.
				while (true)
				{
					var target = dst.GetSpan(TransformLimits.ClampReservation(originalLength - written));
					var read = deflate.Read(target);

					if (read == 0)
						break;

					written += read;

					if (written > originalLength)
						throw new SaveCorruptedException(
							$"Deflate stream expanded past the {originalLength} bytes its prefix declared.",
							SaveCorruptedExceptionReason.EnvelopeIsTooLarge);

					dst.Advance(read);
				}
			}

			if (written != originalLength)
				throw new SaveCorruptedException(
					$"Deflate stream produced {written} bytes, but its prefix declared {originalLength}.",
					SaveCorruptedExceptionReason.EnvelopeTruncated);
		}
	}
}
