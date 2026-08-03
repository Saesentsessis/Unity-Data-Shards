using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Storage.Transforms
{
	/// <summary>
	/// Bounds shared by every length-prefixed compression transform.
	/// <para>
	/// A compressed payload declares how many bytes it expands to. That number arrives from disk,
	/// which means it arrives from whoever last edited the file — so reserving a buffer from it
	/// directly lets a few hundred bytes demand a multi-gigabyte allocation. These helpers make the
	/// declared length something a decoder <i>checks</i> rather than something it trusts.
	/// </para>
	/// <para>
	/// Note that chaining <see cref="AesCbcHmacTransform"/> after compression removes the exposure
	/// entirely: <c>Reverse</c> runs outermost-first, so the authentication tag is verified before a
	/// single compressed byte is examined, and forging that tag needs the key. The envelope's own
	/// xxHash3 does <b>not</b> help here — it is unkeyed, and it is validated only after the whole
	/// transform chain has already run.
	/// </para>
	/// </summary>
	public static class TransformLimits
	{
		/// <summary>
		/// Ceiling on a single speculative reservation, for decoders that can grow their output
		/// incrementally. Large enough that a realistic save is read in a handful of steps rather
		/// than hundreds, small enough that a hostile prefix cannot turn into a memory spike.
		/// </summary>
		public const int MaxReservation = 2 * 1024 * 1024;

		/// <summary>Deflate's theoretical maximum expansion (RFC 1951 stored/fixed-Huffman bound).</summary>
		public const int DeflateMaxRatio = 1032;

		/// <summary>LZ4's theoretical maximum expansion for a raw block.</summary>
		public const int LZ4MaxRatio = 255;

		/// <summary>
		/// Zstd's theoretical maximum expansion: a 4-byte RLE block describes a full 128 KiB block,
		/// so 131072 / 4.
		/// </summary>
		/// <remarks>
		/// This was 1024, described as a "practical ceiling" that real save data would never
		/// approach. It is not one. Measured against ZstdSharp 0.8.8, 32 MB of zeroes compresses
		/// 32202:1 and an ordinary repeating byte pattern reaches 4519:1 — both far past 1024:1. The
		/// consequence was worse than a rejected read: the bound is only checked on the way back in,
		/// so a highly compressible save was written successfully and then refused at load, which is
		/// data loss rather than a guard. Like <see cref="DeflateMaxRatio"/> and
		/// <see cref="LZ4MaxRatio"/>, this must be the format's real maximum, never a guess at what
		/// data "should" look like.
		/// </remarks>
		public const int ZstdMaxRatio = 32768;

		/// <summary>
		/// Rejects a declared output length that no honest payload of this size could produce.
		/// The ratio is the real defence: to claim N bytes of output an attacker has to actually
		/// supply N/<paramref name="maxRatio"/> bytes of input, which puts the allocation back in
		/// proportion to the file they wrote.
		/// </summary>
		public static void ValidateDeclaredLength(int declaredLength, int compressedLength, int maxRatio, string algorithm)
		{
			if (declaredLength < 0)
				throw new SaveCorruptedException($"{algorithm} length prefix {declaredLength} is negative.",
					SaveCorruptedExceptionReason.EnvelopeTruncated);

			// long, so the multiplication cannot itself overflow into a passing value.
			if (declaredLength > (long)compressedLength * maxRatio)
				throw new SaveCorruptedException(
					$"{algorithm} payload of {compressedLength} bytes declares {declaredLength} bytes of output, " +
					$"which exceeds the format's {maxRatio}:1 maximum expansion — the length prefix is corrupt or hostile.",
					SaveCorruptedExceptionReason.EnvelopeIsTooLarge);
		}

		/// <summary>Caps a single reservation at <see cref="MaxReservation"/>, keeping at least one byte.</summary>
		public static int ClampReservation(int remaining)
		{
			if (remaining < 1)
				return 1;

			return remaining > MaxReservation ? MaxReservation : remaining;
		}
	}
}
