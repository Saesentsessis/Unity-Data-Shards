using System;
using System.Buffers;
using MessagePack;
using MessagePack.Resolvers;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Serialization.MessagePack
{
	/// <summary>
	/// <see cref="ISerializer"/> backed by MessagePack-CSharp. Serialization writes straight into the
	/// pipeline arena; deserialization copies the payload span into a managed buffer (MessagePack's
	/// deserialize entry point takes <see cref="ReadOnlyMemory{T}"/>, not a bare span).
	/// </summary>
	/// <remarks>
	/// AOT / IL2CPP: MessagePack requires generated resolvers. Run the <c>mpc</c> code generator and
	/// register the produced resolver, or dynamic serialization will throw at runtime on IL2CPP
	/// (there is no runtime IL emit). See the sample README.
	/// </remarks>
	public sealed class MessagePackShardSerializer : ISerializer
	{
		private readonly MessagePackSerializerOptions _options;

		public MessagePackShardSerializer(MessagePackSerializerOptions options = null)
		{
			if (options == null)
			{
				var resolver = MyApplicationResolver.Instance;

				options = MessagePackSerializerOptions.Standard.WithResolver(resolver);
			}

			_options = options;
		}

		public bool SupportsBackgroundSerialization => true;

		public void Serialize(object value, Type type, IBufferWriter<byte> writer)
		{
			MessagePackSerializer.Serialize(type, writer, value, _options);
		}

		public object Deserialize(ReadOnlySpan<byte> data, Type type)
		{
			return MessagePackSerializer.Deserialize(type, data.ToArray(), _options);
		}
	}
}
