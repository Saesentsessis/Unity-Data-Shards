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
			var manager = new SaveManager(new NewtonsoftJsonSerializer(), new SingleFileSaveLayout(storage));

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
	}
}
#endif
