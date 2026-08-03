#if PERSISTENCE_HAS_PROTOBUF
using NUnit.Framework;
using Saesentsessis.Persistence.Buffers;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Serialization;
using Saesentsessis.Persistence.Serialization.ProtobufNet;
using UnityEngine;

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>
	/// The protobuf-net sample against the shared <see cref="SerializerContractTests"/> suite. Unlike
	/// the other two backends this one needs no attributes on the shard: the sample registers types
	/// on first use and infers members, mirroring Unity's own field-based contract.
	/// </summary>
	public sealed class ProtobufNetSerializerTests : SerializerContractTests
	{
		protected override string Backend => "protobuf-net";

		protected override ISerializer CreateSerializer() => new ProtobufNetSerializer();

		protected override IDataShard CreateShard(SerializableGuid id, int value, string text)
			=> new ProtobufShard(id, value, text);

		protected override (int value, string text) ReadShard(IDataShard shard)
			=> (((ProtobufShard)shard).value, ((ProtobufShard)shard).text);

		[Test]
		public void PrivateSerializeFieldMembers_AreIncluded()
		{
			// The sample deliberately mirrors Unity's contract: public fields plus private fields
			// marked [SerializeField]. The identity below is one of the latter, so if the inference
			// rule regresses to public-only the id silently stops round-tripping.
			var serializer = new ProtobufNetSerializer();
			var id = new SerializableGuid(0xAABBCCDD, 0x11223344);
			var shard = new ProtobufShard(id, 7, "private-id");

			using var writer = new PooledArrayBufferWriter();
			serializer.Serialize(shard, typeof(ProtobufShard), writer);

			var restored = (ProtobufShard)serializer.Deserialize(writer.WrittenSpan, typeof(ProtobufShard));

			Assert.AreEqual(id, restored.Identifier,
				"A private [SerializeField] id must be inferred as a protobuf member.");
		}

		[Test]
		public void GetOnlyProperties_AreSkipped_NotThrown()
		{
			// `Identifier` is get-only and would have no setter to deserialize into. The sample skips
			// such properties; this pins that a shard exposing one still registers cleanly.
			var serializer = new ProtobufNetSerializer();

			using var writer = new PooledArrayBufferWriter();

			Assert.DoesNotThrow(
				() => serializer.Serialize(new ProtobufShard(default, 1, "x"), typeof(ProtobufShard), writer),
				"A get-only property must not break type registration.");
		}

		[Test]
		public void RegisteringTheSameType_Twice_IsIdempotent()
		{
			// EnsureRegistered runs per serializer instance and guards a RuntimeTypeModel that throws
			// on a duplicate Add. Two serializations of the same type must not trip it.
			var serializer = new ProtobufNetSerializer();
			var shard = new ProtobufShard(new SerializableGuid(1, 2), 3, "twice");

			using var first = new PooledArrayBufferWriter();
			using var second = new PooledArrayBufferWriter();

			Assert.DoesNotThrow(() => serializer.Serialize(shard, typeof(ProtobufShard), first));
			Assert.DoesNotThrow(() => serializer.Serialize(shard, typeof(ProtobufShard), second));

			CollectionAssert.AreEqual(first.WrittenSpan.ToArray(), second.WrittenSpan.ToArray(),
				"The same value must encode identically on a second pass.");
		}

		/// <summary>
		/// Shaped like the package's own <c>TestShard</c>: a private <c>[SerializeField]</c> identity
		/// plus public payload fields, which is what the sample's member inference targets.
		/// </summary>
		public sealed class ProtobufShard : IDataShard
		{
			[SerializeField] private SerializableGuid id;

			public int value;
			public string text;

			private bool _dirty = true;

			public ProtobufShard() { }

			public ProtobufShard(SerializableGuid id, int value, string text)
			{
				this.id = id;
				this.value = value;
				this.text = text;
			}

			public SerializableGuid Identifier => id;
			public bool IsDirty => _dirty;

			public void ClearDirty() => _dirty = false;
		}
	}
}
#endif
