using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Saesentsessis.Persistence.Core
{
	[AttributeUsage(AttributeTargets.Class, Inherited = false)]
	public sealed class ShardSchemaAttribute : Attribute
	{
		public int Version { get; }

		public ShardSchemaAttribute(int version)
		{
			Version = version;
		}
	}

	public static class ShardSchemaHelper
	{
		// Concurrent: read from the thread pool during background loads while the
		// main thread may be populating it for a concurrent save.
		private static readonly ConcurrentDictionary<Type, int> Cache = new();

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int GetVersion(Type shardType)
		{
			if (Cache.TryGetValue(shardType, out var version))
				return version;

			return CacheMiss(shardType);
		}

		private static int CacheMiss(Type shardType)
		{
			return Cache.GetOrAdd(shardType, static type =>
				((ShardSchemaAttribute)Attribute.GetCustomAttribute(type, typeof(ShardSchemaAttribute)))?.Version ?? 0);
		}
	}
}
