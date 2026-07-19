using System.Threading;
using Cysharp.Threading.Tasks;
using Persistence.Layout;
using Unity.Collections;

namespace Persistence.Core
{
	/// <summary>
	/// Defines how serialized shard blobs are organized on storage. Implementations
	/// decide whether each shard occupies its own key (multi-file) or all shards are
	/// packed into a single key (single-file). Shard bytes arrive as one contiguous
	/// payload arena indexed by <see cref="ShardBlobRange"/>s, so single-file packing
	/// is a straight gather-write with no re-copy. The envelope is always serialized
	/// via a fixed binary codec, independent of the shard serializer, and the layout
	/// is responsible for computing/verifying the envelope checksum.
	/// </summary>
	public interface ISaveLayout
	{
		/// <summary>
		/// If true, SaveManager must provide blobs for ALL shards on every save
		/// (single-file packing). If false, only dirty shard blobs are passed.
		/// </summary>
		bool RequiresFullSnapshot { get; }

		/// <summary>
		/// Writes the envelope and shard payload to storage. Does not take ownership
		/// of the buffers; they stay valid until the returned task completes.
		/// </summary>
		UniTask WriteAsync(string slot, SaveEnvelope envelope, NativeArray<byte> payload,
			NativeArray<ShardBlobRange> ranges, CancellationToken cancellation = default);

		/// <summary>Reads and returns the envelope, payload arena and blob ranges. Caller owns the result.</summary>
		UniTask<SaveLayoutResult> ReadAsync(string slot, Allocator allocator, CancellationToken cancellation = default);

		/// <summary>Returns true if a save exists for the given slot.</summary>
		UniTask<bool> ExistsAsync(string slot, CancellationToken cancellation = default);

		/// <summary>Removes all persisted data for the slot (envelope and shard blobs).</summary>
		UniTask DeleteAsync(string slot, CancellationToken cancellation = default);
	}
}
