using System;
using System.Buffers;

namespace Persistence.Core
{
	/// <summary>
	/// A single migration step operating on the RAW serialized blob, before any
	/// deserialization — so a step can reshape fields, and the source CLR type does
	/// not need to exist anymore. The source side is therefore identified by the
	/// stored type NAME; the destination is a concrete <see cref="Type"/>, because
	/// the target of a migration always exists in current code.
	/// Implementations must be stateless and pure; the wire format of src/dst is
	/// whatever the configured <see cref="ISerializer"/> produces.
	/// </summary>
	public interface IShardMigration
	{
		/// <summary>The stored (namespace-qualified) type name this migration accepts.</summary>
		string FromTypeName { get; }
		/// <summary>The schema version this migration accepts as input.</summary>
		int FromVersion { get; }

		/// <summary>The concrete type this migration emits blob bytes for.</summary>
		Type ToType { get; }
		/// <summary>The schema version produced after migration.</summary>
		int ToVersion { get; }

		/// <summary>Transforms the raw blob from the From-state to the To-state.</summary>
		void Migrate(ReadOnlySpan<byte> src, IBufferWriter<byte> dst);
	}
}
