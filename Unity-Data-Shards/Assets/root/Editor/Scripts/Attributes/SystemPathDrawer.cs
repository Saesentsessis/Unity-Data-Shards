using System.IO;
using Saesentsessis.Persistence.Attributes;
using UnityEditor;
using UnityEngine;

namespace Saesentsessis.Persistence.Editor.Attributes
{
	/// <summary>
	/// Draws a <see cref="SystemPathAttribute"/> string as a text field with a browse button.
	/// </summary>
	/// <remarks>
	/// The field stays freely editable and the button only fills it in. That matters for a path
	/// pointing outside the project — a key file, say — which the person editing the asset may not
	/// have on the machine they are editing from.
	/// </remarks>
	[CustomPropertyDrawer(typeof(SystemPathAttribute))]
	internal sealed class SystemPathDrawer : PropertyDrawer
	{
		private const string FolderIconString = "📁";
		private const string FallbackTitle = "Select file";
		private const float ButtonWidth = 24f;
		private const float ErrorBoxLines = 2f;

		public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
		{
			if (property.propertyType == SerializedPropertyType.String)
				return EditorGUIUtility.singleLineHeight;

			return EditorGUI.GetPropertyHeight(property, label, true)
				+ EditorGUIUtility.singleLineHeight * ErrorBoxLines;
		}

		public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
		{
			if (property.propertyType != SerializedPropertyType.String)
			{
				// The underlying field is still drawn below the message: a misapplied attribute
				// should not make the value unreachable.
				var boxHeight = EditorGUIUtility.singleLineHeight * ErrorBoxLines;
				var boxRect = new Rect(position.x, position.y, position.width, boxHeight);

				EditorGUI.HelpBox(boxRect, "[SystemPath] requires a string field.", MessageType.Error);

				position.y += boxHeight;
				position.height -= boxHeight;

				EditorGUI.PropertyField(position, property, label);
				return;
			}

			EditorGUI.BeginProperty(position, label, property);

			position.width -= ButtonWidth;
			var buttonRect = new Rect(position.xMax, position.y, ButtonWidth, position.height);

			EditorGUI.BeginChangeCheck();
			var path = EditorGUI.TextField(position, label, property.stringValue);

			if (EditorGUI.EndChangeCheck())
				property.stringValue = path;

			if (GUI.Button(buttonRect, FolderIconString))
				Browse(property, path);

			EditorGUI.EndProperty();
		}

		private void Browse(SerializedProperty property, string current)
		{
			var typedAttribute = (SystemPathAttribute)attribute;
			
			var title = typedAttribute?.Title ?? FallbackTitle;
			var picked = typedAttribute is { IsDirectory: true }
				? EditorUtility.OpenFolderPanel(title, SafeDirectoryOf(current), string.Empty)
				: EditorUtility.OpenFilePanel(title, SafeDirectoryOf(current), string.Empty);

			// OpenFilePanel returns an empty string when the dialog is canceled. Assigning that
			// unconditionally would erase a configured path on a mis-click, which is the one
			// outcome a browse button must never produce.
			if (string.IsNullOrEmpty(picked))
				return;

			property.stringValue = picked;
		}

		/// <summary>
		/// Directory the browser opens in. The field is free text, so the value may be malformed or
		/// point nowhere; that must not throw out of OnGUI, so it degrades to the platform default
		/// rather than being reported.
		/// </summary>
		private static string SafeDirectoryOf(string path)
		{
			if (string.IsNullOrEmpty(path))
				return string.Empty;

			try
			{
				var directory = Path.GetDirectoryName(path);

				return string.IsNullOrEmpty(directory) == false && Directory.Exists(directory)
					? directory
					: string.Empty;
			}
			catch (System.ArgumentException)
			{
				return string.Empty;
			}
		}
	}
}
