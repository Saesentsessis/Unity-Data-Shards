using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Serialization;
using Saesentsessis.Persistence.Storage;
using Saesentsessis.Persistence.Storage.Transforms;
using Saesentsessis.Persistence.Threading;
using Unity.Collections;
using UnityEngine.TestTools;
#if PERSISTENCE_HAS_UNITASK
using TaskType = Cysharp.Threading.Tasks.UniTask;
using BoolTask = Cysharp.Threading.Tasks.UniTask<bool>;
using IntTask = Cysharp.Threading.Tasks.UniTask<int>;
using StorageReadTask = Cysharp.Threading.Tasks.UniTask<Saesentsessis.Persistence.Core.StorageReadResult>;
#else
using TaskType = System.Threading.Tasks.Task;
using BoolTask = System.Threading.Tasks.Task<bool>;
using IntTask = System.Threading.Tasks.Task<int>;
using StorageReadTask = System.Threading.Tasks.Task<Saesentsessis.Persistence.Core.StorageReadResult>;
#endif

namespace Saesentsessis.Persistence.Tests
{
	/// <summary>Slot mapping is pure and synchronous, so it needs no async plumbing to pin down.</summary>
	public class SlotKeyMapperTests
	{
		private const string ShardHex = "0123456789abcdef0123456789abcdef";

		private static string Slot(ISlotKeyMapper mapper, string key)
		{
			Assert.IsTrue(mapper.TryGetSlot(key.AsSpan(), out var slot), $"'{key}' should map to a slot.");
			return slot.ToString();
		}

		[Test]
		public void SingleFile_KeyIsTheSlot()
		{
			var mapper = new SingleFileSaveLayout(new MemoryStorage());

			Assert.AreEqual("save1", Slot(mapper, "save1"));

			// A single-file slot occupies one key, so every key it owns carries the envelope.
			mapper.TryGetSlot("save1".AsSpan(), out var slot);
			Assert.AreEqual("save1".Length, slot.Length, "Equal lengths are what marks an envelope key.");
		}

		[Test]
		public void MultiFile_SplitsAtFirstSeparator()
		{
			var mapper = new MultiFileSaveLayout(new MemoryStorage());

			Assert.AreEqual("save1", Slot(mapper, "save1"));
			Assert.AreEqual("save1", Slot(mapper, $"save1/{ShardHex}"));

			// Only the FIRST separator splits, so everything beyond it stays inside one slot. A slot
			// name containing '/' is not representable under this layout — that character is what
			// separates a slot from its shards.
			Assert.AreEqual("dir", Slot(mapper, "dir/nested/deeper"));
		}

		[Test]
		public void MultiFile_LengthEqualityMarksTheEnvelope()
		{
			var mapper = new MultiFileSaveLayout(new MemoryStorage());

			mapper.TryGetSlot("save1".AsSpan(), out var envelope);
			Assert.AreEqual("save1".Length, envelope.Length, "The slot's own key holds the envelope.");

			var shardKey = $"save1/{ShardHex}";
			mapper.TryGetSlot(shardKey.AsSpan(), out var shard);
			Assert.Less(shard.Length, shardKey.Length, "A shard key must not read as an envelope key.");
		}

		[Test]
		public void MalformedKeys_AreRejected()
		{
			var multi = new MultiFileSaveLayout(new MemoryStorage());
			var single = new SingleFileSaveLayout(new MemoryStorage());

			Assert.IsFalse(multi.TryGetSlot(ReadOnlySpan<char>.Empty, out _));
			Assert.IsFalse(multi.TryGetSlot("/leading".AsSpan(), out _), "A leading separator leaves no slot name.");
			Assert.IsFalse(single.TryGetSlot(ReadOnlySpan<char>.Empty, out _));
		}
	}
}
