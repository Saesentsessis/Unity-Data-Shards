using System;
using NUnit.Framework;
using Saesentsessis.Persistence.Core;

namespace Saesentsessis.Persistence.Tests
{
	public class SerializableGuidTests
	{
		private static readonly SerializableGuid[] Samples =
		{
			SerializableGuid.Empty,
			new SerializableGuid(ulong.MaxValue, ulong.MaxValue),
			new SerializableGuid(0x0123456789ABCDEFUL, 0xFEDCBA9876543210UL),
			(SerializableGuid)Guid.NewGuid(),
			(SerializableGuid)Guid.NewGuid()
		};

		[Test]
		public void ToString_TryParse_RoundTrips()
		{
			foreach (var original in Samples)
			{
				Assert.IsTrue(SerializableGuid.TryParse(original.ToString(), out var parsed));
				Assert.AreEqual(original, parsed);
			}
		}

		[Test]
		public void TryFormatHex_MatchesToString_AndReparses()
		{
			Span<char> buffer = stackalloc char[32];

			foreach (var original in Samples)
			{
				Assert.IsTrue(original.TryFormatHex(buffer, out var written));
				Assert.AreEqual(32, written, "\"N\" format is always 32 hex chars.");
				Assert.AreEqual(original.ToString(), new string(buffer.Slice(0, written)));

				// The zero-alloc span pair every binary/JSON formatter relies on.
				Assert.IsTrue(SerializableGuid.TryParse(buffer.Slice(0, written), out var parsed));
				Assert.AreEqual(original, parsed);
			}
		}

		[Test]
		public void TryFormatHex_ShortBuffer_Fails()
		{
			Span<char> tooSmall = stackalloc char[16];
			Assert.IsFalse(((SerializableGuid)Guid.NewGuid()).TryFormatHex(tooSmall, out _));
		}

		[Test]
		public void GuidInterop_RoundTrips()
		{
			var guid = Guid.NewGuid();
			SerializableGuid serializable = guid;
			Guid back = serializable;
			Assert.AreEqual(guid, back);
		}
	}
}
