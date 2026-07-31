package com.saesentsessis.persistence;

import android.app.Activity;
import android.content.Context;
import android.content.SharedPreferences;

import java.util.Map;
import java.util.Set;

/**
 * Key enumeration for Unity's PlayerPrefs store on Android.
 *
 * <p>PlayerPrefs exposes no way to list keys from C#, and JNI hands strings back with no span
 * view — so a C#-side walk has to materialise a managed string for <em>every</em> key in the
 * store before it can tell which ones belong to the caller. This class exists to move that
 * filter to the Java side of the boundary: only keys that actually match cross it.
 *
 * <p>The single method is static and returns an exactly-sized array, so the whole enumeration is
 * one JNI call plus one array read.
 */
public final class PlayerPrefsReader
{
	/** Unity's SharedPreferences file. The ".v2." infix arrived in Unity 5.3; the old name
	 *  reads as an empty store rather than failing, which is why it must not be guessed. */
	private static final String PREFERENCES_SUFFIX = ".v2.playerprefs";

	private PlayerPrefsReader() { }

	/**
	 * Returns every PlayerPrefs key ending with {@code suffix}, with the suffix removed.
	 *
	 * <p>Counts first and then fills an exactly-sized array rather than growing an
	 * {@code ArrayList} and calling {@code toArray}: iterating a resident key set twice is
	 * cheaper than the doubling reallocations plus the final copy, and it means the array
	 * handed to C# is never oversized.
	 *
	 * <p>Keys equal to the suffix are skipped — stripping one leaves an empty key, which the
	 * C# side never writes.
	 *
	 * @param activity the Unity activity, used only to reach the application context
	 * @param suffix   storage postfix to match; an empty string matches every key
	 * @return matching keys with the suffix stripped, never null
	 */
	public static String[] getKeys(Activity activity, String suffix)
	{
		if (activity == null || suffix == null)
			return new String[0];

		SharedPreferences preferences = activity.getSharedPreferences(
			activity.getPackageName() + PREFERENCES_SUFFIX, Context.MODE_PRIVATE);

		// getAll is the only enumeration SharedPreferences offers. It is a shallow copy of a map
		// already held in memory, so this duplicates references rather than value data.
		Map<String, ?> entries = preferences.getAll();

		if (entries == null || entries.isEmpty())
			return new String[0];

		Set<String> keys = entries.keySet();
		int suffixLength = suffix.length();
		int matches = 0;

		for (String key : keys)
			if (key != null && key.length() > suffixLength && key.endsWith(suffix))
				matches++;

		if (matches == 0)
			return new String[0];

		String[] result = new String[matches];
		int index = 0;

		for (String key : keys)
		{
			if (key == null || key.length() <= suffixLength || !key.endsWith(suffix))
				continue;

			result[index++] = suffixLength == 0 ? key : key.substring(0, key.length() - suffixLength);

			// The map is not modified while it is walked, but a defensive stop keeps a racing
			// writer from turning a surprise into an ArrayIndexOutOfBoundsException.
			if (index == matches)
				break;
		}

		return result;
	}
}
