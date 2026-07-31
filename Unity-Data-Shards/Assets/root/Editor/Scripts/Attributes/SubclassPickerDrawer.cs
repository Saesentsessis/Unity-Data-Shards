using System;
using System.Collections.Generic;
using System.Reflection;
using Saesentsessis.Persistence.Attributes;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Saesentsessis.Persistence.Editor.Attributes
{
	/// <summary>
	/// Draws a type picker over a <c>[SerializeReference]</c> field marked with
	/// <see cref="SubclassPickerAttribute"/>, so a polymorphic field — an
	/// <c>IStorageDescriptor</c>, an <c>ITransformDescriptor</c> — can be populated from the
	/// inspector.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The drawer stays small because <b>only the type choice needs custom drawing</b>. Once a
	/// concrete instance exists, its own <c>[SerializeField]</c> members are rendered by
	/// <see cref="EditorGUI.PropertyField(Rect,SerializedProperty,GUIContent,bool)"/> exactly as
	/// they would be anywhere else, and nesting works for free: a descriptor that itself holds a
	/// <c>[SerializeReference, SubclassPicker]</c> field re-enters this drawer.
	/// </para>
	/// <para>
	/// This is the deliberate in-house alternative to Mackysoft's SerializeReference Extensions.
	/// The package could never be a hard dependency, so a fallback would have to exist regardless;
	/// gating on its presence would mean maintaining two paths for code written either way.
	/// </para>
	/// <para>
	/// A candidate type must be a non-abstract, non-generic class carrying
	/// <see cref="SerializableAttribute"/> and a parameterless constructor. The
	/// <c>[Serializable]</c> requirement is Unity's, not ours: without it the assigned instance
	/// silently fails to serialize, so such types are hidden rather than offered and then lost.
	/// </para>
	/// </remarks>
	[CustomPropertyDrawer(typeof(SubclassPickerAttribute))]
	internal sealed class SubclassPickerDrawer : PropertyDrawer
	{
		private const float LabelGap = 2f;

		// All three caches are static and therefore cleared by a domain reload — which is also when
		// TypeCache is rebuilt and when a rename could invalidate an entry, so they never go stale.
		private static readonly Dictionary<string, Type> ResolvedFieldTypes = new();
		private static readonly Dictionary<Type, SubclassCandidate[]> CandidatesByBaseType = new();
		private static readonly Dictionary<string, AdvancedDropdownState> DropdownStates = new();

		// Reused scratch content
		private static readonly GUIContent SharedContent = new();

		public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
		{
			if (property.propertyType != SerializedPropertyType.ManagedReference)
				return EditorGUIUtility.singleLineHeight * 2f;

			var height = EditorGUIUtility.singleLineHeight;

			if (property.isExpanded == false || property.hasVisibleChildren == false)
				return height;

			var end = property.GetEndProperty();
			var child = property.Copy();
			var enterChildren = true;

			while (child.NextVisible(enterChildren) && SerializedProperty.EqualContents(child, end) == false)
			{
				enterChildren = false;
				height += EditorGUIUtility.standardVerticalSpacing + EditorGUI.GetPropertyHeight(child, true);
			}

			return height;
		}

		public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
		{
			if (property.propertyType != SerializedPropertyType.ManagedReference)
			{
				// The usual cause is [SubclassPicker] without [SerializeReference] beside it, which
				// otherwise fails as a field that simply never populates.
				EditorGUI.HelpBox(position, "[SubclassPicker] requires a [SerializeReference] field.",
					MessageType.Error);
				return;
			}

			label = EditorGUI.BeginProperty(position, label, property);

			var headerRect = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
			var valueX = headerRect.x + EditorGUIUtility.labelWidth + LabelGap;
			var labelRect = new Rect(headerRect.x, headerRect.y, Mathf.Max(valueX - headerRect.x, 0f), headerRect.height);

			var hasVisibleChildren = property.hasVisibleChildren;
			
			if (hasVisibleChildren)
				property.isExpanded = EditorGUI.Foldout(labelRect, property.isExpanded, label, toggleOnLabelClick: true);
			else
				EditorGUI.LabelField(labelRect, label);

			DrawTypeButton(headerRect, valueX, property);

			if (hasVisibleChildren && property.isExpanded)
				DrawChildren(position, headerRect.yMax, property);

			EditorGUI.EndProperty();
		}

		/// <summary>
		/// Draws the chosen instance's own serialized fields.
		/// </summary>
		/// <remarks>
		/// Each <b>child</b> is drawn individually rather than handing the whole property back to
		/// <see cref="EditorGUI.PropertyField(Rect,SerializedProperty,GUIContent,bool)"/>. Whether
		/// Unity re-enters an attribute drawer when a property is redrawn from inside that same
		/// drawer is an implementation detail, and the failure mode if it does is unbounded
		/// recursion — an editor hang. Children resolve to their own handlers, so the question
		/// cannot arise. It costs one loop and buys certainty.
		/// </remarks>
		private static void DrawChildren(Rect position, float y, SerializedProperty property)
		{
			var end = property.GetEndProperty();
			var child = property.Copy();
			var enterChildren = true;

			EditorGUI.indentLevel++;

			while (child.NextVisible(enterChildren) && SerializedProperty.EqualContents(child, end) == false)
			{
				enterChildren = false;

				var height = EditorGUI.GetPropertyHeight(child, true);
				y += EditorGUIUtility.standardVerticalSpacing;

				EditorGUI.PropertyField(new Rect(position.x, y, position.width, height), child, true);

				y += height;
			}

			EditorGUI.indentLevel--;
		}

		private void DrawTypeButton(Rect headerRect, float valueX, SerializedProperty property)
		{
			var buttonRect = new Rect(valueX, headerRect.y, Mathf.Max(headerRect.xMax - valueX, 0f), headerRect.height);

			if (buttonRect.width <= 0f)
				return;

			// The button owns an explicit rect, so the indent applied to nested children must not
			// shift it as well.
			var indent = EditorGUI.indentLevel;
			EditorGUI.indentLevel = 0;

			try
			{
				var current = ResolveInstanceType(property);

				SharedContent.text = current == null
					? SubclassDropdown.NullDisplayName
					: ObjectNames.NicifyVariableName(current.Name);
				
				SharedContent.tooltip = current == null
					? "No instance assigned. Click to choose a type."
					: current.FullName;

				// SerializeReference has no meaningful multi-object edit: managedReferenceValue
				// throws when the targets disagree, so the picker is disabled rather than throwing.
				using (new EditorGUI.DisabledScope(property.serializedObject.isEditingMultipleObjects))
				{
					if (EditorGUI.DropdownButton(buttonRect, SharedContent, FocusType.Keyboard) == false)
						return;
				}

				ShowDropdown(buttonRect, property);
			}
			finally
			{
				EditorGUI.indentLevel = indent;
			}
		}

		private void ShowDropdown(Rect buttonRect, SerializedProperty property)
		{
			var baseType = ResolveFieldType(property);

			if (baseType == null)
			{
				Debug.LogError(
					$"[SubclassPicker] Could not resolve the declared type of '{property.propertyPath}' from " +
					$"'{property.managedReferenceFieldTypename}'. The picker cannot list candidates.");
				return;
			}

			var candidates = GetCandidates(baseType);

			// Keyed by property path so a reopened dropdown remembers its search text and scroll
			// position, which matters most on the field the user is iterating on.
			if (DropdownStates.TryGetValue(property.propertyPath, out var state) == false)
				DropdownStates[property.propertyPath] = state = new AdvancedDropdownState();

			// The selection callback fires on a later event, by which time this SerializedProperty
			// may have been invalidated. Capture the object and the path, and re-find the property
			// when the choice actually arrives.
			var serializedObject = property.serializedObject;
			var propertyPath = property.propertyPath;

			var dropdown = new SubclassDropdown(state, baseType, candidates,
				selected => Assign(serializedObject, propertyPath, selected));

			dropdown.Show(buttonRect);
		}

		private static void Assign(SerializedObject serializedObject, string propertyPath, Type selected)
		{
			// The window that owned it may have closed between click and callback.
			if (serializedObject == null || serializedObject.targetObject == null)
				return;

			serializedObject.Update();

			var property = serializedObject.FindProperty(propertyPath);

			if (property == null)
				return;

			// nonPublic: true so a descriptor can keep its constructor out of the public API.
			property.managedReferenceValue = selected == null ? null : Activator.CreateInstance(selected, nonPublic: true);

			// Expand on assignment: a freshly chosen descriptor whose fields stay collapsed reads
			// as "nothing happened".
			property.isExpanded = selected != null;

			// Routes through the undo system, so the choice is Ctrl+Z-able like any other edit.
			serializedObject.ApplyModifiedProperties();
		}

		#region Type resolution

		/// <summary>Concrete type of the instance currently held by the field, or null.</summary>
		private static Type ResolveInstanceType(SerializedProperty property)
		{
			var typename = property.managedReferenceFullTypename;

			return string.IsNullOrEmpty(typename) ? null : ResolveTypename(typename);
		}

		/// <summary>
		/// Declared type of the field — the base the candidate list is built from.
		/// </summary>
		/// <remarks>
		/// Reflection is tried first, and the reason is the collection case. Unity applies an
		/// attribute drawer to every <i>element</i> of a <c>[SerializeReference]</c> array or list,
		/// so <c>ITransformDescriptor[] transforms</c> re-enters this drawer once per element. What
		/// <see cref="SerializedProperty.managedReferenceFieldTypename"/> reports for an element is
		/// a Unity implementation detail; <see cref="PropertyDrawer.fieldInfo"/> is the declared
		/// field either way, so unwrapping the element type from it is deterministic. The typename
		/// string remains the fallback for the cases where Unity hands out no field info.
		/// </remarks>
		private Type ResolveFieldType(SerializedProperty property)
		{
			var declared = fieldInfo?.FieldType;

			if (declared != null)
			{
				if (declared.IsArray)
					return declared.GetElementType();

				if (declared.IsGenericType && declared.GetGenericTypeDefinition() == typeof(List<>))
					return declared.GetGenericArguments()[0];

				return declared;
			}

			var typename = property.managedReferenceFieldTypename;

			return string.IsNullOrEmpty(typename) ? null : ResolveTypename(typename);
		}

		/// <summary>
		/// Unity reports managed reference types as <c>"&lt;assembly&gt; &lt;full type name&gt;"</c>,
		/// which is not a form <see cref="Type.GetType(string)"/> accepts, so it is rebuilt into an
		/// assembly-qualified name and only then resolved.
		/// </summary>
		private static Type ResolveTypename(string typename)
		{
			if (ResolvedFieldTypes.TryGetValue(typename, out var cached))
				return cached;

			var separator = typename.IndexOf(' ');

			if (separator > 0)
			{
				var assemblyName = typename[..separator];
				var typeName = typename[(separator + 1)..];

				cached = Type.GetType($"{typeName}, {assemblyName}");

				// Fallback for the assemblies Type.GetType will not probe on its own.
				if (cached == null)
					foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
					{
						cached = assembly.GetType(typeName);

						if (cached != null)
							break;
					}
			}

			// Null is cached deliberately: a name that cannot be resolved will not start resolving,
			// and re-scanning every assembly once per OnGUI frame would be the expensive mistake.
			ResolvedFieldTypes[typename] = cached;
			return cached;
		}

		private static SubclassCandidate[] GetCandidates(Type baseType)
		{
			if (CandidatesByBaseType.TryGetValue(baseType, out var cached))
				return cached;

			// TypeCache is precomputed at domain reload — far cheaper than an assembly scan, and it
			// covers interfaces as well as base classes, which is what the descriptors need.
			var derived = TypeCache.GetTypesDerivedFrom(baseType);
			var candidates = new List<SubclassCandidate>(derived.Count);

			foreach (var type in derived)
			{
				if (IsSelectable(type) == false)
					continue;

				candidates.Add(new SubclassCandidate(type));
			}

			candidates.Sort(static (left, right) => string.CompareOrdinal(left.SortKey, right.SortKey));

			cached = candidates.ToArray();
			CandidatesByBaseType[baseType] = cached;
			return cached;
		}

		private static bool IsSelectable(Type type)
		{
			// SerializeReference stores class instances only — no structs, no abstract or open
			// generic types, and Unity refuses to serialize a type without [Serializable].
			if (type.IsClass == false || type.IsAbstract || type.IsGenericTypeDefinition)
				return false;

			if (type.IsDefined(typeof(SerializableAttribute), inherit: false) == false)
				return false;

			// UnityEngine.Object subclasses are reference-serialized by the normal object field, not
			// by SerializeReference; offering them here would produce a value that never persists.
			if (typeof(UnityEngine.Object).IsAssignableFrom(type))
				return false;

			return type.GetConstructor(
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				binder: null, Type.EmptyTypes, modifiers: null) != null;
		}

		#endregion
	}
}
