using System;
using System.Buffers;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence
{
    public static class SerializerExtensions
    {
        public static void Serialize<T>(this ISerializer serializer, T value, IBufferWriter<byte> writer) where T : class
            => serializer.Serialize(value, typeof(T), writer);

        public static T Deserialize<T>(this ISerializer serializer, ReadOnlySpan<byte> data) where T : class
            => (T)serializer.Deserialize(data, typeof(T));
    }
}
