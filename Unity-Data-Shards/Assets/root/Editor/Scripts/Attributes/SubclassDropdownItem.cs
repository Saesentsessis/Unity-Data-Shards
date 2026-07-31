using UnityEditor.IMGUI.Controls;

namespace Saesentsessis.Persistence.Editor.Attributes
{
	/// <summary>
	/// Dropdown row that carries the type it selects.
	/// </summary>
	/// <remarks>
	/// The obvious implementation stores an index in <see cref="AdvancedDropdownItem.id"/> and maps
	/// it back on selection. <b>That does not work:</b> Unity assigns <c>id</c> itself while
	/// building the tree, so any value written there is overwritten before the selection callback
	/// ever runs, and the mapping silently resolves to the wrong type. Subclassing the item and
	/// carrying the payload as a field is the reliable route — <c>id</c> belongs to Unity.
	/// <para>
	/// A <c>default</c> candidate represents the "None" row, whose <see cref="SubclassCandidate.Type"/>
	/// is null and therefore clears the reference.
	/// </para>
	/// </remarks>
	internal sealed class SubclassDropdownItem : AdvancedDropdownItem
	{
		private readonly SubclassCandidate _candidate;

		public SubclassDropdownItem(string name, in SubclassCandidate candidate) : base(name)
		{
			_candidate = candidate;	
		}

		/// <summary>Type this row assigns, or null for the "None" row.</summary>
		public ref readonly SubclassCandidate Candidate => ref _candidate;
	}
}