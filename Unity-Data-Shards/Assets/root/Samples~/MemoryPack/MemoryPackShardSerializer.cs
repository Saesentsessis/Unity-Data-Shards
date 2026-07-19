using System;
using System.Buffers;
using MemoryPack;
using Persistence.Core;

namespace Persistence.Serialization.MemoryPack
{
	/// <summary>
	/// <see cref="ISerializer"/> backed by Cysharp's MemoryPack — a zero-encoding binary serializer
	/// that is <see cref="IBufferWriter{T}"/>-native on write and span-native on read, making it the
	/// tightest fit for this package's arena pipeline.
	/// </summary>
	/// <remarks>
	/// LIMITATION: MemoryPack is source-generated. Every shard type must be declared
	/// <c>[MemoryPackable] partial</c> — arbitrary un-annotated POCOs are not supported (unlike the
	/// JSON backends). This also means IL2CPP works without extra AOT steps, since no runtime code
	/// generation is involved.
	/// </remarks>
	public sealed class MemoryPackShardSerializer : ISerializer
	{
		private readonly MemoryPackSerializerOptions _options;

		static MemoryPackShardSerializer()
		{
			// Registration is process-wide and idempotent.
			MemoryPackFormatterProvider.Register(new SerializableGuidMemoryPackFormatter());
		}

		public MemoryPackShardSerializer(MemoryPackSerializerOptions options = null)
		{
			_options = options ?? MemoryPackSerializerOptions.Default;
		}

		public bool SupportsBackgroundSerialization => true;

		public void Serialize(object value, Type type, IBufferWriter<byte> writer)
		{
			MemoryPackSerializer.Serialize(type, in writer, value, _options);
		}

		public object Deserialize(ReadOnlySpan<byte> data, Type type)
		{
			object result = null;
			MemoryPackSerializer.Deserialize(type, data, ref result, _options);
			return result;
		}
	}
}
