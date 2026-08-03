#if PERSISTENCE_HAS_NEWTONSOFT
using System;
using System.Collections;
using System.Text;
using NUnit.Framework;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Serialization.Newtonsoft;
using UnityEngine.TestTools;

namespace Saesentsessis.Persistence.Tests
{
	public class NewtonsoftSerializerTests
	{
		private const string Slot = "newtonsoft-slot";

		[UnityTest]
		public IEnumerator RoundTrip_PreservesShardData() => AsyncTest.Run(async () =>
		{
			var storage = new MemoryStorage();
			using var manager = new SaveManager(new NewtonsoftJsonSerializer(), new SingleFileSaveLayout(storage));

			var store = new ShardStore();
			var id = Guid.NewGuid();
			store.Add(new TestShard(id, 314, "newtonsoft"));

			await manager.SaveAsync(Slot, store);
			var loaded = (await manager.LoadAsync(Slot)).AsShardStore();
			
			Assert.IsTrue(loaded.TryGet<TestShard>(id, out var shard));
			Assert.AreEqual(314, shard.value);
			Assert.AreEqual("newtonsoft", shard.text);
		});

		[Test]
		public void SerializableGuid_SerializesAsHexString()
		{
			var serializer = new NewtonsoftJsonSerializer();
			var id = Guid.NewGuid();
			var shard = new TestShard(id, 1, "x");

			using var writer = new Saesentsessis.Persistence.Buffers.PooledArrayBufferWriter();
			serializer.Serialize(shard, shard.GetType(), writer);
			var json = Encoding.UTF8.GetString(writer.WrittenSpan);

			// The 32-char hex form must appear verbatim in the JSON.
			StringAssert.Contains(((SerializableGuid)id).ToString(), json);
		}

		[Test]
		public void EmitsUtf8DirectlyIncludingSurrogatePairs()
		{
			// This serializer now encodes straight into the arena through a stateful Encoder rather
			// than building a string. Newtonsoft writes a surrogate pair as two separate chars, so a
			// stateless encode would turn each half into U+FFFD — text that survives a round trip is
			// the only proof the encoder state is carried across Write calls.
			const string text = "emoji 🚀 accents äöü 日本語";

			var serializer = new NewtonsoftJsonSerializer();
			var shard = new TestShard(Guid.NewGuid(), 7, text);

			using var writer = new Saesentsessis.Persistence.Buffers.PooledArrayBufferWriter();
			serializer.Serialize(shard, shard.GetType(), writer);

			var json = Encoding.UTF8.GetString(writer.WrittenSpan);

			StringAssert.Contains("🚀", json, "The surrogate pair was split or replaced.");
			StringAssert.Contains("äöü", json);
			StringAssert.Contains("日本語", json);

			var restored = (TestShard)serializer.Deserialize(writer.WrittenSpan, typeof(TestShard));
			Assert.AreEqual(text, restored.text);
		}

		[Test]
		public void RepeatedSerializeReusesTheWriterWithoutLeakingState()
		{
			// The adapter is [ThreadStatic] and reused, so a leftover pending surrogate or a stale
			// destination from the previous call would corrupt the next shard rather than this one.
			var serializer = new NewtonsoftJsonSerializer();

			for (var i = 0; i < 5; i++)
			{
				var shard = new TestShard(Guid.NewGuid(), i, i % 2 == 0 ? "🚀" : "plain");

				using var writer = new Saesentsessis.Persistence.Buffers.PooledArrayBufferWriter();
				serializer.Serialize(shard, shard.GetType(), writer);

				var restored = (TestShard)serializer.Deserialize(writer.WrittenSpan, typeof(TestShard));

				Assert.AreEqual(shard.value, restored.value);
				Assert.AreEqual(shard.text, restored.text, $"Iteration {i} carried state from the previous one.");
			}
		}
	}
}
#endif
