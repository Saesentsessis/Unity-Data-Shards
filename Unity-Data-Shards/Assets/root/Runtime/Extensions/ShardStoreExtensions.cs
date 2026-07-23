using System.Collections.Generic;
using System.Threading;
using Saesentsessis.Persistence.Core;
#if PERSISTENCE_HAS_UNITASK
using ShardStoreTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.ShardStore>;
#else
using ShardStoreTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.ShardStore>;
#endif

namespace Saesentsessis.Persistence
{
	public static class ShardStoreExtensions
	{
		/// <summary>
		/// Wraps a flat shard list in a queryable <see cref="ShardStore"/>. If the list
		/// already is a ShardStore it is returned as-is, so this is safe to call on a
		/// SaveManager load result without a redundant copy.
		/// </summary>
		public static ShardStore AsShardStore(this IReadOnlyList<IDataShard> self)
			=> self as ShardStore ?? new ShardStore(self);

		/// <summary>
		/// Loads a slot and returns the shards as a queryable <see cref="ShardStore"/>
		/// instead of a bare list. Convenience over <c>LoadAsync(...).AsShardStore()</c>.
		/// </summary>
		public static async ShardStoreTask LoadAsStoreAsync(this SaveManager manager, string slot, CancellationToken cancellation = default)
			=> (await manager.LoadAsync(slot, cancellation)).AsShardStore();
	}
}
