using System;
using System.Collections;
using NUnit.Framework;
using Saesentsessis.Persistence.Buffers;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Storage;
using Saesentsessis.Persistence.Storage.Transforms;
using Unity.Collections;
using UnityEngine.TestTools;

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>
	/// <see cref="XorTransform"/> against the shared contract, plus what is specific to it: a
	/// repeating-pattern mask is its own inverse and refuses a pattern that would mask nothing.
	/// </summary>
	public sealed class XorTransformTests : StorageTransformContractTests
	{
		protected override string TransformName => "Xor";

		protected override IStorageTransform CreateTransform() => new XorTransform(0x5A);

		[Test]
		public void Constructor_RejectsEmptyOrZeroedPattern()
		{
			// An all-zero pattern XORs to a no-op, so a save would be written in the clear while the
			// chain still claims to be masking it. Rejected at construction rather than at the first
			// write, when the mistake is far from its cause.
			Assert.Throws<ArgumentException>(() => new XorTransform((byte)0));
			Assert.Throws<ArgumentException>(() => new XorTransform(0));
			Assert.Throws<ArgumentException>(() => new XorTransform(0u));
			Assert.Throws<ArgumentException>(() => new XorTransform(0L));
			Assert.Throws<ArgumentException>(() => new XorTransform(0UL));
			Assert.Throws<ArgumentException>(() => new XorTransform(ReadOnlySpan<byte>.Empty));
			Assert.Throws<ArgumentException>(() => new XorTransform(null));
		}

		[Test]
		public void IsItsOwnInverse()
		{
			// Apply and Reverse are the same operation here, so applying twice must return the input
			// — a property no other transform in the package has.
			using var transform = new XorTransform(0x3C);
			var payload = Payload(300);

			using var once = new PooledArrayBufferWriter();
			transform.Apply(payload, once);

			using var twice = new PooledArrayBufferWriter();
			transform.Apply(once.WrittenSpan, twice);

			CollectionAssert.AreEqual(payload, twice.WrittenSpan.ToArray());
		}

		[UnityTest]
		public IEnumerator BytesAtRest_AreMasked() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner, new XorTransform(0x5A));
			var payload = Payload(512);

			var data = new NativeArray<byte>(payload, Allocator.Persistent);

			try
			{
				await storage.WriteAsync(Key, data);
			}
			finally
			{
				data.Dispose();
			}

			CollectionAssert.AreNotEqual(payload, inner.Data[Key], "The bytes at rest must be masked.");
		});
	}

	/// <summary>
	/// <see cref="AesCbcHmacTransform"/> against the shared contract, plus the properties that make
	/// it worth using over the masking transform: fresh IV per call, and detection of edits rather
	/// than merely of accidents.
	/// </summary>
	public sealed class AesCbcHmacTransformTests : StorageTransformContractTests
	{
		protected override string TransformName => "AES-CBC-HMAC";

		protected override IStorageTransform CreateTransform() => new AesCbcHmacTransform(TestKey());

		/// <summary>Fixed 32-byte key: tests must be deterministic, so no RNG here.</summary>
		private static byte[] TestKey()
		{
			var key = new byte[32];

			for (var i = 0; i < key.Length; i++)
				key[i] = (byte)(i * 7 + 1);

			return key;
		}

		[Test]
		public void SameInput_EncryptsDifferentlyEachCall()
		{
			// A fresh IV per call is what stops an observer seeing that two saves are identical. It
			// also means Apply is deliberately NOT deterministic — the contract is reversibility.
			using var transform = new AesCbcHmacTransform(TestKey());
			var payload = Payload(256);

			using var first = new PooledArrayBufferWriter();
			transform.Apply(payload, first);

			using var second = new PooledArrayBufferWriter();
			transform.Apply(payload, second);

			CollectionAssert.AreNotEqual(first.WrittenSpan.ToArray(), second.WrittenSpan.ToArray(),
				"The same plaintext must encrypt differently each time.");
		}

		[Test]
		public void TamperedCiphertext_FailsTheMacBeforeDecrypting()
		{
			// The keyed tag does what the envelope's unkeyed checksum cannot: anyone can recompute a
			// checksum, so only this detects a deliberate edit.
			using var transform = new AesCbcHmacTransform(TestKey());

			using var applied = new PooledArrayBufferWriter();
			transform.Apply(Payload(256), applied);

			var tampered = applied.WrittenSpan.ToArray();
			tampered[^1] ^= 0xFF;

			using var output = new PooledArrayBufferWriter();

			var thrown = Assert.Throws<SaveCorruptedException>(() => transform.Reverse(tampered, output));

			Assert.AreEqual(SaveCorruptedExceptionReason.ChecksumMismatch, thrown.Reason,
				"The HMAC must reject the edit before any decryption is attempted.");
		}

		[Test]
		public void WrongKey_IsRejected()
		{
			using var writer = new AesCbcHmacTransform(TestKey());

			using var applied = new PooledArrayBufferWriter();
			writer.Apply(Payload(128), applied);

			var otherKey = TestKey();
			otherKey[0] ^= 0xFF;

			using var reader = new AesCbcHmacTransform(otherKey);
			using var output = new PooledArrayBufferWriter();

			Assert.Throws<SaveCorruptedException>(() => reader.Reverse(applied.WrittenSpan.ToArray(), output));
		}

		[UnityTest]
		public IEnumerator PlaintextNeverReachesStorage() => AsyncTest.Run(async () =>
		{
			var inner = new MemoryStorage();
			using var storage = new TransformStorage(inner, new AesCbcHmacTransform(TestKey()));
			var payload = Payload(1000);

			var data = new NativeArray<byte>(payload, Allocator.Persistent);

			try
			{
				await storage.WriteAsync(Key, data);
			}
			finally
			{
				data.Dispose();
			}

			CollectionAssert.AreNotEqual(payload, inner.Data[Key], "Plaintext must not reach storage.");
		});
	}
}
