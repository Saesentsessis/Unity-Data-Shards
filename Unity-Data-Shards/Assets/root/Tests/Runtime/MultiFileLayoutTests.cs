using System;
using System.Collections;
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

		private static ShardStore CreateShards(int count)
		{
			var store = new ShardStore(count);

			for (var i = 0; i < count; i++)
				store.Add(new TestShard(Guid.NewGuid(), i, $"shard-{i}"));

			return store;
		}

		[UnityTest]
		public IEnumerator RoundTrip_PreservesShardData([Values(0, 1, 10, 80)] int count) => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out var storage);
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
			var manager = CreateManager(out var storage);
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
			var manager = CreateManager(out var storage);
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
			var manager = CreateManager(out var storage);
			await manager.SaveAsync(Slot, CreateShards(3));

			var shardKey = storage.Data.Keys.First(k => k != Slot);
			storage.Data.Remove(shardKey);

			var threw = false;
			try { await manager.LoadAsync(Slot); }
			catch (SaveCorruptedException) { threw = true; }

			Assert.IsTrue(threw, "A missing shard file must be reported as corruption.");
		});

		[UnityTest]
		public IEnumerator Delete_RemovesEnvelopeAndAllShardFiles() => AsyncTest.Run(async () =>
		{
			var manager = CreateManager(out var storage);
			await manager.SaveAsync(Slot, CreateShards(5));
			Assert.AreEqual(6, storage.Data.Count);

			await manager.DeleteAsync(Slot);

			Assert.AreEqual(0, storage.Data.Count, "Delete must remove the envelope and every shard file.");
			Assert.IsFalse(await manager.ExistsAsync(Slot));
		});
	}
}
