using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Serialization;
using Saesentsessis.Persistence.Storage;
using Saesentsessis.Persistence.Storage.Transforms;
using Saesentsessis.Persistence.Threading;
using Unity.Collections;
using UnityEngine.TestTools;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using BoolTask = Cysharp.Threading.Tasks.UniTask<bool>;
using IntTask = Cysharp.Threading.Tasks.UniTask<int>;
using StorageReadTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.Core.StorageReadResult>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using IntTask = System.Threading.Tasks.Task<int>;
using StorageReadTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Core.StorageReadResult>;
#endif

namespace Saesentsessis.Persistence.Tests
{
	public class SaveSlotBrowserTests
	{
		private static byte[] Payload(int length, int seed = 0)
		{
			var bytes = new byte[length];

			for (var i = 0; i < length; i++)
				bytes[i] = (byte)((i * 31 + seed) % 251);

			return bytes;
		}

		private static void Put(MemoryStorage storage, string key, int length)
			=> storage.Data[key] = Payload(length, length);

		[UnityTest]
		public IEnumerator MultiFile_ShardsCollapseIntoOneSlot() => AsyncTest.Run(async () =>
		{
			var storage = new MemoryStorage();
			var browser = new SaveSlotBrowser(storage, new MultiFileSaveLayout(storage));

			Put(storage, "save1", 100);
			Put(storage, "save1/aaaa", 10);
			Put(storage, "save1/bbbb", 20);

			var slots = new List<SaveSlotInfo>();
			var count = await browser.PopulateAsync(slots);

			Assert.AreEqual(1, count);
			Assert.AreEqual(1, slots.Count);
			Assert.AreEqual("save1", slots[0].Slot);
			Assert.AreEqual(3, slots[0].KeyCount, "Envelope plus two shard files.");
			Assert.AreEqual(130, slots[0].TotalBytes, "Sizes of every key the slot owns.");
		});

		[UnityTest]
		public IEnumerator SlotNameReusesTheEnvelopeKeyString() => AsyncTest.Run(async () =>
		{
			// The grouping pass reuses the slot's own key string rather than substringing it, which
			// is what keeps a listing free of slot-name allocations.
			var storage = new MemoryStorage();
			var browser = new SaveSlotBrowser(storage, new MultiFileSaveLayout(storage));

			Put(storage, "save1", 10);
			Put(storage, "save1/aaaa", 10);

			var slots = new List<SaveSlotInfo>();
			await browser.PopulateAsync(slots);

			var envelopeKey = storage.Data.Keys.First(k => k == "save1");
			Assert.AreSame(envelopeKey, slots[0].Slot, "Slot name should be the envelope key instance itself.");
		});

		[UnityTest]
		public IEnumerator PrefixSharingSlots_StaySeparate() => AsyncTest.Run(async () =>
		{
			// The whole grouping pass rests on ordinal order placing a slot's own key immediately
			// before its shards: '/' (0x2F) sorts below every digit and letter, so
			// save1 < save1/ab < save1x < save2. If that ever stops holding, this fails first.
			var storage = new MemoryStorage();
			var browser = new SaveSlotBrowser(storage, new MultiFileSaveLayout(storage));

			Put(storage, "save1", 1);
			Put(storage, "save1/aaaa", 2);
			Put(storage, "save1x", 4);
			Put(storage, "save2", 8);

			var slots = new List<SaveSlotInfo>();
			await browser.PopulateAsync(slots);

			var byName = slots.ToDictionary(s => s.Slot);

			Assert.AreEqual(3, slots.Count, "save1, save1x and save2 are three distinct slots.");
			Assert.AreEqual(3, byName["save1"].TotalBytes, "save1 owns its envelope and its shard, nothing else.");
			Assert.AreEqual(2, byName["save1"].KeyCount);
			Assert.AreEqual(4, byName["save1x"].TotalBytes, "save1x must not absorb save1's shard.");
			Assert.AreEqual(8, byName["save2"].TotalBytes);
		});

		[UnityTest]
		public IEnumerator PunctuatedSiblingSlots_DoNotSplitAGroup() => AsyncTest.Run(async () =>
		{
			// The case a plain ordinal sort of whole keys gets wrong, and the reason the browser
			// sorts through the mapper instead. '-' (0x2D) and '.' (0x2E) collate BELOW '/' (0x2F),
			// so "save-1" sorts between "save" and "save/aaaa" and would split "save" into two
			// entries. Slot names with a dash, a dot or a space are entirely ordinary.
			var storage = new MemoryStorage();
			var browser = new SaveSlotBrowser(storage, new MultiFileSaveLayout(storage));

			Put(storage, "save", 1);
			Put(storage, "save/aaaa", 2);
			Put(storage, "save-1", 4);
			Put(storage, "autosave.2", 8);

			var slots = new List<SaveSlotInfo>();
			await browser.PopulateAsync(slots);

			Assert.AreEqual(3, slots.Count, "save, save-1 and autosave.2 — with save appearing once.");

			var save = slots.Single(s => s.Slot == "save");

			Assert.AreEqual(2, save.KeyCount, "'save' must own its envelope and its shard as one entry.");
			Assert.AreEqual(3, save.TotalBytes);
		});

		[UnityTest]
		public IEnumerator SingleFile_OneKeyPerSlot() => AsyncTest.Run(async () =>
		{
			var storage = new MemoryStorage();
			var browser = new SaveSlotBrowser(storage, new SingleFileSaveLayout(storage));

			Put(storage, "a", 10);
			Put(storage, "b", 20);

			var slots = new List<SaveSlotInfo>();
			var count = await browser.PopulateAsync(slots);

			Assert.AreEqual(2, count);
			CollectionAssert.AreEquivalent(new[] { "a", "b" }, slots.Select(s => s.Slot).ToArray());
			Assert.IsTrue(slots.All(s => s.KeyCount == 1));
		});

		[UnityTest]
		public IEnumerator ModifiedTime_TakesTheNewestKeyInTheSlot() => AsyncTest.Run(async () =>
		{
			var storage = new MemoryStorage { ModifiedTicks = 12345 };
			var browser = new SaveSlotBrowser(storage, new MultiFileSaveLayout(storage));

			Put(storage, "save1", 10);
			Put(storage, "save1/aaaa", 10);

			var slots = new List<SaveSlotInfo>();
			await browser.PopulateAsync(slots);

			Assert.AreEqual(12345, slots[0].ModifiedUtcTicks);
			Assert.IsTrue(slots[0].HasModifiedTime);
			Assert.AreEqual(DateTimeKind.Utc, slots[0].ModifiedUtc.Kind);
		});

		[UnityTest]
		public IEnumerator Populate_AppendsWithoutClearing() => AsyncTest.Run(async () =>
		{
			var storage = new MemoryStorage();
			var browser = new SaveSlotBrowser(storage, new SingleFileSaveLayout(storage));

			Put(storage, "a", 10);

			var slots = new List<SaveSlotInfo> { new("pre-existing", 0, 0, 0) };
			await browser.PopulateAsync(slots);

			Assert.AreEqual(2, slots.Count, "The sink is appended to, never cleared.");
			Assert.AreEqual("pre-existing", slots[0].Slot);
		});

		[UnityTest]
		public IEnumerator EmptyStorage_ProducesNothing() => AsyncTest.Run(async () =>
		{
			var storage = new MemoryStorage();
			var browser = new SaveSlotBrowser(storage, new SingleFileSaveLayout(storage));

			var slots = new List<SaveSlotInfo>();

			Assert.AreEqual(0, await browser.PopulateAsync(slots));
			Assert.IsEmpty(slots);
		});

		[UnityTest]
		public IEnumerator NonListableStorage_IsReportedNotThrownBlindly() => AsyncTest.Run(async () =>
		{
			var storage = new UnlistableStorage();
			var browser = new SaveSlotBrowser(storage, new SingleFileSaveLayout(storage));

			Assert.IsFalse(browser.CanList, "CanList is how a caller avoids the exception below.");

			// PopulateAsync is an async method, so the exception lands in the returned task rather
			// than being raised at the call site — Assert.Throws would never see it.
			var threw = false;

			try { await browser.PopulateAsync(new List<SaveSlotInfo>()); }
			catch (NotSupportedException) { threw = true; }

			Assert.IsTrue(threw, "Listing an unlistable storage must fail loudly, not report zero slots.");
		});

		#region Header reads

		[UnityTest]
		public IEnumerator ReadHeader_ReportsEnvelopeContents() => AsyncTest.Run(async () =>
		{
			var storage = new MemoryStorage();
			var layout = new SingleFileSaveLayout(storage);
			using var manager = new SaveManager(new UnityJsonSerializer(), layout);
			var store = new ShardStore();

			store.Add(new TestShard(Guid.NewGuid(), 1, "a"));
			store.Add(new TestShard(Guid.NewGuid(), 2, "b"));

			await manager.SaveAsync("slot", store);

			var header = await new SaveSlotBrowser(storage, layout).ReadHeaderAsync("slot");

			Assert.AreEqual(SaveSlotStatus.Ok, header.Status);
			Assert.AreEqual(SaveEnvelope.CurrentFormatVersion, header.FormatVersion);
			Assert.AreEqual(2, header.RecordCount);
			Assert.AreEqual(1, header.TypeCount, "Both shards share one type.");
			Assert.Greater(header.TimestampUtc, 0);
		});

		[UnityTest]
		public IEnumerator ReadHeader_MultiFileEnvelopeDecodesTheSameWay() => AsyncTest.Run(async () =>
		{
			// The point of the design: multi-file's slot key holds ONLY an envelope, single-file's
			// holds envelope + index + payload, and the same decode handles both because every
			// layout puts the envelope at offset 0.
			var storage = new MemoryStorage();
			var layout = new MultiFileSaveLayout(storage);
			using var manager = new SaveManager(new UnityJsonSerializer(), layout);
			var store = new ShardStore();

			store.Add(new TestShard(Guid.NewGuid(), 7, "multi"));
			await manager.SaveAsync("slot", store);

			var header = await new SaveSlotBrowser(storage, layout).ReadHeaderAsync("slot");

			Assert.AreEqual(SaveSlotStatus.Ok, header.Status);
			Assert.AreEqual(1, header.RecordCount);
		});

		[UnityTest]
		public IEnumerator ReadHeader_MissingSlot() => AsyncTest.Run(async () =>
		{
			var storage = new MemoryStorage();
			var header = await new SaveSlotBrowser(storage, new SingleFileSaveLayout(storage))
				.ReadHeaderAsync("absent");

			Assert.AreEqual(SaveSlotStatus.Missing, header.Status);
		});

		[UnityTest]
		public IEnumerator ReadHeader_TamperedSaveIsReportedNotThrown() => AsyncTest.Run(async () =>
		{
			var storage = new MemoryStorage();
			var layout = new SingleFileSaveLayout(storage);
			using var manager = new SaveManager(new UnityJsonSerializer(), layout);
			var store = new ShardStore();

			store.Add(new TestShard(Guid.NewGuid(), 1, "a"));
			await manager.SaveAsync("slot", store);

			// Past the checksum field, so the stored checksum no longer matches the content.
			storage.Data["slot"][20] ^= 0xFF;

			var header = await new SaveSlotBrowser(storage, layout).ReadHeaderAsync("slot");

			Assert.AreEqual(SaveSlotStatus.Corrupted, header.Status,
				"A browser listing a folder has to survive one bad file.");
		});

		[UnityTest]
		public IEnumerator ReadHeader_HostileTimestampDoesNotThrow() => AsyncTest.Run(async () =>
		{
			// The envelope's timestamp is eight raw bytes inside the hashed region, and the hash is
			// unkeyed — whoever edits a save recomputes it. new DateTime(ticks) throws for anything
			// outside [0, DateTime.MaxValue.Ticks], so a load-game screen formatting this field
			// would take the exception. The header must stay readable and the date read as absent.
			var storage = new MemoryStorage();
			var layout = new SingleFileSaveLayout(storage);
			using var manager = new SaveManager(new UnityJsonSerializer(), layout);
			var store = new ShardStore();

			store.Add(new TestShard(Guid.NewGuid(), 1, "a"));
			await manager.SaveAsync("slot", store);

			// TimestampUtc sits at offset 16 of the v4 header; long.MaxValue is far past any date.
			var bytes = storage.Data["slot"];
			BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), long.MaxValue);
			EnvelopeCodec.PatchChecksum(bytes);

			var header = await new SaveSlotBrowser(storage, layout).ReadHeaderAsync("slot");

			Assert.AreEqual(SaveSlotStatus.Ok, header.Status, "The rest of the header is still valid.");
			Assert.IsFalse(header.HasTimestamp, "A tick count no date can hold reads as absent.");
			Assert.DoesNotThrow(() => _ = header.WrittenUtc, "Formatting it must not throw.");
			Assert.AreEqual(DateTime.MinValue, header.WrittenUtc);
		});

		[UnityTest]
		public IEnumerator ReadHeader_StorageFailureIsReportedNotThrown() => AsyncTest.Run(async () =>
		{
			// The documented contract is report-not-throw, and it has to hold below the decoder
			// too: an I/O error on one slot must not abort a list of two hundred.
			var browser = new SaveSlotBrowser(new ThrowingStorage(), new SingleFileSaveLayout(new MemoryStorage()));

			var header = await browser.ReadHeaderAsync("slot");

			Assert.AreEqual(SaveSlotStatus.Unreadable, header.Status);
		});

		[UnityTest]
		public IEnumerator ReadHeader_ForeignDataIsDistinguished() => AsyncTest.Run(async () =>
		{
			var storage = new MemoryStorage();
			storage.Data["slot"] = Payload(256, 5);

			var header = await new SaveSlotBrowser(storage, new SingleFileSaveLayout(storage))
				.ReadHeaderAsync("slot");

			Assert.That(header.Status, Is.EqualTo(SaveSlotStatus.Foreign).Or.EqualTo(SaveSlotStatus.Corrupted),
				"Random bytes must not decode as a save.");
		});

		#endregion

		/// <summary>Fails every read, standing in for a permission error or a dead network share.</summary>
		private sealed class ThrowingStorage : IStorage
		{
			public StorageReadTask TryReadAsync(string key, Allocator allocator, CancellationToken cancellation = default)
				=> throw new IOException("simulated I/O failure");

			public TaskType WriteAsync(string key, NativeArray<byte> data, CancellationToken cancellation = default)
				=> PersistenceTask.CompletedTask;

			public BoolTask ExistsAsync(string key, CancellationToken cancellation = default)
				=> PersistenceTask.FromResult(true);

			public TaskType DeleteAsync(string key, CancellationToken cancellation = default)
				=> PersistenceTask.CompletedTask;

			public void Dispose() { }
		}

		/// <summary>Storage without the listing capability, for the negative case.</summary>
		private sealed class UnlistableStorage : IStorage
		{
			public StorageReadTask TryReadAsync(string key, Allocator allocator, CancellationToken cancellation = default)
				=> PersistenceTask.FromResult(StorageReadResult.NotFound);

			public TaskType WriteAsync(string key, NativeArray<byte> data, CancellationToken cancellation = default)
				=> PersistenceTask.CompletedTask;

			public BoolTask ExistsAsync(string key, CancellationToken cancellation = default)
				=> PersistenceTask.FromResult(false);

			public TaskType DeleteAsync(string key, CancellationToken cancellation = default)
				=> PersistenceTask.CompletedTask;

			public void Dispose() { }
		}
	}
}
