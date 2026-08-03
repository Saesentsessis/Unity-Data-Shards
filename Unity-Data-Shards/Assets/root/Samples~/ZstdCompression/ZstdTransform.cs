using System;
using System.Buffers;
using System.Buffers.Binary;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Storage.Transforms;
using ZstdSharp;

namespace Saesentsessis.Persistence.Storage.Transforms.Zstd
{
	/// <summary>
	/// Zstandard compression backed by ZstdSharp — a pure managed C# port of zstd, so like the LZ4
	/// sample it ships no native binaries.
	/// <para>
	/// Wire format is <c>[originalLength:4 LE][zstd frame]</c>, matching the other compression
	/// transforms. A zstd frame usually carries its own content size, but relying on that is
	/// fragile — it is optional in the format — so the prefix is authoritative here and lets
	/// <see cref="Reverse"/> size the output in one shot.
	/// </para>
	/// <para>
	/// Against LZ4: noticeably better ratio, noticeably slower both ways. Prefer this when saves are
	/// large or bandwidth-bound (cloud sync), and LZ4 when save/load latency is what matters.
	/// </para>
	/// <para>
	/// <see cref="Compressor"/> and <see cref="Decompressor"/> hold native-ish state and are not
	/// thread-safe, so they are created per call. <c>TransformStorage</c> runs one operation at a
	/// time per instance, so this stays within the <see cref="IStorageTransform"/> contract.
	/// </para>
	/// </summary>
	public sealed class ZstdTransform : IStorageTransform
	{
		private const int LengthPrefixSize = 4;

		private readonly int _level;

		/// <param name="level">zstd level; 3 is the library default and a good save-data trade-off.</param>
		public ZstdTransform(int level = Compressor.DefaultCompressionLevel)
		{
			_level = level;
		}

		public void Apply(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
		{
			BinaryPrimitives.WriteInt32LittleEndian(dst.GetSpan(LengthPrefixSize), src.Length);
			dst.Advance(LengthPrefixSize);

			if (src.Length == 0)
				return;

			using var compressor = new Compressor(_level);

			// GetCompressBound covers the incompressible worst case.
			var target = dst.GetSpan(Compressor.GetCompressBound(src.Length));
			var written = compressor.Wrap(src, target);

			dst.Advance(written);
		}

		public void Reverse(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
		{
			if (src.Length < LengthPrefixSize)
				throw new SaveCorruptedException(
					$"Zstd payload of {src.Length} bytes is too short for its {LengthPrefixSize}-byte length prefix.",
					SaveCorruptedExceptionReason.EnvelopeTruncated);

			var originalLength = BinaryPrimitives.ReadInt32LittleEndian(src);
			var body = src[LengthPrefixSize..];

			// Unwrap decodes into an exactly-sized buffer, so the reservation cannot grow
			// incrementally the way Deflate's does — the ratio bound keeps it proportional to the
			// bytes actually present rather than to whatever the prefix claims.
			TransformLimits.ValidateDeclaredLength(originalLength, body.Length, TransformLimits.ZstdMaxRatio, "Zstd");

			if (originalLength == 0)
				return;

			using var decompressor = new Decompressor();

			var target = dst.GetSpan(originalLength)[..originalLength];
			int decoded;

			try
			{
				decoded = decompressor.Unwrap(body, target);
			}
			catch (ZstdException exception)
			{
				// ZstdSharp signals a malformed, truncated or mis-sized frame by throwing its own
				// type. Everything reaching this method came off disk and is untrusted, so it has to
				// surface as the package's corruption signal — a caller catching SaveCorruptedException
				// to offer "restore backup?" cannot be expected to also know ZstdSharp's exceptions.
				throw new SaveCorruptedException(
					$"Zstd decompression failed: {exception.Message}",
					SaveCorruptedExceptionReason.EnvelopeTruncated);
			}

			if (decoded != originalLength)
				throw new SaveCorruptedException(
					$"Zstd decompression produced {decoded} bytes, but the prefix declared {originalLength}.",
					SaveCorruptedExceptionReason.EnvelopeTruncated);

			dst.Advance(decoded);
		}
	}
}
