using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Saesentsessis.Persistence.Editor.Attributes
{
	/// <summary>
	/// Searchable type chooser shown by <see cref="SubclassPickerDrawer"/>, grouped by the trailing
	/// namespace segment of each candidate.
	/// </summary>
	/// <remarks>
	/// Rows carry their payload as <see cref="SubclassDropdownItem"/> rather than through
	/// <see cref="AdvancedDropdownItem.id"/>, which Unity overwrites — see that type's remarks.
	/// </remarks>
	internal sealed class SubclassDropdown : AdvancedDropdown
	{
		/// <summary>Label of the row that clears the reference.</summary>
		internal const string NullDisplayName = "None";
		
		private readonly Type _baseType;
		private readonly SubclassCandidate[] _candidates;
		private readonly Action<Type> _onSelected;

		public SubclassDropdown(AdvancedDropdownState state, Type baseType, SubclassCandidate[] candidates,
			Action<Type> onSelected) : base(state)
		{
			_baseType = baseType;
			_candidates = candidates;
			_onSelected = onSelected;

			minimumSize = new Vector2(minimumSize.x, 220f);
		}

		protected override AdvancedDropdownItem BuildRoot()
		{
			var root = new AdvancedDropdownItem(ObjectNames.NicifyVariableName(_baseType.Name));
				
			root.AddChild(new SubclassDropdownItem(NullDisplayName, default));
			root.AddSeparator();

			if (_candidates.Length == 0)
			{
				root.AddChild(new AdvancedDropdownItem($"No [Serializable] implementations of {_baseType.Name}")
				{
					enabled = false,
					id = -1,
				});

				return root;
			}

			var groups = new Dictionary<string, AdvancedDropdownItem>();

			// Forward, because AdvancedDropdown lists children in insertion order and the
			// candidates arrive sorted by SortKey — iterating backwards would present them
			// reverse-alphabetically.
			for (var i = 0; i < _candidates.Length; i++)
			{
				ref readonly var candidate = ref _candidates[i];
				var item = new SubclassDropdownItem(candidate.DisplayName, in candidate);
					
				var parent = root;

				if (string.IsNullOrEmpty(candidate.Group) == false
				    && groups.TryGetValue(candidate.Group, out parent) == false)
				{
					parent = new AdvancedDropdownItem(candidate.Group);
					groups[candidate.Group] = parent;
					root.AddChild(parent);
				}

				parent.AddChild(item);
			}

			return root;
		}

		protected override void ItemSelected(AdvancedDropdownItem item)
		{
			if (item is not SubclassDropdownItem typedItem)
				return;

			ref readonly var candidate = ref typedItem.Candidate;

			_onSelected(candidate.Type);
		}
	}
}