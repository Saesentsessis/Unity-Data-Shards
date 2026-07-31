using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Import;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Serialization;
using Saesentsessis.Persistence.Threading;
using UnityEngine;
using UnityEngine.TestTools;

namespace Saesentsessis.Persistence.Tests
{
	public class ShardImportTests
	{
		private const string Slot = "import-slot";
		private const string LegacyKey = "legacy-blob";

		/// <summary>Stand-in for a user's pre-existing, non-shard save shape.</summary>
		private sealed class LegacyBlob
		{
			public int Coins;
			public int Gems;
			public Guid Id;
		}

		/// <summary>A second legacy type, for unused-importer and multi-type cases.</summary>
		private sealed class OtherLegacy
		{
			public int Value;
		}

		/// <summary>Maps a legacy blob onto shards; thread behaviour is configurable per test.</summary>
		private sealed class BlobImporter : IShardImporter<LegacyBlob>
		{
			private readonly Guid[] _ids;
			private readonly Action _onImport;

			public BlobImporter(bool background, Guid[] ids, Action onImport = null)
			{
				SupportsBackgroundImport = background;
				_ids = ids;
				_onImport = onImport;
			}

			public bool SupportsBackgroundImport { get; }
			public bool RanOnMainThread { get; private set; }
			public bool Ran { get; private set; }

			public void Import(LegacyBlob legacy, ICollection<IDataShard> sink)
			{
				Ran = true;
				RanOnMainThread = PersistenceTask.IsMainThread;
				_onImport?.Invoke();

				for (var i = 0; i < _ids.Length; i++)
					sink.Add(new TestShard(_ids[i], legacy.Coins + i));
			}
		}

		/// <summary>Emits one shard per payload using the payload's own id, for duplicate attribution.</summary>
		private sealed class IdImporter : IShardImporter<LegacyBlob>
		{
			public bool SupportsBackgroundImport => false;

			public void Import(LegacyBlob legacy, ICollection<IDataShard> sink)
				=> sink.Add(new TestShard(legacy.Id, legacy.Coins));
		}

		/// <summary>Records how the pipeline drove it, so batching can be asserted.</summary>
		private sealed class RecordingImporter : IShardImporter<LegacyBlob>
		{
			public readonly List<object> Sinks = new();
			public int Invocations;

			public RecordingImporter(bool background) => SupportsBackgroundImport = background;

			public bool SupportsBackgroundImport { get; }

			public void Import(LegacyBlob legacy, ICollection<IDataShard> sink)
			{
				// A batched step reuses one sink for every payload; separate steps would each bring
				// their own. Single-threaded within a step, so no locking needed.
				Invocations++;

				if (Sinks.Contains(sink) == false)
					Sinks.Add(sink);

				sink.Add(new TestShard(legacy.Id, legacy.Coins));
			}
		}

		private sealed class ThrowingImporter : IShardImporter<LegacyBlob>
		{
			public ThrowingImporter(bool background) => SupportsBackgroundImport = background;

			public bool SupportsBackgroundImport { get; }

			public void Import(LegacyBlob legacy, ICollection<IDataShard> sink)
				=> throw new InvalidOperationException("importer boom");
		}

		private sealed class OtherImporter : IShardImporter<OtherLegacy>
		{
			public bool SupportsBackgroundImport => false;

			public void Import(OtherLegacy legacy, ICollection<IDataShard> sink)
				=> sink.Add(new TestShard(Guid.NewGuid(), legacy.Value));
		}

		private static SaveManager CreateManager(out MemoryStorage storage)
		{
			storage = new MemoryStorage();
			// A pre-existing foreign blob the pipeline must never touch.
			storage.Data[LegacyKey] = new byte[] { 1, 2, 3, 4 };
			return new SaveManager(new UnityJsonSerializer(), new SingleFileSaveLayout(storage));
		}

		private static LegacyBlob Blob() => new() { Coins = 100, Gems = 5, Id = Guid.NewGuid() };

		[UnityTest]
		public IEnumerator Import_RoundTripsAndLeavesLegacySourceIntact() => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out var storage);
			var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

			var pipeline = new ShardImportPipelineBuilder(manager)
				.AddImporter(new BlobImporter(background: false, ids))
				.AddData(Blob())
				.Build();

			var result = await pipeline.RunAsync(Slot);

			Assert.AreEqual(ImportStatus.Imported, result.Status);
			Assert.AreEqual(2, result.ShardCount);
			Assert.IsNotNull(result.Store, "An imported run must hand back the store.");

			var loaded = await manager.LoadAsync(Slot);
			Assert.AreEqual(2, loaded.Count, "Imported shards must be readable through a normal load.");

			CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, storage.Data[LegacyKey],
				"The legacy source must never be modified or deleted by an import.");
		});

		[UnityTest]
		public IEnumerator Import_SecondRun_SkipsExistingSave() => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out var storage);

			await new ShardImportPipelineBuilder(manager)
				.AddImporter(new BlobImporter(background: false, new[] { Guid.NewGuid() }))
				.AddData(Blob())
				.Build()
				.RunAsync(Slot);

			var writesAfterFirst = storage.WriteCounts[Slot];

			var result = await new ShardImportPipelineBuilder(manager)
				.AddImporter(new BlobImporter(background: false, new[] { Guid.NewGuid() }))
				.AddData(Blob())
				.Build()
				.RunAsync(Slot);

			Assert.AreEqual(ImportStatus.SkippedExistingSave, result.Status);
			Assert.IsNull(result.Store);
			Assert.AreEqual(writesAfterFirst, storage.WriteCounts[Slot], "A skipped import must not write.");
		});

		[UnityTest]
		public IEnumerator Import_Overwrite_ReimportsOverExistingSave() => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out _);

			await new ShardImportPipelineBuilder(manager)
				.AddImporter(new BlobImporter(background: false, new[] { Guid.NewGuid() }))
				.AddData(Blob())
				.Build()
				.RunAsync(Slot);

			var result = await new ShardImportPipelineBuilder(manager)
				.AddImporter(new BlobImporter(background: false, new[] { Guid.NewGuid(), Guid.NewGuid() }))
				.AddData(Blob())
				.WithOptions(new ImportOptions { Overwrite = true })
				.Build()
				.RunAsync(Slot);

			Assert.AreEqual(ImportStatus.Imported, result.Status);
			Assert.AreEqual(2, result.ShardCount);
		});

		[UnityTest]
		public IEnumerator Import_BackgroundImporter_RunsOffMainThread() => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out _);
			var background = new BlobImporter(background: true, new[] { Guid.NewGuid() });
			var main = new BlobImporter(background: false, new[] { Guid.NewGuid() });

			var result = await new ShardImportPipelineBuilder(manager)
				.AddImporter(background)
				.AddImporter(main)
				.AddData(Blob())
				.Build()
				.RunAsync(Slot);

			Assert.IsTrue(background.Ran && main.Ran);
			Assert.IsFalse(background.RanOnMainThread, "A background importer must run off the main thread.");
			Assert.IsTrue(main.RanOnMainThread, "A non-background importer must run on the main thread.");
			Assert.AreEqual(2, result.ShardCount, "Both groups' shards must reach the store.");
			Assert.IsTrue(PersistenceTask.IsMainThread, "RunAsync must return on the main thread.");
		});

		[UnityTest]
		public IEnumerator Import_SyncGroup_RunsInParallelWithBackgroundGroup() => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out _);
			using var gate = new ManualResetEventSlim(false);
			var signalled = false;

			// The background importer blocks until the main-thread importer releases it. This only
			// completes if the sync group runs concurrently rather than after the pool group.
			var background = new BlobImporter(background: true, new[] { Guid.NewGuid() },
				onImport: () => signalled = gate.Wait(TimeSpan.FromSeconds(5)));

			var main = new BlobImporter(background: false, new[] { Guid.NewGuid() },
				onImport: () => gate.Set());

			await new ShardImportPipelineBuilder(manager)
				.AddImporter(background)
				.AddImporter(main)
				.AddData(Blob())
				.Build()
				.RunAsync(Slot);

			Assert.IsTrue(signalled, "Sync importers must run in parallel with scheduled background importers.");
		});

		[UnityTest]
		public IEnumerator Import_SyncImporterThrows_SurfacesAndStillJoins() => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out _);
			var background = new BlobImporter(background: true, new[] { Guid.NewGuid() });

			var pipeline = new ShardImportPipelineBuilder(manager)
				.AddImporter(background)
				.AddImporter(new ThrowingImporter(background: false))
				.AddData(Blob())
				.Build();

			var threw = false;
			try { await pipeline.RunAsync(Slot); }
			catch (InvalidOperationException e) when (e.Message.Contains("importer boom")) { threw = true; }

			Assert.IsTrue(threw, "A failing sync importer must surface its exception.");
			Assert.IsTrue(background.Ran, "Scheduled background work must still be joined, not orphaned.");
		});

		[UnityTest]
		public IEnumerator Import_BackgroundImporterThrows_Surfaces() => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out _);

			var pipeline = new ShardImportPipelineBuilder(manager)
				.AddImporter(new ThrowingImporter(background: true))
				.AddData(Blob())
				.Build();

			var threw = false;
			try { await pipeline.RunAsync(Slot); }
			catch (Exception e) when (e.ToString().Contains("importer boom")) { threw = true; }

			Assert.IsTrue(threw, "A failing background importer must surface its exception.");
		});

		[UnityTest]
		public IEnumerator Import_DuplicateIdsAcrossImporters_ThrowsNamingImporter() => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out _);
			var shared = Guid.NewGuid();

			var pipeline = new ShardImportPipelineBuilder(manager)
				.AddImporter(new BlobImporter(background: false, new[] { shared }))
				.AddImporter(new BlobImporter(background: false, new[] { shared }))
				.AddData(Blob())
				.Build();

			var message = string.Empty;
			try { await pipeline.RunAsync(Slot); }
			catch (ArgumentException e) { message = e.Message; }

			StringAssert.Contains(nameof(BlobImporter), message,
				"A duplicate id must name the importer that produced it.");
		});

		[UnityTest]
		public IEnumerator Import_BatchesPayloadsOfTheSameTypeIntoOneStep() => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out _);
			var importer = new RecordingImporter(background: true);
			var payloads = new[] { Blob(), Blob(), Blob(), Blob() };

			var result = await new ShardImportPipelineBuilder(manager)
				.AddImporter(importer)
				.AddDataRange(payloads)
				.Build()
				.RunAsync(Slot);

			Assert.AreEqual(payloads.Length, importer.Invocations, "Every payload must be converted.");
			Assert.AreEqual(payloads.Length, result.ShardCount);
			Assert.AreEqual(1, importer.Sinks.Count,
				"Same-type payloads must share one batched step (one sink, one scheduled task) — not one step each.");
		});

		[UnityTest]
		public IEnumerator Import_MultipleImportersForSameType_AllRun() => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out _);
			var first = new BlobImporter(background: false, new[] { Guid.NewGuid() });
			var second = new BlobImporter(background: false, new[] { Guid.NewGuid() });

			var result = await new ShardImportPipelineBuilder(manager)
				.AddImporter(first)
				.AddImporter(second)
				.AddData(Blob())
				.Build()
				.RunAsync(Slot);

			Assert.IsTrue(first.Ran && second.Ran, "Every importer registered for a type must run.");
			Assert.AreEqual(2, result.ShardCount);
		});

		[UnityTest]
		public IEnumerator Import_DuplicateWithinBatch_NamesOffendingPayload() => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out _);
			var collision = Guid.NewGuid();

			// Payload 2 repeats payload 1's id.
			var payloads = new[]
			{
				new LegacyBlob { Id = Guid.NewGuid(), Coins = 1 },
				new LegacyBlob { Id = collision, Coins = 2 },
				new LegacyBlob { Id = collision, Coins = 3 }
			};

			var pipeline = new ShardImportPipelineBuilder(manager)
				.AddImporter(new IdImporter())
				.AddDataRange(payloads)
				.Build();

			var message = string.Empty;
			try { await pipeline.RunAsync(Slot); }
			catch (ArgumentException e) { message = e.Message; }

			StringAssert.Contains(nameof(IdImporter), message);
			StringAssert.Contains("payload 2 of 3", message,
				"A duplicate inside a batch must identify which payload produced it.");
		});

		[UnityTest]
		public IEnumerator Import_StepOrderFollowsRegistrationOrder() => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out _);
			var firstId = Guid.NewGuid();
			var secondId = Guid.NewGuid();

			var result = await new ShardImportPipelineBuilder(manager)
				.AddImporter(new BlobImporter(background: false, new[] { firstId }))
				.AddImporter(new BlobImporter(background: false, new[] { secondId }))
				.AddData(Blob())
				.Build()
				.RunAsync(Slot);

			Assert.AreEqual((SerializableGuid)firstId, result.Store[0].Identifier,
				"Steps must run in importer registration order.");
			Assert.AreEqual((SerializableGuid)secondId, result.Store[1].Identifier);
		});

		[Test]
		public void Build_PayloadWithoutImporter_ThrowsNamingType()
		{
			var manager = CreateManager(out _);

			var exception = Assert.Throws<InvalidOperationException>(() =>
				new ShardImportPipelineBuilder(manager).AddData(Blob()).Build());

			StringAssert.Contains(nameof(LegacyBlob), exception.Message,
				"An unmatched payload type must be named.");
		}

		[UnityTest]
		public IEnumerator Build_ImporterWithoutPayload_WarnsButSucceeds() => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out _);

			LogAssert.Expect(LogType.Warning, new Regex(nameof(OtherImporter)));

			var result = await new ShardImportPipelineBuilder(manager)
				.AddImporter(new BlobImporter(background: false, new[] { Guid.NewGuid() }))
				.AddImporter(new OtherImporter())   // registered, but no OtherLegacy payload added
				.AddData(Blob())
				.Build()
				.RunAsync(Slot);

			Assert.AreEqual(ImportStatus.Imported, result.Status,
				"An unused importer is a warning, not a failure.");
			Assert.AreEqual(1, result.ShardCount);
		});

		[Test]
		public void EmptyPipeline_Throws()
		{
			var manager = CreateManager(out _);
			Assert.Throws<InvalidOperationException>(() => new ShardImportPipelineBuilder(manager).Build());
		}

		[Test]
		public void DeterministicGuid_IsStableAcrossCallsAndDistinctPerKey()
		{
			var first = SerializableGuidExtensions.Compute("player/inventory");
			var again = SerializableGuidExtensions.Compute("player/inventory");
			var other = SerializableGuidExtensions.Compute("player/stats");

			Assert.AreEqual(first, again, "The same key must always yield the same id.");
			Assert.AreNotEqual(first, other, "Different keys must yield different ids.");
			Assert.AreNotEqual(SerializableGuid.Empty, first);
			Assert.AreEqual(first, SerializableGuidExtensions.Compute("player/inventory".AsSpan()),
				"String and span overloads must agree.");
		}

		[Test]
		public void DeterministicGuid_HandlesKeysLongerThanTheStackBuffer()
		{
			var key = new string('k', 512);

			Assert.AreEqual(SerializableGuidExtensions.Compute(key), SerializableGuidExtensions.Compute(key),
				"Long keys take the pooled-buffer path and must stay stable.");
		}
	}
}
