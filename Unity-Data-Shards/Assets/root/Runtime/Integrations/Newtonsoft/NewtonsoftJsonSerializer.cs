#if PERSISTENCE_HAS_NEWTONSOFT
using System;
using System.Buffers;
using System.Text;
using Newtonsoft.Json;
using Persistence.Core;

namespace Persistence.Serialization.Newtonsoft
{
	/// <summary>
	/// <see cref="ISerializer"/> backed by Newtonsoft.Json (<c>com.unity.nuget.newtonsoft-json</c>).
	/// Use this over <see cref="UnityJsonSerializer"/> when you need contract control:
	/// <see cref="JsonSerializerSettings"/>, custom converters, private-field handling, polymorphism.
	/// The <see cref="SerializableGuidNewtonsoftConverter"/> is registered by default so ids serialize
	/// as compact hex strings.
	/// </summary>
	public sealed class NewtonsoftJsonSerializer : ISerializer
	{
		private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

		private readonly JsonSerializer _serializer;

		public NewtonsoftJsonSerializer(JsonSerializerSettings settings = null)
		{
			settings ??= new JsonSerializerSettings();

			// Default to Unity's field-based contract so shards written for UnityJsonSerializer
			// (private [SerializeField] fields, get-only Identifier) round-trip unchanged. A caller
			// who supplies their own resolver keeps full control.
			settings.ContractResolver ??= new UnitySerializationContractResolver();

			// Register the id converter unless the caller already supplied one.
			var hasGuidConverter = false;
			foreach (var converter in settings.Converters)
				if (converter is SerializableGuidNewtonsoftConverter)
				{
					hasGuidConverter = true;
					break;
				}

			if (!hasGuidConverter)
				settings.Converters.Add(new SerializableGuidNewtonsoftConverter());

			_serializer = JsonSerializer.Create(settings);
		}

		// Newtonsoft is thread-safe for plain data types.
		public bool SupportsBackgroundSerialization => true;

		public void Serialize(object value, Type type, IBufferWriter<byte> writer)
		{
			// Newtonsoft targets TextWriter, not bytes; serialize to a string then UTF-8 encode
			// in a single pass into the arena, mirroring UnityJsonSerializer.
			var json = SerializeToString(value, type);

			var span = writer.GetSpan(Utf8NoBom.GetMaxByteCount(json.Length));
			var written = Utf8NoBom.GetBytes(json.AsSpan(), span);
			writer.Advance(written);
		}

		public object Deserialize(ReadOnlySpan<byte> data, Type type)
		{
			var json = Utf8NoBom.GetString(data);

			using var reader = new JsonTextReader(new System.IO.StringReader(json));
			return _serializer.Deserialize(reader, type);
		}

		private string SerializeToString(object value, Type type)
		{
			var builder = new StringBuilder(256);

			using (var writer = new System.IO.StringWriter(builder))
			using (var jsonWriter = new JsonTextWriter(writer))
				_serializer.Serialize(jsonWriter, value, type);

			return builder.ToString();
		}
	}
}
#endif
