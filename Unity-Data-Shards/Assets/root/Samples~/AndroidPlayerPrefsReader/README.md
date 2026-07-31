# Android PlayerPrefs Reader

Java helper that lets `PlayerPrefsStorage` list save slots on Android.

**Import this sample if you use `PlayerPrefsStorage` with `SaveSlotBrowser` or the Save Viewer on
Android.** Nothing else needs it — reading, writing and deleting saves work without it. Only
*enumeration* does, because PlayerPrefs exposes no way to list keys.

Without it, `PopulateAsync` throws a `NotSupportedException` naming this sample. There is
deliberately no silent fallback; see below.

## Install

1. Package Manager → **Unity Data Shards** → Samples → **Android PlayerPrefs Reader** → *Import*.
2. **Rebuild the APK.** The helper is Java compiled into your build, so importing it into an
   existing build does nothing — this is the most common reason the exception persists after
   importing.

Unity compiles `.java` under `Plugins/Android/`, which is where the imported file lands. If your
project uses a custom Gradle template that restricts source sets, move `PlayerPrefsReader.java`
into an `.androidlib` directory instead — the class name and signature are what matter, not the
location.

Requires no manifest entry, no permission, and no third-party dependency. It reads your own app's
private preferences.

## What it does

```java
public static String[] getKeys(Activity activity, String suffix)
```

Opens `<package>.v2.playerprefs` — the SharedPreferences store Unity's PlayerPrefs writes to —
and returns the keys ending in `suffix`, with the suffix stripped.

## Why a plugin rather than JNI

Enumeration is possible from C# alone, via `getAll().keySet().iterator()`. The problem is where the
filter runs. JNI returns a `jstring` with no span view, so a C#-side walk must turn **every key in
the store** into a managed string before it can test whether the key belongs to your storage. A
project with a few hundred settings pays a few hundred allocations to find three saves.

Doing the filter in Java moves it to the far side of the boundary. Only matches cross, so the cost
becomes one string per *match*, and the whole enumeration is a single JNI call instead of two per
key. It is the same principle the Windows reader uses, where `RegEnumValueW` writes into a buffer
the package owns and candidates are matched as spans.

The Java side counts matches first and fills an exactly-sized array, avoiding `ArrayList` growth
and the `toArray` copy.

## Why there is no fallback

A silent fallback would mean the allocation profile this sample removes gets chosen invisibly at
runtime — the listing still works, just worse, on the platform where allocation matters most, and
nothing tells you. Failing with a message that names the sample is the more useful behaviour.

## Limitations

`SharedPreferences` offers no key-only view, so `getAll()` is unavoidable. It is a shallow copy of
a map Android already holds resident, so it duplicates references rather than value data — but it
does mean enumeration touches the whole store. Listing is user-initiated (opening a load-game
screen), not per-frame, so this is not on any hot path.
