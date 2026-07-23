using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Saesentsessis.Persistence.Import
{
	/// <summary>
	/// Fluent wiring for a <see cref="ShardImportPipeline"/>. Importers and payloads are registered
	/// independently — <see cref="AddImporter{TLegacy}"/> and <see cref="AddData{TLegacy}(TLegacy)"/> —
	/// and paired by legacy type at <see cref="Build"/>, which validates that every payload has an
	/// importer. Payloads of the same type are batched into a single step, so a background importer
	/// costs one scheduled task per type rather than one per payload.
	/// <para>
	/// Both registration methods are generic, so every closed generic the pipeline needs is emitted
	/// by the compiler at the call site. Nothing here reflects over types at build time, which keeps
	/// the pipeline safe under IL2CPP/AOT.
	/// </para>
	/// </summary>
	public sealed class ShardImportPipelineBuilder
	{
		/// <summary>An importer plus the statically-typed factory that turns payloads into a step.</summary>
		private sealed class ImporterEntry
		{
			public string Name;
			public bool Background;
			public Func<object, IImportStep> CreateStep;
		}

		private readonly SaveManager _manager;
		private readonly Dictionary<Type, List<ImporterEntry>> _importers = new();

		// Value is a List<TLegacy> boxed as object; only the generic registration methods ever cast it.
		private readonly Dictionary<Type, object> _payloads = new();

		// Dictionary iteration order is unspecified, so first-registration order is tracked
		// explicitly to keep step order deterministic across builds.
		private readonly List<Type> _payloadOrder = new();

		private ImportOptions _options;

		public ShardImportPipelineBuilder(SaveManager manager)
		{
			_manager = manager ?? throw new ArgumentNullException(nameof(manager));
		}

		/// <summary>
		/// Registers an importer for a legacy type. Several importers may be registered for the same
		/// type; each becomes its own step over the same payloads.
		/// </summary>
		public ShardImportPipelineBuilder AddImporter<TLegacy>(IShardImporter<TLegacy> importer)
		{
			if (importer == null)
				throw new ArgumentNullException(nameof(importer));

			var type = typeof(TLegacy);

			if (_importers.TryGetValue(type, out var entries) == false)
			{
				entries = new List<ImporterEntry>();
				_importers[type] = entries;
			}

			entries.Add(new ImporterEntry
			{
				Name = importer.GetType().Name,
				Background = importer.SupportsBackgroundImport,
				// Closed over TLegacy here, where it is statically known — no reflection at Build().
				CreateStep = boxed => new BatchedImportStep<TLegacy>((List<TLegacy>)boxed, importer)
			});

			return this;
		}

		/// <summary>
		/// Registers one already-loaded legacy object. Keyed on the static <typeparamref name="TLegacy"/>,
		/// not the runtime type — pass the type argument explicitly to feed a subclass to an importer
		/// registered for its base type.
		/// </summary>
		public ShardImportPipelineBuilder AddData<TLegacy>(TLegacy payload)
		{
			PayloadList<TLegacy>().Add(payload);
			return this;
		}

		/// <summary>
		/// Registers several legacy objects of the same type; they share one batched step.
		/// <para>
		/// Deliberately not an <c>AddData</c> overload: passing an array to <see cref="AddData{TLegacy}(TLegacy)"/>
		/// would bind <c>TLegacy</c> to the array type itself (the more specific parameter wins), silently
		/// registering the whole array as a single payload.
		/// </para>
		/// </summary>
		public ShardImportPipelineBuilder AddDataRange<TLegacy>(IReadOnlyList<TLegacy> payloads)
		{
			if (payloads == null)
				throw new ArgumentNullException(nameof(payloads));

			var list = PayloadList<TLegacy>();

			for (var i = 0; i < payloads.Count; i++)
				list.Add(payloads[i]);

			return this;
		}

		public ShardImportPipelineBuilder WithOptions(ImportOptions options)
		{
			_options = options;
			return this;
		}

		private List<TLegacy> PayloadList<TLegacy>()
		{
			var type = typeof(TLegacy);

			if (_payloads.TryGetValue(type, out var boxed))
				return (List<TLegacy>)boxed;

			var list = new List<TLegacy>();
			_payloads[type] = list;
			_payloadOrder.Add(type);

			return list;
		}

		/// <summary>
		/// Pairs payloads with importers, validating that none is left unmatched, and partitions the
		/// resulting steps into background/main-thread groups once so a run does no sorting. Steps are
		/// ordered by payload-type registration order, then importer registration order within a type.
		/// </summary>
		public ShardImportPipeline Build()
		{
			if (_payloadOrder.Count == 0)
				throw new InvalidOperationException("An import pipeline needs at least one payload; call AddData before Build.");

			ThrowIfPayloadsUnmatched();
			WarnOnUnusedImporters();

			int backgroundCount = 0, syncCount = 0;

			foreach (var payloadType in _payloadOrder)
			{
				var entries = _importers[payloadType];

				foreach (var entry in entries)
				{
					if (entry.Background)
						backgroundCount++;
					else
						syncCount++;
				}
			}

			var background = backgroundCount > 0 ? new IImportStep[backgroundCount] : Array.Empty<IImportStep>();
			var sync = syncCount > 0 ? new IImportStep[syncCount] : Array.Empty<IImportStep>();
			int backgroundIndex = 0, syncIndex = 0;

			foreach (var type in _payloadOrder)
			{
				var boxed = _payloads[type];
				var entries = _importers[type];

				foreach (var entry in entries)
				{
					var step = entry.CreateStep(boxed);

					if (entry.Background)
						background[backgroundIndex++] = step;
					else
						sync[syncIndex++] = step;
				}
			}

			return new ShardImportPipeline(_manager, background, sync, _options);
		}

		/// <summary>Reports every payload type lacking an importer at once, rather than the first.</summary>
		private void ThrowIfPayloadsUnmatched()
		{
			StringBuilder unmatched = null;

			foreach (var type in _payloadOrder)
			{
				if (_importers.ContainsKey(type))
					continue;

				unmatched ??= new StringBuilder();

				if (unmatched.Length > 0)
					unmatched.Append(", ");

				unmatched.Append(type.FullName);
			}

			if (unmatched != null)
				throw new InvalidOperationException(
					$"No importer registered for payload type(s): {unmatched}. Call AddImporter for each legacy type passed to AddData.");
		}

		/// <summary>
		/// An importer with no payloads is usually a wiring slip, but it is harmless — warn and carry
		/// on rather than failing the build.
		/// </summary>
		private void WarnOnUnusedImporters()
		{
			foreach (var pair in _importers)
			{
				if (_payloads.ContainsKey(pair.Key))
					continue;

				var entries = pair.Value;

				foreach (var entry in entries)
					Debug.LogWarning(
						$"Importer '{entry.Name}' is registered for {pair.Key.FullName} but no payload of that type was added; it will not run.");
			}
		}
	}
}
