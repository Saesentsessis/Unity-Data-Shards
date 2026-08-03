#if PERSISTENCE_HAS_MEMORYPACK
using MemoryPack;
using NUnit.Framework;
using Saesentsessis.Persistence.Buffers;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Serialization;
using Saesentsessis.Persistence.Serialization.MemoryPack;

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>
	/// The MemoryPack sample against the shared <see cref="SerializerContractTests"/> suite. This is
	/// the backend the pipeline fits tightest — it writes through <c>IBufferWriter&lt;byte&gt;</c>
	/// and reads from a span, so nothing is copied on either side.
	/// </summary>
	/// <remarks>
	/// <c>partial</c> because the shard types below are: MemoryPack's generator emits their
	/// formatters as partial extensions, so every <i>containing</i> type has to be partial as well
	/// (MEMPACK042). Only the real generator reports that — a plain compile accepts it.
	/// </remarks>
	public sealed partial class MemoryPackSerializerTests : SerializerContractTests
	{
		protected override string Backend => "MemoryPack";

		protected override ISerializer CreateSerializer() => new MemoryPackShardSerializer();

		protected override IDataShard CreateShard(SerializableGuid id, int value, string text)
			=> new MemoryPackShard { id = id, value = value, text = text };

		protected override (int value, string text) ReadShard(IDataShard shard)
			=> (((MemoryPackShard)shard).value, ((MemoryPackShard)shard).text);

		[Test]
		public void CustomFormatter_WritesTheGuidUnmanaged()
		{
			// SerializableGuid is a blittable pair of ulongs, so the sample's formatter writes it as
			// 16 raw bytes. Anything materially larger means the formatter was not registered and
			// MemoryPack fell back to a generated member-wise encoding.
			var serializer = new MemoryPackShardSerializer();
			var probe = new GuidOnlyShard { id = new SerializableGuid(ulong.MaxValue, 1) };

			using var writer = new PooledArrayBufferWriter();
			serializer.Serialize(probe, typeof(GuidOnlyShard), writer);

			Assert.LessOrEqual(writer.WrittenLength, 24,
				"The id is not going through SerializableGuidMemoryPackFormatter.");
		}

		[Test]
		public void NullString_RoundTripsAsNull()
		{
			// MemoryPack distinguishes null from empty on the wire, unlike the JSON backends. Worth
			// pinning: a shard that stores null must not come back as "" and vice versa.
			var serializer = new MemoryPackShardSerializer();
			var shard = new MemoryPackShard { id = new SerializableGuid(5, 5), value = 1, text = null };

			using var writer = new PooledArrayBufferWriter();
			serializer.Serialize(shard, typeof(MemoryPackShard), writer);

			var restored = (MemoryPackShard)serializer.Deserialize(writer.WrittenSpan, typeof(MemoryPackShard));

			Assert.IsNull(restored.text, "MemoryPack preserves the null/empty distinction.");
		}

		[MemoryPackable]
		public sealed partial class MemoryPackShard : IDataShard
		{
			public SerializableGuid id;
			public int value;
			public string text;

			[MemoryPackIgnore] private bool _dirty = true;

			[MemoryPackIgnore] public SerializableGuid Identifier => id;
			[MemoryPackIgnore] public bool IsDirty => _dirty;

			public void ClearDirty() => _dirty = false;
		}

		[MemoryPackable]
		public sealed partial class GuidOnlyShard : IDataShard
		{
			public SerializableGuid id;

			[MemoryPackIgnore] public SerializableGuid Identifier => id;
		}
	}
}
#endif
