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
	public class FileStorageListingTests
	{
		private string _directory;
		private FileStorage _storage;

		[SetUp]
		public void SetUp()
		{
			_directory = Path.Combine(Path.GetTempPath(), "uds-list-" + Guid.NewGuid().ToString("N"));
			_storage = new FileStorage(_directory);
		}

		[TearDown]
		public void TearDown()
		{
			_storage.Dispose();

			if (Directory.Exists(_directory))
				Directory.Delete(_directory, recursive: true);
		}

		private async TaskType Write(string key, int length)
		{
			var bytes = new byte[length];
			var data = new NativeArray<byte>(bytes, Allocator.Persistent);

			try { await _storage.WriteAsync(key, data); }
			finally { data.Dispose(); }
		}

		private async IntTask List(List<StorageKeyInfo> into) => await _storage.PopulateAsync(into);

		[UnityTest]
		public IEnumerator ListsWrittenKeysWithSizes() => AsyncTest.Run(async () =>
		{
			await Write("a", 10);
			await Write("b", 20);

			var keys = new List<StorageKeyInfo>();
			var count = await List(keys);

			Assert.AreEqual(2, count);
			CollectionAssert.AreEquivalent(new[] { "a", "b" }, keys.Select(k => k.Key).ToArray());
			Assert.AreEqual(10, keys.Single(k => k.Key == "a").Size);
			Assert.IsTrue(keys.All(k => k.HasModifiedTime), "A filesystem always has a write time.");
		});

		[UnityTest]
		public IEnumerator NestedKeysKeepTheirSeparator() => AsyncTest.Run(async () =>
		{
			// MultiFileSaveLayout addresses shards this way, and the key must come back in the same
			// form it went in — with '/', not the platform separator.
			await Write("slot/0123456789abcdef0123456789abcdef", 8);

			var keys = new List<StorageKeyInfo>();
			await List(keys);

			Assert.AreEqual("slot/0123456789abcdef0123456789abcdef", keys.Single().Key);
		});

		[UnityTest]
		public IEnumerator EnumeratedKeysRoundTripThroughRead() => AsyncTest.Run(async () =>
		{
			await Write("slot/aaaa", 4);
			await Write("plain", 4);

			var keys = new List<StorageKeyInfo>();
			await List(keys);

			foreach (var info in keys)
			{
				var read = await _storage.TryReadAsync(info.Key, Allocator.Persistent);

				try
				{
					Assert.IsTrue(read.Found, $"Enumerated key '{info.Key}' must be readable as-is.");
					Assert.AreEqual(info.Size, read.Data.Length);
				}
				finally
				{
					read.Dispose();
				}
			}
		});

		[UnityTest]
		public IEnumerator TempAndBackupFilesAreNotListedAsSaves() => AsyncTest.Run(async () =>
		{
			await Write("slot", 10);

			// Leftovers the crash-safe write dance can strand.
			File.WriteAllBytes(Path.Combine(_directory, "slot.save.tmp"), new byte[3]);
			File.WriteAllBytes(Path.Combine(_directory, "other.save.tmp"), new byte[3]);

			var keys = new List<StorageKeyInfo>();
			await List(keys);

			Assert.AreEqual(1, keys.Count);
			Assert.AreEqual("slot", keys[0].Key);
		});

		[UnityTest]
		public IEnumerator BackupOnlySlotIsListed() => AsyncTest.Run(async () =>
		{
			await Write("slot", 10);

			// Simulate a crash between the two moves: the live file is gone, the backup remains.
			var path = Path.Combine(_directory, "slot.save");
			File.Move(path, path + ".bak");

			var keys = new List<StorageKeyInfo>();
			await List(keys);

			Assert.AreEqual(1, keys.Count, "TryReadAsync restores a .bak, so the slot is still loadable.");
			Assert.AreEqual("slot", keys[0].Key);

			var read = await _storage.TryReadAsync("slot", Allocator.Persistent);

			try { Assert.IsTrue(read.Found); }
			finally { read.Dispose(); }
		});

		[UnityTest]
		public IEnumerator BackupAlongsideLiveFileIsNotDoubleCounted() => AsyncTest.Run(async () =>
		{
			await Write("slot", 10);
			File.WriteAllBytes(Path.Combine(_directory, "slot.save.bak"), new byte[3]);

			var keys = new List<StorageKeyInfo>();
			await List(keys);

			Assert.AreEqual(1, keys.Count, "The live file wins; its backup is not a second key.");
			Assert.AreEqual(10, keys[0].Size);
		});

		[UnityTest]
		public IEnumerator TwoIndependentInstancesSerializeOverTheSameFile() => AsyncTest.Run(async () =>
		{
			// The case a shared gate object could never have covered: the Save Viewer builds its own
			// FileStorage from a descriptor and has no way to reach a running game's SaveManager.
			// They coordinate through the resolved path instead, so a write and a read of the same
			// file cannot interleave even though neither instance knows the other exists.
			using var writer = new FileStorage(_directory);
			using var reader = new FileStorage(_directory);

			await Write("slot", 4096);

			// Issued together and awaited together: without a process-wide lock the read can catch
			// the write's rename dance between its two moves and see no file at all.
			var payload = new NativeArray<byte>(new byte[4096], Allocator.Persistent);

			try
			{
				var write = writer.WriteAsync("slot", payload);
				var read = reader.TryReadAsync("slot", Allocator.Persistent);

				await write;
				var result = await read;

				try
				{
					// Either ordering is valid; a torn state is not. The read either precedes the
					// write and sees the original, or follows it and sees the new bytes — never a
					// missing file mid-rename.
					Assert.IsTrue(result.Found, "The slot must never vanish while it is being rewritten.");
					Assert.AreEqual(4096, result.Data.Length);
				}
				finally
				{
					result.Dispose();
				}
			}
			finally
			{
				payload.Dispose();
			}
		});

		[UnityTest]
		public IEnumerator MissingRootDirectoryListsNothing() => AsyncTest.Run(async () =>
		{
			// A fresh install has no save folder yet.
			using var storage = new FileStorage(Path.Combine(_directory, "not-created-yet"));
			var keys = new List<StorageKeyInfo>();

			Assert.AreEqual(0, await storage.PopulateAsync(keys));
			Assert.IsEmpty(keys);
		});

		[UnityTest]
		public IEnumerator ForeignExtensionsAreIgnored() => AsyncTest.Run(async () =>
		{
			await Write("slot", 10);
			File.WriteAllBytes(Path.Combine(_directory, "notes.txt"), new byte[3]);

			var keys = new List<StorageKeyInfo>();
			await List(keys);

			Assert.AreEqual(1, keys.Count);
		});

		[UnityTest]
		public IEnumerator EndToEnd_BrowseThroughATransformChain() => AsyncTest.Run(async () =>
		{
			// Proves the two halves compose: listing forwards through the decorator, and the header
			// decode reverses the whole chain on the way back.
			var key = new byte[32];

			for (var i = 0; i < key.Length; i++)
				key[i] = (byte)(i + 1);

			using var aes = new AesCbcHmacTransform(key);
			using var storage = new TransformStorage(_storage, new DeflateTransform(), aes);
			var layout = new SingleFileSaveLayout(storage);
			using var manager = new SaveManager(new UnityJsonSerializer(), layout);
			var store = new ShardStore();

			store.Add(new TestShard(Guid.NewGuid(), 42, "encrypted"));
			await manager.SaveAsync("secret", store);

			var browser = new SaveSlotBrowser(storage, layout);
			var slots = new List<SaveSlotInfo>();

			Assert.AreEqual(1, await browser.PopulateAsync(slots));
			Assert.AreEqual("secret", slots[0].Slot);

			var header = await browser.ReadHeaderAsync("secret");

			Assert.AreEqual(SaveSlotStatus.Ok, header.Status, "The chain must reverse before the header is read.");
			Assert.AreEqual(1, header.RecordCount);
		});
	}
}
