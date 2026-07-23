using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Threading;
using UnityEngine.Pool;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using ExceptionTask = Cysharp.Threading.Tasks.UniTask<System.Exception>;
using ImportResultTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.Import.ImportResult>;
#else
using TaskType = System.Threading.Tasks.Task;
using ExceptionTask = System.Threading.Tasks.Task<System.Exception>;
using ImportResultTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Import.ImportResult>;
#endif

namespace Saesentsessis.Persistence.Import
{
	/// <summary>
	/// One-shot adoption of foreign save data into the shard model. Foreign data has no envelope,
	/// type table or checksum, so it cannot enter the load-time migration chain — this runs BEFORE
	/// the load pipeline and commits a single normal save, after which the slot loads like any
	/// other.
	/// <para>
	/// Deliberately separate from <see cref="SaveManager"/>: it holds per-run state (importers,
	/// payloads, options) and is meant to be built, run once and discarded, while the manager is
	/// long-lived. Build one with <see cref="ShardImportPipelineBuilder"/>.
	/// </para>
	/// <para>
	/// The legacy source is NEVER read, moved or deleted by this type — the caller loads it and
	/// remains its owner, so an import is always reversible.
	/// </para>
	/// </summary>
	public sealed class ShardImportPipeline
	{
		private readonly SaveManager _manager;
		private readonly IImportStep[] _background;
		private readonly IImportStep[] _sync;
		private readonly ImportOptions _options;

		internal ShardImportPipeline(SaveManager manager, IImportStep[] background, IImportStep[] sync, ImportOptions options)
		{
			_manager = manager;
			_background = background;
			_sync = sync;
			_options = options;
		}

		/// <summary>
		/// Runs every importer and commits the produced shards to <paramref name="slot"/>.
		/// Background-capable importers are all scheduled onto the thread pool first; the
		/// main-thread importers then run concurrently with them, and both groups are joined
		/// before the save. If the slot already holds a save and
		/// <see cref="ImportOptions.Overwrite"/> is false, nothing runs.
		/// </summary>
		public async ImportResultTask RunAsync(string slot, CancellationToken cancellation = default)
		{
			cancellation.ThrowIfCancellationRequested();

			if (_options.Overwrite == false && await _manager.ExistsAsync(slot, cancellation))
				return ImportResult.Skipped();

			// Sync steps share one sink (they run sequentially on this thread); only the genuinely
			// concurrent background steps need private buffers. All are pooled and returned below.
			var syncSink = ListPool<IDataShard>.Get();
			var backgroundSinks = _background.Length > 0 ? new List<IDataShard>[_background.Length] : null;

			// Per-step payload boundaries, so a duplicate id can be traced to the exact payload.
			// ArrayPool rather than a TempJob NativeArray: these are written during the pool/sync
			// phase and read after the join, so they cross await boundaries — TempJob's 4-frame
			// lifetime would warn whenever a user's import work runs long.
			var syncBoundaries = RentBoundaries(_sync);
			var backgroundBoundaries = RentBoundaries(_background);
			var syncStarts = _sync.Length > 0 ? new int[_sync.Length] : null;

			EnsureCapacity(syncSink, TotalCount(_sync));

			try
			{
				var pending = ScheduleBackground(backgroundSinks, backgroundBoundaries, cancellation);
				var syncError = RunSync(syncSink, syncBoundaries, syncStarts);
				var joinError = await JoinAsync(pending);

				Rethrow(syncError, joinError);

				// The package avoids SynchronizationContext by design, so the post-join
				// continuation can land on a pool thread; SaveAsync must start on the main thread.
				if (PersistenceTask.IsMainThread == false)
					await PersistenceTask.SwitchToMainThread(cancellation);

				var store = Drain(syncSink, backgroundSinks, syncBoundaries, syncStarts, backgroundBoundaries);

				await _manager.SaveAsync(slot, store, cancellation);

				return ImportResult.Committed(store);
			}
			finally
			{
				ListPool<IDataShard>.Release(syncSink);
				ReturnBoundaries(syncBoundaries);
				ReturnBoundaries(backgroundBoundaries);

				if (backgroundSinks != null)
				{
					for (var i = backgroundSinks.Length - 1; i >= 0; i--)
						if (backgroundSinks[i] != null)
							ListPool<IDataShard>.Release(backgroundSinks[i]);
				}
			}
		}

		private static int[][] RentBoundaries(IImportStep[] steps)
		{
			if (steps.Length == 0)
				return null;

			var boundaries = new int[steps.Length][];

			for (var i = 0; i < steps.Length; i++)
				boundaries[i] = ArrayPool<int>.Shared.Rent(Math.Max(1, steps[i].Count));

			return boundaries;
		}

		private static void ReturnBoundaries(int[][] boundaries)
		{
			if (boundaries == null)
				return;

			for (var i = boundaries.Length - 1; i >= 0; i--)
				if (boundaries[i] != null)
					ArrayPool<int>.Shared.Return(boundaries[i]);
		}

		/// <summary>
		/// Payload total across steps. This counts payloads, not shards — an importer emitting
		/// several shards per payload will still grow the sink — so it is only a starting size.
		/// </summary>
		private static int TotalCount(IImportStep[] steps)
		{
			var total = 0;

			for (var i = steps.Length - 1; i >= 0; i--)
				total += steps[i].Count;

			return total;
		}

		private static void EnsureCapacity(List<IDataShard> list, int capacity)
		{
			if (capacity > list.Capacity)
				list.Capacity = capacity;
		}

		/// <summary>Dispatches every background step without awaiting, so the caller can proceed.</summary>
		private TaskType[] ScheduleBackground(List<IDataShard>[] sinks, int[][] boundaries, CancellationToken cancellation)
		{
			if (_background.Length == 0)
				return null;

			var pending = new TaskType[_background.Length];

			for (var i = 0; i < _background.Length; i++)
			{
				var step = _background[i];
				var sink = ListPool<IDataShard>.Get();
				EnsureCapacity(sink, step.Count);
				sinks[i] = sink;

				pending[i] = PersistenceTask.RunOnThreadPool(
					static state => state.Step.Run(state.Sink, state.Boundaries),
					(Step: step, Sink: (ICollection<IDataShard>)sink, Boundaries: boundaries[i]),
					cancellation);
			}

			return pending;
		}

		/// <summary>
		/// Runs the main-thread importers inline. Returns the failure instead of throwing so the
		/// scheduled pool work is still joined (an unobserved task exception would otherwise leak).
		/// </summary>
		private Exception RunSync(List<IDataShard> sink, int[][] boundaries, int[] starts)
		{
			try
			{
				for (var i = 0; i < _sync.Length; i++)
				{
					starts[i] = sink.Count;
					_sync[i].Run(sink, boundaries[i]);
				}

				return null;
			}
			catch (Exception exception)
			{
				return exception;
			}
		}

		private static async ExceptionTask JoinCore(TaskType[] pending)
		{
			try
			{
				await PersistenceTask.WhenAll(pending);
				return null;
			}
			catch (Exception exception)
			{
				return exception;
			}
		}

		private static ExceptionTask JoinAsync(TaskType[] pending)
			=> pending == null ? PersistenceTask.FromResult<Exception>(null) : JoinCore(pending);

		private static void Rethrow(Exception syncError, Exception joinError)
		{
			if (syncError != null && joinError != null)
				throw new AggregateException(
					"Import failed on both the main thread and the thread pool.", syncError, joinError);

			if (syncError != null)
				ExceptionDispatchInfo.Capture(syncError).Throw();

			if (joinError != null)
				ExceptionDispatchInfo.Capture(joinError).Throw();
		}

		/// <summary>
		/// Collects every sink into a store. Order is deterministic: background steps in registration
		/// order, then the main-thread steps. Duplicate ids throw, naming the importer and the payload
		/// responsible — the same uniqueness rule the <see cref="ShardStore"/> list constructor enforces.
		/// </summary>
		private ShardStore Drain(List<IDataShard> syncSink, List<IDataShard>[] backgroundSinks,
			int[][] syncBoundaries, int[] syncStarts, int[][] backgroundBoundaries)
		{
			var capacity = _options.CapacityHint > 0
				? _options.CapacityHint
				: TotalCount(_background) + TotalCount(_sync);

			var store = capacity > 0 ? new ShardStore(capacity) : new ShardStore();

			if (backgroundSinks != null)
			{
				for (var i = 0; i < backgroundSinks.Length; i++)
				{
					var step = _background[i];
					var sink = backgroundSinks[i];

					for (var j = 0; j < sink.Count; j++)
					{
						var shard = sink[j];

						if (store.Add(shard))
							continue;
						
						throw DuplicateShard(shard, step, PayloadIndex(backgroundBoundaries[i], step.Count, j));
					}
				}
			}

			for (var i = 0; i < syncSink.Count; i++)
			{
				var shard = syncSink[i];

				if (store.Add(shard))
					continue;
				
				var stepIndex = SyncStepAt(syncStarts, i);
				var step = _sync[stepIndex];

				throw DuplicateShard(shard, step, PayloadIndex(syncBoundaries[stepIndex], step.Count, i));
			}

			return store;
		}

		/// <summary>Maps an index in the shared sync sink back to the step that appended it.</summary>
		private int SyncStepAt(int[] starts, int index)
		{
			for (var i = _sync.Length - 1; i >= 0; i--)
				if (index >= starts[i])
					return i;

			return 0;
		}

		/// <summary>
		/// Maps a sink index to the payload that produced it. Boundaries hold the sink count after
		/// each payload, so the first boundary past the index owns it.
		/// </summary>
		private static int PayloadIndex(int[] boundaries, int count, int sinkIndex)
		{
			for (var i = 0; i < count; i++)
				if (sinkIndex < boundaries[i])
					return i;

			return -1;
		}

		private static ArgumentException DuplicateShard(IDataShard shard, IImportStep step, int payloadIndex)
		{
			var origin = step.Count > 1 && payloadIndex >= 0
				? $"Importer '{step.Name}' produced shard id {shard.Identifier} at payload {payloadIndex} of {step.Count}"
				: $"Importer '{step.Name}' produced shard id {shard.Identifier}";

			return new ArgumentException(
				$"{origin}, which another importer already added. Shard ids must be unique across all importers.");
		}
	}
}
