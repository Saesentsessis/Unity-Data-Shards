using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEditor.Build;
using Saesentsessis.Persistence.Utils;

namespace Saesentsessis.Persistence.Editor
{
	/// <summary>
	/// Toggles the package's optional scripting defines from
	/// <c>Tools/Saesentsessis/Persistence</c>.
	/// <para>
	/// The scripting define symbols are the single source of truth: the menu's checkmark reads
	/// them back instead of mirroring the state into a separate preference, so the two can never
	/// disagree — including after a platform switch, since defines are stored per build target.
	/// </para>
	/// </summary>
	[InitializeOnLoad]
	internal static class PackageKeywordManager
	{
		private const string PersistenceIntegrityChecksKeyword = "ENABLE_PERSISTENCE_INTEGRITY_CHECKS";
		private const string TogglePersistenceIntegrityChecksPath = "Tools/Saesentsessis/Persistence/Integrity Checks";
		
		private const string PersistenceSafeConcurrencyKeyword = "ENABLE_PERSISTENCE_SAFE_CONCURRENCY";
		private const string TogglePersistenceSafeConcurrencyPath = "Tools/Saesentsessis/Persistence/Safe Concurrency";
		
		private const string PersistenceIntegrityChecksSeededKey = "saesentsessis.persistence.integrity_checks_seeded";

		static PackageKeywordManager()
		{
			EditorApplication.delayCall += LoadKeywords;
		}

		/// <summary>
		/// Enables integrity checks the first time the package is loaded in a project.
		/// </summary>
		private static void LoadKeywords()
		{
			EditorApplication.delayCall -= LoadKeywords;

			if (string.IsNullOrEmpty(EditorUserSettings.GetConfigValue(PersistenceIntegrityChecksSeededKey)))
			{
				EditorUserSettings.SetConfigValue(PersistenceIntegrityChecksSeededKey, "1");

				SetKeyword(PersistenceIntegrityChecksKeyword, true);
				SetKeyword(PersistenceSafeConcurrencyKeyword, true);
			}
			
			Menu.SetChecked(TogglePersistenceIntegrityChecksPath, HasKeyword(PersistenceIntegrityChecksKeyword));
			Menu.SetChecked(TogglePersistenceSafeConcurrencyPath, HasKeyword(PersistenceSafeConcurrencyKeyword));
		}

		[MenuItem(TogglePersistenceIntegrityChecksPath, false)]
		public static void TogglePersistenceIntegrityChecks()
		{
			var state = HasKeyword(PersistenceIntegrityChecksKeyword) == false;
			
			SetKeyword(PersistenceIntegrityChecksKeyword, state);
			Menu.SetChecked(TogglePersistenceIntegrityChecksPath, state);
		}

		[MenuItem(TogglePersistenceSafeConcurrencyPath, false)]
		public static void TogglePersistenceSafeConcurrencyChecks()
		{
			var state = HasKeyword(PersistenceSafeConcurrencyKeyword) == false;
			
			SetKeyword(PersistenceSafeConcurrencyKeyword, state);
			Menu.SetChecked(TogglePersistenceSafeConcurrencyPath, state);
		}

		/// <summary>
		/// True if the keyword is defined for the active build target.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static bool HasKeyword(string keyword)
		{
			var symbols = GetSymbols();

			return StringUtils.RangeOfKeyword(symbols, keyword).start >= 0;
		}

		/// <summary>
		/// Adds or removes the keyword for the active build target.
		/// </summary>
		private static void SetKeyword(string keyword, bool state)
		{
			var symbols = GetSymbols();
			var range = StringUtils.RangeOfKeyword(symbols, keyword);
			
			if (state)
			{
				if (range.start >= 0)
					return;

				SetSymbols(StringUtils.Join(symbols, keyword));
				return;
			}

			if (range.start < 0)
				return;
			
			SetSymbols(StringUtils.Remove(symbols, range));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static string GetSymbols()
		{
#if UNITY_6000_0_OR_NEWER
			return PlayerSettings.GetScriptingDefineSymbols(CurrentTarget());
#else
			return PlayerSettings.GetScriptingDefineSymbolsForGroup(CurrentGroup());
#endif
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void SetSymbols(string symbols)
		{
#if UNITY_6000_0_OR_NEWER
			PlayerSettings.SetScriptingDefineSymbols(CurrentTarget(), symbols);
#else
			PlayerSettings.SetScriptingDefineSymbolsForGroup(CurrentGroup(), symbols);
#endif
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static BuildTargetGroup CurrentGroup()
		{
			return EditorUserBuildSettings.selectedBuildTargetGroup;
		}

#if UNITY_6000_0_OR_NEWER
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static NamedBuildTarget CurrentTarget()
		{
			return NamedBuildTarget.FromBuildTargetGroup(CurrentGroup());
		}
#endif
	}
}
