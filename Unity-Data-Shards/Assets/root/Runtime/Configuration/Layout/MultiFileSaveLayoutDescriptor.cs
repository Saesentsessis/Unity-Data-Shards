using System;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Configuration.Layout
{
	/// <summary>
	/// Builds a <see cref="Persistence.Layout.MultiFileSaveLayout"/> — an envelope key per slot plus
	/// one key per shard.
	/// </summary>
	/// <remarks>
	/// The incremental option: only dirty shards are rewritten, which is what makes large stores
	/// cheap to save. The trade is weaker cross-file atomicity, and a shard count that costs one
	/// storage key each — worth checking against a backend that caps how many keys a player may hold.
	/// </remarks>
	[Serializable]
	public sealed class MultiFileSaveLayoutDescriptor : ISaveLayoutDescriptor
	{
		/// <inheritdoc />
		public ISaveLayout Create(IStorage storage) => new Persistence.Layout.MultiFileSaveLayout(storage);
	}
}
