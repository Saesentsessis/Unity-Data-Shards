using System;
using System.Buffers;
using System.Buffers.Binary;
using MessagePack;
using MessagePack.Formatters;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Serialization.MessagePack
{
	/// <summary>
	/// Encodes <see cref="SerializableGuid"/> as a raw 16-byte MessagePack bin (Head, Tail — both
	/// little-endian). Zero allocation, more compact than a hex string.
	/// </summary>
	public sealed class SerializableGuidMessagePackFormatter : IMessagePackFormatter<SerializableGuid>
	{
		public void Serialize(ref MessagePackWriter writer, SerializableGuid value, MessagePackSerializerOptions options)
		{
			Span<byte> tmp = stackalloc byte[16];
			BinaryPrimitives.WriteUInt64LittleEndian(tmp, value.Head);
			BinaryPrimitives.WriteUInt64LittleEndian(tmp.Slice(8), value.Tail);
			writer.Write(tmp);
		}

		public SerializableGuid Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
		{
			if (reader.TryReadNil())
				return SerializableGuid.Empty;

			var sequence = reader.ReadBytes();

			if (sequence is not { Length: 16 } bytes)
				throw new MessagePackSerializationException("Expected a 16-byte SerializableGuid payload.");

			Span<byte> tmp = stackalloc byte[16];
			bytes.CopyTo(tmp);

			var head = BinaryPrimitives.ReadUInt64LittleEndian(tmp);
			var tail = BinaryPrimitives.ReadUInt64LittleEndian(tmp.Slice(8));
			return new SerializableGuid(head, tail);
		}
	}
}
