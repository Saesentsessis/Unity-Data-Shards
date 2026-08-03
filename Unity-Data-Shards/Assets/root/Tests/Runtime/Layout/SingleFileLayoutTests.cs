using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Serialization;
using UnityEngine.TestTools;
#if PERSISTENCE_HAS_UNITASK
using SampleTask = Cysharp.Threading.Tasks.UniTask<System.ValueTuple<Saesentsessis.Persistence.Tests.MemoryStorage, Saesentsessis.Persistence.SaveManager>>;
#else
using SampleTask = System.Threading.Tasks.Task<System.ValueTuple<Saesentsessis.Persistence.Tests.MemoryStorage, Saesentsessis.Persistence.SaveManager>>;
#endif

namespace Saesentsessis.Persistence.Tests
{
	public class SingleFileCorruptionTests
	{
		private const string Slot = "fuzz-slot";

		private static async SampleTask SaveSample()
		{
			var storage = new MemoryStorage();
			using var manager = new SaveManager(new UnityJsonSerializer(), new SingleFileSaveLayout(storage));
			var shards = new List<IDataShard>
			{
				new TestShard(Guid.NewGuid(), 1, "one"),
				new TestShard(Guid.NewGuid(), 2, "two")
			};
			await manager.SaveAsync(Slot, shards);
			return (storage, manager);
		}

		[UnityTest]
		public IEnumerator TruncationAtEveryOffset_ThrowsCorrupted() => AsyncTest.Run(async () =>
		{
			var (storage, manager) = await SaveSample();
			var intact = storage.Data[Slot];

			for (var length = 0; length < intact.Length; length++)
			{
				var truncated = new byte[length];
				Array.Copy(intact, truncated, length);
				storage.Data[Slot] = truncated;

				var threw = false;
				try { await manager.LoadAsync(Slot); }
				catch (SaveCorruptedException) { threw = true; }

				Assert.IsTrue(threw, $"Truncation to {length}/{intact.Length} bytes must throw SaveCorruptedException.");
			}
		});

		[UnityTest]
		public IEnumerator SingleBitFlip_ThrowsCorrupted() => AsyncTest.Run(async () =>
		{
			var (storage, manager) = await SaveSample();
			var intact = storage.Data[Slot];

			// Every byte past the checksum field is covered by the hash.
			for (var i = 0; i < intact.Length; i++)
			{
				var mutated = (byte[])intact.Clone();
				mutated[i] ^= 0x01;
				storage.Data[Slot] = mutated;

				var threw = false;
				try { await manager.LoadAsync(Slot); }
				catch (SaveCorruptedException) { threw = true; }

				Assert.IsTrue(threw, $"Bit flip at offset {i} must throw SaveCorruptedException.");
			}
		});
	}
}
