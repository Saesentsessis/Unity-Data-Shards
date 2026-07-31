using System;
using System.Collections.Generic;
using System.Threading;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Threading;
using Unity.Collections;
using UnityEngine.Pool;
#if PERSISTENCE_HAS_UNITASK
using IntTask = Cysharp.Threading.Tasks.UniTask<int>;
using SlotHeaderTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.SaveSlotHeader>;
#else
using IntTask = System.Threading.Tasks.Task<int>;
using SlotHeaderTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.SaveSlotHeader>;
#endif

namespace Saesentsessis.Persistence
{
	/// <summary>
	/// Lists the save slots a storage holds, and decodes their envelope headers on request.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Composes an <see cref="IListableStorage"/> (which keys exist) with an
	/// <see cref="ISlotKeyMapper"/> (which slot each key belongs to). Both layouts implement the
	/// mapper, so the usual construction is the same storage and layout the
	/// <see cref="SaveManager"/> was given.
	/// </para>
	/// <para>
	/// <b>Two phases, deliberately.</b> <see cref="PopulateAsync"/> reads nothing — sizes and write
	/// times come from the listing itself — while <see cref="ReadHeaderAsync"/> costs a full read of
	/// the slot's bytes. Populate a list first and decode headers only for what is actually looked
	/// at; a folder of two hundred saves would otherwise read two hundred files to draw one screen.
	/// </para>
	/// <para>
	/// Reading only the first 32 bytes is not an option and should not be attempted: a transform
	/// chain has to be reversed from the start — Deflate decodes sequentially, and the HMAC covers
	/// the whole file — so there is no such thing as a cheap prefix read through one.
	/// </para>
	/// <para>
	/// This type holds no state and owns nothing. It does not dispose the storage it was handed.
	/// </para>
	/// </remarks>
	public sealed class SaveSlotBrowser
	{
		private readonly IStorage _storage;
		private readonly ISlotKeyMapper _mapper;

		// Built once per browser rather than per call, so repeated refreshes allocate nothing here.
		private readonly SlotThenKeyComparer _comparer;

		/// <param name="storage">
		/// Where the keys live. Must also implement <see cref="IListableStorage"/> for
		/// <see cref="PopulateAsync"/>; header reads work with any storage.
		/// </param>
		/// <param name="mapper">
		/// Usually the layout in use — both <see cref="SingleFileSaveLayout"/> and
		/// <see cref="MultiFileSaveLayout"/> implement it.
		/// </param>
		/// <remarks>
		/// Needs no coordination with the <see cref="SaveManager"/> writing these slots, and takes
		/// none. Serialising a read against a concurrent write happens one layer down, inside the
		/// storage, keyed by the resource itself — so it holds even when the writer is a different
		/// storage instance, or another window entirely, which is the case a shared object could
		/// never have covered.
		/// </remarks>
		public SaveSlotBrowser(IStorage storage, ISlotKeyMapper mapper)
		{
			_storage = storage ?? throw new ArgumentNullException(nameof(storage));
			_mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
			_comparer = new SlotThenKeyComparer(_mapper);
		}

		/// <summary>
		/// Whether <see cref="PopulateAsync"/> is usable. False when the storage cannot enumerate —
		/// PlayerPrefs, or a custom backend that has not opted in.
		/// </summary>
		/// <remarks>
		/// A decorator reports true by forwarding, so this can still be true while the call fails on
		/// what it wraps. Treat it as "worth trying", not as a guarantee.
		/// </remarks>
		public bool CanList => _storage is IListableStorage;

		/// <summary>
		/// Appends one <see cref="SaveSlotInfo"/> per slot and returns how many were added.
		/// </summary>
		/// <param name="destination">Sink to append to. Not cleared, and reusable across refreshes.</param>
		/// <param name="cancellation">Forwarded to the storage's enumeration.</param>
		/// <exception cref="NotSupportedException">The storage cannot enumerate its keys.</exception>
		public async IntTask PopulateAsync(IList<SaveSlotInfo> destination, CancellationToken cancellation = default)
		{
			if (destination == null)
				throw new ArgumentNullException(nameof(destination));

			if (_storage is not IListableStorage listable)
				throw new NotSupportedException(
					$"[SaveSlotBrowser] {_storage.GetType().Name} does not implement IListableStorage, " +
					"so its slots cannot be listed. Check CanList first.");

			var keys = ListPool<StorageKeyInfo>.Get();

			try
			{
				await listable.PopulateAsync(keys, cancellation);

				// Span locals are forbidden in async methods, so the grouping — which slices slot
				// names straight out of the key strings — lives in the sync helper below.
				return Group(keys, _mapper, _comparer, destination);
			}
			finally
			{
				ListPool<StorageKeyInfo>.Release(keys);
			}
		}

		/// <summary>
		/// Collapses a flat key listing into slots.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Sorting by slot first makes this a single linear pass with no dictionary and no
		/// intermediate strings. Within a slot the slot's own key sorts first — it is a proper
		/// prefix of every other key the slot owns — so its string is reused verbatim as the slot
		/// name and a listing allocates no slot names at all in the normal case.
		/// </para>
		/// <para>
		/// A plain ordinal sort of whole keys is <b>not</b> good enough, which is worth stating
		/// because it looks like it should be. Every character below the separator collates between
		/// a slot and its own satellites: with <c>'/'</c> at 0x2F, keys sort
		/// <c>s</c> &lt; <c>s-</c> &lt; <c>s.</c> &lt; <c>s/0</c>, so a sibling slot named
		/// <c>s-</c> lands in the middle of slot <c>s</c> and splits it into two entries.
		/// Slot names containing <c>-</c>, <c>.</c> or a space are entirely ordinary, so the sort
		/// asks the mapper instead of assuming anything about how keys collate.
		/// </para>
		/// </remarks>
		private static int Group(List<StorageKeyInfo> keys, ISlotKeyMapper mapper,
			IComparer<StorageKeyInfo> comparer, IList<SaveSlotInfo> destination)
		{
			keys.Sort(comparer);

			var added = 0;
			var current = ReadOnlySpan<char>.Empty;

			string slotName = null;
			long totalBytes = 0;
			long modifiedTicks = 0;
			var keyCount = 0;

			for (var i = 0; i < keys.Count; i++)
			{
				var info = keys[i];

				if (info.Key == null)
					continue;

				var key = info.Key.AsSpan();

				// A key the layout does not recognise belongs to something else sharing this
				// storage; skip rather than invent a slot for it.
				if (mapper.TryGetSlot(key, out var slot) == false)
					continue;

				if (slotName == null || current.SequenceEqual(slot) == false)
				{
					if (slotName != null)
					{
						destination.Add(new SaveSlotInfo(slotName, totalBytes, modifiedTicks, keyCount));
						added++;
					}

					current = slot;

					// Equal lengths mean this key IS the slot's key, so its string already holds
					// exactly the slot name — no substring needed. The fallback only runs for a slot
					// whose own key is absent, which means orphaned satellites.
					slotName = slot.Length == info.Key.Length ? info.Key : slot.ToString();

					totalBytes = 0;
					modifiedTicks = 0;
					keyCount = 0;
				}

				totalBytes += info.Size;
				keyCount++;

				if (info.ModifiedUtcTicks > modifiedTicks)
					modifiedTicks = info.ModifiedUtcTicks;
			}

			if (slotName == null)
				return added;

			destination.Add(new SaveSlotInfo(slotName, totalBytes, modifiedTicks, keyCount));
			return added + 1;
		}

		/// <summary>
		/// Reads and decodes a slot's envelope header.
		/// </summary>
		/// <remarks>
		/// No layout is consulted: every layout writes the envelope at offset 0 of the slot's own
		/// key, so the header is found the same way for all of them. Single-file slots carry the
		/// blob index and payload after the envelope, which is why the decode ignores how many bytes
		/// it consumed. Reading through a <c>TransformStorage</c> reverses the chain on the way, so
		/// compressed and encrypted saves need no special handling here.
		/// </remarks>
		/// <returns>
		/// A header whose <see cref="SaveSlotHeader.Status"/> explains any failure. <b>Nothing but
		/// cancellation escapes this method</b> — a browser drawing a list of slots must survive one
		/// bad file, so every other failure becomes a status. That includes failures from below the
		/// decoder: an I/O error, a permission problem, a decryption chain rejecting the payload.
		/// </returns>
		public async SlotHeaderTask ReadHeaderAsync(string slot, CancellationToken cancellation = default)
		{
			if (string.IsNullOrEmpty(slot))
				throw new ArgumentNullException(nameof(slot));

			var read = default(StorageReadResult);

			try
			{
				// Persistent rather than Temp: the read spans awaits, and a Temp allocation is not
				// guaranteed to outlive the frame it was made on.
				read = await _storage.TryReadAsync(slot, Allocator.Persistent, cancellation);

				if (read.Found == false)
					return new SaveSlotHeader(SaveSlotStatus.Missing);

				return Decode(read.Data);
			}
			catch (OperationCanceledException)
			{
				// The only escape. A caller that cancelled is not asking about this slot any more,
				// and swallowing it would leave them awaiting a result they no longer want.
				throw;
			}
			catch (Exception)
			{
				// Deliberately broad. The alternative is enumerating every exception a storage, a
				// transform chain or a filesystem might raise, and being wrong about one of them in
				// front of a player looking at their save list.
				return new SaveSlotHeader(SaveSlotStatus.Unreadable);
			}
			finally
			{
				read.Dispose();
			}
		}

		/// <summary>Sync helper — <see cref="EnvelopeCodec"/> works in spans.</summary>
		private static SaveSlotHeader Decode(NativeArray<byte> data)
		{
			try
			{
				var span = data.AsReadOnlySpan();

				// Checksum before parsing, matching what the layouts do: a corrupt buffer must not
				// reach the decoder at all.
				EnvelopeCodec.ValidateChecksum(span);

				var envelope = EnvelopeCodec.Read(span, out _);

				return new SaveSlotHeader(SaveSlotStatus.Ok, envelope.FormatVersion, envelope.TimestampUtc,
					envelope.TypeCount, envelope.RecordCount, envelope.Checksum);
			}
			catch (SaveCorruptedException exception)
			{
				return new SaveSlotHeader(ToStatus(exception.Reason));
			}
		}

		/// <summary>
		/// Orders keys by slot, then by the key itself, so every key of a slot is contiguous no
		/// matter what characters neighbouring slot names contain.
		/// </summary>
		/// <remarks>
		/// The mapper is the only authority on where a slot name ends, so the sort consults it
		/// rather than assuming a separator or how it collates. That also keeps this correct for a
		/// custom layout that separates keys some other way. Two mapper calls per comparison is
		/// nothing against the directory walk or network round trip that produced the listing.
		/// </remarks>
		private sealed class SlotThenKeyComparer : IComparer<StorageKeyInfo>
		{
			private readonly ISlotKeyMapper _mapper;

			public SlotThenKeyComparer(ISlotKeyMapper mapper) => _mapper = mapper;

			public int Compare(StorageKeyInfo left, StorageKeyInfo right)
			{
				var leftKey = left.Key.AsSpan();
				var rightKey = right.Key.AsSpan();

				var leftMapped = _mapper.TryGetSlot(leftKey, out var leftSlot);
				var rightMapped = _mapper.TryGetSlot(rightKey, out var rightSlot);

				// Unmappable keys are dropped by the grouping pass anyway; park them at the end so
				// they cannot land between two keys of the same slot.
				if (leftMapped == false)
					return rightMapped ? 1 : 0;

				if (rightMapped == false)
					return -1;

				var bySlot = leftSlot.CompareTo(rightSlot, StringComparison.Ordinal);

				// Ties break on the whole key, which puts the slot's own key first: it is a proper
				// prefix of every satellite, and a prefix sorts before what extends it.
				return bySlot != 0 ? bySlot : leftKey.CompareTo(rightKey, StringComparison.Ordinal);
			}
		}

		private static SaveSlotStatus ToStatus(SaveCorruptedExceptionReason reason)
		{
			return reason switch
			{
				// Worth distinguishing: "not our file" and "our file, too new" are user-actionable
				// in ways that generic corruption is not.
				SaveCorruptedExceptionReason.InvalidMagic => SaveSlotStatus.Foreign,
				SaveCorruptedExceptionReason.UnsupportedVersion => SaveSlotStatus.UnsupportedVersion,
				SaveCorruptedExceptionReason.MissingFile => SaveSlotStatus.Missing,
				_ => SaveSlotStatus.Corrupted
			};
		}
	}
}
