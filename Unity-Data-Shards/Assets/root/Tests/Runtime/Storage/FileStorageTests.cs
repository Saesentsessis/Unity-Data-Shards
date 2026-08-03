using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using Saesentsessis.Persistence.Core;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Serialization;
using Saesentsessis.Persistence.Storage;
using Unity.Collections;
using UnityEngine.TestTools;

namespace Saesentsessis.Persistence.Tests
{
	public class FileStorageTests
	{
		private string _directory;
		private Storage.FileStorage _storage;

		[SetUp]
		public void SetUp()
		{
			_directory = Path.Combine(Path.GetTempPath(), "uds-tests-" + Guid.NewGuid().ToString("N"));
			_storage = new Storage.FileStorage(_directory);
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(_directory))
				Directory.Delete(_directory, recursive: true);
		}

		private static NativeArray<byte> Bytes(params byte[] values)
			=> new NativeArray<byte>(values, Allocator.Persistent);

		#region Path confinement

		// Every case below goes through ExistsAsync, which resolves the key synchronously before it
		// hands anything to the thread pool — so a rejected key throws on this thread rather than
		// landing in a task nobody inspects.

		[Test]
		public void AbsoluteRootDirectory_IsAccepted()
		{
			// The default root is Application.persistentDataPath, which is absolute, so an absolute
			// root is the *normal* case. Rejecting one would make the constructor parameter useless
			// and silently drop saves into the process working directory instead.
			Assert.DoesNotThrow(() => new Storage.FileStorage(_directory).Dispose());
			Assert.DoesNotThrow(() => _storage.ExistsAsync("slot"));
		}

		[Test]
		public void NestedKey_IsAccepted()
		{
			// MultiFileSaveLayout addresses shard files as "slot/<guid-hex>", so a separator inside
			// a key has to keep working — confinement must reject escapes, not subdirectories.
			Assert.DoesNotThrow(() => _storage.ExistsAsync("slot/0123456789abcdef0123456789abcdef"));
		}

		[Test]
		public void RootedKey_IsRejected()
		{
			// Path.Combine returns its second argument verbatim when that argument is rooted, so
			// without a confinement check this key simply *becomes* the path being written.
			var rooted = Path.Combine(Path.GetTempPath(), "uds-escape");

			Assert.Throws<InvalidPathException>(() => _storage.ExistsAsync(rooted));
		}

		[Test]
		public void TraversalKey_IsRejected()
		{
			// Path.Combine performs no normalisation, so "../../x" survives it intact and would pass
			// a prefix test on the combined string while resolving somewhere else entirely.
			Assert.Throws<InvalidPathException>(() => _storage.ExistsAsync("../escaped"));
			Assert.Throws<InvalidPathException>(() => _storage.ExistsAsync("..\\escaped"));
			Assert.Throws<InvalidPathException>(() => _storage.ExistsAsync("a/../../escaped"));
			Assert.Throws<InvalidPathException>(() => _storage.ExistsAsync("a/b/../../../escaped"));
			Assert.Throws<InvalidPathException>(() => _storage.ExistsAsync("/absolute/unix/style"));
		}

		[Test]
		public void BareDotDotKey_StaysInsideTheRoot()
		{
			// Not an escape, and worth pinning rather than assuming: the extension is always
			// appended, so ".." becomes the filename "...save" *inside* the root.
			Assert.DoesNotThrow(() => _storage.ExistsAsync(".."));
		}

		[Test]
		public void EmptyFileExtension_IsRejectedAtConstruction()
		{
			// Refused up front rather than left to produce "<key>." paths, because a trailing dot is
			// exactly where runtimes stop agreeing: Mono keeps it, CoreCLR strips it, and Windows
			// strips it again underneath both — so a key would resolve to one path and be stored at
			// another. The extension is also what keeps a save apart from its own .tmp and .bak.
			Assert.Throws<InvalidPathException>(() => new Storage.FileStorage(_directory, ""));
			Assert.Throws<InvalidPathException>(() => new Storage.FileStorage(_directory, "   "));
			Assert.Throws<InvalidPathException>(() => new Storage.FileStorage(_directory, "."));
		}

		[Test]
		public void ExtensionWithASeparator_IsRejected()
		{
			// Would silently move every key into a subdirectory.
			Assert.Throws<InvalidPathException>(() => new Storage.FileStorage(_directory, "a/b"));
		}

		[Test]
		public void NullFileExtension_UsesTheDefault()
		{
			// null means "unspecified", which is not the same as "none".
			using var storage = new Storage.FileStorage(_directory);

			Assert.DoesNotThrow(() => storage.ExistsAsync("slot"));
		}

		[Test]
		public void LeadingDotOnExtension_IsAccepted()
		{
			// ".save" and "save" are the same intent; normalising avoids "slot..save".
			using var storage = new Storage.FileStorage(_directory, ".save");

			Assert.DoesNotThrow(() => storage.ExistsAsync("slot"));
		}

		[Test]
		public void SiblingDirectorySharingAPrefix_IsRejected()
		{
			// "<root>2/x" starts with "<root>" as a string but is a different directory. This is
			// what the trailing separator on the stored root is for.
			var sibling = new Storage.FileStorage(_directory + "-other");

			try
			{
				Assert.Throws<InvalidPathException>(() => sibling.ExistsAsync("../" +
					Path.GetFileName(_directory) + "/stolen"));
			}
			finally
			{
				sibling.Dispose();
			}
		}

		[Test]
		public void EmptyKey_IsRejected()
		{
			Assert.Throws<InvalidPathException>(() => _storage.ExistsAsync(""));
			Assert.Throws<InvalidPathException>(() => _storage.ExistsAsync(null));
		}

		#endregion

		[UnityTest]
		public IEnumerator WriteRead_RoundTrips() => AsyncTest.Run(async () =>
		{
			var data = Bytes(1, 2, 3, 4, 5);

			try
			{
				await _storage.WriteAsync("slot", data);
			}
			finally
			{
				data.Dispose();
			}

			var read = await _storage.TryReadAsync("slot", Allocator.Persistent);
			
			try
			{
				Assert.IsTrue(read.Found);
				CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, read.Data.ToArray());
			}
			finally
			{
				read.Data.Dispose();
			}
		});

		[UnityTest]
		public IEnumerator TryRead_MissingKey_NotFound() => AsyncTest.Run(async () =>
		{
			var read = await _storage.TryReadAsync("missing", Allocator.Persistent);
			Assert.IsFalse(read.Found);
			Assert.IsFalse(await _storage.ExistsAsync("missing"));
		});

		[UnityTest]
		public IEnumerator StaleBak_DoesNotBrickTheSlot() => AsyncTest.Run(async () =>
		{
			var first = Bytes(1);
			try { await _storage.WriteAsync("slot", first); }
			finally { first.Dispose(); }

			// Simulate a crash that left a stale .bak behind.
			var path = Path.Combine(_directory, "slot.save");
			File.WriteAllBytes(path + ".bak", new byte[] { 9, 9 });

			var second = Bytes(2, 2);
			try { await _storage.WriteAsync("slot", second); }
			finally { second.Dispose(); }

			var read = await _storage.TryReadAsync("slot", Allocator.Persistent);
			try
			{
				Assert.IsTrue(read.Found);
				CollectionAssert.AreEqual(new byte[] { 2, 2 }, read.Data.ToArray());
			}
			finally
			{
				read.Data.Dispose();
			}
		});

		[UnityTest]
		public IEnumerator BakRestore_RecoversAfterLostMainFile() => AsyncTest.Run(async () =>
		{
			var data = Bytes(7, 7, 7);
			try { await _storage.WriteAsync("slot", data); }
			finally { data.Dispose(); }

			// Simulate a crash between the two moves: main file gone, .bak intact.
			var path = Path.Combine(_directory, "slot.save");
			File.Move(path, path + ".bak");

			var read = await _storage.TryReadAsync("slot", Allocator.Persistent);
			try
			{
				Assert.IsTrue(read.Found, ".bak must be restored transparently.");
				CollectionAssert.AreEqual(new byte[] { 7, 7, 7 }, read.Data.ToArray());
			}
			finally
			{
				read.Data.Dispose();
			}
		});

		[UnityTest]
		public IEnumerator EndToEnd_SaveManagerOverFileStorage() => AsyncTest.Run(async () =>
		{
			using var manager = new SaveManager(new UnityJsonSerializer(), new SingleFileSaveLayout(_storage));
			var store = new ShardStore();
			store.Add(new TestShard(Guid.NewGuid(), 123, "file"));

			await manager.SaveAsync("slot", store);
			Assert.IsTrue(await manager.ExistsAsync("slot"));

			var loaded = (await manager.LoadAsync("slot")).AsShardStore();
			Assert.AreEqual(1, loaded.Count);
			Assert.AreEqual(123, ((TestShard)loaded[0]).value);

			await manager.DeleteAsync("slot");
			Assert.IsFalse(await manager.ExistsAsync("slot"));
		});
	}
}
