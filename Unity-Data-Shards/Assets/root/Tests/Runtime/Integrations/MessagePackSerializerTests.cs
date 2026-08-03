#if PERSISTENCE_HAS_MESSAGEPACK
using System;
using MessagePack;
using NUnit.Framework;
using Saesentsessis.Persistence.Buffers;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Serialization;
using Saesentsessis.Persistence.Serialization.MessagePack;

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>
	/// The MessagePack sample against the shared <see cref="SerializerContractTests"/> suite, plus
	/// the checks specific to this backend's resolver wiring.
	/// </summary>
	public sealed class MessagePackSerializerTests : SerializerContractTests
	{
		protected override string Backend => "MessagePack";

		protected override ISerializer CreateSerializer() => new MessagePackShardSerializer();

		protected override IDataShard CreateShard(SerializableGuid id, int value, string text)
			=> new MessagePackShard { id = id, value = value, text = text };

		protected override (int value, string text) ReadShard(IDataShard shard)
			=> (((MessagePackShard)shard).value, ((MessagePackShard)shard).text);

		[Test]
		public void CustomFormatter_EncodesTheGuidAsSixteenRawBytes()
		{
			// The sample's selling point over a default encoding: a 16-byte bin rather than a hex
			// string. If a future resolver change drops the custom formatter this silently doubles
			// every save, and nothing else in the suite would notice.
			var serializer = new MessagePackShardSerializer();
			var probe = new GuidOnlyShard { id = new SerializableGuid(ulong.MaxValue, ulong.MaxValue) };

			using var writer = new PooledArrayBufferWriter();
			serializer.Serialize(probe, typeof(GuidOnlyShard), writer);

			// Array header (1) + bin8 header (2) + 16 payload bytes. A hex-string encoding of the
			// same value would need 32+ bytes of payload alone.
			Assert.LessOrEqual(writer.WrittenLength, 24,
				"The id is not being written through SerializableGuidMessagePackFormatter.");
		}

		[Test]
		public void Resolver_ReportsAMissingFormatterRatherThanWritingGarbage()
		{
			// An un-annotated type has no formatter under StandardResolver. It must throw, not
			// silently produce an empty payload that fails much later at load time.
			var serializer = new MessagePackShardSerializer();

			using var writer = new PooledArrayBufferWriter();

			Assert.Throws<MessagePackSerializationException>(
				() => serializer.Serialize(new UnannotatedShard(), typeof(UnannotatedShard), writer),
				"An unsupported shard type must fail loudly at save time.");
		}

		[MessagePackObject]
		public sealed class MessagePackShard : IDataShard
		{
			[Key(0)] public SerializableGuid id;
			[Key(1)] public int value;
			[Key(2)] public string text;

			[IgnoreMember] private bool _dirty = true;

			[IgnoreMember] public SerializableGuid Identifier => id;
			[IgnoreMember] public bool IsDirty => _dirty;

			public void ClearDirty() => _dirty = false;
		}

		[MessagePackObject]
		public sealed class GuidOnlyShard : IDataShard
		{
			[Key(0)] public SerializableGuid id;

			[IgnoreMember] public SerializableGuid Identifier => id;
		}

		/// <summary>Deliberately carries no MessagePack contract.</summary>
		public sealed class UnannotatedShard : IDataShard
		{
			public SerializableGuid Identifier => default;
		}
	}
}
#endif
