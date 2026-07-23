using System.Collections.Generic;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Import
{
	/// <summary>
	/// Maps a already-loaded legacy object onto <see cref="IDataShard"/> instances so foreign save
	/// data can be adopted into the shard model. The package never parses a foreign format: the
	/// caller loads <typeparamref name="TLegacy"/> however it likes (old JsonUtility call, a
	/// BinaryReader, an existing PlayerPrefs blob) and hands the materialized object in.
	/// <para>
	/// Implementations should be stateless and pure — the pipeline may run several importers at
	/// once. This is not a schema migration: shard-to-shard evolution belongs to
	/// <see cref="IShardMigration"/>, which operates on data that already has a save envelope.
	/// </para>
	/// </summary>
	/// <typeparam name="TLegacy">The caller-supplied legacy shape being converted.</typeparam>
	public interface IShardImporter<in TLegacy>
	{
		/// <summary>
		/// True if <see cref="Import"/> may run off the main thread. Mirrors
		/// <see cref="ISerializer.SupportsBackgroundSerialization"/>: return false when the mapping
		/// touches <c>UnityEngine.Object</c> state, true for plain-data conversions so the pipeline
		/// can move the work to the thread pool.
		/// </summary>
		bool SupportsBackgroundImport { get; }

		/// <summary>
		/// Converts <paramref name="legacy"/> into shards appended to <paramref name="sink"/>.
		/// <para>
		/// The sink is a pooled buffer BORROWED for the duration of this call — it is returned to
		/// the pool once the pipeline drains it, so implementations must not retain or reuse it
		/// afterwards.
		/// </para>
		/// </summary>
		void Import(TLegacy legacy, ICollection<IDataShard> sink);
	}
}
