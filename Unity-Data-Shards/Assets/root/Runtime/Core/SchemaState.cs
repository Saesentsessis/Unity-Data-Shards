using System;
using System.Runtime.CompilerServices;

namespace Persistence.Core
{
	/// <summary>
	/// A point in migration space: the stored type name (as written into the save
	/// envelope — the CLR type may no longer exist) plus the schema version.
	/// </summary>
	public readonly struct SchemaState : IEquatable<SchemaState>
	{
		public readonly string TypeName;
		public readonly int Version;

		private readonly int _hashCode;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public SchemaState(string typeName, int version)
		{
			TypeName = typeName;
			Version = version;
			_hashCode = HashCode.Combine(typeName, version);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool Equals(SchemaState other)
			=> Version == other.Version && string.Equals(TypeName, other.TypeName, StringComparison.Ordinal);

		public override bool Equals(object obj)
			=> obj is SchemaState other && Equals(other);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public override int GetHashCode() => _hashCode;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool operator ==(SchemaState left, SchemaState right) => left.Equals(right);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool operator !=(SchemaState left, SchemaState right) => !left.Equals(right);
	}
}
