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
	/// Packs the envelope, blob ranges and the payload arena into ONE storage key:
	/// <code>[envelope][rangeCount:4][ranges: 24B each][payloadLen:4][payload]</code>
	/// The write is a straight gather of already-contiguous buffers — no per-shard
	/// work. The xxHash3 checksum is patched over the full assembled buffer before
	/// the storage write and verified before anything is parsed on read.
	/// </summary>
	public sealed class SingleFileSaveLayout : ISaveLayout
	{
		// guid(16) + offset(4) + length(4)
		private const int RangeSize = 24;

		private readonly IStorage _storage;

		public SingleFileSaveLayout(IStorage storage)
		{
			_storage = storage ?? throw new ArgumentNullException(nameof(storage));
		}

		// Single-file packing rewrites the whole payload, so every shard is needed.
		public bool RequiresFullSnapshot => true;

		public async TaskType WriteAsync(string slot, SaveEnvelope envelope, NativeArray<byte> payload,
			NativeArray<ShardBlobRange> ranges, CancellationToken cancellation = default)
		{
			// Envelope strings are small; payload dominates — size the arena accordingly.
			var arena = new NativeListBufferWriter(payload.Length + ranges.Length * RangeSize + 1024, Allocator.Persistent);

			try
			{
				Pack(envelope, payload, ranges, arena);
				await _storage.WriteAsync(slot, arena.AsArray(), cancellation);
			}
			finally
			{
				arena.Dispose();
			}
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

		private static void Pack(in SaveEnvelope envelope, NativeArray<byte> payload,
			NativeArray<ShardBlobRange> ranges, NativeListBufferWriter writer)
		{
			EnvelopeCodec.Write(envelope, writer);

			var rangeBytes = 4 + ranges.Length * RangeSize;
			var span = writer.GetSpan(rangeBytes);
			BinaryPrimitives.WriteInt32LittleEndian(span, ranges.Length);
			var offset = 4;

			for (var i = 0; i < ranges.Length; i++)
			{
				var range = ranges[i];
				BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], range.Id.Head);
				BinaryPrimitives.WriteUInt64LittleEndian(span[(offset + 8)..], range.Id.Tail);
				BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 16)..], range.Offset);
				BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 20)..], range.Length);
				offset += RangeSize;
			}

			writer.Advance(rangeBytes);

			var payloadSpan = writer.GetSpan(4 + payload.Length);
			BinaryPrimitives.WriteInt32LittleEndian(payloadSpan, payload.Length);
			payload.AsReadOnlySpan().CopyTo(payloadSpan[4..]);
			writer.Advance(4 + payload.Length);

			// A8: hash covers everything past the checksum field — envelope body,
			// ranges and payload alike.
			EnvelopeCodec.PatchChecksum(writer.AsArray().AsSpan());
		}

		private static SaveLayoutResult Unpack(NativeArray<byte> data, Allocator allocator)
		{
			var span = data.AsReadOnlySpan();

			// Checksum first: the primary corruption gate, before any parsing.
			EnvelopeCodec.ValidateChecksum(span);

			var envelope = EnvelopeCodec.Read(span, out var offset);

			var rangeCount = ReadInt(span, ref offset);

			if (rangeCount != envelope.RecordCount)
				throw new SaveCorruptedException($"Range count {rangeCount} does not match record count {envelope.RecordCount}.");

			if (span.Length - offset < rangeCount * RangeSize)
				throw new SaveCorruptedException($"Save truncated: {rangeCount} ranges need {rangeCount * RangeSize} bytes, {span.Length - offset} remain.");

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
					throw new SaveCorruptedException($"Save truncated: payload of {payloadLength} bytes declared, {span.Length - offset} remain.");

				// Every range must land inside the payload.
				for (var i = 0; i < rangeCount; i++)
				{
					var range = ranges[i];

					if (range.Offset < 0 || range.Length < 0 || (long)range.Offset + range.Length > payloadLength)
						throw new SaveCorruptedException($"Blob range {i} [{range.Offset}, {range.Offset + range.Length}) exceeds payload of {payloadLength} bytes.");
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
				throw new SaveCorruptedException($"Save truncated at offset {offset}.");

			var value = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
			offset += 4;
			return value;
		}
	}
}
