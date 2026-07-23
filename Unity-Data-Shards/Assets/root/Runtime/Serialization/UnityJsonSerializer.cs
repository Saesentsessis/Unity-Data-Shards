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

        public void Serialize(object value, Type type, IBufferWriter<byte> writer)
        {
            var json = JsonUtility.ToJson(value, _prettyPrint);

            // Worst-case UTF-8 length for UTF-16 input is 3 bytes/char, so one
            // GetSpan reservation + one encode pass — no GetByteCount pre-pass.
            var span = writer.GetSpan(json.Length * 3);
            var written = Utf8NoBom.GetBytes(json.AsSpan(), span);
            writer.Advance(written);
        }

        public object Deserialize(ReadOnlySpan<byte> data, Type type)
        {
            var json = Utf8NoBom.GetString(data);
            return JsonUtility.FromJson(json, type);
        }
    }
}
