using System;
using UnityEditor;

namespace Saesentsessis.Persistence.Editor.Attributes
{
	/// <summary>
	/// One selectable implementation, with its presentation precomputed.
	/// </summary>
	/// <remarks>
	/// Built once per base type and cached by <see cref="SubclassPickerDrawer"/>, so the strings
	/// here are never rebuilt during an OnGUI frame.
	/// </remarks>
	internal readonly struct SubclassCandidate
	{
		/// <summary>Concrete type this candidate assigns. Null on the "None" row.</summary>
		public readonly Type Type;

		/// <summary>Row label — the type name, spaced for readability.</summary>
		public readonly string DisplayName;

		/// <summary>Submenu this row lives under; empty for a type in the global namespace.</summary>
		public readonly string Group;

		/// <summary><c>Group/DisplayName</c>, so an ordinal sort groups and alphabetises in one pass.</summary>
		public readonly string SortKey;

		public SubclassCandidate(Type type)
		{
			Type = type;
			DisplayName = ObjectNames.NicifyVariableName(type.Name);

			// Grouped by the trailing namespace segment, which for this package separates
			// "Storage" descriptors from "Transforms" without any per-type annotation.
			var space = type.Namespace;
			var lastDot = space?.LastIndexOf('.') ?? -1;

			if (string.IsNullOrEmpty(space))
				Group = string.Empty;
			else if (lastDot >= 0)
				Group = space[(lastDot + 1)..];
			else
				Group = space;

			SortKey = string.Create(Group.Length + DisplayName.Length + 1, (Group, DisplayName),
				static (span, state) =>
				{
					state.Group.AsSpan().CopyTo(span);
					var offset = state.Group.Length;
					span[offset] = '/';
					state.DisplayName.AsSpan().CopyTo(span[++offset..]);
				});
		}
	}
}