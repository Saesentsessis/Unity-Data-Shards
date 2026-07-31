using UnityEngine;

namespace Saesentsessis.Persistence.Attributes
{
	/// <summary>
	/// Provides basic functionality for SerializeReference subclass picker.
	/// Do not attempt to use this attribute in production, as it serves as a cheap
	/// built-in alternative to Mackysoft's SerializeReference Extensions package.
	/// </summary>
	internal class SubclassPickerAttribute : PropertyAttribute { }
}