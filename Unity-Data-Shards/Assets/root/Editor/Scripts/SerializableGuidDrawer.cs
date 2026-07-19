using System;
using System.Runtime.CompilerServices;
using Persistence.Core;
using UnityEditor;
using UnityEngine;

namespace Persistence.Editor
{
	[CustomPropertyDrawer(typeof(SerializableGuid))]
	public class SerializableGuidDrawer : PropertyDrawer
	{
		private const string RegenerateSymbol = "\u21BA";
		private const string CopySymbol = "\U0001f4cb";
		
		public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
		{
			return EditorGUIUtility.singleLineHeight;
		}

		public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
		{
			EditorGUI.BeginProperty(position, label, property);
			
			ref var guid = ref Unsafe.Unbox<SerializableGuid>(property.boxedValue);

			position = EditorGUI.PrefixLabel(position, label);
			
			if (DrawButtonToTheRight(ref position, RegenerateSymbol, "Generate new GUID."))
				property.boxedValue = (SerializableGuid)Guid.NewGuid();
			
			var guidString = guid.ToString();
			
			if (DrawButtonToTheRight(ref position, CopySymbol, "Copy to Clipboard."))
				EditorGUIUtility.systemCopyBuffer = guidString;
			
			EditorGUI.SelectableLabel(position, guidString);
			
			EditorGUI.EndProperty();
		}

		// Reused scratch content — never GUIContent.none: mutating the shared global
		// sentinel would be visible to any other IMGUI code observing it mid-frame.
		private static readonly GUIContent SharedContent = new();

		private static bool DrawButtonToTheRight(ref Rect rect, string text, string tooltip)
		{
			SharedContent.text = text;
			SharedContent.tooltip = tooltip;

			var buttonWidth = GUI.skin.button.CalcSize(SharedContent).x;
			var subRect = new Rect(rect.xMax - buttonWidth, rect.y, buttonWidth, rect.height);
			rect.width -= buttonWidth;

			return GUI.Button(subRect, SharedContent);
		}
	}
}