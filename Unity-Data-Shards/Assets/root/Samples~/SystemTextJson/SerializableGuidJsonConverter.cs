using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Persistence.Core;

namespace Persistence.Serialization.SystemTextJson
{
	/// <summary>
	/// Reads/writes <see cref="SerializableGuid"/> as its 32-char "N" hex string with no heap
	/// allocation: the write path formats into a stack buffer, the read path copies the token into
	/// a stack buffer before parsing.
	/// </summary>
	public sealed class SerializableGuidJsonConverter : JsonConverter<SerializableGuid>
	{
		public override void Write(Utf8JsonWriter writer, SerializableGuid value, JsonSerializerOptions options)
		{
			Span<char> hex = stackalloc char[32];
			value.TryFormatHex(hex, out _);
			writer.WriteStringValue(hex);
		}

		public override SerializableGuid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType == JsonTokenType.Null)
				return SerializableGuid.Empty;

			// 64 chars covers the 32-char hex form with margin; CopyString unescapes in place.
			Span<char> chars = stackalloc char[64];
			var count = reader.CopyString(chars);

			if (!SerializableGuid.TryParse(chars.Slice(0, count), out var guid))
				throw new JsonException($"Cannot parse '{new string(chars.Slice(0, count))}' as a SerializableGuid.");

			return guid;
		}
	}
}
