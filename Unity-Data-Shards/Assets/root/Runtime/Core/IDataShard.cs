using System;

namespace Persistence.Core
{
	/// <summary>
	/// Atomic unit of save data identified by a stable GUID.
	/// Implementations must be reference types (structs are rejected by ShardStore).
	/// </summary>
	public interface IDataShard : IEquatable<IDataShard>
	{
		/// <summary>Stable identity that survives serialization round-trips.</summary>
		SerializableGuid Identifier { get; }

		/// <summary>
		/// Whether this shard has unsaved mutations. Defaults to true so that
		/// shards without explicit tracking are always persisted.
		/// </summary>
		bool IsDirty => true;

		/// <summary>
		/// Resets dirty state after a successful save. Called by SaveManager once
		/// all shards have been written to storage.
		/// </summary>
		void ClearDirty() { }

		bool IEquatable<IDataShard>.Equals(IDataShard other)
			=> other != null && Identifier.Equals(other.Identifier);
	}
}