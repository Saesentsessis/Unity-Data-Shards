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
	[Serializable]
	[ShardSchema(1)]
	public class TestShard : IDataShard
	{
		[SerializeField] private SerializableGuid id;
		[SerializeField] public int value;
		[SerializeField] public string text;

		[NonSerialized] private bool _dirty = true;

		public TestShard() { }

		public TestShard(Guid guid, int value, string text = "")
		{
			id = guid;
			this.value = value;
			this.text = text;
		}

		public SerializableGuid Identifier => id;
		public bool IsDirty => _dirty;
		public void ClearDirty() => _dirty = false;
		public void MarkDirty() => _dirty = true;
	}

	[Serializable]
	[ShardSchema(1)]
	public class LegacyShard : IDataShard
	{
		[SerializeField] private SerializableGuid id;
		[SerializeField] public int value;

		public LegacyShard() { }

		public LegacyShard(Guid guid, int value)
		{
			id = guid;
			this.value = value;
		}

		public SerializableGuid Identifier => id;
	}

	[Serializable]
	[ShardSchema(2)]
	public class ModernShard : IDataShard
	{
		[SerializeField] private SerializableGuid id;
		[SerializeField] public int points;

		public ModernShard() { }

		public ModernShard(Guid guid, int points)
		{
			id = guid;
			this.points = points;
		}

		public SerializableGuid Identifier => id;
	}

}
