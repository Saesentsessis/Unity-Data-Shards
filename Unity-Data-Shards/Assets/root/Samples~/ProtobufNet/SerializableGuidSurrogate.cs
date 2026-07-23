using Saesentsessis.Persistence.Core;
using ProtoBuf;

namespace Saesentsessis.Persistence.Serialization.ProtobufNet
{
	/// <summary>
	/// protobuf-net surrogate for <see cref="SerializableGuid"/> — protobuf-net maps unsupported
	/// types through a surrogate rather than a converter. Stores the id as its two raw ulongs
	/// (fixed64), string-free. Registered on the serializer's <c>RuntimeTypeModel</c>.
	/// </summary>
	[ProtoContract]
	public struct SerializableGuidSurrogate
	{
		[ProtoMember(1, DataFormat = DataFormat.FixedSize)]
		public ulong Head;

		[ProtoMember(2, DataFormat = DataFormat.FixedSize)]
		public ulong Tail;

		public static implicit operator SerializableGuidSurrogate(SerializableGuid value)
			=> new() { Head = value.Head, Tail = value.Tail };

		public static implicit operator SerializableGuid(SerializableGuidSurrogate surrogate)
			=> new(surrogate.Head, surrogate.Tail);
	}
}
