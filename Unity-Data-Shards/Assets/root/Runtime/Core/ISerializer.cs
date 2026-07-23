using System;
using System.Buffers;

namespace Saesentsessis.Persistence.Core
{
    /// <summary>
    /// Converts objects to/from raw bytes. Implementations choose the wire format
    /// (MessagePack, JSON, etc.). The append-style <see cref="IBufferWriter{T}"/>
    /// contract lets the pipeline own the backing memory (native arena or pooled
    /// managed buffer), so serializers never allocate output buffers themselves
    /// and never need to know the payload size up front.
    /// </summary>
    public interface ISerializer
    {
        /// <summary>
        /// True if <see cref="Serialize"/>/<see cref="Deserialize"/> may be called
        /// off the main thread. Serializers touching UnityEngine.Object state must
        /// return false; plain-data serializers should return true so the pipeline
        /// can move serialization to the thread pool.
        /// </summary>
        bool SupportsBackgroundSerialization { get; }

        /// <summary>Serializes value by appending its bytes to <paramref name="writer"/>.</summary>
        void Serialize(object value, Type type, IBufferWriter<byte> writer);

        /// <summary>Deserializes the given bytes back into an object of the specified type.</summary>
        object Deserialize(ReadOnlySpan<byte> data, Type type);
    }
}
