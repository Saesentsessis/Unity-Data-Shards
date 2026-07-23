using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Saesentsessis.Persistence.Buffers
{
	/// <summary>
	/// <see cref="IBufferWriter{T}"/> over <see cref="ArrayPool{T}"/> — the managed
	/// pipeline's arena. Same role as <see cref="NativeListBufferWriter"/> but GC-tracked,
	/// with the backing array recycled through the shared pool. Not thread-safe.
	/// </summary>
	public sealed class PooledArrayBufferWriter : IArenaWriter, IDisposable
	{
		private const int DefaultChunkSize = 256;
		private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

		private byte[] _buffer;
		private int _written;

		public PooledArrayBufferWriter(int initialCapacity = 4096)
		{
			_buffer = Pool.Rent(initialCapacity);
		}

		public int WrittenLength
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get => _written;
		}

		public ReadOnlyMemory<byte> WrittenMemory
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get => new(_buffer, 0, _written);
		}

		public ReadOnlySpan<byte> WrittenSpan
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get => new(_buffer, 0, _written);
		}

		/// <summary>Resets the writer for reuse without returning the backing array.</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Clear() => _written = 0;

		public void Advance(int count)
		{
			if (count < 0 || _written + count > _buffer.Length)
				throw new ArgumentOutOfRangeException(nameof(count));

			_written += count;
		}

		public Span<byte> GetSpan(int sizeHint = 0)
		{
			EnsureFreeCapacity(sizeHint);
			return _buffer.AsSpan(_written);
		}

		public Memory<byte> GetMemory(int sizeHint = 0)
		{
			EnsureFreeCapacity(sizeHint);
			return _buffer.AsMemory(_written);
		}

		public void Dispose()
		{
			if (_buffer == null)
				return;

			Pool.Return(_buffer);
			_buffer = null;
		}

		private void EnsureFreeCapacity(int sizeHint)
		{
			if (sizeHint < 1)
				sizeHint = DefaultChunkSize;

			var required = _written + sizeHint;

			if (required <= _buffer.Length)
				return;

			var newCapacity = _buffer.Length * 2;

			if (newCapacity < required)
				newCapacity = required;

			var next = Pool.Rent(newCapacity);
			Buffer.BlockCopy(_buffer, 0, next, 0, _written);
			Pool.Return(_buffer);
			_buffer = next;
		}
	}
}
