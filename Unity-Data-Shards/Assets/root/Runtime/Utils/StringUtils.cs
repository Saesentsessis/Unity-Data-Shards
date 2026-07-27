using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Saesentsessis.Persistence.Utils
{
	internal static partial class StringUtils
	{
		/// <summary>
		/// A helper functions that searches a keyword inside unity symbols string,
		/// that are separated by a semicolon.
		/// </summary>
		/// <param name="symbols">String where keyword would be searched for.</param>
		/// <param name="keyword">String that would be searched inside symbols.</param>
		/// <returns>
		/// Integer range, where keyword lays inside the symbols, prefix/postfix semicolon
		/// included.
		/// </returns>
		public static RangeInt RangeOfKeyword(string symbols, string keyword)
		{
			if (symbols.Length == 0 || keyword.Length > symbols.Length)
				return new RangeInt(-1, 0);

			if (symbols.Equals(keyword, StringComparison.Ordinal))
				return new RangeInt(0, symbols.Length);
			
			var lastSymbol = symbols.Length - keyword.Length;
			var keywordSpan = keyword.AsSpan();
			for (var index = 0; index <= lastSymbol; index++)
			{
				var symbolsSpan = symbols.AsSpan(index);

				if (symbolsSpan.StartsWith(keywordSpan, StringComparison.Ordinal) == false)
					continue;

				var length = keyword.Length;
				
				if (index + length < symbols.Length)
				{
					if (symbolsSpan[length] == ';')
						// Consume the trailing semicolon (e.g., matching "AB" inside "AB;")
						length++;
					else
						// Reject partial matches on the right (e.g., matching "A" inside "AB")
						continue;
				}
				else if (index > 0 && symbols[index - 1] == ';')
				{
					// If it's the last word in the string, consume the leading semicolon instead
					index--;
					length++;
				}

				return new RangeInt(index, length);
			}
			
			return new RangeInt(-1, 0);
		}
		
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static string Join(string part1, string part2)
		{
			if (part1.Length == 0)
				return part2;
			
			if (part2.Length == 0)
				return part1;

			return string.Create(part1.Length + part2.Length + 1, (part1, part2), static (span, state) =>
			{
				state.part1.AsSpan().CopyTo(span);
				var offset = state.part1.Length;
				span[offset] = ';';
				state.part2.AsSpan().CopyTo(span[++offset..]);
			});
		}
		
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static string Remove(string str, RangeInt range)
		{
			if (range.start < 0 || str.Length < range.end)
				throw new ArgumentOutOfRangeException(nameof(range));
			
			if (range.start == 0 && str.Length == range.length)
				return string.Empty;

			return string.Create(str.Length - range.length, (str, range), static (span, state) =>
			{
				var oldSpan = state.str.AsSpan();
				var offset = state.range.start;
				oldSpan[..offset].CopyTo(span);
				offset += state.range.length;
				oldSpan[offset..].CopyTo(span[state.range.start..]);
			});
		}
	}
}