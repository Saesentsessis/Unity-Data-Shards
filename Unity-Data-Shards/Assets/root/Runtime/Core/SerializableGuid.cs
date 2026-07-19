using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Persistence.Core
{
	[Serializable]
	[StructLayout(LayoutKind.Sequential)]
	public struct SerializableGuid : IComparable<SerializableGuid>, IEquatable<SerializableGuid>, IEquatable<Guid>
	{
		public static readonly SerializableGuid Empty = new (0L, 0L);

		[SerializeField] private ulong head;
		[SerializeField] private ulong tail;
		
		public ulong Head => head;
		public ulong Tail => tail;
		
		public SerializableGuid(ulong head, ulong tail)
		{
			this.head = head;
			this.tail = tail;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool Equals(SerializableGuid other)
		{
			return head == other.head && tail == other.tail;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool Equals(Guid other)
		{
			return UnsafeUtility.As<SerializableGuid, Guid>(ref this) == other;
		}

		[BurstDiscard]
		public override bool Equals(object obj)
		{
			return obj is SerializableGuid other && Equals(other);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public override int GetHashCode()
		{
			return UnsafeUtility.As<SerializableGuid, Guid>(ref this).GetHashCode();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool operator ==(SerializableGuid left, SerializableGuid right)
		{
			return left.Equals(right);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool operator !=(SerializableGuid left, SerializableGuid right)
		{
			return !left.Equals(right);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int CompareTo(SerializableGuid other)
		{
			var headComparison = head.CompareTo(other.head);
            
			if (headComparison != 0)
				return headComparison;
            
			return tail.CompareTo(other.tail);
		}
        
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static implicit operator Guid(SerializableGuid self)
			=> UnsafeUtility.As<SerializableGuid, Guid>(ref self);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static implicit operator SerializableGuid(Guid other)
			=> UnsafeUtility.As<Guid, SerializableGuid>(ref other);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool TryParse(string value, out SerializableGuid guid)
		{
			var result = Guid.TryParse(value, out var rawResult);
			guid = UnsafeUtility.As<Guid, SerializableGuid>(ref rawResult);
			return result;
		}

		/// <summary>Allocation-free <see cref="ReadOnlySpan{Char}"/> overload of <see cref="TryParse(string,out SerializableGuid)"/>.</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool TryParse(ReadOnlySpan<char> value, out SerializableGuid guid)
		{
			var result = Guid.TryParse(value, out var rawResult);
			guid = UnsafeUtility.As<Guid, SerializableGuid>(ref rawResult);
			return result;
		}

		/// <summary>
		/// Writes the 32-char "N" hex form into <paramref name="destination"/> without allocating.
		/// Symmetric with <see cref="TryParse(ReadOnlySpan{char},out SerializableGuid)"/>; lets
		/// serializers format the id straight into a stack buffer instead of via <see cref="ToString()"/>.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool TryFormatHex(Span<char> destination, out int charsWritten)
		{
			var raw = UnsafeUtility.As<SerializableGuid, Guid>(ref this);
			return raw.TryFormat(destination, out charsWritten, "N");
		}
        
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public override string ToString()
		{
			return UnsafeUtility.As<SerializableGuid, Guid>(ref this).ToString("N");
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public string ToString(string format, IFormatProvider formatProvider)
		{
			return UnsafeUtility.As<SerializableGuid, Guid>(ref this).ToString(format, formatProvider);
		}
	}
}