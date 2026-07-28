using System;
using System.Runtime.InteropServices;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Layout
{
	/// <summary>
	/// One record per persisted shard: its identity plus an index into the envelope's
	/// deduplicated type table.
	/// <para>
	/// This struct is written to disk verbatim (the record block is a single memcpy), so its
	/// layout IS the wire format. <c>Pack = 4</c> drops the 4 trailing padding bytes the natural
	/// 8-byte alignment would add, giving a 20-byte stride. Packing is used rather than an
	/// undersized <c>Size = 20</c>: the latter asks the runtime to emit a struct smaller than its
	/// natural size, and Mono, IL2CPP and CoreCLR are not guaranteed to agree on that — a
	/// disagreement would make editor-written saves unreadable in a player build.
	/// </para>
	/// </summary>
	[Serializable]
	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	public struct ShardRecord
	{
		public SerializableGuid Id;
		public int TypeIndex;
	}
}
