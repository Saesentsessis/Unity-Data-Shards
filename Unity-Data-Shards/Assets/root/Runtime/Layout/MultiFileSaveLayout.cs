using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
using Saesentsessis.Persistence.Utils;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Pool;

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
	/// <para>
	/// Removing a shard from the store orphans its file. The layout remembers the membership it
	/// last saw on disk — from a read or a write — and deletes the difference after the next
	/// commit, so a load/remove/save round trip cleans up after itself. The deletion runs
	/// <i>after</i> the envelope is committed on purpose: a crash in between leaks files, whereas
	/// the other order would leave a live envelope pointing at files that no longer exist.
	/// </para>
	/// <para>
	/// Membership seen by neither a read nor a write in this session cannot be diffed — a slot
	/// written by an older build, for instance — so orphans from before are still swept only by
	/// <see cref="DeleteAsync"/>. Orphans cost disk space and nothing else: loads walk the
	/// envelope's record list, so a file nothing points at is invisible to every read path.
	/// </para>
	/// <para>
	/// The same membership answers <see cref="GetPersistedIds"/>, which is what keeps the deletion
	/// above from cutting the other way: a shard removed and later restored has no file left, and
	/// nothing about the shard itself says so.
	/// </para>
	/// </remarks>
	public sealed class MultiFileSaveLayout : ISaveLayout, IIncrementalSaveLayout, ISlotKeyMapper
	{
		private const int HashPrefixSize = 8;

		private readonly IStorage _storage;

		// Last membership this layout observed on disk per slot, so a shrink can delete the files
		// it orphaned without an extra read of the previous envelope on every save.
		private readonly Dictionary<string, SerializableGuid[]> _membership = new();

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
			// Sized for whichever of the two is larger — the envelope is written into this same
			// buffer, and for a slot with many small shards it is by far the bigger of them.
			var scratch = new NativeListBufferWriter(
				math.max(HashPrefixSize + maxBlobLength, math.max(EnvelopeCodec.MaxEncodedSize(envelope), 256)),
				Allocator.Persistent);

			try
			{
				// Shard files first — the envelope below is the commit point.
				foreach (var range in ranges)
				{
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

			// Strictly after the commit: leaking a file is recoverable, dangling a committed
			// envelope over a deleted shard is not.
			await DeleteOrphansAsync(slot, envelope, cancellation);
			RememberMembership(slot, envelope);
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
						throw new SaveCorruptedException($"Shard file missing for record {i} ({record.Id}) in slot '{slot}'.",
							SaveCorruptedExceptionReason.MissingFile);

					files[i] = read.Data;

					if (read.Data.Length < HashPrefixSize)
						throw new SaveCorruptedException($"Shard file for record {i} is {read.Data.Length} bytes — smaller than its {HashPrefixSize}-byte checksum prefix.",
							SaveCorruptedExceptionReason.EnvelopeTruncated);

					totalLength += read.Data.Length - HashPrefixSize;
				}

				if (totalLength > int.MaxValue)
					throw new SaveCorruptedException($"Combined shard payload of {totalLength} bytes exceeds the 2 GB arena limit.",
						SaveCorruptedExceptionReason.FileIsTooLarge);

				var result = Assemble(envelope, files, (int)totalLength, allocator);

				// A read establishes what is on disk just as well as a write does, and it is what
				// makes the common load -> remove a shard -> save sequence self-cleaning.
				RememberMembership(slot, envelope);

				return result;
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
				envelopeRead.Dispose();
			}

			if (envelopeReadable)
				for (var i = 0; i < envelope.RecordCount; i++)
					await _storage.DeleteAsync(BuildShardKey(slot, envelope.Records[i].Id), cancellation);

			await _storage.DeleteAsync(slot, cancellation);

			// Nothing on disk for this slot any more; a stale membership would make the next save
			// try to delete files that are already gone.
			_membership.Remove(slot);
		}

		/// <inheritdoc />
		/// <remarks>
		/// The membership array itself, in envelope-record order — no copy, no allocation. A slot
		/// this instance has neither read nor written yields an empty span, so the first save of a
		/// session writes every shard. That is the honest answer: this layout genuinely does not
		/// know what is on disk until it has looked, and guessing "everything is there" is what
		/// produces an envelope pointing at files that were never written.
		/// </remarks>
		public ReadOnlySpan<SerializableGuid> GetPersistedIds(string slot)
			=> _membership.GetValueOrDefault(slot);

		/// <summary>
		/// Deletes shard files that the previous membership had and the committed one does not.
		/// </summary>
		private async TaskType DeleteOrphansAsync(string slot, SaveEnvelope envelope, CancellationToken cancellation)
		{
			// Span locals are forbidden in async methods, so the comparison lives in the sync helper
			// below and only the resulting id list crosses back here. Null means nothing to do,
			// which is the overwhelmingly common case and rents nothing.
			var orphans = CollectOrphans(slot, envelope);

			if (orphans == null)
				return;

			try
			{
				foreach (var orphan in orphans)
					await _storage.DeleteAsync(BuildShardKey(slot, orphan), cancellation);
			}
			finally
			{
				ListPool<SerializableGuid>.Release(orphans);
			}
		}

		/// <summary>
		/// Ids present in the previously observed membership and absent from the committed one.
		/// Returns null when there is nothing to delete.
		/// </summary>
		private List<SerializableGuid> CollectOrphans(string slot, in SaveEnvelope envelope)
		{
			if (_membership.TryGetValue(slot, out var previous) == false || previous.Length == 0)
				return null;

			var records = envelope.Records;

			// Cheap gate first. Membership can change without the count changing — swap one shard
			// for another — so an equal count still needs the element check, but an unchanged save
			// is settled by a length test plus one ordered pass, renting nothing at all.
			if (previous.Length == records.Length && MembershipUnchanged(previous, records))
				return null;

			var survivors = HashSetPool<SerializableGuid>.Get();
			List<SerializableGuid> orphans = null;

			try
			{
				for (var i = 0; i < records.Length; i++)
					survivors.Add(records[i].Id);

				for (var i = 0; i < previous.Length; i++)
				{
					if (survivors.Contains(previous[i]))
						continue;

					orphans ??= ListPool<SerializableGuid>.Get();
					orphans.Add(previous[i]);
				}
			}
			finally
			{
				HashSetPool<SerializableGuid>.Release(survivors);
			}

			return orphans;
		}

		/// <summary>Ordered comparison — record order is stable for an unchanged store.</summary>
		private static bool MembershipUnchanged(SerializableGuid[] previous, ReadOnlySpan<ShardRecord> records)
		{
			for (var i = 0; i < records.Length; i++)
				if (previous[i].Equals(records[i].Id) == false)
					return false;

			return true;
		}

		private void RememberMembership(string slot, in SaveEnvelope envelope)
		{
			var records = envelope.Records;

			// Reuse the existing array whenever the count matches, so the steady state — saving the
			// same set over and over — allocates nothing here.
			if (_membership.TryGetValue(slot, out var stored) == false || stored.Length != records.Length)
			{
				stored = new SerializableGuid[records.Length];
				_membership[slot] = stored;
			}

			for (var i = 0; i < records.Length; i++)
				stored[i] = records[i].Id;
		}

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

			if (consumed == span.Length)
				return envelope;
			
			throw new SaveCorruptedException($"Envelope file has {span.Length - consumed} unexpected trailing bytes.",
					SaveCorruptedExceptionReason.EnvelopeIsTooLarge);
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
						throw new SaveCorruptedException($"Shard file checksum mismatch for record {i} ({envelope.Records[i].Id}).",
							SaveCorruptedExceptionReason.ChecksumMismatch);

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

		/// <inheritdoc />
		/// <remarks>
		/// Inverts <see cref="BuildShardKey"/>. A key with no <c>'/'</c> is the slot's envelope, so
		/// the slot spans the whole key; a shard key contributes only the part before the first
		/// separator. Both answers are slices of the caller's string, so grouping a listing costs no
		/// allocation at all.
		/// </remarks>
		public bool TryGetSlot(ReadOnlySpan<char> storageKey, out ReadOnlySpan<char> slot)
		{
			var separator = storageKey.IndexOf('/');

			// A leading separator leaves no slot name, and this layout never writes one.
			if (separator == 0 || storageKey.IsEmpty)
			{
				slot = default;
				return false;
			}

			slot = separator < 0 ? storageKey : storageKey[..separator];
			return true;
		}

		/// <summary>
		/// Builds <c>slot/&lt;32-char-guid-hex&gt;</c>. Always a fresh string instance.
		/// </summary>
		private static string BuildShardKey(string slot, in SerializableGuid id)
		{
			return string.Create(slot.Length + 33, (slot, id), static (span, state) =>
			{
				state.slot.AsSpan().CopyTo(span);
				var offset = state.slot.Length - 1;
				span[++offset] = '/';
				UnsafeStringUtils.Write(span, state.id, ++offset);
			});
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

		public void Dispose()
		{
			_membership.Clear();
			_storage?.Dispose();
		}
	}
}
