namespace Saesentsessis.Persistence.Utils
{
	/// <summary>
	/// Picks the <see cref="IPlayerPrefsReader"/> for the current build target.
	/// </summary>
	/// <remarks>
	/// Resolved by preprocessor rather than by <c>Application.platform</c> so the platform-specific
	/// code — a <c>DllImport</c> on Windows, JNI on Android — never has to compile for a target it
	/// cannot run on. Unsupported targets return null, and listing degrades to "not supported"
	/// rather than to an exception.
	/// </remarks>
	internal static class PlayerPrefsUtils
	{
		private static IPlayerPrefsReader _shared;
		private static bool _resolved;

		/// <summary>
		/// The process-wide reader, or null on a platform with no implementation. There is one
		/// preferences store however many <c>PlayerPrefsStorage</c> instances address it, so the
		/// reader that walks it is shared and owned by nobody — never dispose this.
		/// </summary>
		/// <remarks>
		/// Built on first use rather than in a field initializer. A field initializer would run in
		/// the type initializer, where a failure — <c>AndroidJavaClass</c> throwing on a device with
		/// no activity yet — becomes a <see cref="System.TypeInitializationException"/> that poisons
		/// the type for the rest of the process, with no way to retry. It also pins construction to
		/// whichever thread first touches the class, which for JNI is exactly the wrong property.
		/// Main-thread only, like every other PlayerPrefs operation.
		/// </remarks>
		public static IPlayerPrefsReader Shared
		{
			get
			{
				if (_resolved)
					return _shared;

				// Set first: a platform that throws on construction reports "unsupported" from then
				// on rather than re-throwing on every refresh of a save browser.
				_resolved = true;
				_shared = CreateReader();

				return _shared;
			}
		}

		/// <summary>Creates a reader, or null when this platform has no implementation.</summary>
		/// <remarks>
		/// Not supported yet:
		/// <list type="bullet">
		/// <item><b>iOS/tvOS</b> — needs a native <c>.mm</c> exposing
		/// <c>NSUserDefaults.dictionaryRepresentation</c>, plus on-device confirmation of the key
		/// prefix Unity applies there.</item>
		/// <item><b>WebGL</b> — PlayerPrefs is a single binary file in IndexedDB
		/// (<c>/idbfs/&lt;hash&gt;/PlayerPrefs</c>), in a format Unity does not document, so
		/// enumeration would mean reverse-engineering it rather than reading a directory.</item>
		/// <item><b>Consoles</b> — the implementations live in platform modules distributed under
		/// NDA, so they could not ship here even if they were understood. Use <c>FileStorage</c>
		/// there: console certification wants explicit save-data mount and commit anyway, which
		/// PlayerPrefs cannot express.</item>
		/// </list>
		/// </remarks>
		public static IPlayerPrefsReader CreateReader()
		{
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
			return new WindowsPlayerPrefsReader();
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
			return new MacPlayerPrefsReader();
#elif UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
			return new LinuxPlayerPrefsReader();
#elif UNITY_ANDROID && !UNITY_EDITOR
			return new AndroidPlayerPrefsReader();
#else
			return null;
#endif
		}
	}
}
