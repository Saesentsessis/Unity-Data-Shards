using MemoryPack;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Serialization.MemoryPack
{
	/// <summary>
	/// Encodes <see cref="SerializableGuid"/> as its raw 16 unmanaged bytes. Because the struct is a
	/// blittable <c>LayoutKind.Sequential</c> pair of ulongs, MemoryPack can read/write it directly
	/// with no allocation and no hex conversion.
	/// </summary>
	public sealed class SerializableGuidMemoryPackFormatter : MemoryPackFormatter<SerializableGuid>
	{
		public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, ref SerializableGuid value)
		{
			writer.WriteUnmanaged(value);
		}

		public override void Deserialize(ref MemoryPackReader reader, ref SerializableGuid value)
		{
			reader.ReadUnmanaged(out value);
		}
	}
}
