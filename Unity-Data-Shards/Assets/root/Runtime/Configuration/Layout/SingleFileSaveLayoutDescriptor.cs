using System;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Configuration.Layout
{
	/// <summary>
	/// Builds a <see cref="Persistence.Layout.SingleFileSaveLayout"/> — one gather-written,
	/// checksummed key per slot.
	/// </summary>
	/// <remarks>
	/// The atomic option: a save either lands whole or not at all. Every save rewrites the entire
	/// slot, so it suits small-to-moderate shard counts and is the right pairing for cloud storage,
	/// where the per-player file count is capped.
	/// </remarks>
	[Serializable]
	public sealed class SingleFileSaveLayoutDescriptor : ISaveLayoutDescriptor
	{
		/// <inheritdoc />
		public ISaveLayout Create(IStorage storage) => new Persistence.Layout.SingleFileSaveLayout(storage);
	}
}
