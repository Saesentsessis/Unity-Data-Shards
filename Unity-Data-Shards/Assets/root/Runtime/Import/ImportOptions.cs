using System.Runtime.InteropServices;

namespace Saesentsessis.Persistence.Import
{
	/// <summary>
	/// Tuning for a <see cref="ShardImportPipeline"/> run. Fields are declared largest-alignment
	/// first so the sequential layout packs without padding holes.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct ImportOptions
	{
		/// <summary>
		/// Pre-sizes the shard sinks and the resulting store. Zero uses the default growth.
		/// </summary>
		public int CapacityHint { get; set; }

		/// <summary>
		/// When false (the default) an existing save in the target slot makes the run a no-op —
		/// import is a run-once adoption step. Set true to deliberately re-import over it.
		/// <para>
		/// This never affects the legacy source, which the pipeline never reads, moves or deletes.
		/// </para>
		/// </summary>
		public bool Overwrite { get; set; }
	}
}
