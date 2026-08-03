using System;
using System.Buffers;

namespace Saesentsessis.Persistence.Buffers
{
	/// <summary>
	/// <see cref="IBufferWriter{T}"/> over a region of memory that is already allocated and cannot
	/// grow — a reservation a layout was handed inside the pipeline arena.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Exists so <see cref="Layout.EnvelopeCodec"/> can encode into a fixed span without a second
	/// implementation: the codec writes through <see cref="IBufferWriter{T}"/>, and duplicating it
	/// against <see cref="Span{T}"/> would mean maintaining the wire format twice.
	/// </para>
	/// <para>
	/// <b>Overrunning throws rather than growing</b>, and that is the point. The region is sized by
	/// <c>EnvelopeCodec.ExactEncodedSize</c>; if the two ever disagree the failure is a loud
	/// exception at the moment of encoding, not payload bytes silently overwritten by a header.
	/// The check is never <c>[Conditional]</c> — a stripped build is exactly where a silent
	/// overwrite would corrupt saves.
	/// </para>
	/// <para>
	/// Reusable: <see cref="Reset"/> re-points it, so a layout holds one instance for its lifetime
	/// and allocates nothing per save.
	/// </para>
	/// </remarks>
	internal sealed unsafe class FixedBufferWriter : IBufferWriter<byte>
	{
		private byte* _buffer;
		private int _capacity;
		private int _written;

		/// <summary>Bytes committed since the last <see cref="Reset"/>.</summary>
		public int WrittenLength => _written;

		/// <summary>Points the writer at a new region. The caller owns the memory's lifetime.</summary>
		public void Reset(byte* buffer, int capacity)
		{
			_buffer = buffer;
			_capacity = capacity;
			_written = 0;
		}

		public void Advance(int count)
		{
			if (count < 0 || _written + count > _capacity)
				throw new ArgumentOutOfRangeException(nameof(count),
					$"[FixedBufferWriter] Advance({count}) past the {_capacity}-byte reservation with {_written} " +
					"already written. The region was sized too small for what is being encoded into it.");

			_written += count;
		}

		public Span<byte> GetSpan(int sizeHint = 0)
		{
			var free = _capacity - _written;

			if (sizeHint > free)
				throw new InvalidOperationException(
					$"[FixedBufferWriter] {sizeHint} bytes requested with {free} left of a {_capacity}-byte " +
					"reservation. A fixed region cannot grow — size it with EnvelopeCodec.ExactEncodedSize.");

			return new Span<byte>(_buffer + _written, free);
		}

		// The codec never asks for Memory, and a raw pointer cannot back one without a manager whose
		// lifetime nobody here owns. Failing loudly beats handing back something detached.
		public Memory<byte> GetMemory(int sizeHint = 0)
			=> throw new NotSupportedException("[FixedBufferWriter] writes through GetSpan only.");
	}
}
