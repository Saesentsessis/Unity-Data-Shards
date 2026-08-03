#if PERSISTENCE_HAS_SYSTEMTEXTJSON
using System;
using System.Text;
using System.Text.Json.Serialization;
using NUnit.Framework;
using Saesentsessis.Persistence.Buffers;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Serialization.SystemTextJson;

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>
	/// The System.Text.Json sample against the shared <see cref="SerializerContractTests"/> suite.
	/// <c>Utf8JsonWriter</c> targets <c>IBufferWriter&lt;byte&gt;</c> natively, so this backend
	/// writes into the arena without an intermediate string.
	/// </summary>
	public sealed class SystemTextJsonSerializerTests : SerializerContractTests
	{
		protected override string Backend => "System.Text.Json";

		protected override ISerializer CreateSerializer() => new SystemTextJsonSerializer();

		protected override IDataShard CreateShard(SerializableGuid id, int value, string text)
			=> new SystemJsonShard { id = id, value = value, text = text };

		protected override (int value, string text) ReadShard(IDataShard shard)
			=> (((SystemJsonShard)shard).value, ((SystemJsonShard)shard).text);

		[Test]
		public void CustomConverter_WritesTheGuidAsOneHexString()
		{
			// Shape, not size: a text format cannot match MemoryPack's 16 raw bytes, so asserting a
			// byte count here only ever encoded a misunderstanding. What the converter actually
			// promises is that the id is ONE 32-char hex string rather than the struct's two ulongs
			// spelled out member-wise, which is both larger and a different schema.
			var serializer = new SystemTextJsonSerializer();
			var probe = new GuidOnlyShard { id = new SerializableGuid(ulong.MaxValue, 1) };

			using var writer = new PooledArrayBufferWriter();
			serializer.Serialize(probe, typeof(GuidOnlyShard), writer);

			var json = Encoding.UTF8.GetString(writer.WrittenSpan.ToArray());

			StringAssert.IsMatch("\"id\"\\s*:\\s*\"[0-9a-fA-F]{32}\"", json);
			StringAssert.DoesNotContain("head", json,
				"The id fell back to member-wise encoding — SerializableGuidJsonConverter was not used.");
			StringAssert.DoesNotContain("tail", json);
		}

		[Test]
		public void NullString_RoundTripsAsNull()
		{
			// JSON has a real null literal, so unlike Unity's JsonUtility this backend can tell an
			// absent string from an empty one. Pinned because the round trip goes through a writer
			// the pipeline owns, where a dropped null would silently become "".
			var serializer = new SystemTextJsonSerializer();
			var shard = new SystemJsonShard { id = new SerializableGuid(5, 5), value = 1, text = null };

			using var writer = new PooledArrayBufferWriter();
			serializer.Serialize(shard, typeof(SystemJsonShard), writer);

			var restored = (SystemJsonShard)serializer.Deserialize(writer.WrittenSpan, typeof(SystemJsonShard));

			Assert.IsNull(restored.text, "SystemTextJson preserves the null/empty distinction.");
		}
		
		[Serializable]
		public sealed class SystemJsonShard : IDataShard
		{
			public SerializableGuid id;
			public int value;
			public string text;

			[JsonIgnore] private bool _dirty = true;

			[JsonIgnore]public SerializableGuid Identifier => id;
			[JsonIgnore]public bool IsDirty => _dirty;

			public void ClearDirty() => _dirty = false;
		}

		[Serializable]
		public sealed class GuidOnlyShard : IDataShard
		{
			public SerializableGuid id;
			
			[JsonIgnore] public SerializableGuid Identifier => id;
		}
	}
}
#endif