using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Persistence.Layout
{
	/// <summary>
	/// Compact, version-free descriptor of a shard type, mirroring Unity's [SerializeReference]
	/// approach: the namespace-qualified type name plus the simple assembly name (no Version,
	/// Culture, or PublicKeyToken), together with the schema version its instances were written
	/// at. One of these is stored per distinct type in a save envelope's deduplicated type table.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public readonly struct SerializedType : IEquatable<SerializedType>
	{
		public readonly string TypeName;
		public readonly string AssemblyName;
		public readonly int SchemaVersion;

		// Precomputed at construction: dictionary probes (SerializedTypeHelper caches)
		// would otherwise hash both strings on every lookup.
		private readonly int _hashCode;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public SerializedType(string typeName, string assemblyName, int schemaVersion)
		{
			TypeName = typeName;
			AssemblyName = assemblyName;
			SchemaVersion = schemaVersion;
			_hashCode = HashCode.Combine(typeName, assemblyName, schemaVersion);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool Equals(SerializedType other)
			=> SchemaVersion == other.SchemaVersion
			   && string.Equals(TypeName, other.TypeName, StringComparison.Ordinal)
			   && string.Equals(AssemblyName, other.AssemblyName, StringComparison.Ordinal);

		public override bool Equals(object obj) => obj is SerializedType other && Equals(other);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public override int GetHashCode() => _hashCode;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool operator ==(SerializedType left, SerializedType right) => left.Equals(right);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool operator !=(SerializedType left, SerializedType right) => !left.Equals(right);
	}
}
