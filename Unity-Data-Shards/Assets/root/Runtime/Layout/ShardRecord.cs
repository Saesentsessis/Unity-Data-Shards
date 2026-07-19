using System;
using System.Runtime.InteropServices;
using Persistence.Core;

namespace Persistence.Layout
{
	[Serializable]
	[StructLayout(LayoutKind.Sequential)]
	public struct ShardRecord
	{
		public SerializableGuid Id;
		public int TypeIndex;
	}
}