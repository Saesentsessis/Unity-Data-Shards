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
	/// <summary>Blob-level migration: renames LegacyShard's json field "value" to "points".</summary>
	public sealed class LegacyToModernMigration : IShardMigration
	{
		private readonly int _toVersion;

		public LegacyToModernMigration(int toVersion = 2)
		{
			_toVersion = toVersion;
		}

		public string FromTypeName => typeof(LegacyShard).FullName;
		public int FromVersion => 1;
		public Type ToType => typeof(ModernShard);
		public int ToVersion => _toVersion;

		public void Migrate(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
		{
			var json = System.Text.Encoding.UTF8.GetString(src).Replace("\"value\"", "\"points\"");
			var bytes = System.Text.Encoding.UTF8.GetBytes(json);
			dst.Write(bytes);
		}
	}

	/// <summary>Typed counterpart of <see cref="LegacyToModernMigration"/>: converts via plain C#
	/// instead of touching serialized bytes. Identity is carried over explicitly.</summary>
	public sealed class TypedLegacyToModern : TypedShardMigration<LegacyShard, ModernShard>
	{
		public TypedLegacyToModern() : base(fromVersion: 1, toVersion: 2) { }

		protected override ModernShard Convert(LegacyShard old)
			=> new ModernShard(old.Identifier, old.value);
	}

	/// <summary>Reverse of <see cref="LegacyToModernMigration"/>; registered together they form a cycle.</summary>
	public sealed class ModernToLegacyCycleMigration : IShardMigration
	{
		public string FromTypeName => typeof(ModernShard).FullName;
		public int FromVersion => 1;
		public Type ToType => typeof(LegacyShard);
		public int ToVersion => 1;

		public void Migrate(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
		{
			var json = System.Text.Encoding.UTF8.GetString(src).Replace("\"points\"", "\"value\"");
			dst.Write(System.Text.Encoding.UTF8.GetBytes(json));
		}
	}

}
