using System;
using System.Collections.Concurrent;
using System.Security;
using Saesentsessis.Persistence.Layout;

namespace Saesentsessis.Persistence.Core
{
	/// <summary>
	/// Builds and resolves <see cref="SerializedType"/> descriptors, caching both directions.
	/// Type identity is the namespace-qualified name plus the simple assembly name (Unity's
	/// version-free style), so saves survive assembly Version/Culture/PublicKeyToken changes.
	/// Caches are concurrent: background loads read them from the thread pool while the
	/// main thread may be populating them for a concurrent save.
	/// </summary>
	public static class SerializedTypeHelper
	{
		private static readonly ConcurrentDictionary<Type, SerializedType> DescribeCache = new();
		private static readonly ConcurrentDictionary<SerializedType, Type> ResolveCache = new();

		public static SerializedType Describe(Type type)
		{
			if (DescribeCache.TryGetValue(type, out var descriptor))
				return descriptor;

			return DescribeMiss(type);
		}

		public static Type Resolve(in SerializedType descriptor)
		{
			if (ResolveCache.TryGetValue(descriptor, out var type))
				return type;

			return ResolveMiss(descriptor);
		}

		private static SerializedType DescribeMiss(Type type)
		{
			return DescribeCache.GetOrAdd(type, static t => new SerializedType(
				t.FullName,
				t.Assembly.GetName().Name,
				ShardSchemaHelper.GetVersion(t)
			));
		}

		private static Type ResolveMiss(SerializedType descriptor)
		{
			var result = descriptor.ToString();

			var type = Type.GetType(result)
				?? throw new InvalidOperationException($"Cannot resolve type '{result}'.");

			// Type.GetType resolves an attacker-controlled name from the save file, so the result
			// must be constrained before anything instantiates it. Never gated: this is the gate
			// itself, and it costs one check per unique type per process thanks to ResolveCache.
			if (typeof(IDataShard).IsAssignableFrom(type) == false)
				throw new SecurityException(
					$"Stored type '{result}' does not implement {nameof(IDataShard)} and will not be instantiated.");
			
			return ResolveCache.GetOrAdd(descriptor, type);
		}
	}
}
