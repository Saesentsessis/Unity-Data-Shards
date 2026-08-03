using System;
using System.Collections.Generic;
using System.Threading;
using Saesentsessis.Persistence.Buffers;
using Saesentsessis.Persistence.Core;
using Unity.Collections;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using BoolTask = Cysharp.Threading.Tasks.UniTask<bool>;
using IntTask = Cysharp.Threading.Tasks.UniTask<int>;
using StorageReadTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.Core.StorageReadResult>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using IntTask = System.Threading.Tasks.Task<int>;
using StorageReadTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Core.StorageReadResult>;
#endif

namespace Saesentsessis.Persistence.Storage
{
	/// <summary>
	/// <see cref="IStorage"/> decorator applying an <see cref="IStorageTransform"/> chain
	/// (compression, encryption, ...) around any inner storage: Apply in declaration
	/// order on write, Reverse in reverse order on read. Transforms compose with every
	/// backend automatically — SaveManager and layouts are untouched.
	/// </summary>
	/// <remarks>
	/// The two internal arenas are reused across calls, so one write/read may be in
	/// flight at a time per instance. Not thread-safe by design.
	/// </remarks>
	public sealed class TransformStorage : IStorage, IListableStorage
	{
		private readonly IStorage _inner;
		private readonly IStorageTransform[] _transforms;

		// Ping-pong arenas: step N reads the previous step's buffer while writing the other.
		private NativeListBufferWriter _frontBuffer;
		private NativeListBufferWriter _backBuffer;

		/// <summary>
		/// Wraps <paramref name="inner"/> in a transform chain that this storage owns.
		/// </summary>
		/// <remarks>
		/// <b>Each transform belongs to exactly one storage.</b> Handing the same instance to two
		/// chains is not supported: a transform carries per-operation scratch state — the cipher's
		/// IV and arena, this decorator's own ping-pong buffers — and two storages driving one
		/// instance would interleave through it. Disposing this storage disposes the chain with it.
		/// </remarks>
		public TransformStorage(IStorage inner, params IStorageTransform[] transforms)
		{
			transforms ??= Array.Empty<IStorageTransform>();

			try
			{
				if (inner == null)
					throw new ArgumentNullException(nameof(inner));

				// Runs once per storage, and turns a null element into a message that names the slot
				// instead of a NullReferenceException on the first save.
				for (var i = transforms.Length - 1; i >= 0; i--)
					if (transforms[i] == null)
						throw new ArgumentNullException(nameof(transforms),
							$"Transform at index {i} of {transforms.Length} is null.");
			}
			catch
			{
				// This storage owns everything it is handed and releases it in Dispose. A
				// constructor that throws never produces the object that would do the releasing,
				// and the call site usually holds no reference of its own — in
				// `new TransformStorage(inner, new XorTransform(key), null)` there is nowhere to
				// dispose that XorTransform from, and its native pattern buffer would leak. So a
				// failed construction releases exactly what a successful one would have.
				ReleaseChain(inner, transforms);

				throw;
			}

			_inner = inner;
			_transforms = transforms;
		}

		/// <summary>Disposes an owned chain. Tolerates nulls: it runs on the failure path.</summary>
		private static void ReleaseChain(IStorage inner, IStorageTransform[] transforms)
		{
			foreach (var transform in transforms)
				if (transform is IDisposable disposable)
					disposable.Dispose();

			inner?.Dispose();
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

		/// <inheritdoc />
		/// <remarks>
		/// A straight forward to the wrapped storage: the chain rewrites values, never keys, so a
		/// listing is identical either side of the decorator. Sizes therefore describe the bytes
		/// <i>at rest</i> — compressed and encrypted — which is what a browser wants to report
		/// anyway, since that is what the save occupies.
		/// </remarks>
		/// <exception cref="NotSupportedException">
		/// The wrapped storage cannot enumerate. Decorating something unlistable makes
		/// <c>is IListableStorage</c> true without making the call work, so this says so plainly
		/// rather than returning an empty listing that reads as "no saves".
		/// </exception>
		public IntTask PopulateAsync(IList<StorageKeyInfo> destination, CancellationToken cancellation = default)
		{
			if (_inner is IListableStorage listable)
				return listable.PopulateAsync(destination, cancellation);

			throw new NotSupportedException(
				$"[TransformStorage] The wrapped {_inner.GetType().Name} does not implement IListableStorage, " +
				"so this chain cannot enumerate its keys.");
		}

		public BoolTask ExistsAsync(string key, CancellationToken cancellation = default)
			=> _inner.ExistsAsync(key, cancellation);

		public TaskType DeleteAsync(string key, CancellationToken cancellation = default)
			=> _inner.DeleteAsync(key, cancellation);

		public void Dispose()
		{
			_frontBuffer?.Dispose();
			_backBuffer?.Dispose();
			_frontBuffer = null;
			_backBuffer = null;

			// The chain belongs to this storage, so it goes with it. Prohibiting a shared transform
			// is what makes ownership unambiguous — there is only ever one owner to dispose it.
			foreach (var transform in _transforms)
				if (transform is IDisposable disposable)
					disposable.Dispose();

			// Same reasoning one level down: the inner storage is wrapped, not shared, and the
			// package's layout -> storage disposal chain assumes each link releases the next.
			_inner.Dispose();
		}

		// Span locals are forbidden in async methods; the chains run in these sync helpers.
		private NativeArray<byte> ApplyChain(NativeArray<byte> data)
		{
			var src = data.AsReadOnlySpan();
			NativeListBufferWriter dst = null;

			foreach (var transform in _transforms)
			{
				dst = Alternate(dst, src.Length);
				dst.Clear();
				
				transform.Apply(src, dst);

				if (src.Length > 0 && dst.WrittenLength == 0)
					throw new InvalidOperationException(
						$"Transformation with {transform.GetType().Name} resulted in a buffer with 0 length.");
				
				src = dst.AsArray().AsReadOnlySpan();
			}

			return dst!.AsArray();
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
				return _frontBuffer ??= new NativeListBufferWriter(Math.Max(capacityHint, 4096), Allocator.Persistent);

			if (ReferenceEquals(previous, _frontBuffer))
				return _backBuffer ??= new NativeListBufferWriter(Math.Max(capacityHint, 4096), Allocator.Persistent);

			return _frontBuffer;
		}
	}
}
