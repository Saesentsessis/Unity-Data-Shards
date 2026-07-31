#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;

namespace Saesentsessis.Persistence.Utils
{
	/// <summary>
	/// Enumerates PlayerPrefs keys on Android through the <b>Android PlayerPrefs Reader</b> sample's
	/// Java helper, which reads the <c>&lt;package&gt;.v2.playerprefs</c> SharedPreferences store.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>The sample is required; there is no managed fallback.</b> A pure-JNI walk is possible —
	/// <c>getAll().keySet().iterator()</c> — but it cannot filter before it allocates: JNI returns
	/// a <c>jstring</c> with no span view, so every key in the store becomes a managed string
	/// before its postfix is tested. A store of a few hundred unrelated settings pays a few hundred
	/// string allocations to find three saves. The Java helper applies the filter on its own side
	/// of the boundary, so the cost drops to one string per <i>match</i> and one JNI call for the
	/// whole enumeration. Keeping the slow path as a silent fallback would have meant shipping the
	/// allocation profile the sample exists to remove, chosen invisibly at runtime.
	/// </para>
	/// <para>
	/// Missing plugin therefore fails loudly, at the point of use, naming what to import.
	/// </para>
	/// </remarks>
	internal sealed class AndroidPlayerPrefsReader : IPlayerPrefsReader
	{
		private const string SampleName = "Android PlayerPrefs Reader";
		private const string JavaClass = "com/saesentsessis/persistence/PlayerPrefsReader";
		private const string MethodName = "getKeys";
		private const string MethodSignature = "(Landroid/app/Activity;Ljava/lang/String;)[Ljava/lang/String;";

		private readonly IntPtr _reader;   // global ref, or Zero when the sample is absent
		private readonly IntPtr _method;
		private readonly IntPtr _activity; // global ref

		// One reused argument array: the call shape is fixed, so allocating it per enumeration
		// would be the only garbage this reader produces beyond the keys themselves.
		private readonly jvalue[] _arguments = new jvalue[2];

		private string[] _keys = Array.Empty<string>();

		public AndroidPlayerPrefsReader()
		{
			using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
			using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");

			if (activity == null)
				return;

			_activity = AndroidJNI.NewGlobalRef(activity.GetRawObject());

			var local = FindReaderClass();

			if (local == IntPtr.Zero)
				return;

			_reader = AndroidJNI.NewGlobalRef(local);
			AndroidJNI.DeleteLocalRef(local);
			_method = AndroidJNI.GetStaticMethodID(_reader, MethodName, MethodSignature);
		}

		/// <summary>
		/// Locates the sample's class, clearing the pending JNI exception when it is absent so the
		/// failure surfaces as our own message rather than as a stray NoClassDefFoundError later.
		/// </summary>
		private static IntPtr FindReaderClass()
		{
			try
			{
				var type = AndroidJNI.FindClass(JavaClass);

				if (AndroidJNI.ExceptionOccurred() == IntPtr.Zero)
					return type;

				AndroidJNI.ExceptionClear();
			}
			catch (Exception)
			{
				AndroidJNI.ExceptionClear();
			}

			return IntPtr.Zero;
		}

		public ReadOnlySpan<string> EnumerateKeys(string postfix)
		{
			if (_reader == IntPtr.Zero || _method == IntPtr.Zero)
				throw new NotSupportedException(
					$"[PlayerPrefsStorage] Listing PlayerPrefs keys on Android requires the '{SampleName}' " +
					$"sample, whose Java helper ({JavaClass.Replace('/', '.')}) was not found in this build. " +
					"Import it from Package Manager > Unity Data Shards > Samples, then rebuild — the plugin is " +
					"compiled into the APK, so importing it without a rebuild is not enough. There is deliberately " +
					"no managed fallback: the pure-JNI walk allocates one string per stored key rather than per " +
					"match, which is the cost this sample exists to remove.");

			var count = 0;
			var suffix = AndroidJNI.NewStringUTF(postfix);

			try
			{
				_arguments[0].l = _activity;
				_arguments[1].l = suffix;

				var array = AndroidJNI.CallStaticObjectMethod(_reader, _method, _arguments);

				if (array == IntPtr.Zero)
					return ReadOnlySpan<string>.Empty;

				try
				{
					// Every element here already matched and was already stripped Java-side, so each
					// one becomes a string exactly once and none is discarded.
					var length = AndroidJNI.GetArrayLength(array);

					if (length > _keys.Length)
						Array.Resize(ref _keys, length);

					for (var i = 0; i < length; i++)
					{
						var element = AndroidJNI.GetObjectArrayElement(array, i);

						if (element == IntPtr.Zero)
							continue;

						_keys[count++] = AndroidJNI.GetStringUTFChars(element);
						AndroidJNI.DeleteLocalRef(element);
					}
				}
				finally
				{
					AndroidJNI.DeleteLocalRef(array);
				}
			}
			finally
			{
				AndroidJNI.DeleteLocalRef(suffix);
			}

			return new ReadOnlySpan<string>(_keys, 0, count);
		}

		public void Dispose()
		{
			if (_reader != IntPtr.Zero)
				AndroidJNI.DeleteGlobalRef(_reader);

			if (_activity != IntPtr.Zero)
				AndroidJNI.DeleteGlobalRef(_activity);

			Array.Clear(_keys, 0, _keys.Length);
			_keys = Array.Empty<string>();
		}
	}
}
#endif
