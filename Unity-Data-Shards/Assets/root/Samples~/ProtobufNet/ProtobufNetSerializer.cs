using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using Saesentsessis.Persistence.Core;
using ProtoBuf.Meta;
using UnityEngine;

namespace Saesentsessis.Persistence.Serialization.ProtobufNet
{
	/// <summary>
	/// <see cref="ISerializer"/> backed by protobuf-net (v3, which is <see cref="IBufferWriter{T}"/>-
	/// and span-native). Holds a private <see cref="RuntimeTypeModel"/> configured with the
	/// <see cref="SerializableGuidSurrogate"/>; shard types are registered on first use with their
	/// public instance members auto-mapped, so no <c>[ProtoContract]</c> attributes are required.
	/// </summary>
	/// <remarks>
	/// Field numbers are assigned from member metadata order. Do NOT reorder or remove a shard's
	/// public members between builds without a migration — protobuf is positional. IL2CPP: protobuf-net
	/// uses reflection; preserve shard types from managed stripping (<c>link.xml</c> / <c>[Preserve]</c>).
	/// </remarks>
	public sealed class ProtobufNetSerializer : ISerializer
	{
		private readonly RuntimeTypeModel _model;
		private readonly HashSet<Type> _registered = new();
		private readonly object _gate = new();

		public ProtobufNetSerializer(RuntimeTypeModel model = null)
		{
			_model = model ?? RuntimeTypeModel.Create();
			_model.Add(typeof(SerializableGuid), false).SetSurrogate(typeof(SerializableGuidSurrogate));
		}

		public bool SupportsBackgroundSerialization => true;

		public void Serialize(object value, Type type, IBufferWriter<byte> writer)
		{
			EnsureRegistered(type);

			// protobuf-net v3: buffer-native, appends straight into the arena. The IBufferWriter
			// overload manages its own writer state, so there is nothing to create or dispose here.
			_model.Serialize(writer, value, null);
		}

		public object Deserialize(ReadOnlySpan<byte> data, Type type)
		{
			EnsureRegistered(type);

			// Span-native, so the payload is read straight out of the pipeline's buffer. Note the
			// argument order: the type-first overload takes (type, source, value, userState) — the
			// ReadOnlyMemory one is (source, type, value, userState), and mixing the two up makes
			// protobuf-net infer a contract for System.RuntimeType instead of the shard.
			return _model.Deserialize(type, data, null, null);
		}

		/// <summary>Registers a shard type with public-member inference (protobuf-net has no runtime ImplicitFields toggle).</summary>
		private void EnsureRegistered(Type type)
		{
			if (_registered.Contains(type))
				return;

			lock (_gate)
			{
				if (_registered.Contains(type) || _model.IsDefined(type))
				{
					_registered.Add(type);
					return;
				}

				var meta = _model.Add(type, false);
				var field = 1;

				// Mirror Unity's field-based contract: public fields plus private [SerializeField]
				// fields (so shards with a private [SerializeField] id round-trip), and public
				// read/write properties. Get-only properties (e.g. Identifier) are skipped.
				var members = new List<MemberInfo>();

				foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
				{
					if (f.IsDefined(typeof(NonSerializedAttribute), inherit: false))
						continue;

					if (f.IsPublic || f.IsDefined(typeof(SerializeField), inherit: false))
						members.Add(f);
				}

				foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
					if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
						members.Add(property);

				members.Sort(static (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));

				foreach (var member in members)
					meta.Add(field++, member.Name);

				_registered.Add(type);
			}
		}
	}
}
