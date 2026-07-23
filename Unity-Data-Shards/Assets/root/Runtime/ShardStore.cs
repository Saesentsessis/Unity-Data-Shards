using System;
using System.Collections;
using System.Collections.Generic;
using Saesentsessis.Persistence.Core;
using Unity.Collections;
using UnityEngine;

namespace Saesentsessis.Persistence
{
	/// <summary>
	/// A flat, GUID-indexed set of shards. This is the unit the SaveManager persists:
	/// every shard is serialized independently, so there is no nesting and no topology
	/// to reconstruct on load. Implements <see cref="IReadOnlyList{IDataShard}"/> so it
	/// can be passed straight to <see cref="SaveManager.SaveAsync"/>, and a load result
	/// can be wrapped back into one via <c>AsShardStore()</c>.
	/// </summary>
	[Serializable]
	public sealed class ShardStore : IReadOnlyList<IDataShard>, ISerializationCallbackReceiver
	{
		[SerializeReference] private List<IDataShard> shards;

		[NonSerialized] private Dictionary<SerializableGuid, IDataShard> _index;

		/// <summary>
		/// Incremented on every membership change (Add/Remove/Clear/deserialize).
		/// SaveManager uses it to reuse a cached save envelope: as long as the
		/// generation is unchanged, the type table and records are still valid.
		/// </summary>
		public int Generation { get; private set; }

		public ShardStore()
		{
			shards = new List<IDataShard>();
			_index = new Dictionary<SerializableGuid, IDataShard>();
		}

		public ShardStore(int capacity)
		{
			shards = new List<IDataShard>(capacity);
			_index = new Dictionary<SerializableGuid, IDataShard>(capacity);
		}

		public ShardStore(IReadOnlyList<IDataShard> source)
		{
			var count = source.Count;
			shards = new List<IDataShard>(count);
			_index = new Dictionary<SerializableGuid, IDataShard>(count);

			for (var i = 0; i < count; i++)
				if (!Add(source[i]))
					throw new ArgumentException(
						$"Duplicate shard id {source[i].Identifier} in source list.", nameof(source));
		}

		public int Count => shards.Count;

		public IDataShard this[int index] => shards[index];

		/// <summary>Adds a shard. Returns false if a shard with the same id is already present.</summary>
		public bool Add(IDataShard shard)
		{
			ThrowIfValueType(shard);

			if (_index.ContainsKey(shard.Identifier))
				return false;

			shards.Add(shard);
			_index[shard.Identifier] = shard;
			Generation++;
			return true;
		}

		/// <summary>Removes a shard by id. Returns false if not found.</summary>
		public bool Remove(SerializableGuid id)
		{
			if (!_index.Remove(id, out var shard))
				return false;

			shards.RemoveAtSwapBack(shards.IndexOf(shard));
			Generation++;
			return true;
		}

		/// <summary>Removes a shard by reference. Returns false if not found.</summary>
		public bool Remove(IDataShard shard)
		{
			return shard != null && Remove(shard.Identifier);
		}

		/// <summary>Returns true if a shard with the given id is present.</summary>
		public bool Contains(SerializableGuid id)
		{
			return _index.ContainsKey(id);
		}

		/// <summary>Retrieves a shard by id. Returns false if not found.</summary>
		public bool TryGet(SerializableGuid id, out IDataShard shard)
		{
			return _index.TryGetValue(id, out shard);
		}

		/// <summary>Retrieves a typed shard by id. Returns false if not found or the wrong type.</summary>
		public bool TryGet<T>(SerializableGuid id, out T shard) where T : class, IDataShard
		{
			if (_index.TryGetValue(id, out var raw) && raw is T typed)
			{
				shard = typed;
				return true;
			}

			shard = null;
			return false;
		}

		/// <summary>Removes every shard.</summary>
		public void Clear()
		{
			shards.Clear();
			_index.Clear();
			Generation++;
		}

		public List<IDataShard>.Enumerator GetEnumerator() => shards.GetEnumerator();

		IEnumerator<IDataShard> IEnumerable<IDataShard>.GetEnumerator() => shards.GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => shards.GetEnumerator();

		public void OnBeforeSerialize() { }

		public void OnAfterDeserialize() => RebuildIndex();

		private void RebuildIndex()
		{
			if (_index == null)
				_index = new Dictionary<SerializableGuid, IDataShard>(shards.Count);
			else
				_index.Clear();

			foreach (var shard in shards)
				if (shard != null)
					_index[shard.Identifier] = shard;

			Generation++;
		}

		private static void ThrowIfValueType(IDataShard shard)
		{
			if (shard.GetType().IsValueType)
				throw new ArgumentException(
					$"IDataShard implementations must be classes. '{shard.GetType().Name}' is a value type.",
					nameof(shard));
		}
	}
}
