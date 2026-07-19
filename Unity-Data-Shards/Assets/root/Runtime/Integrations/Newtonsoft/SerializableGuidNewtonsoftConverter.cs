#if PERSISTENCE_HAS_NEWTONSOFT
using System;
using Newtonsoft.Json;
using Persistence.Core;

namespace Persistence.Serialization.Newtonsoft
{
	/// <summary>
	/// Writes <see cref="SerializableGuid"/> as its 32-char "N" hex string. Newtonsoft has no
	/// span write API, so the write path allocates one string (the single documented allocation
	/// among the shipped serializer integrations).
	/// </summary>
	public sealed class SerializableGuidNewtonsoftConverter : JsonConverter<SerializableGuid>
	{
		public override void WriteJson(JsonWriter writer, SerializableGuid value, JsonSerializer serializer)
		{
			writer.WriteValue(value.ToString());
		}

		public override SerializableGuid ReadJson(JsonReader reader, Type objectType, SerializableGuid existingValue,
			bool hasExistingValue, JsonSerializer serializer)
		{
			if (reader.TokenType == JsonToken.Null)
				return SerializableGuid.Empty;

			var text = (string)reader.Value;

			if (!SerializableGuid.TryParse(text, out var guid))
				throw new JsonSerializationException($"Cannot parse '{text}' as a SerializableGuid.");

			return guid;
		}
	}
}
#endif
