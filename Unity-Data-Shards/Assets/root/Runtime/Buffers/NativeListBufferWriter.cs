using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Persistence.Buffers
{
	/// <summary>
	/// <see cref="IBufferWriter{T}"/> over a <see cref="NativeList{T}"/> — the save
	/// pipeline's arena. Serializers append through the standard writer contract while
	/// the bytes land directly in unmanaged memory; the pipeline reads blob offsets
	/// from <see cref="WrittenLength"/> deltas and hands <see cref="AsArray"/> to the
	/// layout without a copy.
	/// </summary>
	/// <remarks>
	/// Standard <see cref="IBufferWriter{T}"/> rules apply: spans/memory obtained from
	/// <see cref="GetSpan"/>/<see cref="GetMemory"/> are invalidated by <see cref="Advance"/>
	/// or a subsequent Get call (the arena may reallocate on growth). Not thread-safe.
	/// </remarks>
	public sealed unsafe class NativeListBufferWriter : IArenaWriter, IDisposable
	{
		private const int DefaultChunkSize = 256;

		private NativeList<byte> _list;
		private UnmanagedMemoryManager _memoryManager;

		public NativeListBufferWriter(int initialCapacity, Allocator allocator)
		{
			_list = new NativeList<byte>(initialCapacity, allocator);
		}

		/// <summary>Total bytes committed so far. Blob ranges are computed from before/after deltas.</summary>
		public int WrittenLength
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get => _list.Length;
		}

		/// <summary>No-copy view over the committed bytes, valid until the next write or Dispose.</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public NativeArray<byte> AsArray() => _list.AsArray();

		/// <summary>Resets the writer for reuse without releasing capacity.</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Clear() => _list.Clear();

		public void Advance(int count)
		{
			if (count < 0 || _list.Length + count > _list.Capacity)
				throw new ArgumentOutOfRangeException(nameof(count));

			// Bytes were already written through the span; just commit the length.
			_list.ResizeUninitialized(_list.Length + count);
		}

		public Span<byte> GetSpan(int sizeHint = 0)
		{
			EnsureFreeCapacity(sizeHint);
			return new Span<byte>((byte*)_list.GetUnsafePtr() + _list.Length, _list.Capacity - _list.Length);
		}

		public Memory<byte> GetMemory(int sizeHint = 0)
		{
			EnsureFreeCapacity(sizeHint);

			// Memory<byte> cannot wrap a raw pointer directly; a reusable MemoryManager
			// re-pointed at the current tail chunk bridges the gap for serializers that
			// prefer GetMemory (e.g. MessagePack).
			_memoryManager ??= new UnmanagedMemoryManager();
			_memoryManager.Reset((byte*)_list.GetUnsafePtr() + _list.Length, _list.Capacity - _list.Length);
			return _memoryManager.Memory;
		}

		public void Dispose()
		{
			if (_list.IsCreated)
				_list.Dispose();

			_memoryManager = null;
		}

		private void EnsureFreeCapacity(int sizeHint)
		{
			if (sizeHint < 1)
				sizeHint = DefaultChunkSize;

			var required = _list.Length + sizeHint;

			if (required <= _list.Capacity)
				return;

			var newCapacity = _list.Capacity * 2;

			if (newCapacity < required)
				newCapacity = required;

			_list.SetCapacity(newCapacity);
		}

		private sealed class UnmanagedMemoryManager : MemoryManager<byte>
		{
			private byte* _ptr;
			private int _length;

			public void Reset(byte* ptr, int length)
			{
				_ptr = ptr;
				_length = length;
			}

			public override Span<byte> GetSpan() => new(_ptr, _length);

			public override MemoryHandle Pin(int elementIndex = 0) => new(_ptr + elementIndex);

			public override void Unpin() { }

			protected override void Dispose(bool disposing) { }
		}
	}
}
