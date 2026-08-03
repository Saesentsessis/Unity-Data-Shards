#if PERSISTENCE_HAS_NEWTONSOFT
using System;
using System.Buffers;
using System.IO;
using System.Text;

namespace Saesentsessis.Persistence.Serialization.Newtonsoft
{
	/// <summary>
	/// Write-only <see cref="TextWriter"/> that UTF-8 encodes straight into an
	/// <see cref="IBufferWriter{T}"/>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Newtonsoft targets <see cref="TextWriter"/>, while the pipeline is writer-based. Bridging
	/// them through a <see cref="StringBuilder"/> and <c>ToString()</c> costs a payload-sized string
	/// plus a payload-sized encode on every shard; this adapter lets the JSON writer emit directly
	/// into the arena instead. Same trick, and same reason, as
	/// <c>Storage.Transforms.BufferWriterStream</c> for the stream-based compressors.
	/// </para>
	/// <para>
	/// The <see cref="Encoder"/> is <b>stateful and must be</b>: Newtonsoft writes a surrogate pair
	/// as two separate <c>char</c>s, so a stateless encode would emit a replacement character for
	/// each half. The encoder carries the high surrogate across calls and
	/// <see cref="Flush"/> settles anything left pending.
	/// </para>
	/// </remarks>
	internal sealed class BufferWriterTextWriter : TextWriter
	{
		// Enough for any single code point, so a Convert call can always make progress.
		private const int MinimumSpan = 8;

		private readonly Encoder _encoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetEncoder();

		private IBufferWriter<byte> _writer;

		public override Encoding Encoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

		/// <summary>Points the adapter at a new destination and clears any pending surrogate.</summary>
		public void Reset(IBufferWriter<byte> writer)
		{
			_writer = writer;
			_encoder.Reset();
		}

		public override void Write(char value)
		{
			Span<char> single = stackalloc char[1];
			single[0] = value;
			Encode(single, flush: false);
		}

		public override void Write(char[] buffer, int index, int count)
			=> Encode(buffer.AsSpan(index, count), flush: false);

		public override void Write(string value)
		{
			if (string.IsNullOrEmpty(value) == false)
				Encode(value.AsSpan(), flush: false);
		}

		public override void Write(ReadOnlySpan<char> buffer) => Encode(buffer, flush: false);

		/// <summary>Emits any character the encoder is still holding. Must run before the arena is read.</summary>
		public override void Flush() => Encode(ReadOnlySpan<char>.Empty, flush: true);

		private void Encode(ReadOnlySpan<char> chars, bool flush)
		{
			if (_writer == null)
				throw new InvalidOperationException("[BufferWriterTextWriter] used before Reset.");

			while (true)
			{
				var span = _writer.GetSpan(Math.Max(chars.Length, MinimumSpan));

				_encoder.Convert(chars, span, flush, out var consumed, out var written, out var completed);
				_writer.Advance(written);

				chars = chars[consumed..];

				if (chars.IsEmpty && (flush == false || completed))
					return;

				// Convert reports progress or the loop would spin; the arena always offers at least
				// MinimumSpan, which is more than any one code point needs.
				if (consumed == 0 && written == 0)
					throw new InvalidOperationException(
						"[BufferWriterTextWriter] encoder made no progress — the destination refused a span.");
			}
		}

		protected override void Dispose(bool disposing)
		{
			// Deliberately does not dispose the destination: the arena belongs to the pipeline, and
			// JsonTextWriter disposal must not end the save.
			_writer = null;
			base.Dispose(disposing);
		}
	}
}
#endif
