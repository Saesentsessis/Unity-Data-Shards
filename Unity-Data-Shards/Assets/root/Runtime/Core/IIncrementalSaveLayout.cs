using System;

namespace Saesentsessis.Persistence.Core
{
	/// <summary>
	/// Optional capability: an incremental <see cref="ISaveLayout"/> that can report which shard
	/// blobs it already holds on storage for a slot.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A layout with <see cref="ISaveLayout.RequiresFullSnapshot"/> set to <c>false</c> is only
	/// handed the blobs of dirty shards, and is expected to already hold the rest. That expectation
	/// is not something the layout can verify on its own and not something <c>SaveManager</c> can
	/// know — so without this interface the two silently disagree, and the envelope commits records
	/// pointing at blobs that were never written. Two ordinary sequences produce exactly that:
	/// </para>
	/// <list type="bullet">
	/// <item>load a slot, remove a shard, save, add the same shard back unmodified, save again — the
	/// first save deleted the blob, and the second never rewrites it because the shard is clean;</item>
	/// <item>load one slot and save the result to a <i>different</i> one — loading clears every dirty
	/// flag, so the new slot gets an envelope and no blobs at all.</item>
	/// </list>
	/// <para>
	/// Implementing this closes both: <c>SaveManager</c> captures a shard when it is dirty
	/// <i>or</i> when the layout does not report its id, so absence from storage is as good a reason
	/// to write as modification. A layout that cannot track its own membership should not implement
	/// this interface rather than answer inaccurately — the manager treats a layout that is absent
	/// or silent as holding nothing, which costs a full write and is always safe.
	/// </para>
	/// </remarks>
	public interface IIncrementalSaveLayout
	{
		/// <summary>
		/// Ids whose blobs this layout currently holds on storage for the slot.
		/// </summary>
		/// <param name="slot">Slot being saved.</param>
		/// <returns>
		/// An empty span for an unknown or empty slot, which is read as <i>holds nothing</i> and
		/// forces every shard to be written. Never report an id whose blob might be missing:
		/// over-reporting produces an unloadable save, while under-reporting only costs bytes.
		/// </returns>
		/// <remarks>
		/// The span may alias the layout's own storage, so it is valid only until the next call into
		/// the layout and must never be stored. <c>SaveManager</c> reads it synchronously, before any
		/// await. Return the ids in envelope-record order when that order is stable: the manager
		/// settles an unchanged shard set with one ordered pass and skips the set-membership check
		/// entirely.
		/// </remarks>
		ReadOnlySpan<SerializableGuid> GetPersistedIds(string slot);
	}
}
