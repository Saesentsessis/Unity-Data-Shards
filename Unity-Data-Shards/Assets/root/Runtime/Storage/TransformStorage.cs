using System;
using System.Threading;
using Persistence.Buffers;
using Persistence.Core;
using Unity.Collections;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using BoolTask = Cysharp.Threading.Tasks.UniTask<bool>;
using StorageReadTask = Cysharp.Threading.Tasks.UniTask<Persistence.Core.StorageReadResult>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using StorageReadTask = System.Threading.Tasks.Task<Persistence.Core.StorageReadResult>;
#endif

namespace Persistence.Storage
{
	/// <summary>
	/// <see cref="IStorage"/> decorator applying an <see cref="ISaveTransform"/> chain
	/// (compression, encryption, ...) around any inner storage: Apply in declaration
	/// order on write, Reverse in reverse order on read. Transforms compose with every
	/// backend automatically — SaveManager and layouts are untouched.
	/// </summary>
	/// <remarks>
	/// The two internal arenas are reused across calls, so one write/read may be in
	/// flight at a time per instance. Not thread-safe by design.
	/// </remarks>
	public sealed class TransformStorage : IStorage, IDisposable
	{
		private readonly IStorage _inner;
		private readonly ISaveTransform[] _transforms;

		// Ping-pong arenas: step N reads the previous step's buffer while writing the other.
		private NativeListBufferWriter _front;
		private NativeListBufferWriter _back;

		public TransformStorage(IStorage inner, params ISaveTransform[] transforms)
		{
			_inner = inner ?? throw new ArgumentNullException(nameof(inner));
			_transforms = transforms ?? Array.Empty<ISaveTransform>();
		}

		public async StorageReadTask TryReadAsync(string key, Allocator allocator, CancellationToken cancellation = default)
		{
			if (_transforms.Length == 0)
				return await _inner.TryReadAsync(key, allocator, cancellation);

			var inner = await _inner.TryReadAsync(key, Allocator.Persistent, cancellation);

			if (!inner.Found)
				return StorageReadResult.NotFound;

			try
			{
				return new StorageReadResult(ReverseChain(inner.Data, allocator));
			}
			finally
			{
				inner.Data.Dispose();
			}
		}

		public async TaskType WriteAsync(string key, NativeArray<byte> data, CancellationToken cancellation = default)
		{
			if (_transforms.Length == 0)
			{
				await _inner.WriteAsync(key, data, cancellation);
				return;
			}

			// The arena backing `transformed` is an instance field, so it satisfies the
			// IStorage lifetime contract: it stays valid until the inner write completes.
			var transformed = ApplyChain(data);
			await _inner.WriteAsync(key, transformed, cancellation);
		}

		public BoolTask ExistsAsync(string key, CancellationToken cancellation = default)
			=> _inner.ExistsAsync(key, cancellation);

		public TaskType DeleteAsync(string key, CancellationToken cancellation = default)
			=> _inner.DeleteAsync(key, cancellation);

		public void Dispose()
		{
			_front?.Dispose();
			_back?.Dispose();
			_front = null;
			_back = null;

			if (_inner is IDisposable disposable)
				disposable.Dispose();
		}

		// Span locals are forbidden in async methods; the chains run in these sync helpers.
		private NativeArray<byte> ApplyChain(NativeArray<byte> data)
		{
			var src = data.AsReadOnlySpan();
			NativeListBufferWriter dst = null;

			for (var i = 0; i < _transforms.Length; i++)
			{
				dst = Alternate(dst, src.Length);
				dst.Clear();
				_transforms[i].Apply(src, dst);
				src = dst.AsArray().AsReadOnlySpan();
			}

			return dst.AsArray();
		}

		private NativeArray<byte> ReverseChain(NativeArray<byte> data, Allocator allocator)
		{
			var src = data.AsReadOnlySpan();
			NativeListBufferWriter dst = null;

			for (var i = _transforms.Length - 1; i >= 0; i--)
			{
				dst = Alternate(dst, src.Length);
				dst.Clear();
				_transforms[i].Reverse(src, dst);
				src = dst.AsArray().AsReadOnlySpan();
			}

			// Hand the caller its own buffer in the requested allocator.
			var result = new NativeArray<byte>(src.Length, allocator, NativeArrayOptions.UninitializedMemory);
			src.CopyTo(result.AsSpan());
			return result;
		}

		private NativeListBufferWriter Alternate(NativeListBufferWriter previous, int capacityHint)
		{
			if (previous == null)
				return _front ??= new NativeListBufferWriter(Math.Max(capacityHint, 4096), Allocator.Persistent);

			if (ReferenceEquals(previous, _front))
				return _back ??= new NativeListBufferWriter(Math.Max(capacityHint, 4096), Allocator.Persistent);

			return _front;
		}
	}
}
