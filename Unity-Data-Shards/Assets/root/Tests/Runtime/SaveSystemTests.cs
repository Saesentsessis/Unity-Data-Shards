using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Persistence.Core;
using Persistence.Layout;
using Persistence.Serialization;
using UnityEngine.TestTools;

namespace Persistence.Tests
{
	public class SaveSystemTests
	{
		private const string Slot = "test-slot";

		private static SaveManager CreateManager(out MemoryStorage storage, MigrationRegistry migrations = null)
		{
			storage = new MemoryStorage();
			return new SaveManager(new UnityJsonSerializer(), new SingleFileSaveLayout(storage), migrations);
		}

		private static ShardStore CreateShards(int count)
		{
			var store = new ShardStore(count);

			for (var i = 0; i < count; i++)
				store.Add(new TestShard(Guid.NewGuid(), i, $"shard-{i}"));

			return store;
		}

		[UnityTest]
		public IEnumerator RoundTrip_PreservesShardData([Values(0, 1, 10, 80, 1000)] int count) => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out _);
			var store = CreateShards(count);

			await manager.SaveAsync(Slot, store);
			var loaded = (await manager.LoadAsync(Slot)).AsShardStore();

			Assert.AreEqual(count, loaded.Count);

			foreach (var original in store)
			{
				Assert.IsTrue(loaded.TryGet<TestShard>(original.Identifier, out var shard), $"Missing shard {original.Identifier}.");
				var source = (TestShard)original;
				Assert.AreEqual(source.value, shard.value);
				Assert.AreEqual(source.text, shard.text);
				Assert.IsFalse(shard.IsDirty, "Loaded shards must start clean.");
			}
		});

		[UnityTest]
		public IEnumerator RoundTrip_PlainList_WorksWithoutShardStore() => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out _);
			var shards = new List<IDataShard> { new TestShard(Guid.NewGuid(), 42, "answer") };

			await manager.SaveAsync(Slot, shards);
			var loaded = await manager.LoadAsync(Slot);

			Assert.AreEqual(1, loaded.Count);
			Assert.AreEqual(42, ((TestShard)loaded[0]).value);
		});

		[UnityTest]
		public IEnumerator SaveLoad_MissingSlot_ExistsFalse_LoadThrows() => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out _);

			Assert.IsFalse(await manager.ExistsAsync("nope"));

			var threw = false;
			try { await manager.LoadAsync("nope"); }
			catch (InvalidOperationException) { threw = true; }

			Assert.IsTrue(threw, "Loading a missing slot must throw.");
		});

		[UnityTest]
		public IEnumerator IncrementalSave_SerializesOnlyDirtyShards_AndClearsOnlySnapshot() => AsyncTest.Run(async () =>
		{
			var serializer = new CountingSerializer();
			var layout = new CapturingLayout { FullSnapshot = false };
			var manager = new SaveManager(serializer, layout);
			var store = CreateShards(10);

			await manager.SaveAsync(Slot, store);
			Assert.AreEqual(10, serializer.SerializeCalls, "First save writes everything (all dirty).");
			Assert.AreEqual(10, layout.LastBlobCount);

			// Mark exactly two dirty.
			var dirtyA = (TestShard)store[2];
			var dirtyB = (TestShard)store[7];
			dirtyA.MarkDirty();
			dirtyB.MarkDirty();

			await manager.SaveAsync(Slot, store);

			Assert.AreEqual(12, serializer.SerializeCalls, "Second save must serialize only the two dirty shards.");
			Assert.AreEqual(2, layout.LastBlobCount);
			CollectionAssert.AreEquivalent(
				new[] { dirtyA.Identifier, dirtyB.Identifier },
				layout.LastBlobIds);
			Assert.IsFalse(dirtyA.IsDirty);
			Assert.IsFalse(dirtyB.IsDirty);
		});

		[UnityTest]
		public IEnumerator EnvelopeCache_ReusedWhileGenerationUnchanged_InvalidatedByAdd() => AsyncTest.Run(async () =>
		{
			var layout = new CapturingLayout { FullSnapshot = true };
			var manager = new SaveManager(new CountingSerializer(), layout);
			var store = CreateShards(5);

			await manager.SaveAsync(Slot, store);
			var firstTypes = layout.LastTypesArray;
			var firstRecords = layout.LastRecordsArray;

			await manager.SaveAsync(Slot, store);
			Assert.AreSame(firstTypes, layout.LastTypesArray, "Unchanged store must reuse the cached type table.");
			Assert.AreSame(firstRecords, layout.LastRecordsArray, "Unchanged store must reuse the cached records.");

			var generationBefore = store.Generation;
			store.Add(new TestShard(Guid.NewGuid(), 99));
			Assert.Greater(store.Generation, generationBefore, "Add must bump the generation.");

			await manager.SaveAsync(Slot, store);
			Assert.AreEqual(6, layout.LastBlobCount, "Rebuilt envelope must cover the added shard.");
		});

		[UnityTest]
		public IEnumerator BackgroundSerialization_RoundTrips() => AsyncTest.Run(async () =>
		{
			var storage = new MemoryStorage();
			var manager = new SaveManager(new CountingSerializer(background: true), new SingleFileSaveLayout(storage));
			var store = CreateShards(20);

			await manager.SaveAsync(Slot, store);
			var loaded = (await manager.LoadAsync(Slot)).AsShardStore();

			Assert.AreEqual(20, loaded.Count);

			foreach (var original in store)
			{
				Assert.IsTrue(loaded.TryGet<TestShard>(original.Identifier, out var shard));
				Assert.AreEqual(((TestShard)original).value, shard.value);
			}
		});

		[UnityTest]
		public IEnumerator Migration_BlobLevel_RewritesTypeAndFields() => AsyncTest.Run(async () =>
		{
			var migrations = new MigrationRegistry();
			migrations.Register(new LegacyToModernMigration());

			var manager = CreateManager(out _, migrations);
			var legacyId = Guid.NewGuid();
			var shards = new List<IDataShard> { new LegacyShard(legacyId, 1234) };

			await manager.SaveAsync(Slot, shards);
			var loaded = await manager.LoadAsync(Slot);

			Assert.AreEqual(1, loaded.Count);
			var modern = loaded[0] as ModernShard;
			Assert.IsNotNull(modern, $"Expected ModernShard, got {loaded[0].GetType().Name}.");
			Assert.AreEqual(1234, modern.points, "Field must be carried over by the blob migration.");
			Assert.IsTrue(modern.Identifier.Equals((SerializableGuid)legacyId), "Identity must survive migration.");
		});

		[Test]
		public void ShardStore_DuplicateIdInCtor_Throws()
		{
			var id = Guid.NewGuid();
			var source = new List<IDataShard>
			{
				new TestShard(id, 1),
				new TestShard(id, 2)
			};

			Assert.Throws<ArgumentException>(() => new ShardStore(source));
		}

		[UnityTest]
		public IEnumerator Migration_Cycle_ThrowsInsteadOfHanging() => AsyncTest.Run(async () =>
		{
			var migrations = new MigrationRegistry();
			// Legacy v1 -> Modern v1 -> Legacy v1 -> ... : an accidental cycle.
			migrations.Register(new LegacyToModernMigration(toVersion: 1));
			migrations.Register(new ModernToLegacyCycleMigration());

			var manager = CreateManager(out _, migrations);
			await manager.SaveAsync(Slot, new List<IDataShard> { new LegacyShard(Guid.NewGuid(), 1) });

			var threw = false;
			try { await manager.LoadAsync(Slot); }
			catch (InvalidOperationException e) when (e.Message.Contains("exceeded")) { threw = true; }

			Assert.IsTrue(threw, "A migration cycle must throw, not loop forever.");
		});

		[UnityTest]
		public IEnumerator Migration_BrokenChain_Throws() => AsyncTest.Run(async () =>
		{
			var migrations = new MigrationRegistry();
			// Chain stops at ModernShard v1, but its schema declares v2 -> broken.
			migrations.Register(new LegacyToModernMigration(toVersion: 1));

			var manager = CreateManager(out _, migrations);
			await manager.SaveAsync(Slot, new List<IDataShard> { new LegacyShard(Guid.NewGuid(), 1) });

			var threw = false;
			try { await manager.LoadAsync(Slot); }
			catch (InvalidOperationException e) when (e.Message.Contains("Migration chain is broken")) { threw = true; }

			Assert.IsTrue(threw);
		});
	}
}
