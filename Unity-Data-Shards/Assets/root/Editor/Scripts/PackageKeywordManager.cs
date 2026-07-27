using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
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
			// Never touch PlayerSettings in batch mode. An automated run's compile flags must come
			// from the project it checked out, not from a package rewriting them mid-session:
			// changing defines requests a recompilation, and a recompilation racing the Test
			// Runner's player build fails the whole job with "Error building Player because
			// scripts are compiling". CI projects declare the defines they want in ProjectSettings.
			if (Application.isBatchMode)
				return;

			EditorApplication.delayCall += LoadKeywords;
		}

		/// <summary>
		/// Enables the optional checks the first time the package is loaded in a project, then
		/// mirrors the current state onto the menu items.
		/// </summary>
		private static void LoadKeywords()
		{
			EditorApplication.delayCall -= LoadKeywords;

			if (string.IsNullOrEmpty(EditorUserSettings.GetConfigValue(PersistenceIntegrityChecksSeededKey)))
			{
				// Seeding triggers a domain reload, so it must not land inside an import, a compile
				// or a player build. Re-arm rather than seed now; the marker below is only written
				// once the write actually happens, so retrying is safe.
				if (EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)
				{
					EditorApplication.delayCall += LoadKeywords;
					return;
				}

				EditorUserSettings.SetConfigValue(PersistenceIntegrityChecksSeededKey, "1");

				// One write for both keywords: setting them separately requested two compilations.
				SetKeywords(true, PersistenceIntegrityChecksKeyword, PersistenceSafeConcurrencyKeyword);
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
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void SetKeyword(string keyword, bool state)
		{
			SetKeywords(state, keyword);
		}

		/// <summary>
		/// Adds or removes several keywords in a single write. Every write to the define symbols
		/// costs a domain reload, so the whole batch is folded into one — and a batch that changes
		/// nothing writes nothing at all.
		/// </summary>
		private static void SetKeywords(bool state, params string[] keywords)
		{
			var symbols = GetSymbols();
			var changed = false;

			foreach (var keyword in keywords)
			{
				var range = StringUtils.RangeOfKeyword(symbols, keyword);

				if (state)
				{
					if (range.start >= 0)
						continue;

					symbols = StringUtils.Join(symbols, keyword);
				}
				else
				{
					if (range.start < 0)
						continue;

					symbols = StringUtils.Remove(symbols, range);
				}

				changed = true;
			}

			if (changed)
				SetSymbols(symbols);
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
