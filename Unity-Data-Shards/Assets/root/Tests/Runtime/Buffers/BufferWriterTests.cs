using System;
using NUnit.Framework;
using Saesentsessis.Persistence.Buffers;
using Unity.Collections;

namespace Saesentsessis.Persistence.Tests
{
	public class BufferWriterTests
	{
		[Test]
		public void NativeListBufferWriter_GrowsAndPreservesContent()
		{
			using var writer = new NativeListBufferWriter(16, Allocator.Persistent);

			for (var i = 0; i < 100_000; i++)
			{
				var span = writer.GetSpan(1);
				span[0] = (byte)(i % 251);
				writer.Advance(1);
			}

			Assert.AreEqual(100_000, writer.WrittenLength);

			var array = writer.AsArray();
			for (var i = 0; i < array.Length; i++)
				if (array[i] != (byte)(i % 251))
					Assert.Fail($"Content mismatch at {i}.");
		}

		[Test]
		public void NativeListBufferWriter_GetMemory_WritesThrough()
		{
			using var writer = new NativeListBufferWriter(16, Allocator.Persistent);

			var memory = writer.GetMemory(4);
			memory.Span[0] = 0xAB;
			memory.Span[1] = 0xCD;
			writer.Advance(2);

			var array = writer.AsArray();
			Assert.AreEqual(0xAB, array[0]);
			Assert.AreEqual(0xCD, array[1]);
		}

		[Test]
		public void PooledArrayBufferWriter_GrowsAndPreservesContent()
		{
			using var writer = new PooledArrayBufferWriter(16);

			for (var i = 0; i < 100_000; i++)
			{
				var span = writer.GetSpan(1);
				span[0] = (byte)(i % 251);
				writer.Advance(1);
			}

			Assert.AreEqual(100_000, writer.WrittenLength);

			var span2 = writer.WrittenSpan;
			for (var i = 0; i < span2.Length; i++)
				if (span2[i] != (byte)(i % 251))
					Assert.Fail($"Content mismatch at {i}.");
		}
	}
}
