using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Serialization;
using UnityEngine.TestTools;

namespace Saesentsessis.Persistence.Tests
{
	public class MultiFileLayoutTests
	{
		private const string Slot = "multi-slot";

		private static SaveManager CreateManager(out MemoryStorage storage)
		{
			storage = new MemoryStorage();
			return new SaveManager(new UnityJsonSerializer(), new MultiFileSaveLayout(storage));
		}

		/// <summary>Every stored key except the envelope, i.e. the shard files.</summary>
		private static HashSet<string> ShardKeys(MemoryStorage storage)
			=> storage.Data.Keys.Where(k => k != Slot).ToHashSet();

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
			using var manager = CreateManager(out var storage);
			var store = CreateShards(count);

			await manager.SaveAsync(Slot, store);

			// One file per shard + the envelope.
			Assert.AreEqual(count + 1, storage.Data.Count);

			var loaded = (await manager.LoadAsync(Slot)).AsShardStore();
			Assert.AreEqual(count, loaded.Count);

			foreach (var original in store)
			{
				Assert.IsTrue(loaded.TryGet<TestShard>(original.Identifier, out var shard));
				Assert.AreEqual(((TestShard)original).value, shard.value);
				Assert.AreEqual(((TestShard)original).text, shard.text);
			}
		});

		[UnityTest]
		public IEnumerator IncrementalSave_RewritesOnlyDirtyFilesAndEnvelope() => AsyncTest.Run(async () =>
		{
			using var manager = CreateManager(out var storage);
			var store = CreateShards(10);

			await manager.SaveAsync(Slot, store);
			Assert.AreEqual(11, storage.WriteCounts.Values.Sum(), "First save: 10 shard files + envelope.");

			((TestShard)store[3]).MarkDirty();
			((TestShard)store[6]).MarkDirty();

			await manager.SaveAsync(Slot, store);

			Assert.AreEqual(14, storage.WriteCounts.Values.Sum(), "Second save: 2 dirty files + envelope only.");
			Assert.AreEqual(2, storage.WriteCounts[Slot], "Envelope is rewritten every save.");
			Assert.AreEqual(8, storage.WriteCounts.Count(kv => kv.Value == 1 && kv.Key != Slot), "Clean shard files untouched.");
		});

		[UnityTest]
		public IEnumerator CorruptedShardFile_ThrowsCorrupted() => AsyncTest.Run(async () =>
		{
			using var manager = CreateManager(out var storage);
			await manager.SaveAsync(Slot, CreateShards(3));

			// Flip one payload byte in the first shard file (past its 8-byte hash prefix).
			var shardKey = storage.Data.Keys.First(k => k != Slot);
			storage.Data[shardKey][8] ^= 0x01;

			var threw = false;
			try { await manager.LoadAsync(Slot); }
			catch (SaveCorruptedException) { threw = true; }

			Assert.IsTrue(threw, "A flipped shard-file byte must fail the per-file checksum.");
		});

		[UnityTest]
		public IEnumerator MissingShardFile_ThrowsCorrupted() => AsyncTest.Run(async () =>
		{
			using var manager = CreateManager(out var storage);
			await manager.SaveAsync(Slot, CreateShards(3));

			var shardKey = storage.Data.Keys.First(k => k != Slot);
			storage.Data.Remove(shardKey);

			var threw = false;
			try { await manager.LoadAsync(Slot); }
			catch (SaveCorruptedException) { threw = true; }

			Assert.IsTrue(threw, "A missing shard file must be reported as corruption.");
		});

		[UnityTest]
		public IEnumerator RemovingAShard_DeletesTheFileItOrphaned() => AsyncTest.Run(async () =>
		{
			using var manager = CreateManager(out var storage);
			var store = CreateShards(5);

			await manager.SaveAsync(Slot, store);
			Assert.AreEqual(6, storage.Data.Count, "5 shard files + envelope.");

			// Compared as key sets rather than by formatting the id: shard keys carry the guid's raw
			// byte order, which is not what Guid.ToString("N") prints.
			var before = ShardKeys(storage);

			Assert.IsTrue(store.Remove(store[2].Identifier));

			await manager.SaveAsync(Slot, store);

			var after = ShardKeys(storage);

			Assert.AreEqual(4, after.Count,
				"The file for the removed shard must not survive the save that dropped it.");
			Assert.IsTrue(after.IsProperSubsetOf(before),
				"The surviving files must be the originals minus the removed shard.");

			var loaded = (await manager.LoadAsync(Slot)).AsShardStore();
			Assert.AreEqual(4, loaded.Count);
		});

		[UnityTest]
		public IEnumerator SwappingAShard_DeletesTheReplacedFile() => AsyncTest.Run(async () =>
		{
			// Membership changed without the count changing, so the cheap length gate cannot settle
			// it — this is the case an "only when it shrinks" check would miss.
			using var manager = CreateManager(out var storage);
			var store = CreateShards(3);

			await manager.SaveAsync(Slot, store);

			var before = ShardKeys(storage);

			store.Remove(store[1].Identifier);
			store.Add(new TestShard(Guid.NewGuid(), 99, "replacement"));

			await manager.SaveAsync(Slot, store);

			var after = ShardKeys(storage);

			Assert.AreEqual(3, after.Count, "3 shard files, with no leftover from the replaced one.");
			Assert.AreEqual(1, before.Except(after).Count(), "Exactly the replaced shard's file should be gone.");
			Assert.AreEqual(1, after.Except(before).Count(), "Exactly the replacement's file should be new.");
		});

		[UnityTest]
		public IEnumerator LoadThenRemoveThenSave_CleansUpAcrossSessions() => AsyncTest.Run(async () =>
		{
			// The realistic sequence: a fresh manager that never wrote this slot learns the on-disk
			// membership from the load, so the following save can still diff against it.
			using var first = CreateManager(out var storage);
			await first.SaveAsync(Slot, CreateShards(4));

			using var second = new SaveManager(new UnityJsonSerializer(), new MultiFileSaveLayout(storage));
			var loaded = (await second.LoadAsync(Slot)).AsShardStore();

			loaded.Remove(loaded[0].Identifier);

			foreach (var shard in loaded)
				((TestShard)shard).MarkDirty();

			await second.SaveAsync(Slot, loaded);

			Assert.AreEqual(4, storage.Data.Count,
				"3 shard files + envelope; the removed shard's file must be swept by the diff.");
		});

		[UnityTest]
		public IEnumerator UnchangedMembership_DeletesNothing() => AsyncTest.Run(async () =>
		{
			using var manager = CreateManager(out var storage);
			var store = CreateShards(4);

			await manager.SaveAsync(Slot, store);
			((TestShard)store[1]).MarkDirty();
			await manager.SaveAsync(Slot, store);

			Assert.AreEqual(5, storage.Data.Count, "A save that changes no membership must delete nothing.");
			Assert.AreEqual(0, storage.DeleteCount, "The orphan sweep must not touch storage when nothing moved.");
		});

		[UnityTest]
		public IEnumerator RestoringARemovedShard_RewritesItsFileEvenThoughItIsClean() => AsyncTest.Run(async () =>
		{
			// The orphan sweep cuts both ways: the save that dropped the shard deleted its file, and
			// nothing about the shard object records that. Dirtiness alone would leave the next
			// envelope pointing at a file that no longer exists.
			using var manager = CreateManager(out var storage);
			var store = CreateShards(3);

			await manager.SaveAsync(Slot, store);

			var restored = store[1];
			Assert.IsTrue(store.Remove(restored.Identifier));

			await manager.SaveAsync(Slot, store);
			Assert.AreEqual(3, storage.Data.Count, "2 shard files + envelope after the removal.");

			// Back in, untouched — IsDirty is false, because the load/save cycle cleared it.
			Assert.IsTrue(store.Add(restored));
			Assert.IsFalse(restored.IsDirty, "The shard must be clean, or the test proves nothing.");

			await manager.SaveAsync(Slot, store);

			Assert.AreEqual(4, storage.Data.Count, "The restored shard's file must be written again.");

			var loaded = (await manager.LoadAsync(Slot)).AsShardStore();
			Assert.AreEqual(3, loaded.Count);
			Assert.IsTrue(loaded.TryGet<TestShard>(restored.Identifier, out _),
				"The restored shard must load back, not fail as a missing file.");
		});

		[UnityTest]
		public IEnumerator SaveAs_WritesEveryShardIntoTheNewSlot() => AsyncTest.Run(async () =>
		{
			// Loading clears every dirty flag, so "save this loaded game under another name" would
			// otherwise commit an envelope with no shard files at all behind it.
			using var manager = CreateManager(out var storage);
			await manager.SaveAsync(Slot, CreateShards(3));

			var loaded = (await manager.LoadAsync(Slot)).AsShardStore();

			foreach (var shard in loaded)
				Assert.IsFalse(shard.IsDirty, "A load must leave its shards clean.");

			await manager.SaveAsync("copy", loaded);

			Assert.AreEqual(8, storage.Data.Count, "Both slots hold 3 shard files + an envelope.");

			var reloaded = (await manager.LoadAsync("copy")).AsShardStore();
			Assert.AreEqual(3, reloaded.Count);
		});

		[UnityTest]
		public IEnumerator FreshManagerOverAnExistingSlot_WritesEverythingOnce() => AsyncTest.Run(async () =>
		{
			// A layout that has neither read nor written the slot knows nothing about it and says so,
			// rather than assuming the files are still there. The first save through it is therefore
			// a full write; only once membership is established does incrementality resume.
			using var first = CreateManager(out var storage);
			var store = CreateShards(4);

			await first.SaveAsync(Slot, store);
			Assert.AreEqual(5, storage.WriteCounts.Values.Sum(), "4 shard files + envelope.");

			foreach (var shard in store)
				Assert.IsFalse(shard.IsDirty, "A successful save must leave its shards clean.");

			using var second = new SaveManager(new UnityJsonSerializer(), new MultiFileSaveLayout(storage));
			await second.SaveAsync(Slot, store);

			Assert.AreEqual(10, storage.WriteCounts.Values.Sum(),
				"Nothing is dirty, but the new layout cannot vouch for the files, so it rewrites them.");
			Assert.AreEqual(5, storage.Data.Count, "Same ids, so no new keys appear.");

			((TestShard)store[0]).MarkDirty();
			await second.SaveAsync(Slot, store);

			Assert.AreEqual(12, storage.WriteCounts.Values.Sum(),
				"Membership is known now: one dirty file + the envelope.");
		});

		[UnityTest]
		public IEnumerator Delete_RemovesEnvelopeAndAllShardFiles() => AsyncTest.Run(async () =>
		{
			using var manager = CreateManager(out var storage);
			await manager.SaveAsync(Slot, CreateShards(5));
			Assert.AreEqual(6, storage.Data.Count);

			await manager.DeleteAsync(Slot);

			Assert.AreEqual(0, storage.Data.Count, "Delete must remove the envelope and every shard file.");
			Assert.IsFalse(await manager.ExistsAsync(Slot));
		});
	}
}
