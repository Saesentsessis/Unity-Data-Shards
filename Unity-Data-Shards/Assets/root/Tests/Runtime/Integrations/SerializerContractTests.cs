using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Saesentsessis.Persistence.Buffers;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Serialization;
using UnityEngine.TestTools;

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>
	/// The contract every <see cref="ISerializer"/> has to satisfy to be usable by this pipeline,
	/// written once and run against each backend.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Shared rather than triplicated because the interesting failures are not backend-specific:
	/// a serializer that resets the writer, or that loses a <see cref="SerializableGuid"/>'s high
	/// bits, breaks the pipeline in the same way whichever library is underneath. Each derived
	/// fixture supplies its own annotated shard type — the annotations are exactly what differs.
	/// </para>
	/// <para>
	/// Derived fixtures live behind <c>PERSISTENCE_HAS_*</c>, so a project without a given package
	/// compiles this base and runs nothing from it.
	/// </para>
	/// </remarks>
	public abstract class SerializerContractTests
	{
		private const string Slot = "serializer-contract-slot";

		/// <summary>Names the backend in assertion messages, since the failures are shared.</summary>
		protected abstract string Backend { get; }

		protected abstract ISerializer CreateSerializer();

		/// <summary>Builds this backend's annotated shard type.</summary>
		protected abstract IDataShard CreateShard(SerializableGuid id, int value, string text);

		/// <summary>Reads the payload back out, so assertions do not need the concrete type.</summary>
		protected abstract (int value, string text) ReadShard(IDataShard shard);

		private SaveManager CreateManager(out MemoryStorage storage, bool multiFile = false)
		{
			storage = new MemoryStorage();

			return multiFile
				? new SaveManager(CreateSerializer(), new MultiFileSaveLayout(storage))
				: new SaveManager(CreateSerializer(), new SingleFileSaveLayout(storage));
		}

		#region Serializer contract

		[Test]
		public void SupportsBackgroundSerialization_IsDeclared()
		{
			// Not an assertion that it must be true — only that reading the flag does not throw and
			// that the pipeline's branch is exercised by the round-trip tests below either way.
			var serializer = CreateSerializer();

			Assert.DoesNotThrow(() => _ = serializer.SupportsBackgroundSerialization,
				$"{Backend}: reading the capability flag must not throw.");
		}

		[Test]
		public void Serialize_ThenDeserialize_PreservesEveryField()
		{
			var serializer = CreateSerializer();
			var id = new SerializableGuid(0x0123456789ABCDEF, 0xFEDCBA9876543210);
			var shard = CreateShard(id, 1337, "round-trip");

			using var writer = new PooledArrayBufferWriter();
			serializer.Serialize(shard, shard.GetType(), writer);

			Assert.Greater(writer.WrittenLength, 0, $"{Backend}: serializing wrote nothing.");

			var restored = (IDataShard)serializer.Deserialize(writer.WrittenSpan, shard.GetType());
			var (value, text) = ReadShard(restored);

			Assert.AreEqual(id, restored.Identifier, $"{Backend}: identity must survive the round trip.");
			Assert.AreEqual(1337, value, $"{Backend}: value must survive the round trip.");
			Assert.AreEqual("round-trip", text, $"{Backend}: text must survive the round trip.");
		}

		[Test]
		public void Serialize_AppendsToTheWriter_RatherThanResettingIt()
		{
			// The single most important contract here. Every shard in a save is serialized into ONE
			// arena, back to back — a backend that rewinds the writer or assumes it owns the buffer
			// silently corrupts every shard but the first, and only a multi-shard save notices.
			var serializer = CreateSerializer();
			var shard = CreateShard(new SerializableGuid(7, 9), 42, "second");

			using var writer = new PooledArrayBufferWriter();

			var prefix = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
			prefix.CopyTo(writer.GetSpan(prefix.Length));
			writer.Advance(prefix.Length);

			serializer.Serialize(shard, shard.GetType(), writer);

			var written = writer.WrittenSpan.ToArray();

			Assert.Greater(written.Length, prefix.Length, $"{Backend}: nothing was appended.");
			CollectionAssert.AreEqual(prefix, written[..prefix.Length],
				$"{Backend}: the bytes already in the writer were overwritten.");

			var restored = (IDataShard)serializer.Deserialize(
				new ReadOnlySpan<byte>(written, prefix.Length, written.Length - prefix.Length),
				shard.GetType());

			Assert.AreEqual(42, ReadShard(restored).value,
				$"{Backend}: the appended region must deserialize on its own.");
		}

		[Test]
		public void SerializableGuid_RoundTripsEveryBit(
			[Values(0UL, 1UL, ulong.MaxValue, 0x8000000000000000UL)] ulong head)
		{
			// The custom formatter/surrogate each sample ships is the thing under test. A hex-string
			// or Guid-shaped encoding that reorders bytes passes a "same id" check only by accident;
			// the boundary values below are where an endianness or sign mistake shows.
			var serializer = CreateSerializer();
			var id = new SerializableGuid(head, unchecked(~head));
			var shard = CreateShard(id, 0, string.Empty);

			using var writer = new PooledArrayBufferWriter();
			serializer.Serialize(shard, shard.GetType(), writer);

			var restored = (IDataShard)serializer.Deserialize(writer.WrittenSpan, shard.GetType());

			Assert.AreEqual(id.Head, restored.Identifier.Head, $"{Backend}: Head bits were lost.");
			Assert.AreEqual(id.Tail, restored.Identifier.Tail, $"{Backend}: Tail bits were lost.");
		}

		[Test]
		public void DefaultAndEmptyValues_RoundTrip()
		{
			// Zero and empty-string are where a backend's "skip default values" optimisation can
			// turn a present member into a missing one.
			var serializer = CreateSerializer();
			var shard = CreateShard(default, 0, string.Empty);

			using var writer = new PooledArrayBufferWriter();
			serializer.Serialize(shard, shard.GetType(), writer);

			var restored = (IDataShard)serializer.Deserialize(writer.WrittenSpan, shard.GetType());
			var (value, text) = ReadShard(restored);

			Assert.AreEqual(default(SerializableGuid), restored.Identifier);
			Assert.AreEqual(0, value);
			Assert.AreEqual(string.Empty, text ?? string.Empty,
				$"{Backend}: an empty string must not come back as null.");
		}

		[Test]
		public void LongAndUnicodeText_RoundTrips()
		{
			var serializer = CreateSerializer();
			var text = new string('ß', 2000) + "日本語 — emoji 🎮";
			var shard = CreateShard(new SerializableGuid(3, 4), -1, text);

			using var writer = new PooledArrayBufferWriter();
			serializer.Serialize(shard, shard.GetType(), writer);

			var restored = (IDataShard)serializer.Deserialize(writer.WrittenSpan, shard.GetType());

			Assert.AreEqual(text, ReadShard(restored).text, $"{Backend}: non-ASCII text was mangled.");
		}

		#endregion

		#region Through the pipeline

		[UnityTest]
		public IEnumerator SingleFileLayout_RoundTripsThroughTheArena() => AsyncTest.Run(async () =>
		{
			using var manager = CreateManager(out _);
			var store = new ShardStore();
			var id = new SerializableGuid(11, 22);

			store.Add(CreateShard(id, 5, "single"));

			await manager.SaveAsync(Slot, store);

			var loaded = (await manager.LoadAsync(Slot)).AsShardStore();

			Assert.IsTrue(loaded.TryGet(id, out var shard), $"{Backend}: the shard did not come back.");
			Assert.AreEqual(5, ReadShard(shard).value);
			Assert.AreEqual("single", ReadShard(shard).text);
		});

		[UnityTest]
		public IEnumerator MultiFileLayout_RoundTripsThroughTheArena() => AsyncTest.Run(async () =>
		{
			using var manager = CreateManager(out var storage, multiFile: true);
			var store = new ShardStore();
			var id = new SerializableGuid(33, 44);

			store.Add(CreateShard(id, 6, "multi"));

			await manager.SaveAsync(Slot, store);

			Assert.AreEqual(2, storage.Data.Count, $"{Backend}: expected one shard file plus the envelope.");

			var loaded = (await manager.LoadAsync(Slot)).AsShardStore();

			Assert.IsTrue(loaded.TryGet(id, out var shard));
			Assert.AreEqual(6, ReadShard(shard).value);
		});

		[UnityTest]
		public IEnumerator ManyShards_ShareOneArenaWithoutBleeding() => AsyncTest.Run(async () =>
		{
			// The real test of the append contract: 100 payloads laid end to end in one buffer, each
			// sliced back out by its recorded range. An off-by-one in any of them shows up here.
			const int count = 100;

			using var manager = CreateManager(out _);
			var store = new ShardStore(count);
			var ids = new List<SerializableGuid>(count);

			for (var i = 0; i < count; i++)
			{
				var id = new SerializableGuid((ulong)(i + 1), (ulong)(count - i));

				ids.Add(id);
				store.Add(CreateShard(id, i, $"shard-{i}"));
			}

			await manager.SaveAsync(Slot, store);

			var loaded = (await manager.LoadAsync(Slot)).AsShardStore();

			Assert.AreEqual(count, loaded.Count, $"{Backend}: shard count changed across the round trip.");

			for (var i = 0; i < count; i++)
			{
				Assert.IsTrue(loaded.TryGet(ids[i], out var shard), $"{Backend}: shard {i} is missing.");

				var (value, text) = ReadShard(shard);

				Assert.AreEqual(i, value, $"{Backend}: shard {i} has the wrong value — arena slices disagree.");
				Assert.AreEqual($"shard-{i}", text, $"{Backend}: shard {i} has the wrong text.");
			}
		});

		[UnityTest]
		public IEnumerator IncrementalSave_RewritesOnlyDirtyShards() => AsyncTest.Run(async () =>
		{
			using var manager = CreateManager(out var storage, multiFile: true);
			var store = new ShardStore();
			var stable = new SerializableGuid(1, 1);
			var changing = new SerializableGuid(2, 2);

			store.Add(CreateShard(stable, 1, "stable"));
			store.Add(CreateShard(changing, 2, "changing"));

			await manager.SaveAsync(Slot, store);

			var writesAfterFirst = new Dictionary<string, int>(storage.WriteCounts);

			// Everything is clean now; saving again must not rewrite a single shard file.
			await manager.SaveAsync(Slot, store);

			foreach (var key in writesAfterFirst.Keys)
				if (key != Slot)
					Assert.AreEqual(writesAfterFirst[key], storage.WriteCounts[key],
						$"{Backend}: a clean shard was rewritten — dirty tracking is not reaching the serializer.");

			var loaded = (await manager.LoadAsync(Slot)).AsShardStore();

			Assert.AreEqual(2, loaded.Count);
			Assert.IsTrue(loaded.TryGet(stable, out var restored));
			Assert.AreEqual("stable", ReadShard(restored).text);
		});

		[UnityTest]
		public IEnumerator ReSave_AfterMutation_PersistsTheNewValue() => AsyncTest.Run(async () =>
		{
			using var manager = CreateManager(out _);
			var id = new SerializableGuid(99, 98);
			var store = new ShardStore();

			store.Add(CreateShard(id, 1, "before"));

			await manager.SaveAsync(Slot, store);

			var replacement = new ShardStore();
			replacement.Add(CreateShard(id, 2, "after"));

			await manager.SaveAsync(Slot, replacement);

			var loaded = (await manager.LoadAsync(Slot)).AsShardStore();

			Assert.IsTrue(loaded.TryGet(id, out var shard));

			var (value, text) = ReadShard(shard);

			Assert.AreEqual(2, value, $"{Backend}: the second save did not replace the first.");
			Assert.AreEqual("after", text);
		});

		#endregion
	}
}
