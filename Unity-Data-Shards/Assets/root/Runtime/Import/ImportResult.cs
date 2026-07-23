using System.Runtime.InteropServices;

namespace Saesentsessis.Persistence.Import
{
	/// <summary>Outcome of a <see cref="ShardImportPipeline"/> run.</summary>
	public enum ImportStatus
	{
		/// <summary>Importers ran and the resulting shards were committed to the slot.</summary>
		Imported = 0,

		/// <summary>
		/// The slot already held a save, so nothing was imported. Load the slot normally —
		/// adoption already happened on an earlier run.
		/// </summary>
		SkippedExistingSave = 1
	}

	/// <summary>
	/// Result of an import run. Fields are declared largest-alignment first so the sequential
	/// layout packs without padding holes.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public readonly struct ImportResult
	{
		/// <summary>The imported shards, ready to use without reloading. Null when skipped.</summary>
		public ShardStore Store { get; }

		public ImportStatus Status { get; }

		/// <summary>Number of shards committed; zero when skipped.</summary>
		public int ShardCount { get; }

		private ImportResult(ShardStore store, ImportStatus status, int shardCount)
		{
			Store = store;
			Status = status;
			ShardCount = shardCount;
		}

		/// <summary>True when importers ran and the slot was written.</summary>
		public bool WasImported => Status == ImportStatus.Imported;

		public static ImportResult Committed(ShardStore store)
			=> new(store, ImportStatus.Imported, store.Count);

		public static ImportResult Skipped()
			=> new(null, ImportStatus.SkippedExistingSave, 0);
	}
}
