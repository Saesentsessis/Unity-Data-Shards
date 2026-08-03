using System;
using System.Threading;
using Saesentsessis.Persistence.Layout;
using Unity.Collections;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using BoolTask = Cysharp.Threading.Tasks.UniTask<bool>;
using SaveLayoutTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.Layout.SaveLayoutResult>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using SaveLayoutTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Layout.SaveLayoutResult>;
#endif

namespace Saesentsessis.Persistence.Core
{
	/// <summary>
	/// Defines how serialized shard blobs are organized on storage. Implementations
	/// decide whether each shard occupies its own key (multi-file) or all shards are
	/// packed into a single key (single-file). Shard bytes arrive as one contiguous
	/// payload arena indexed by <see cref="ShardBlobRange"/>s, so a layout never walks
	/// per-shard structures — it gathers ranges. The envelope is always serialized
	/// via a fixed binary codec, independent of the shard serializer, and the layout
	/// is responsible for computing/verifying the envelope checksum.
	/// </summary>
	/// <remarks>
	/// The payload arena is a borrowed view, not a buffer the layout can extend, and
	/// <see cref="IStorage.WriteAsync"/> takes one contiguous array — so a layout that has to
	/// prefix bytes onto the payload copies it into a buffer of its own. Single-file packing pays
	/// that once for the whole payload; multi-file pays it per shard file, but only ever holds one
	/// blob at a time. Neither is copy-free; the difference is peak memory, not total bytes moved.
	/// </remarks>
	public interface ISaveLayout : IDisposable
	{
		/// <summary>
		/// If true, SaveManager must provide blobs for ALL shards on every save
		/// (single-file packing). If false, only dirty shard blobs are passed.
		/// </summary>
		/// <remarks>
		/// Returning false takes on an obligation: every envelope record whose blob is not in this
		/// call must already be on storage. Implement <see cref="IIncrementalSaveLayout"/> to let
		/// SaveManager check that for you — a shard can be clean and still have no blob, and without
		/// the capability neither side notices until the save fails to load.
		/// </remarks>
		bool RequiresFullSnapshot { get; }

		/// <summary>
		/// Bytes to leave free at the head of the payload arena, before the first blob.
		/// </summary>
		/// <remarks>
		/// <para>
		/// A layout that must put bytes <i>in front of</i> the shard data — an envelope, a file
		/// header — would otherwise have to copy the whole payload into a buffer of its own. Asking
		/// for the room up front instead lets the shards be serialized into their final position,
		/// and the arena handed to <see cref="WriteAsync"/> becomes the file itself.
        /// </para>
		/// <para>
		/// The reservation must be <b>exact</b>: whatever is written into it has to fill it
		/// completely, because the payload begins at the very next byte and no format here can
		/// express a gap. Size it with <c>EnvelopeCodec.ExactEncodedSize</c>, never with the bound.
		/// The region arrives uninitialised.
		/// </para>
		/// <para>
		/// Blob offsets in <c>ranges</c> are absolute within the arena, so they already include this
		/// reservation. A layout writing them into a file where offsets are payload-relative must
		/// subtract it.
		/// </para>
		/// </remarks>
		int HeaderReservation(in SaveEnvelope envelope, int blobCount) => 0;

		/// <summary>
		/// Bytes to leave free immediately before <b>every</b> blob.
		/// </summary>
		/// <remarks>
		/// The per-shard counterpart of <see cref="HeaderReservation"/>, for layouts that frame each
		/// blob individually. <c>MultiFileSaveLayout</c> reserves eight bytes for its per-file
		/// checksum, writes the hash into the gap, and hands storage a <c>GetSubArray</c> view
		/// spanning gap and blob together — no scratch buffer and no copy. Must be filled
		/// completely; the bytes arrive uninitialised.
		/// </remarks>
		int BlobReservation => 0;

		/// <summary>
		/// Writes the envelope and shard payload to storage. Does not take ownership
		/// of the buffers; they stay valid until the returned task completes.
		/// </summary>
		/// <remarks>
		/// <paramref name="payload"/> includes any space requested through
		/// <see cref="HeaderReservation"/> and <see cref="BlobReservation"/>, and filling that space
		/// is this method's job.
		/// </remarks>
		TaskType WriteAsync(string slot, SaveEnvelope envelope, NativeArray<byte> payload,
			NativeArray<ShardBlobRange> ranges, CancellationToken cancellation = default);

		/// <summary>Reads and returns the envelope, payload arena and blob ranges. Caller owns the result.</summary>
		SaveLayoutTask ReadAsync(string slot, Allocator allocator, CancellationToken cancellation = default);

		/// <summary>Returns true if a save exists for the given slot.</summary>
		BoolTask ExistsAsync(string slot, CancellationToken cancellation = default);

		/// <summary>Removes all persisted data for the slot (envelope and shard blobs).</summary>
		TaskType DeleteAsync(string slot, CancellationToken cancellation = default);
	}
}
