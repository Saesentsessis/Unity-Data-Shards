using System;
using System.Buffers;
using System.Buffers.Binary;
using K4os.Compression.LZ4;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Storage.Transforms;

namespace Saesentsessis.Persistence.Storage.Transforms.LZ4
{
	/// <summary>
	/// LZ4 compression backed by K4os.Compression.LZ4 — pure managed C#, so it carries none of the
	/// IL2CPP risk that the built-in <c>DeflateStream</c> does.
	/// <para>
	/// Wire format is <c>[originalLength:4 LE][lz4 block]</c>. The prefix is mandatory here:
	/// <see cref="LZ4Codec.Decode(ReadOnlySpan{byte}, Span{byte})"/> needs the exact output size up
	/// front, because a raw LZ4 block carries no length of its own.
	/// </para>
	/// <para>
	/// Neither direction allocates: the compressor writes into the pipeline arena reserved through
	/// <see cref="LZ4Codec.MaximumOutputSize"/>, and the decompressor writes into a span sized from
	/// the prefix.
	/// </para>
	/// </summary>
	public sealed class LZ4Transform : IStorageTransform
	{
		private const int LengthPrefixSize = 4;

		private readonly LZ4Level _level;

		/// <param name="level">
		/// <see cref="LZ4Level.L00_FAST"/> is the usual pick for saves — the ratio difference on save
		/// data is small and the speed difference is not.
		/// </param>
		public LZ4Transform(LZ4Level level = LZ4Level.L00_FAST)
		{
			_level = level;
		}

		public void Apply(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
		{
			BinaryPrimitives.WriteInt32LittleEndian(dst.GetSpan(LengthPrefixSize), src.Length);
			dst.Advance(LengthPrefixSize);

			if (src.Length == 0)
				return;

			// MaximumOutputSize covers the incompressible worst case, where LZ4 output exceeds input.
			var target = dst.GetSpan(LZ4Codec.MaximumOutputSize(src.Length));
			var written = LZ4Codec.Encode(src, target, _level);

			if (written < 0)
				throw new InvalidOperationException(
					$"LZ4 compression failed for {src.Length} bytes (target buffer reported too small).");

			dst.Advance(written);
		}

		public void Reverse(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
		{
			if (src.Length < LengthPrefixSize)
				throw new SaveCorruptedException(
					$"LZ4 payload of {src.Length} bytes is too short for its {LengthPrefixSize}-byte length prefix.",
					SaveCorruptedExceptionReason.EnvelopeTruncated);

			var originalLength = BinaryPrimitives.ReadInt32LittleEndian(src);
			var body = src[LengthPrefixSize..];

			// A raw LZ4 block must be decoded into an exactly-sized buffer, so unlike Deflate this
			// decoder cannot grow incrementally — the ratio bound is what keeps the reservation
			// proportional to the bytes actually present in the file.
			TransformLimits.ValidateDeclaredLength(originalLength, body.Length, TransformLimits.LZ4MaxRatio, "LZ4");

			if (originalLength == 0)
				return;

			var target = dst.GetSpan(originalLength)[..originalLength];
			var decoded = LZ4Codec.Decode(body, target);

			if (decoded != originalLength)
				throw new SaveCorruptedException(
					$"LZ4 decompression produced {decoded} bytes, but the prefix declared {originalLength}.",
					SaveCorruptedExceptionReason.EnvelopeTruncated);

			dst.Advance(decoded);
		}
	}
}
