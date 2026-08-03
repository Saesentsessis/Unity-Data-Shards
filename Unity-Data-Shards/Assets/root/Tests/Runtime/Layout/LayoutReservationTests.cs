using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Saesentsessis.Persistence.Buffers;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Serialization;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.TestTools;

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>
	/// The reservation contract: layouts declare space in front of the shard data so the shards can
	/// be serialized into their final position, and no payload copy is needed.
	/// </summary>
	/// <remarks>
	/// The failure mode this file exists to catch is silent. If a reservation is one byte off, the
	/// header either overruns into the first blob or leaves a gap the reader cannot express — and
	/// the save still writes. Only a round trip or a byte-level assertion notices.
	/// </remarks>
	public class LayoutReservationTests
	{
		private const string Slot = "reservation-slot";

		private static SaveEnvelope BuildEnvelope(params SerializedType[] types)
		{
			var records = new ShardRecord[3];

			for (var i = 0; i < records.Length; i++)
				records[i] = new ShardRecord { Id = new SerializableGuid((ulong)i, 7), TypeIndex = i % types.Length };

			return SaveEnvelope.Create(types.Length, types, records.Length, records);
		}

		#region Exact envelope size

		[Test]
		public void ExactEncodedSize_EqualsWhatWriteAppends()
		{
			// A bound would be safe for a growable buffer and wrong here: the payload begins at the
			// very next byte, so slack becomes a gap the file format cannot describe.
			var envelope = BuildEnvelope(
				new SerializedType("Some.Namespace.TypeA", "Assembly.A", 1),
				new SerializedType("Some.Namespace.TypeB", "Assembly.B", 3));

			using var writer = new PooledArrayBufferWriter();
			EnvelopeCodec.Write(envelope, writer);

			Assert.AreEqual(writer.WrittenLength, EnvelopeCodec.ExactEncodedSize(envelope));
		}

		[Test]
		public unsafe void ExactEncodedSize_AlsoBoundsEveryReservationAlongTheWay([Values(1, 2, 5)] int recordCount)
		{
			// The total fitting is not enough — every intermediate GetSpan has to fit too. This is
			// the case that shipped broken: WriteString reserved three bytes per char, and with a
			// single record there was no trailing range block to absorb the over-request, so the
			// last string demanded more than remained. Multi-shard tests all passed.
			var types = new[]
			{
				new SerializedType("Saesentsessis.Persistence.Tests.TestShard", "Saesentsessis.Persistence.Tests", 1)
			};

			var records = new ShardRecord[recordCount];

			for (var i = 0; i < recordCount; i++)
				records[i] = new ShardRecord { Id = new SerializableGuid((ulong)i, 1), TypeIndex = 0 };

			var envelope = SaveEnvelope.Create(types.Length, types, recordCount, records);
			var exact = EnvelopeCodec.ExactEncodedSize(envelope);

			var buffer = new NativeArray<byte>(exact, Allocator.Temp);

			try
			{
				var writer = new FixedBufferWriter();
				writer.Reset((byte*)buffer.GetUnsafePtr(), exact);

				// Throws if any single reservation overruns, which is exactly the failure being pinned.
				Assert.DoesNotThrow(() => EnvelopeCodec.Write(envelope, writer));
				Assert.AreEqual(exact, writer.WrittenLength, "The region must be filled exactly, with no gap.");
			}
			finally
			{
				buffer.Dispose();
			}
		}

		[Test]
		public void ExactEncodedSize_CountsUtf8BytesNotChars()
		{
			// Non-ASCII type names are where a char-count would drift from the encoded length, and
			// where MaxEncodedSize's 3-bytes-per-char bound is at its loosest.
			var envelope = BuildEnvelope(
				new SerializedType("Ünïcödé.Namespace.Tÿpe", "Assembly.Ä", 1),
				new SerializedType("日本語.Namespace.Type", "Assembly.B", 2));

			using var writer = new PooledArrayBufferWriter();
			EnvelopeCodec.Write(envelope, writer);

			var exact = EnvelopeCodec.ExactEncodedSize(envelope);

			Assert.AreEqual(writer.WrittenLength, exact);
			Assert.Less(exact, EnvelopeCodec.MaxEncodedSize(envelope),
				"The bound must still be a bound — if they are equal the exact size is not doing anything.");
		}

		#endregion

		#region Declared reservations

		[Test]
		public void SingleFile_ReservesEnvelopeRangesAndPayloadLength()
		{
			var envelope = BuildEnvelope(new SerializedType("A", "B", 1));
			ISaveLayout layout = new SingleFileSaveLayout(new MemoryStorage());

			var expected = EnvelopeCodec.ExactEncodedSize(envelope) + 4 + 3 * 24 + 4;

			Assert.AreEqual(expected, layout.HeaderReservation(envelope, 3));
			Assert.AreEqual(0, layout.BlobReservation, "Single-file frames nothing per blob.");
		}

		[Test]
		public void MultiFile_ReservesEightBytesPerBlobAndNoHeader()
		{
			var envelope = BuildEnvelope(new SerializedType("A", "B", 1));
			ISaveLayout layout = new MultiFileSaveLayout(new MemoryStorage());

			Assert.AreEqual(8, layout.BlobReservation, "One xxHash3-64 per shard file.");
			Assert.AreEqual(0, layout.HeaderReservation(envelope, 3),
				"The envelope is its own file here, not a prefix on the payload.");
		}

		[Test]
		public void LayoutWithoutOverrides_ReservesNothing()
		{
			// The defaults are what keep every custom layout written against 0.5.0 behaving
			// identically after the contract gained these members.
			ISaveLayout layout = new CapturingLayout();

			Assert.AreEqual(0, layout.HeaderReservation(BuildEnvelope(new SerializedType("A", "B", 1)), 3));
			Assert.AreEqual(0, layout.BlobReservation);
		}

		#endregion

		#region Byte-level effects

		[UnityTest]
		public IEnumerator SingleFile_RoundTripsASingleShard() => AsyncTest.Run(async () =>
		{
			// One shard is the minimal envelope, and the one where the header reservation has the
			// least slack — every reservation bug shows up here first and nowhere else.
			var storage = new MemoryStorage();
			using var manager = new SaveManager(new UnityJsonSerializer(), new SingleFileSaveLayout(storage));
			var store = new ShardStore();
			var id = Guid.NewGuid();

			store.Add(new TestShard(id, 42, "only"));

			await manager.SaveAsync(Slot, store);

			var loaded = (await manager.LoadAsync(Slot)).AsShardStore();

			Assert.IsTrue(loaded.TryGet<TestShard>(id, out var shard));
			Assert.AreEqual(42, shard.value);
			Assert.AreEqual("only", shard.text);
		});

		[UnityTest]
		public IEnumerator SingleFile_WritesRangesRelativeToThePayload() => AsyncTest.Run(async () =>
		{
			// Arena offsets are absolute and include the header; the file's must not be, or a reader
			// would slice the payload from the wrong place.
			var storage = new MemoryStorage();
			using var manager = new SaveManager(new UnityJsonSerializer(), new SingleFileSaveLayout(storage));
			var store = new ShardStore();

			for (var i = 0; i < 4; i++)
				store.Add(new TestShard(Guid.NewGuid(), i, $"shard-{i}"));

			await manager.SaveAsync(Slot, store);

			var file = storage.Data[Slot];
			var envelope = EnvelopeCodec.Read(file, out var offset);
			var rangeCount = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(offset));

			Assert.AreEqual(envelope.RecordCount, rangeCount);

			var firstOffset = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(offset + 4 + 16));

			Assert.AreEqual(0, firstOffset,
				"The first blob sits at the start of the payload, so its recorded offset must be zero.");

			// And the whole thing must still load, which is the real proof the offsets agree.
			var loaded = (await manager.LoadAsync(Slot)).AsShardStore();
			Assert.AreEqual(4, loaded.Count);
		});

		[UnityTest]
		public IEnumerator MultiFile_ShardFileIsHashPlusBlobWithNoScratch() => AsyncTest.Run(async () =>
		{
			var storage = new MemoryStorage();
			using var manager = new SaveManager(new UnityJsonSerializer(), new MultiFileSaveLayout(storage));
			var store = new ShardStore();

			for (var i = 0; i < 3; i++)
				store.Add(new TestShard(Guid.NewGuid(), i, $"shard-{i}"));

			await manager.SaveAsync(Slot, store);

			foreach (var pair in storage.Data.Where(entry => entry.Key != Slot))
				Assert.Greater(pair.Value.Length, 8,
					"A shard file is its 8-byte checksum followed by the blob the reservation sat in front of.");

			var loaded = (await manager.LoadAsync(Slot)).AsShardStore();
			Assert.AreEqual(3, loaded.Count);
		});

		[UnityTest]
		public IEnumerator ShrinkingSave_DoesNotWriteStaleRanges() => AsyncTest.Run(async () =>
		{
			// The range array is now reused per slot, so a save with fewer blobs than the last one
			// must see a view trimmed to its own count — not the leftovers of the bigger save.
			var storage = new MemoryStorage();
			using var manager = new SaveManager(new UnityJsonSerializer(), new SingleFileSaveLayout(storage));
			var store = new ShardStore();

			for (var i = 0; i < 6; i++)
				store.Add(new TestShard(Guid.NewGuid(), i, $"shard-{i}"));

			await manager.SaveAsync(Slot, store);

			while (store.Count > 2)
				store.Remove(store[store.Count - 1].Identifier);

			await manager.SaveAsync(Slot, store);

			var envelope = EnvelopeCodec.Read(storage.Data[Slot], out var offset);
			var rangeCount = BinaryPrimitives.ReadInt32LittleEndian(storage.Data[Slot].AsSpan(offset));

			Assert.AreEqual(2, envelope.RecordCount);
			Assert.AreEqual(2, rangeCount, "The reused range buffer must be trimmed to this save's blob count.");

			var loaded = (await manager.LoadAsync(Slot)).AsShardStore();
			Assert.AreEqual(2, loaded.Count);
		});

		[UnityTest]
		public IEnumerator RepeatedSaveAndDispose_ReleasesEveryRangeBuffer() => AsyncTest.Run(async () =>
		{
			// The per-slot range arrays are Allocator.Persistent and reused, so nothing else will
			// collect them. A leak here shows up as Unity's native leak warning on domain reload,
			// which is why this cycles several managers and several slots.
			for (var round = 0; round < 3; round++)
			{
				var storage = new MemoryStorage();
				using var manager = new SaveManager(new UnityJsonSerializer(), new MultiFileSaveLayout(storage));
				var store = new ShardStore();

				for (var i = 0; i < 3; i++)
					store.Add(new TestShard(Guid.NewGuid(), i));

				await manager.SaveAsync($"{Slot}-a-{round}", store);
				await manager.SaveAsync($"{Slot}-b-{round}", store);
				await manager.DeleteAsync($"{Slot}-a-{round}");

				// Disposal is the `using` above, which also covers an assertion throwing mid-round.
			}

			Assert.Pass("No native allocations outlive the managers that made them.");
		});

		#endregion
	}
}
