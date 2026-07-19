using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Persistence.Layout;

namespace Persistence.Core
{
	/// <summary>
	/// Managed-memory counterpart of <see cref="ISaveLayout"/>. Identical arena +
	/// ranges model, backed by GC-tracked (pooled) buffers.
	/// </summary>
	public interface IManagedSaveLayout
	{
		/// <summary>
		/// If true, SaveManager must provide blobs for ALL shards on every save.
		/// If false, only dirty shard blobs are passed.
		/// </summary>
		bool RequiresFullSnapshot { get; }

		/// <summary>
		/// Writes the envelope and shard payload to storage. Does not take ownership
		/// of the buffers; they stay valid until the returned task completes.
		/// </summary>
		UniTask WriteAsync(string slot, SaveEnvelope envelope, ReadOnlyMemory<byte> payload,
			ReadOnlyMemory<ShardBlobRange> ranges, CancellationToken cancellation = default);

		/// <summary>Reads and returns the envelope, payload and blob ranges. Caller owns (and must Dispose) the result.</summary>
		UniTask<ManagedSaveLayoutResult> ReadAsync(string slot, CancellationToken cancellation = default);

		/// <summary>Returns true if a save exists for the given slot.</summary>
		UniTask<bool> ExistsAsync(string slot, CancellationToken cancellation = default);

		/// <summary>Removes all persisted data for the slot.</summary>
		UniTask DeleteAsync(string slot, CancellationToken cancellation = default);
	}
}
