using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Persistence.Buffers;
using Persistence.Core;
using Persistence.Layout;
using Persistence.Serialization;
using Unity.Collections;
using UnityEngine.TestTools;
#if PERSISTENCE_HAS_UNITASK
using SampleTask = Cysharp.Threading.Tasks.UniTask<System.ValueTuple<Persistence.Tests.MemoryStorage, Persistence.SaveManager>>;
#else
using SampleTask = System.Threading.Tasks.Task<System.ValueTuple<Persistence.Tests.MemoryStorage, Persistence.SaveManager>>;
#endif

namespace Persistence.Tests
{
	public class CodecTests
	{
		private static SaveEnvelope BuildEnvelope()
		{
			return new SaveEnvelope
			{
				FormatVersion = SaveEnvelope.CurrentFormatVersion,
				TimestampUtc = DateTime.UtcNow.Ticks,
				Types = new[]
				{
					new SerializedType("Some.Namespace.TypeA", "Assembly.A", 1),
					new SerializedType("Some.Namespace.TypeB", "Assembly.B", 3)
				},
				TypeCount = 2,
				Records = new[]
				{
					new ShardRecord { Id = new SerializableGuid(1, 2), TypeIndex = 0 },
					new ShardRecord { Id = new SerializableGuid(3, 4), TypeIndex = 1 },
					new ShardRecord { Id = new SerializableGuid(5, 6), TypeIndex = 0 }
				},
				RecordCount = 3
			};
		}

		private static byte[] Encode(in SaveEnvelope envelope)
		{
			using var writer = new PooledArrayBufferWriter();
			EnvelopeCodec.Write(envelope, writer);
			return writer.WrittenSpan.ToArray();
		}

		[Test]
		public void Codec_RoundTrips()
		{
			var envelope = BuildEnvelope();
			var bytes = Encode(envelope);
			var decoded = EnvelopeCodec.Read(bytes, out var consumed);

			Assert.AreEqual(bytes.Length, consumed);
			Assert.AreEqual(envelope.FormatVersion, decoded.FormatVersion);
			Assert.AreEqual(envelope.TimestampUtc, decoded.TimestampUtc);
			Assert.AreEqual(envelope.TypeCount, decoded.TypeCount);
			Assert.AreEqual(envelope.RecordCount, decoded.RecordCount);

			for (var i = 0; i < envelope.TypeCount; i++)
				Assert.AreEqual(envelope.Types[i], decoded.Types[i]);

			for (var i = 0; i < envelope.RecordCount; i++)
			{
				Assert.AreEqual(envelope.Records[i].Id, decoded.Records[i].Id);
				Assert.AreEqual(envelope.Records[i].TypeIndex, decoded.Records[i].TypeIndex);
			}
		}

		[Test]
		public void Codec_TruncatedAtEveryOffset_ThrowsCorrupted()
		{
			var bytes = Encode(BuildEnvelope());

			for (var length = 0; length < bytes.Length; length++)
			{
				var truncated = new byte[length];
				Array.Copy(bytes, truncated, length);

				Assert.Throws<SaveCorruptedException>(
					() => EnvelopeCodec.Read(truncated, out _),
					$"Truncation to {length}/{bytes.Length} bytes must throw.");
			}
		}

		[Test]
		public void Codec_WrongVersion_ThrowsInvalidOperation()
		{
			var bytes = Encode(BuildEnvelope());
			bytes[0] = 0xFF;

			// Not SaveCorruptedException: an unknown version is a compatibility error.
			var e = Assert.Throws<InvalidOperationException>(() => EnvelopeCodec.Read(bytes, out _));
			Assert.IsFalse(e is SaveCorruptedException);
		}

		[Test]
		public void Codec_InvalidTypeIndex_ThrowsCorrupted()
		{
			var envelope = BuildEnvelope();
			envelope.Records[1].TypeIndex = 7;
			var bytes = Encode(envelope);

			Assert.Throws<SaveCorruptedException>(() => EnvelopeCodec.Read(bytes, out _));
		}

		[Test]
		public void Checksum_DetectsEveryBitFlipInHashedRegion()
		{
			using var writer = new PooledArrayBufferWriter();
			EnvelopeCodec.Write(BuildEnvelope(), writer);
			var bytes = writer.WrittenSpan.ToArray();
			EnvelopeCodec.PatchChecksum(bytes);

			Assert.DoesNotThrow(() => EnvelopeCodec.ValidateChecksum(bytes));

			// Flip one bit per byte across checksum field + hashed region.
			for (var i = EnvelopeCodec.ChecksumOffset; i < bytes.Length; i++)
			{
				bytes[i] ^= 0x10;
				Assert.Throws<SaveCorruptedException>(
					() => EnvelopeCodec.ValidateChecksum(bytes),
					$"Bit flip at offset {i} must fail validation.");
				bytes[i] ^= 0x10;
			}
		}
	}

	public class BufferWriterTests
	{
		[Test]
		public void NativeListBufferWriter_GrowsAndPreservesContent()
		{
			using var writer = new NativeListBufferWriter(16, Allocator.Persistent);

			for (var i = 0; i < 100_000; i++)
			{
				var span = writer.GetSpan(1);
				span[0] = (byte)(i % 251);
				writer.Advance(1);
			}

			Assert.AreEqual(100_000, writer.WrittenLength);

			var array = writer.AsArray();
			for (var i = 0; i < array.Length; i++)
				if (array[i] != (byte)(i % 251))
					Assert.Fail($"Content mismatch at {i}.");
		}

		[Test]
		public void NativeListBufferWriter_GetMemory_WritesThrough()
		{
			using var writer = new NativeListBufferWriter(16, Allocator.Persistent);

			var memory = writer.GetMemory(4);
			memory.Span[0] = 0xAB;
			memory.Span[1] = 0xCD;
			writer.Advance(2);

			var array = writer.AsArray();
			Assert.AreEqual(0xAB, array[0]);
			Assert.AreEqual(0xCD, array[1]);
		}

		[Test]
		public void PooledArrayBufferWriter_GrowsAndPreservesContent()
		{
			using var writer = new PooledArrayBufferWriter(16);

			for (var i = 0; i < 100_000; i++)
			{
				var span = writer.GetSpan(1);
				span[0] = (byte)(i % 251);
				writer.Advance(1);
			}

			Assert.AreEqual(100_000, writer.WrittenLength);

			var span2 = writer.WrittenSpan;
			for (var i = 0; i < span2.Length; i++)
				if (span2[i] != (byte)(i % 251))
					Assert.Fail($"Content mismatch at {i}.");
		}
	}

	public class SingleFileCorruptionTests
	{
		private const string Slot = "fuzz-slot";

		private static async SampleTask SaveSample()
		{
			var storage = new MemoryStorage();
			var manager = new SaveManager(new UnityJsonSerializer(), new SingleFileSaveLayout(storage));
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
			for (var i = EnvelopeCodec.ChecksumOffset; i < intact.Length; i++)
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

	public class FileStorageTests
	{
		private string _directory;
		private Storage.FileStorage _storage;

		[SetUp]
		public void SetUp()
		{
			_directory = Path.Combine(Path.GetTempPath(), "uds-tests-" + Guid.NewGuid().ToString("N"));
			_storage = new Storage.FileStorage(_directory);
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(_directory))
				Directory.Delete(_directory, recursive: true);
		}

		private static NativeArray<byte> Bytes(params byte[] values)
			=> new NativeArray<byte>(values, Allocator.Persistent);

		[UnityTest]
		public IEnumerator WriteRead_RoundTrips() => AsyncTest.Run(async () =>
		{
			var data = Bytes(1, 2, 3, 4, 5);

			try
			{
				await _storage.WriteAsync("slot", data);
			}
			finally
			{
				data.Dispose();
			}

			var read = await _storage.TryReadAsync("slot", Allocator.Persistent);
			try
			{
				Assert.IsTrue(read.Found);
				CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, read.Data.ToArray());
			}
			finally
			{
				read.Data.Dispose();
			}
		});

		[UnityTest]
		public IEnumerator TryRead_MissingKey_NotFound() => AsyncTest.Run(async () =>
		{
			var read = await _storage.TryReadAsync("missing", Allocator.Persistent);
			Assert.IsFalse(read.Found);
			Assert.IsFalse(await _storage.ExistsAsync("missing"));
		});

		[UnityTest]
		public IEnumerator StaleBak_DoesNotBrickTheSlot() => AsyncTest.Run(async () =>
		{
			var first = Bytes(1);
			try { await _storage.WriteAsync("slot", first); }
			finally { first.Dispose(); }

			// Simulate a crash that left a stale .bak behind.
			var path = Path.Combine(_directory, "slot.save");
			File.WriteAllBytes(path + ".bak", new byte[] { 9, 9 });

			var second = Bytes(2, 2);
			try { await _storage.WriteAsync("slot", second); }
			finally { second.Dispose(); }

			var read = await _storage.TryReadAsync("slot", Allocator.Persistent);
			try
			{
				Assert.IsTrue(read.Found);
				CollectionAssert.AreEqual(new byte[] { 2, 2 }, read.Data.ToArray());
			}
			finally
			{
				read.Data.Dispose();
			}
		});

		[UnityTest]
		public IEnumerator BakRestore_RecoversAfterLostMainFile() => AsyncTest.Run(async () =>
		{
			var data = Bytes(7, 7, 7);
			try { await _storage.WriteAsync("slot", data); }
			finally { data.Dispose(); }

			// Simulate a crash between the two moves: main file gone, .bak intact.
			var path = Path.Combine(_directory, "slot.save");
			File.Move(path, path + ".bak");

			var read = await _storage.TryReadAsync("slot", Allocator.Persistent);
			try
			{
				Assert.IsTrue(read.Found, ".bak must be restored transparently.");
				CollectionAssert.AreEqual(new byte[] { 7, 7, 7 }, read.Data.ToArray());
			}
			finally
			{
				read.Data.Dispose();
			}
		});

		[UnityTest]
		public IEnumerator EndToEnd_SaveManagerOverFileStorage() => AsyncTest.Run(async () =>
		{
			var manager = new SaveManager(new UnityJsonSerializer(), new SingleFileSaveLayout(_storage));
			var store = new ShardStore();
			store.Add(new TestShard(Guid.NewGuid(), 123, "file"));

			await manager.SaveAsync("slot", store);
			Assert.IsTrue(await manager.ExistsAsync("slot"));

			var loaded = (await manager.LoadAsync("slot")).AsShardStore();
			Assert.AreEqual(1, loaded.Count);
			Assert.AreEqual(123, ((TestShard)loaded[0]).value);

			await manager.DeleteAsync("slot");
			Assert.IsFalse(await manager.ExistsAsync("slot"));
		});
	}
}
