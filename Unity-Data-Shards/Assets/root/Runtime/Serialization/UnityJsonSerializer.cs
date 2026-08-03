using System;
using System.Buffers;
using System.Text;
using Saesentsessis.Persistence.Core;
using UnityEngine;

namespace Saesentsessis.Persistence.Serialization
{
    /// <summary>
    /// Uses Unity's <see cref="JsonUtility"/> under the hood. Serializes [SerializeField]
    /// fields exactly as Unity does, but is opaque: it cannot honor [MessagePack.IgnoreMember] and
    /// writes <see cref="SerializableGuid"/> as its two backing ulongs. Use
    /// <c>NewtonsoftJsonSerializer</c> when you need contract control or string GUIDs.
    /// </summary>
    public sealed class UnityJsonSerializer : ISerializer
    {
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly bool _prettyPrint;

        public UnityJsonSerializer(bool prettyPrint = false)
        {
            _prettyPrint = prettyPrint;
        }

        // JsonUtility.ToJson/FromJson are thread-safe for plain serializable types
        // (no UnityEngine.Object access), which is the shard contract.
        public bool SupportsBackgroundSerialization => true;

        /// <inheritdoc />
        /// <remarks>
        /// Reserves the <b>exact</b> encoded length rather than the three-bytes-per-char worst case.
        /// The worst case looked free — one reservation, one encode, no pre-pass — but it asked the
        /// arena for three times what it wrote, and the arena is pre-sized to the previous save's
        /// payload. Demanding <c>P + 2·blob</c> at the last shard exceeded that every time, so the
        /// arena doubled and memcpy'd everything already written, on every save, invisibly.
        /// <para>
        /// <see cref="Encoding.GetByteCount(string)"/> is a second pass over a string that is still
        /// in cache and allocates nothing — a far better trade than a payload-sized reallocation.
        /// </para>
        /// <para>
        /// The string <see cref="JsonUtility"/> returns is what this serializer cannot avoid:
        /// there is no streaming entry point, so a UTF-16 copy of every shard is inherent to the
        /// backend. Use a buffer-native serializer where that matters.
        /// </para>
        /// </remarks>
        public void Serialize(object value, Type type, IBufferWriter<byte> writer)
        {
            var json = JsonUtility.ToJson(value, _prettyPrint);

            var byteCount = Utf8NoBom.GetByteCount(json);
            var span = writer.GetSpan(byteCount);

            writer.Advance(Utf8NoBom.GetBytes(json.AsSpan(), span));
        }

        public object Deserialize(ReadOnlySpan<byte> data, Type type)
        {
            var json = Utf8NoBom.GetString(data);
            return JsonUtility.FromJson(json, type);
        }
    }
}
