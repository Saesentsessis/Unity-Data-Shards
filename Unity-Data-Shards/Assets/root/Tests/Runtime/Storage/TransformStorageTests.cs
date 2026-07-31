using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Saesentsessis.Persistence.Buffers;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Serialization;
using Saesentsessis.Persistence.Storage;
using Saesentsessis.Persistence.Storage.Transforms;
using Unity.Collections;
using UnityEngine.TestTools;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using ByteArrayTask = Cysharp.Threading.Tasks.UniTask<byte[]>;
#else
using TaskType = System.Threading.Tasks.Task;
using ByteArrayTask = System.Threading.Tasks.Task<byte[]>;
#endif

namespace Saesentsessis.Persistence.Tests
{
	public class TransformStorageTests
	{
		private const string Key = "transform-slot";

		#region Test doubles

		/// <summary>
		/// Prepends a tag on Apply and verifies it on Reverse. Chaining two with different tags makes
		/// ordering self-checking: if the chain reverses in the wrong order the assert fires inside
		/// the transform, so a passing test genuinely proves the order rather than just the result.
		/// </summary>
		private sealed class TaggedPrefixTransform : IStorageTransform
		{
			private readonly byte _tag;

			public TaggedPrefixTransform(byte tag) => _tag = tag;

			public void Apply(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
			{
				var span = dst.GetSpan(src.Length + 1);
				span[0] = _tag;
				src.CopyTo(span[1..]);
				dst.Advance(src.Length + 1);
			}

			public void Reverse(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
			{
				Assert.Greater(src.Length, 0, $"Transform 0x{_tag:x2} reversed an empty buffer.");
				Assert.AreEqual(_tag, src[0],
					$"Transform 0x{_tag:x2} saw tag 0x{src[0]:x2} — the chain reversed in the wrong order.");

				var body = src[1..];
				body.CopyTo(dst.GetSpan(body.Length));
				dst.Advance(body.Length);
			}
		}

		/// <summary>Doubles every byte, so the chain has to cope with a transform that grows its input.</summary>
		private sealed class DuplicateBytesTransform : IStorageTransform
		{
			public void Apply(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
			{
				var span = dst.GetSpan(src.Length * 2);

				for (var i = 0; i < src.Length; i++)
				{
					span[i * 2] = src[i];
					span[i * 2 + 1] = src[i];
				}

				dst.Advance(src.Length * 2);
			}

			public void Reverse(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
			{
				Assert.AreEqual(0, src.Length % 2, "Duplicated payload must have an even length.");

				var span = dst.GetSpan(src.Length / 2);

				for (var i = 0; i < src.Length; i += 2)
				{
					Assert.AreEqual(src[i], src[i + 1], $"Duplicated pair at {i} does not match.");
					span[i / 2] = src[i];
				}

				dst.Advance(src.Length / 2);
			}
		}

		/// <summary>Counts invocations, to assert the per-storage-key contract on multi-file layouts.</summary>
		private sealed class CountingTransform : IStorageTransform
		{
			public int ApplyCalls;
			public int ReverseCalls;

			public void Apply(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
			{
				ApplyCalls++;
				Passthrough(src, dst);
			}

			public void Reverse(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
			{
				ReverseCalls++;
				Passthrough(src, dst);
			}

			private static void Passthrough(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
			{
				src.CopyTo(dst.GetSpan(src.Length));
				dst.Advance(src.Length);
			}
		}

		#endregion

		#region Helpers

		/// <summary>Fixed 32-byte key: tests must be deterministic, so no RNG here.</summary>
		private static byte[] TestKey()
		{
			var key = new byte[32];

			for (var i = 0; i < key.Length; i++)
				key[i] = (byte)(i * 7 + 1);

			return key;
		}

		private static byte[] Payload(int length, int seed = 0)
		{
			var bytes = new byte[length];

			for (var i = 0; i < length; i++)
				bytes[i] = (byte)((i * 31 + seed) % 251);

			return bytes;
		}

		private static async TaskType WriteAsync(IStorage storage, string key, byte[] bytes)
		{
			var data = new NativeArray<byte>(bytes, Allocator.Persistent);

			try
			{
				await storage.WriteAsync(key, data);
			}
			finally
			{
				data.Dispose();
			}
		}

		private static async ByteArrayTask ReadAsync(IStorage storage, string key)
		{
			var result = await storage.TryReadAsync(key, Allocator.Persistent);

			if (result.Found == false)
				return null;

			try
			{
				return result.Data.ToArray();
			}
			finally
			{
				result.Data.Dispose();
			}
		}

		#endregion

		[UnityTest]
		public IEnumerator NoTransforms_PassThroughUntouched() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner);
			var payload = Payload(64);

			await WriteAsync(storage, Key, payload);

			CollectionAssert.AreEqual(payload, inner.Data[Key],
				"With no transforms the inner storage must receive the bytes verbatim.");
			CollectionAssert.AreEqual(payload, await ReadAsync(storage, Key));
		});

		[UnityTest]
		public IEnumerator SingleTransform_RoundTrips() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner, new TaggedPrefixTransform(0xAA));
			var payload = Payload(128);

			await WriteAsync(storage, Key, payload);

			Assert.AreEqual(payload.Length + 1, inner.Data[Key].Length, "The tag must reach storage.");
			Assert.AreEqual(0xAA, inner.Data[Key][0]);
			CollectionAssert.AreEqual(payload, await ReadAsync(storage, Key));
		});

		[UnityTest]
		public IEnumerator TwoTransforms_ApplyInOrder_ReverseInReverse() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner,
				new TaggedPrefixTransform(0xA1),
				new TaggedPrefixTransform(0xB2));

			var payload = Payload(32);
			await WriteAsync(storage, Key, payload);

			var atRest = inner.Data[Key];

			// Declaration order applies first, so its tag ends up innermost.
			Assert.AreEqual(0xB2, atRest[0], "The last transform's tag must be outermost at rest.");
			Assert.AreEqual(0xA1, atRest[1], "The first transform's tag must sit beneath it.");
			CollectionAssert.AreEqual(payload, atRest[2..]);

			CollectionAssert.AreEqual(payload, await ReadAsync(storage, Key));
		});

		[UnityTest]
		public IEnumerator ThreeTransforms_PingPongBuffersStayCorrect() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner,
				new TaggedPrefixTransform(0x01),
				new TaggedPrefixTransform(0x02),
				new TaggedPrefixTransform(0x03));

			var payload = Payload(256);
			await WriteAsync(storage, Key, payload);

			// Three steps force the front/back arenas to rotate rather than just swap once.
			Assert.AreEqual(payload.Length + 3, inner.Data[Key].Length);
			CollectionAssert.AreEqual(payload, await ReadAsync(storage, Key));
		});

		[UnityTest]
		public IEnumerator RepeatedWrites_LargeThenSmall_DoNotBleedStaleBytes() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner, new TaggedPrefixTransform(0x7F));

			var large = Payload(4096, seed: 1);
			var small = Payload(16, seed: 2);

			await WriteAsync(storage, Key, large);
			await WriteAsync(storage, Key, small);

            // The arenas are reused across calls; without the Clear() the second write would carry
            // the tail of the first.
			Assert.AreEqual(small.Length + 1, inner.Data[Key].Length,
				"A smaller second write must not inherit the previous payload's length.");
			CollectionAssert.AreEqual(small, await ReadAsync(storage, Key));
		});

		[UnityTest]
		public IEnumerator SizeChangingTransform_RoundTrips() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner, new DuplicateBytesTransform());
			var payload = Payload(100);

			await WriteAsync(storage, Key, payload);

			Assert.AreEqual(payload.Length * 2, inner.Data[Key].Length);
			CollectionAssert.AreEqual(payload, await ReadAsync(storage, Key));
		});

		[UnityTest]
		public IEnumerator MixedGrowAndShrink_RoundTrips() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner,
				new DuplicateBytesTransform(),
				new TaggedPrefixTransform(0x5C),
				new DuplicateBytesTransform());

			var payload = Payload(48);
			await WriteAsync(storage, Key, payload);

			CollectionAssert.AreEqual(payload, await ReadAsync(storage, Key));
		});

		[UnityTest]
		public IEnumerator MissingKey_ReportsNotFound() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner, new TaggedPrefixTransform(0x11));

			var result = await storage.TryReadAsync("absent", Allocator.Persistent);

			Assert.IsFalse(result.Found, "A missing key must report NotFound, not run the reverse chain.");
		});

		[UnityTest]
		public IEnumerator ExistsAndDelete_DelegateToInner() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner, new TaggedPrefixTransform(0x22));

			await WriteAsync(storage, Key, Payload(8));

			Assert.IsTrue(await storage.ExistsAsync(Key));

			await storage.DeleteAsync(Key);

			Assert.IsFalse(await storage.ExistsAsync(Key));
			Assert.IsFalse(inner.Data.ContainsKey(Key), "Delete must reach the inner storage.");
		});

		[Test]
		public void Dispose_CascadesToInnerStorage()
		{
			var inner = new MemoryStorage();
			var storage = new TransformStorage(inner, new TaggedPrefixTransform(0x33));

			storage.Dispose();

			Assert.IsTrue(inner.Disposed, "Disposing the decorator must dispose the storage it wraps.");
		}

		/// <summary>Counts Dispose calls so the ownership rule can be asserted rather than assumed.</summary>
		private sealed class DisposableTransform : IStorageTransform, IDisposable
		{
			public int DisposeCount;

			public void Apply(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
			{
				src.CopyTo(dst.GetSpan(src.Length));
				dst.Advance(src.Length);
			}

			public void Reverse(ReadOnlySpan<byte> src, IBufferWriter<byte> dst) => Apply(src, dst);

			public void Dispose() => DisposeCount++;
		}

		[Test]
		public void Dispose_DisposesItsTransforms()
		{
			// A transform belongs to exactly one storage — sharing is prohibited because the scratch
			// state transforms carry is per-operation — so ownership is unambiguous and the chain is
			// released with the storage. Without this a XorTransform's native pattern buffer and an
			// AesCbcHmacTransform's cipher would leak on every chain that was ever built.
			var transform = new DisposableTransform();

			using (new TransformStorage(new MemoryStorage(), transform))
			{
			}

			Assert.AreEqual(1, transform.DisposeCount, "The storage owns its chain and must release it.");
		}

		[Test]
		public void Dispose_DisposesEveryTransformInTheChain()
		{
			var first = new DisposableTransform();
			var second = new DisposableTransform();

			using (new TransformStorage(new MemoryStorage(), first, second))
			{
			}

			Assert.AreEqual(1, first.DisposeCount);
			Assert.AreEqual(1, second.DisposeCount, "A later transform must not be skipped.");
		}

		[Test]
		public void Constructor_RejectsNullTransformElement()
		{
			// Caught at construction rather than as a NullReferenceException on the first save.
			Assert.Throws<ArgumentNullException>(() =>
				new TransformStorage(new MemoryStorage(), new XorTransform(0x11), null));
		}

		[UnityTest]
		public IEnumerator XorTransform_RoundTripsThroughStorage() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner, new XorTransform(0x5A));
			var payload = Payload(512);

			await WriteAsync(storage, Key, payload);

			CollectionAssert.AreNotEqual(payload, inner.Data[Key], "The bytes at rest must be masked.");
			CollectionAssert.AreEqual(payload, await ReadAsync(storage, Key));
		});

		[Test]
		public void XorTransform_ThrowsOnEmptyOrZeroedPattern()
		{
			Assert.Throws<ArgumentException>(() => new XorTransform((byte)0));
			Assert.Throws<ArgumentException>(() => new XorTransform(0));
			Assert.Throws<ArgumentException>(() => new XorTransform(0u));
			Assert.Throws<ArgumentException>(() => new XorTransform(0L));
			Assert.Throws<ArgumentException>(() => new XorTransform(0UL));
			Assert.Throws<ArgumentException>(() => new XorTransform(ReadOnlySpan<byte>.Empty));
			Assert.Throws<ArgumentException>(() => new XorTransform(null));
		}

		[Test]
		public void XorTransform_HandlesEmptyInput()
		{
			var transform = new XorTransform(0x5A);
			using var writer = new PooledArrayBufferWriter();

			Assert.DoesNotThrow(() => transform.Apply(ReadOnlySpan<byte>.Empty, writer));
			Assert.AreEqual(0, writer.WrittenLength);
		}

		[Test]
		public void XorTransform_IsItsOwnInverse()
		{
			var transform = new XorTransform(0x3C);
			var payload = Payload(300);

			using var applied = new PooledArrayBufferWriter();
			transform.Apply(payload, applied);

			using var reversed = new PooledArrayBufferWriter();
			transform.Reverse(applied.WrittenSpan, reversed);

			CollectionAssert.AreEqual(payload, reversed.WrittenSpan.ToArray());
		}

		[UnityTest]
		public IEnumerator DeflateTransform_RoundTripsAndCompresses() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner, new DeflateTransform());

			// Highly repetitive, so a working compressor must shrink it well below the input.
			var payload = new byte[8192];
			await WriteAsync(storage, Key, payload);

			Assert.Less(inner.Data[Key].Length, payload.Length / 4,
				"Deflate must compress a run of zeroes to a fraction of its size.");
			CollectionAssert.AreEqual(payload, await ReadAsync(storage, Key));
		});

		[UnityTest]
		public IEnumerator DeflateTransform_HandlesIncompressibleAndTinyPayloads() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner, new DeflateTransform());

			foreach (var length in new[] { 1, 2, 15, 16, 17, 1023 })
			{
				var payload = Payload(length, seed: length);
				await WriteAsync(storage, Key, payload);

				CollectionAssert.AreEqual(payload, await ReadAsync(storage, Key),
					$"Deflate round-trip failed for a {length}-byte payload.");
			}
		});

		[Test]
		public void DeflateTransform_HostileLengthPrefix_IsRejectedWithoutAllocating()
		{
			// A decompression bomb: a tiny body claiming ~2 GB of output. The declared length comes
			// off disk, which means it comes from whoever last edited the save, so reserving from it
			// is how a 40-byte file turns into a 2 GB allocation.
			var transform = new DeflateTransform();
			using var compressed = new PooledArrayBufferWriter();

			transform.Apply(Payload(64), compressed);

			var tampered = compressed.WrittenSpan.ToArray();
			BinaryPrimitives.WriteInt32LittleEndian(tampered, int.MaxValue);

			using var output = new PooledArrayBufferWriter();

			var thrown = Assert.Throws<SaveCorruptedException>(() => transform.Reverse(tampered, output));

			Assert.AreEqual(SaveCorruptedExceptionReason.EnvelopeIsTooLarge, thrown.Reason,
				"A prefix beyond the format's maximum expansion must be rejected on the ratio check, " +
				"before anything is reserved.");
		}

		[Test]
		public void DeflateTransform_ShortenedLengthPrefix_IsRejected()
		{
			// The mirror case: a prefix small enough to pass the ratio check but smaller than what
			// the stream actually produces. The decoder is driven by the stream, so this surfaces
			// as an overrun rather than as a silently truncated save.
			var transform = new DeflateTransform();
			using var compressed = new PooledArrayBufferWriter();

			transform.Apply(Payload(2048), compressed);

			var tampered = compressed.WrittenSpan.ToArray();
			BinaryPrimitives.WriteInt32LittleEndian(tampered, 16);

			using var output = new PooledArrayBufferWriter();

			Assert.Throws<SaveCorruptedException>(() => transform.Reverse(tampered, output));
		}

		[Test]
		public void DeflateTransform_TruncatedBody_IsRejected()
		{
			// Honest prefix, missing bytes: the stream stops early and the length check catches it.
			var transform = new DeflateTransform();
			using var compressed = new PooledArrayBufferWriter();

			transform.Apply(Payload(4096), compressed);

			var truncated = compressed.WrittenSpan[..(compressed.WrittenLength / 2)].ToArray();

			using var output = new PooledArrayBufferWriter();

			Assert.Throws<SaveCorruptedException>(() => transform.Reverse(truncated, output));
		}

		[Test]
		public void DeflateTransform_SurvivesPayloadLargerThanOneReservation()
		{
			// Bigger than TransformLimits.MaxReservation, so Reverse has to loop and grow rather
			// than reserve the whole thing up front — the path the bomb fix introduced.
			var transform = new DeflateTransform();
			var payload = Payload(TransformLimits.MaxReservation + 4096, seed: 7);

			using var compressed = new PooledArrayBufferWriter();
			transform.Apply(payload, compressed);

			using var output = new PooledArrayBufferWriter();
			transform.Reverse(compressed.WrittenSpan, output);

			CollectionAssert.AreEqual(payload, output.WrittenSpan.ToArray());
		}

		[UnityTest]
		public IEnumerator AesTransform_RoundTripsThroughStorage() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner, new AesCbcHmacTransform(TestKey()));

			foreach (var length in new[] { 1, 15, 16, 17, 1000 })
			{
				var payload = Payload(length, seed: length);
				await WriteAsync(storage, Key, payload);

				CollectionAssert.AreNotEqual(payload, inner.Data[Key], "Plaintext must not reach storage.");
				CollectionAssert.AreEqual(payload, await ReadAsync(storage, Key),
					$"AES round-trip failed for a {length}-byte payload.");
			}
		});

		[UnityTest]
		public IEnumerator AesTransform_UsesAFreshIvPerCall() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner, new AesCbcHmacTransform(TestKey()));
			var payload = Payload(256);

			await WriteAsync(storage, Key, payload);
			var first = (byte[])inner.Data[Key].Clone();

			await WriteAsync(storage, Key, payload);
			var second = inner.Data[Key];

			CollectionAssert.AreNotEqual(first, second,
				"The same plaintext must encrypt differently each time — the IV has to be random per call.");
			CollectionAssert.AreEqual(payload, await ReadAsync(storage, Key));
		});

		[UnityTest]
		public IEnumerator AesTransform_TamperedCiphertext_ThrowsChecksumMismatch() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner, new AesCbcHmacTransform(TestKey()));

			await WriteAsync(storage, Key, Payload(256));

			// Flip a byte inside the ciphertext, past the IV.
			inner.Data[Key][32] ^= 0xFF;

			var exception = Assert.ThrowsAsync<SaveCorruptedException>(
				async () => await ReadAsync(storage, Key));

			Assert.AreEqual(SaveCorruptedExceptionReason.ChecksumMismatch, exception.Reason,
				"The HMAC must reject the edit before any decryption is attempted.");
		});

		[UnityTest]
		public IEnumerator AesTransform_WrongKey_IsRejected() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();

			using (var storage = new TransformStorage(inner, new AesCbcHmacTransform(TestKey())))
				await WriteAsync(storage, Key, Payload(128));

			var otherKey = TestKey();
			otherKey[0] ^= 0xFF;

			using var wrong = new TransformStorage(inner, new AesCbcHmacTransform(otherKey));

			Assert.ThrowsAsync<SaveCorruptedException>(async () => await ReadAsync(wrong, Key));
		});

		[UnityTest]
		public IEnumerator CompressThenEncrypt_RoundTrips() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();

			// The documented order: compress first, because ciphertext does not compress.
			using var storage = new TransformStorage(inner,
				new DeflateTransform(),
				new AesCbcHmacTransform(TestKey()));

			var payload = new byte[4096];
			await WriteAsync(storage, Key, payload);

			Assert.Less(inner.Data[Key].Length, payload.Length,
				"Compressing before encrypting must still shrink a repetitive payload.");
			CollectionAssert.AreEqual(payload, await ReadAsync(storage, Key));
		});

		[UnityTest]
		public IEnumerator SaveManager_SingleFile_OverTransformStorage_RoundTrips() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			var storage = new TransformStorage(inner, new XorTransform(0x5A), new TaggedPrefixTransform(0x99));
			using var manager = new SaveManager(new UnityJsonSerializer(), new SingleFileSaveLayout(storage));

			var id = Guid.NewGuid();
			await manager.SaveAsync(Key, new List<IDataShard> { new TestShard(id, 4242, "transformed") });

			var loaded = await manager.LoadAsync(Key);

			Assert.AreEqual(1, loaded.Count);
			Assert.AreEqual(4242, ((TestShard)loaded[0]).value,
				"The v4 envelope and its checksum must survive the transform chain.");
		});

		[UnityTest]
		public IEnumerator SaveManager_MultiFile_RunsTransformOncePerStorageKey() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			var counter = new CountingTransform();
			var storage = new TransformStorage(inner, counter);
			using var manager = new SaveManager(new UnityJsonSerializer(), new MultiFileSaveLayout(storage));

			var shards = new List<IDataShard>
			{
				new TestShard(Guid.NewGuid(), 1, "a"),
				new TestShard(Guid.NewGuid(), 2, "b"),
				new TestShard(Guid.NewGuid(), 3, "c")
			};

			await manager.SaveAsync(Key, shards);

			// The unit of work is one storage key: a multi-file layout writes one envelope file plus
			// one file per shard, so the transform runs N+1 times on individual blobs — not once on a
			// whole save.
			Assert.AreEqual(shards.Count + 1, counter.ApplyCalls,
				"Expected one Apply per storage key (envelope + one per shard).");

			var loaded = await manager.LoadAsync(Key);
			Assert.AreEqual(shards.Count, loaded.Count);
		});

		[UnityTest]
		public IEnumerator TamperedBytesAtRest_ThrowSaveCorrupted() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			var storage = new TransformStorage(inner, new XorTransform(0x5A));
			using var manager = new SaveManager(new UnityJsonSerializer(), new SingleFileSaveLayout(storage));

			await manager.SaveAsync(Key, new List<IDataShard> { new TestShard(Guid.NewGuid(), 7, "x") });

			// Flip a byte in the middle of the stored payload; after the reverse chain the envelope
			// checksum must reject it.
			var stored = inner.Data[Key];
			stored[stored.Length / 2] ^= 0xFF;

			var threw = false;

			try { await manager.LoadAsync(Key); }
			catch (SaveCorruptedException) { threw = true; }

			Assert.IsTrue(threw, "Tampering beneath the transform chain must surface as SaveCorruptedException.");
		});
	}
}
