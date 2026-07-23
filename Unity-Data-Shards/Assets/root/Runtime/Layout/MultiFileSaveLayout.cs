using System;
using System.Buffers.Binary;
using System.Threading;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using BoolTask = Cysharp.Threading.Tasks.UniTask<bool>;
using SaveLayoutTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.Layout.SaveLayoutResult>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using SaveLayoutTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Layout.SaveLayoutResult>;
#endif
using Saesentsessis.Persistence.Buffers;
using Saesentsessis.Persistence.Core;
using Unity.Collections;

namespace Saesentsessis.Persistence.Layout
{
	/// <summary>
	/// Incremental layout: one envelope file per slot (key = <c>slot</c>) plus one raw
	/// file per shard (key = <c>slot/&lt;32-char-guid-hex&gt;</c>). Only dirty shards'
	/// files are rewritten on save; the envelope is written LAST and acts as the commit
	/// point. Each shard file is framed as <c>[xxHash3-64:8][blob bytes]</c> so per-file
	/// corruption throws <see cref="SaveCorruptedException"/>, mirroring the envelope's
	/// own checksum gate.
	/// </summary>
	/// <remarks>
	/// Cross-file atomicity is inherently weaker than <see cref="SingleFileSaveLayout"/>:
	/// each file write is atomic (storage-level), but a crash mid-save can leave old
	/// envelope + partially updated shard files — a torn state across shards. Shards are
	/// independent by design, so this is the accepted trade for incremental writes.
	/// Shard files orphaned by membership shrink are cleaned up on <see cref="DeleteAsync"/>.
	/// </remarks>
	public sealed class MultiFileSaveLayout : ISaveLayout
	{
		private const int HashPrefixSize = 8;

		private readonly IStorage _storage;

		public MultiFileSaveLayout(IStorage storage)
		{
			_storage = storage ?? throw new ArgumentNullException(nameof(storage));
		}

		// Incremental by design: SaveManager passes only dirty blobs.
		public bool RequiresFullSnapshot => false;

		public async TaskType WriteAsync(string slot, SaveEnvelope envelope, NativeArray<byte> payload,
			NativeArray<ShardBlobRange> ranges, CancellationToken cancellation = default)
		{
			var maxBlobLength = 0;

			for (var i = 0; i < ranges.Length; i++)
				if (ranges[i].Length > maxBlobLength)
					maxBlobLength = ranges[i].Length;

			// One scratch arena reused for every shard file and the envelope; each write
			// is awaited before the next mutation, satisfying the IStorage lifetime rule.
			var scratch = new NativeListBufferWriter(HashPrefixSize + Math.Max(maxBlobLength, 256), Allocator.Persistent);

			try
			{
				// Shard files first — the envelope below is the commit point.
				for (var i = 0; i < ranges.Length; i++)
				{
					var range = ranges[i];
					FrameShardFile(payload, range, scratch);
					await _storage.WriteAsync(BuildShardKey(slot, range.Id), scratch.AsArray(), cancellation);
				}

				EncodeEnvelope(envelope, scratch);
				await _storage.WriteAsync(slot, scratch.AsArray(), cancellation);
			}
			finally
			{
				scratch.Dispose();
			}
		}

		public async SaveLayoutTask ReadAsync(string slot, Allocator allocator, CancellationToken cancellation = default)
		{
			var envelopeRead = await _storage.TryReadAsync(slot, Allocator.Persistent, cancellation);

			if (!envelopeRead.Found)
				throw new InvalidOperationException($"No save found for slot '{slot}'.");

			SaveEnvelope envelope;

			try
			{
				envelope = DecodeEnvelope(envelopeRead.Data);
			}
			finally
			{
				envelopeRead.Data.Dispose();
			}

			// Per-shard file sizes are unknown until read, so blobs are collected first
			// and gathered into the contiguous payload arena afterwards — one memcpy per
			// shard on load; saves stay zero-copy.
			var count = envelope.RecordCount;
			var files = new NativeArray<byte>[count];

			try
			{
				long totalLength = 0;

				for (var i = 0; i < count; i++)
				{
					var record = envelope.Records[i];
					var read = await _storage.TryReadAsync(BuildShardKey(slot, record.Id), Allocator.Persistent, cancellation);

					if (!read.Found)
						throw new SaveCorruptedException($"Shard file missing for record {i} ({record.Id}) in slot '{slot}'.");

					files[i] = read.Data;

					if (read.Data.Length < HashPrefixSize)
						throw new SaveCorruptedException($"Shard file for record {i} is {read.Data.Length} bytes — smaller than its {HashPrefixSize}-byte checksum prefix.");

					totalLength += read.Data.Length - HashPrefixSize;
				}

				if (totalLength > int.MaxValue)
					throw new SaveCorruptedException($"Combined shard payload of {totalLength} bytes exceeds the 2 GB arena limit.");

				return Assemble(envelope, files, (int)totalLength, allocator);
			}
			finally
			{
				for (var i = 0; i < count; i++)
					if (files[i].IsCreated)
						files[i].Dispose();
			}
		}

		public BoolTask ExistsAsync(string slot, CancellationToken cancellation = default)
			=> _storage.ExistsAsync(slot, cancellation);

		public async TaskType DeleteAsync(string slot, CancellationToken cancellation = default)
		{
			var envelopeRead = await _storage.TryReadAsync(slot, Allocator.Persistent, cancellation);

			if (!envelopeRead.Found)
				return;

			var envelope = default(SaveEnvelope);
			var envelopeReadable = true;

			try
			{
				envelope = DecodeEnvelope(envelopeRead.Data);
			}
			catch (SaveCorruptedException)
			{
				// Best effort: a corrupted envelope can't enumerate its shard files,
				// but the slot itself must still be deletable.
				envelopeReadable = false;
			}
			finally
			{
				envelopeRead.Data.Dispose();
			}

			if (envelopeReadable)
				for (var i = 0; i < envelope.RecordCount; i++)
					await _storage.DeleteAsync(BuildShardKey(slot, envelope.Records[i].Id), cancellation);

			await _storage.DeleteAsync(slot, cancellation);
		}

		// Spans are forbidden in async methods; all buffer work lives in sync helpers.

		private static void FrameShardFile(NativeArray<byte> payload, in ShardBlobRange range, NativeListBufferWriter scratch)
		{
			var blob = payload.AsReadOnlySpan().Slice(range.Offset, range.Length);

			scratch.Clear();
			var span = scratch.GetSpan(HashPrefixSize + blob.Length);
			BinaryPrimitives.WriteUInt64LittleEndian(span, Hash(blob));
			blob.CopyTo(span[HashPrefixSize..]);
			scratch.Advance(HashPrefixSize + blob.Length);
		}

		private static void EncodeEnvelope(in SaveEnvelope envelope, NativeListBufferWriter scratch)
		{
			scratch.Clear();
			EnvelopeCodec.Write(envelope, scratch);
			EnvelopeCodec.PatchChecksum(scratch.AsArray().AsSpan());
		}

		private static SaveEnvelope DecodeEnvelope(NativeArray<byte> data)
		{
			var span = data.AsReadOnlySpan();

			EnvelopeCodec.ValidateChecksum(span);
			var envelope = EnvelopeCodec.Read(span, out var consumed);

			if (consumed != span.Length)
				throw new SaveCorruptedException($"Envelope file has {span.Length - consumed} unexpected trailing bytes.");

			return envelope;
		}

		private static SaveLayoutResult Assemble(in SaveEnvelope envelope, NativeArray<byte>[] files, int totalLength, Allocator allocator)
		{
			var count = envelope.RecordCount;
			var payload = new NativeArray<byte>(totalLength, allocator, NativeArrayOptions.UninitializedMemory);
			var ranges = new NativeArray<ShardBlobRange>(count, allocator, NativeArrayOptions.UninitializedMemory);

			try
			{
				var offset = 0;

				for (var i = 0; i < count; i++)
				{
					var file = files[i].AsReadOnlySpan();
					var storedHash = BinaryPrimitives.ReadUInt64LittleEndian(file);
					var blob = file[HashPrefixSize..];

					if (storedHash != Hash(blob))
						throw new SaveCorruptedException($"Shard file checksum mismatch for record {i} ({envelope.Records[i].Id}).");

					blob.CopyTo(payload.AsSpan().Slice(offset, blob.Length));
					ranges[i] = new ShardBlobRange(envelope.Records[i].Id, offset, blob.Length);
					offset += blob.Length;
				}

				return new SaveLayoutResult(envelope, payload, ranges);
			}
			catch
			{
				payload.Dispose();
				ranges.Dispose();
				throw;
			}
		}

		/// <summary>
		/// Builds <c>slot/&lt;32-char-guid-hex&gt;</c>. Always a FRESH string instance:
		/// storages cache keys in dictionaries, and mutating a string that already sits
		/// in a dictionary poisons its hash bucket. Mutation happens only before the key
		/// escapes this method.
		/// </summary>
		private static string BuildShardKey(string slot, in SerializableGuid id)
		{
			var key = new string(' ', slot.Length + 33);

			UnsafeStringUtils.Write(key, slot);
			UnsafeStringUtils.Write(key, '/', slot.Length);
			UnsafeStringUtils.Write(key, id, slot.Length + 1);

			return key;
		}

		private static unsafe ulong Hash(ReadOnlySpan<byte> data)
		{
			// `fixed` yields a null pointer for empty spans; hash a valid dummy address
			// with length 0 to get the canonical empty-input hash instead.
			if (data.IsEmpty)
			{
				byte zero = 0;
				var empty = xxHash3.Hash64(&zero, 0);
				return ((ulong)empty.y << 32) | empty.x;
			}

			fixed (byte* ptr = data)
			{
				var hash = xxHash3.Hash64(ptr, data.Length);
				return ((ulong)hash.y << 32) | hash.x;
			}
		}
	}
}
