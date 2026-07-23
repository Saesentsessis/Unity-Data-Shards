using System;
using System.Buffers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Serialization.SystemTextJson
{
	/// <summary>
	/// <see cref="ISerializer"/> backed by System.Text.Json. <see cref="Utf8JsonWriter"/> targets
	/// <see cref="IBufferWriter{T}"/> natively, so serialization writes straight into the pipeline
	/// arena, and deserialization reads straight from the payload span — no intermediate copies.
	/// The <see cref="SerializableGuidJsonConverter"/> is registered by default.
	/// </summary>
	/// <remarks>
	/// System.Text.Json is reflection-based here (Unity cannot run its source generator). On IL2CPP,
	/// preserve your shard types via a <c>link.xml</c> or <c>[Preserve]</c> so their members survive
	/// managed stripping.
	/// </remarks>
	public sealed class SystemTextJsonSerializer : ISerializer
	{
		private static JsonSerializerOptions CreateDefaultOptions()
		{
			return new JsonSerializerOptions
			{
				IncludeFields = true,
				TypeInfoResolverChain =
				{
					new DefaultJsonTypeInfoResolver
					{
						Modifiers =
						{
							CreateSerializeFieldModifier
						}
					}
				},
				Converters =
				{
					new SerializableGuidJsonConverter(),
				}
			};

			void CreateSerializeFieldModifier(JsonTypeInfo info)
			{
				foreach (var field in info.Type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
				{
					if (field.IsDefined(typeof(UnityEngine.SerializeField), false) == false)
						continue;
								
					var property = info.CreateJsonPropertyInfo(field.FieldType, field.Name);
					property.Get = field.GetValue;
					property.Set = field.SetValue;
					info.Properties.Add(property);
				}
			}
		}
		
		private readonly JsonSerializerOptions _options;

		public SystemTextJsonSerializer(JsonSerializerOptions options = null)
		{
			_options = options ?? CreateDefaultOptions();
		}

		public bool SupportsBackgroundSerialization => true;

		public void Serialize(object value, Type type, IBufferWriter<byte> writer)
		{
			using var jsonWriter = new Utf8JsonWriter(writer);
			JsonSerializer.Serialize(jsonWriter, value, type, _options);
		}

		public object Deserialize(ReadOnlySpan<byte> data, Type type)
		{
			return JsonSerializer.Deserialize(data, type, _options);
		}
	}
}
