using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Serialization;
using Saesentsessis.Persistence.Threading;
using Unity.Collections;
using UnityEngine;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using BoolTask = Cysharp.Threading.Tasks.UniTask<bool>;
using StorageReadTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.Core.StorageReadResult>;
using SaveLayoutTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.Layout.SaveLayoutResult>;
using IntTask = Cysharp.Threading.Tasks.UniTask<int>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using StorageReadTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Core.StorageReadResult>;
using SaveLayoutTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Layout.SaveLayoutResult>;
using IntTask = System.Threading.Tasks.Task<int>;
#endif


namespace Saesentsessis.Persistence.Tests
{
	/// <summary>Counts Serialize calls; background flag is configurable for deterministic tests.</summary>
	public sealed class CountingSerializer : ISerializer
	{
		private readonly UnityJsonSerializer _inner = new();

		public int SerializeCalls;
		public int DeserializeCalls;

		public CountingSerializer(bool background = false)
		{
			SupportsBackgroundSerialization = background;
		}

		public bool SupportsBackgroundSerialization { get; }

		public void Serialize(object value, Type type, IBufferWriter<byte> writer)
		{
			SerializeCalls++;
			_inner.Serialize(value, type, writer);
		}

		public object Deserialize(ReadOnlySpan<byte> data, Type type)
		{
			DeserializeCalls++;
			return _inner.Deserialize(data, type);
		}
	}

}
