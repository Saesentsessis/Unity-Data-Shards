#if PERSISTENCE_HAS_NEWTONSOFT
using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine;

namespace Saesentsessis.Persistence.Serialization.Newtonsoft
{
	/// <summary>
	/// Makes Newtonsoft serialize the same members <see cref="UnityEngine.JsonUtility"/> does, so a
	/// shard authored for <see cref="UnityJsonSerializer"/> round-trips unchanged through Newtonsoft:
	/// public instance fields plus private instance fields marked <c>[SerializeField]</c>, walking the
	/// base hierarchy, excluding <c>[NonSerialized]</c> and <c>[JsonIgnore]</c>. Properties are not
	/// serialized (Unity is field-based) — this is what fixes the common
	/// "<c>[SerializeField] private id</c> + get-only <c>Identifier</c>" shard shape, whose id would
	/// otherwise never deserialize.
	/// </summary>
	public sealed class UnitySerializationContractResolver : DefaultContractResolver
	{
		protected override List<MemberInfo> GetSerializableMembers(Type objectType)
		{
			var members = new List<MemberInfo>();

			for (var type = objectType; type != null && type != typeof(object); type = type.BaseType)
			{
				foreach (var field in type.GetFields(
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				{
					if (field.IsDefined(typeof(NonSerializedAttribute), inherit: false))
						continue;

					if (field.IsDefined(typeof(JsonIgnoreAttribute), inherit: false))
						continue;

					if (field.IsPublic || field.IsDefined(typeof(SerializeField), inherit: false))
						members.Add(field);
				}
			}

			return members;
		}

		protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
		{
			var property = base.CreateProperty(member, memberSerialization);

			// Private [SerializeField] fields are otherwise reported as non-writable.
			if (member is FieldInfo)
			{
				property.Readable = true;
				property.Writable = true;
			}

			return property;
		}
	}
}
#endif
