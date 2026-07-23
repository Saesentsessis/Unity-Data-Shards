using System;
using System.Runtime.InteropServices;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Layout
{
	[Serializable]
	[StructLayout(LayoutKind.Sequential)]
	public struct ShardRecord
	{
		public SerializableGuid Id;
		public int TypeIndex;
	}
}