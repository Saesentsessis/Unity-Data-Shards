#if PERSISTENCE_HAS_NEWTONSOFT
using System;
using System.Buffers;
using System.Text;
using Newtonsoft.Json;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Serialization.Newtonsoft
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

		[ThreadStatic] private static BufferWriterTextWriter _textWriter;

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

		/// <inheritdoc />
		/// <remarks>
		/// Emits straight into the arena. Newtonsoft targets <c>TextWriter</c>, so the obvious
		/// bridge is a <c>StringBuilder</c> plus <c>ToString()</c> — which costs a payload-sized
		/// string and a payload-sized encode per shard, and made this the most allocation-hungry
		/// serializer in the package. <see cref="BufferWriterTextWriter"/> removes both.
		/// <para>
		/// The adapter is <c>[ThreadStatic]</c> rather than a field:
		/// <see cref="SupportsBackgroundSerialization"/> is true, so this runs on pool threads, and
		/// one serializer instance may be shared by several managers. Per-thread reuse keeps it
		/// allocation-free without assuming a caller.
		/// </para>
		/// </remarks>
		public void Serialize(object value, Type type, IBufferWriter<byte> writer)
		{
			var text = _textWriter ??= new BufferWriterTextWriter();
			text.Reset(writer);

			// CloseOutput: the adapter wraps the pipeline's arena, and disposing the JSON writer
			// must not be able to end the save.
			using (var jsonWriter = new JsonTextWriter(text) { CloseOutput = false })
				_serializer.Serialize(jsonWriter, value, type);

			// Settles any surrogate half the encoder is still carrying.
			text.Flush();
		}

		public object Deserialize(ReadOnlySpan<byte> data, Type type)
		{
			var json = Utf8NoBom.GetString(data);

			using var reader = new JsonTextReader(new System.IO.StringReader(json));
			return _serializer.Deserialize(reader, type);
		}

	}
}
#endif
