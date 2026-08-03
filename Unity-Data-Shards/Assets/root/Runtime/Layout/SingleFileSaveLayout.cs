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
using Unity.Collections.LowLevel.Unsafe;

namespace Saesentsessis.Persistence.Layout
{
	/// <summary>
	/// Packs the envelope, blob ranges and the payload arena into ONE storage key:
	/// <code>[envelope][rangeCount:4][ranges: 24B each][payloadLen:4][payload]</code>
	/// The write is a straight gather of already-contiguous buffers — no per-shard
	/// work. The xxHash3 checksum is patched over the full assembled buffer before
	/// the storage write and verified before anything is parsed on read.
	/// </summary>
	/// <remarks>
	/// <b>No payload copy.</b> Everything this layout writes precedes the shard data and all of it
	/// is known before serialization starts, so it reserves the header through
	/// <see cref="ISaveLayout.HeaderReservation"/> and the shards are serialized directly behind it.
	/// The arena it receives is already the file, byte for byte, and goes to storage unchanged —
	/// one buffer, no second arena, peak memory equal to the save. That the envelope must precede
	/// the payload turned out not to force a copy at all; it only forces the size to be known
	/// exactly, which <see cref="EnvelopeCodec.ExactEncodedSize"/> supplies.
	/// </remarks>
	public sealed class SingleFileSaveLayout : ISaveLayout, ISlotKeyMapper
	{
		// guid(16) + offset(4) + length(4)
		private const int RangeSize = 24;

		private readonly IStorage _storage;

		// One per layout, re-pointed at each save's reserved header. Never per-save garbage.
		private readonly FixedBufferWriter _headerWriter = new();

		public SingleFileSaveLayout(IStorage storage)
		{
			_storage = storage ?? throw new ArgumentNullException(nameof(storage));
		}

		// Single-file packing rewrites the whole payload, so every shard is needed.
		public bool RequiresFullSnapshot => true;

		/// <inheritdoc />
		/// <remarks>
		/// Everything this layout puts in the file precedes the payload, and all of it is known
		/// before a shard is serialized — the envelope is built by <c>SaveManager</c> first, and the
		/// range block is a multiplication. So the whole header is reserved up front and the shards
		/// land behind it.
		/// </remarks>
		public int HeaderReservation(in SaveEnvelope envelope, int blobCount)
			=> EnvelopeCodec.ExactEncodedSize(envelope) + 4 + blobCount * RangeSize + 4;

		/// <inheritdoc />
		/// <remarks>
		/// The arena arrives with the header space already reserved, so this fills it in place and
		/// hands the same memory to storage. There is no second buffer and no payload copy: the
		/// arena <i>is</i> the file.
		/// </remarks>
		public TaskType WriteAsync(string slot, SaveEnvelope envelope, NativeArray<byte> payload,
			NativeArray<ShardBlobRange> ranges, CancellationToken cancellation = default)
		{
			PackHeader(envelope, payload, ranges);

			return _storage.WriteAsync(slot, payload, cancellation);
		}

		public async SaveLayoutTask ReadAsync(string slot, Allocator allocator, CancellationToken cancellation = default)
		{
			var read = await _storage.TryReadAsync(slot, Allocator.Persistent, cancellation);

			if (!read.Found)
				throw new InvalidOperationException($"No save found for slot '{slot}'.");

			try
			{
				return Unpack(read.Data, allocator);
			}
			finally
			{
				read.Data.Dispose();
			}
		}

		public BoolTask ExistsAsync(string slot, CancellationToken cancellation = default)
			=> _storage.ExistsAsync(slot, cancellation);

		public TaskType DeleteAsync(string slot, CancellationToken cancellation = default)
			=> _storage.DeleteAsync(slot, cancellation);

		/// <inheritdoc />
		/// <remarks>
		/// A slot is exactly one key here, so the key <i>is</i> the slot and every key holds an
		/// envelope — the lengths always match.
		/// </remarks>
		public bool TryGetSlot(ReadOnlySpan<char> storageKey, out ReadOnlySpan<char> slot)
		{
			slot = storageKey;

			return storageKey.IsEmpty == false;
		}

		/// <summary>
		/// Fills the reserved head of <paramref name="arena"/> with envelope, ranges and payload
		/// length, then checksums the whole buffer.
		/// </summary>
		/// <remarks>
		/// Synchronous because spans are forbidden in async methods, and separated from the write so
		/// the reservation arithmetic sits next to the code that consumes it.
		/// </remarks>
		private unsafe void PackHeader(in SaveEnvelope envelope, NativeArray<byte> arena,
			NativeArray<ShardBlobRange> ranges)
		{
			var header = HeaderReservation(envelope, ranges.Length);

			if (arena.Length < header)
				throw new InvalidOperationException(
					$"[SingleFileSaveLayout] Arena is {arena.Length} bytes, shorter than the {header}-byte header " +
					"it reserved. The pipeline did not honour HeaderReservation.");

			// One reusable writer, re-pointed per save; it throws rather than growing, so a
			// disagreement with ExactEncodedSize surfaces here instead of eating payload bytes.
			_headerWriter.Reset((byte*)arena.GetUnsafePtr(), header);

			EnvelopeCodec.Write(envelope, _headerWriter);

			var rangeBytes = 4 + ranges.Length * RangeSize;
			var span = _headerWriter.GetSpan(rangeBytes);
			BinaryPrimitives.WriteInt32LittleEndian(span, ranges.Length);
			var offset = 4;

			foreach (var range in ranges)
			{
				BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], range.Id.Head);
				BinaryPrimitives.WriteUInt64LittleEndian(span[(offset + 8)..], range.Id.Tail);

				// Arena offsets are absolute and include the header; the file's are payload-relative.
				BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 16)..], range.Offset - header);
				BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 20)..], range.Length);
				offset += RangeSize;
			}

			_headerWriter.Advance(rangeBytes);

			var lengthSpan = _headerWriter.GetSpan(4);
			BinaryPrimitives.WriteInt32LittleEndian(lengthSpan, arena.Length - header);
			_headerWriter.Advance(4);

			// Hash covers everything past the checksum field — envelope body, ranges and payload.
			EnvelopeCodec.PatchChecksum(arena.AsSpan());
		}

		private static SaveLayoutResult Unpack(NativeArray<byte> data, Allocator allocator)
		{
			var span = data.AsReadOnlySpan();

			// Checksum first: the primary corruption gate, before any parsing.
			EnvelopeCodec.ValidateChecksum(span);

			var envelope = EnvelopeCodec.Read(span, out var offset);

			var rangeCount = ReadInt(span, ref offset);

			if (rangeCount != envelope.RecordCount)
				throw new SaveCorruptedException($"Range count {rangeCount} does not match record count {envelope.RecordCount}.",
					SaveCorruptedExceptionReason.RecordCountOverflow);

			if (span.Length - offset < rangeCount * RangeSize)
				throw new SaveCorruptedException($"Save truncated: {rangeCount} ranges need {rangeCount * RangeSize} bytes, {span.Length - offset} remain.",
					SaveCorruptedExceptionReason.TruncatedFile);

			var ranges = new NativeArray<ShardBlobRange>(rangeCount, allocator, NativeArrayOptions.UninitializedMemory);

			// Single cleanup point: any validation failure below must release `ranges`.
			try
			{
				for (var i = 0; i < rangeCount; i++)
				{
					var head = BinaryPrimitives.ReadUInt64LittleEndian(span[offset..]);
					var tail = BinaryPrimitives.ReadUInt64LittleEndian(span[(offset + 8)..]);
					ranges[i] = new ShardBlobRange(
						new SerializableGuid(head, tail),
						BinaryPrimitives.ReadInt32LittleEndian(span[(offset + 16)..]),
						BinaryPrimitives.ReadInt32LittleEndian(span[(offset + 20)..]));
					offset += RangeSize;
				}

				var payloadLength = ReadInt(span, ref offset);

				if (payloadLength < 0 || span.Length - offset < payloadLength)
					throw new SaveCorruptedException($"Save truncated: payload of {payloadLength} bytes declared, {span.Length - offset} remain.",
						SaveCorruptedExceptionReason.TruncatedFile);

				// Every range must land inside the payload.
				for (var i = 0; i < rangeCount; i++)
				{
					var range = ranges[i];

					if (range.Offset < 0 || range.Length < 0 || (long)range.Offset + range.Length > payloadLength)
						throw new SaveCorruptedException($"Blob range {i} [{range.Offset}, {range.Offset + range.Length}) exceeds payload of {payloadLength} bytes.",
							SaveCorruptedExceptionReason.TruncatedFile);
				}

				var payload = new NativeArray<byte>(payloadLength, allocator, NativeArrayOptions.UninitializedMemory);
				span.Slice(offset, payloadLength).CopyTo(payload.AsSpan());

				return new SaveLayoutResult(envelope, payload, ranges);
			}
			catch
			{
				ranges.Dispose();
				throw;
			}
		}

		private static int ReadInt(ReadOnlySpan<byte> data, ref int offset)
		{
			if (data.Length - offset < 4)
				throw new SaveCorruptedException($"Save truncated at offset {offset}.",
					SaveCorruptedExceptionReason.TruncatedFile);

			var value = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
			offset += 4;
			return value;
		}

		public void Dispose()
		{
			_storage?.Dispose();
		}
	}
}
