using System.Collections.Generic;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Import
{
	/// <summary>
	/// Non-generic view over an importer already paired with the payloads it converts. Pairing
	/// happens at registration so importers stay generic and stateless while the pipeline can hold a
	/// heterogeneous list of steps whose <c>TLegacy</c> types differ.
	/// </summary>
	internal interface IImportStep
	{
		bool Background { get; }

		/// <summary>Importer type name, used to attribute errors to the right importer.</summary>
		string Name { get; }

		/// <summary>
		/// Number of payloads this step will convert. Used to pre-size sinks — note this counts
		/// payloads, not shards, so an importer emitting several shards per payload still grows the
		/// sink.
		/// </summary>
		int Count { get; }

		/// <summary>
		/// Converts every payload into <paramref name="sink"/>, recording into
		/// <paramref name="boundaries"/> the sink count after each payload so a duplicate id can be
		/// traced back to the payload that produced it. <paramref name="boundaries"/> must hold at
		/// least <see cref="Count"/> entries.
		/// </summary>
		void Run(ICollection<IDataShard> sink, int[] boundaries);
	}

	/// <summary>
	/// Runs one importer over every payload of its legacy type. Batching is a step-level concern:
	/// <see cref="IShardImporter{TLegacy}"/> still maps exactly one object, and the loop stays a
	/// private detail here. Grouping same-type payloads into a single step is what keeps a
	/// background import at one scheduled task per type instead of one per payload.
	/// </summary>
	internal sealed class BatchedImportStep<TLegacy> : IImportStep
	{
		private readonly List<TLegacy> _payloads;
		private readonly IShardImporter<TLegacy> _importer;

		public BatchedImportStep(List<TLegacy> payloads, IShardImporter<TLegacy> importer)
		{
			_payloads = payloads;
			_importer = importer;
		}

		public bool Background => _importer.SupportsBackgroundImport;

		public string Name => _importer.GetType().Name;

		public int Count => _payloads.Count;

		public void Run(ICollection<IDataShard> sink, int[] boundaries)
		{
			for (var i = 0; i < _payloads.Count; i++)
			{
				_importer.Import(_payloads[i], sink);
				boundaries[i] = sink.Count;
			}
		}
	}
}
