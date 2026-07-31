using System;
using System.Buffers;
using System.IO;

namespace Saesentsessis.Persistence.Storage.Transforms
{
	/// <summary>
	/// Write-only <see cref="Stream"/> forwarding straight into an <see cref="IBufferWriter{T}"/>.
	/// <para>
	/// The BCL compression types are stream-based while <see cref="Core.IStorageTransform"/> is
	/// span/writer-based. Bridging them through a <see cref="MemoryStream"/> would buy an extra full
	/// copy of the payload on every save; this adapter lets the compressor emit directly into the
	/// pipeline's arena instead.
	/// </para>
	/// </summary>
	internal sealed class BufferWriterStream : Stream
	{
		private IBufferWriter<byte> _writer;
		private long _written;

		public BufferWriterStream(IBufferWriter<byte> writer)
		{
			_writer = writer;
		}

		/// <summary>Re-points the adapter at another writer so one instance can serve many calls.</summary>
		public void Reset(IBufferWriter<byte> writer)
		{
			_writer = writer;
			_written = 0;
		}

		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;

		/// <summary>Bytes written since the last <see cref="Reset"/>.</summary>
		public override long Length => _written;

		public override long Position
		{
			get => _written;
			set => throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
			=> Write(new ReadOnlySpan<byte>(buffer, offset, count));

		public override void Write(ReadOnlySpan<byte> buffer)
		{
			if (buffer.Length == 0)
				return;

			buffer.CopyTo(_writer.GetSpan(buffer.Length));
			_writer.Advance(buffer.Length);
			_written += buffer.Length;
		}

		public override void WriteByte(byte value)
		{
			_writer.GetSpan(1)[0] = value;
			_writer.Advance(1);
			_written++;
		}

		// The writer owns its memory and has no notion of flushing; nothing to do.
		public override void Flush() { }

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
	}
}
