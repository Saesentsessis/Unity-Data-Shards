using System;

namespace Saesentsessis.Persistence.Core
{
	/// <summary>
	/// Optional capability: an <see cref="ISaveLayout"/> that can say which slot a storage key
	/// belongs to, and which key of that slot carries the envelope.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A layout decides how many keys a slot occupies, so it is the only thing that can undo that
	/// mapping. <c>SingleFileSaveLayout</c> uses one key per slot;
	/// <c>MultiFileSaveLayout</c> uses <c>slot</c> for the envelope plus <c>slot/&lt;32-hex&gt;</c>
	/// per shard. A slot browser needs both facts to turn a flat key listing back into slots.
	/// </para>
	/// <para>
	/// This is the <i>only</i> place a layout is consulted during browsing. Decoding the envelope
	/// header needs no layout at all, because every layout writes it at offset 0 of the slot key.
	/// </para>
	/// </remarks>
	public interface ISlotKeyMapper
	{
		/// <summary>
		/// Resolves the slot a storage key belongs to.
		/// </summary>
		/// <param name="storageKey">Key as returned by <see cref="IListableStorage"/>.</param>
		/// <param name="slot">
		/// A <b>slice of <paramref name="storageKey"/></b>, so attributing a shard to its slot
		/// allocates nothing. Empty when this returns false.
		/// </param>
		/// <returns>False when the key does not belong to this layout at all — malformed, or shaped
		/// in a way the layout never writes.</returns>
		/// <remarks>
		/// <b>The envelope rule:</b> <c>slot.Length == storageKey.Length</c> means this key is the
		/// slot's own key and therefore holds the envelope; anything shorter means the key is one of
		/// the slot's satellites. One comparison answers both questions, so the interface needs no
		/// second member and implementations cannot let the two answers disagree.
		/// </remarks>
		bool TryGetSlot(ReadOnlySpan<char> storageKey, out ReadOnlySpan<char> slot);
	}
}
