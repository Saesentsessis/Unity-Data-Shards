<div align="center">
    <h1>Unity Data Shards</h1>

[![OpenUPM](https://img.shields.io/npm/v/com.saesentsessis.unity-data-shards?label=OpenUPM&registry_uri=https://package.openupm.com&labelColor=333A41 'OpenUPM package')](https://openupm.com/packages/com.saesentsessis.unity-data-shards/)
[![Unity Editor](https://img.shields.io/badge/Editor-X?style=flat&logo=unity&labelColor=333A41&color=2A2A2A 'Unity Editor supported')](https://unity.com/releases/editor/archive)
[![Unity Runtime](https://img.shields.io/badge/Runtime-X?style=flat&logo=unity&labelColor=333A41&color=2A2A2A 'Unity Runtime supported')](https://unity.com/releases/editor/archive)
[![Tests Passed](https://github.com/Saesentsessis/Unity-Data-Shards/actions/workflows/release.yml/badge.svg 'Tests Passed')](https://github.com/Saesentsessis/Unity-Data-Shards/actions/workflows/release.yml)<br/>
[![Releases](https://img.shields.io/github/release/Saesentsessis/Unity-Data-Shards.svg)](https://github.com/Saesentsessis/Unity-Data-Shards/releases)
[![Stars](https://img.shields.io/github/stars/Saesentsessis/Unity-Data-Shards 'Stars')](https://github.com/Saesentsessis/Unity-Data-Shards/stargazers)
[![License](https://img.shields.io/github/license/Saesentsessis/Unity-Data-Shards?label=License&labelColor=333A41)](https://github.com/Saesentsessis/Unity-Data-Shards/blob/main/LICENSE)

</div>

**Unity Data Shards** is a performance-first save-system abstraction. Save data is
modeled as a flat set of **shards** — GUID-identified units serialized independently —
flowing through a zero-copy arena pipeline into pluggable serializers, layouts, and
storage backends.

One payload-sized allocation per save — the arena — regardless of shard count, plus a bitmask of
the dirty set. Shards are serialized into their final position, so the recommended configuration
copies the payload zero times on its way to disk. No exact-size buffer contracts. No main-thread
serialization stalls.

## The Problem & The Solution

Typical Unity save systems serialize one monolithic object graph: every save rewrites
everything, every schema change risks the whole file, and the serializer's allocation
habits leak into your frame time. Worse, most serializer contracts force either a
double serialization pass (pre-measuring the output size) or a full intermediate copy.

**This package splits save data into independent shards and pools their bytes in a
single contiguous arena.** Serializers append through the standard
`IBufferWriter<byte>` protocol, so the pipeline — not the serializer — owns the memory.
Dirty tracking makes incremental saves write only what changed; the envelope (type
table + records) is cached per slot and rebuilt only when membership changes; blob-level
migrations upgrade old data before deserialization, even when the original C# type no
longer exists.

```text
IDataShard[] ──► ISerializer ──► arena (NativeList<byte> / pooled byte[])
                                   │  layout framing reserved in place,
                                   │  + ShardBlobRange[] (id, offset, length)
                                   ▼
                              ISaveLayout  (fills the reservations — no payload copy)
                                   │  envelope codec v4 + xxHash3 checksum
                                   ▼
                          [TransformStorage]  (optional compression/encryption chain)
                                   ▼
                               IStorage  (FileStorage / PlayerPrefsStorage / custom)
```

## Core Features

- **Arena Pipeline:** all shard bytes land in one contiguous buffer indexed by
blittable `(id, offset, length)` ranges. **One** payload-sized allocation per save whatever the
shard count — the range array is reused per slot, and layouts reserve their framing inside the
arena rather than assembling in a buffer of their own. Disposal is a couple of frees instead of one
per shard.
- **`IBufferWriter<byte>` Serializer Contract:** no exact-size returns, no pre-measure
pass, no intermediate copies. Any writer-based serializer (MessagePack-CSharp,
`Utf8JsonWriter`, custom binary) plugs in directly.
- **Background Serialization:** serializers declaring `SupportsBackgroundSerialization`
run on the thread pool; the pipeline restores main-thread affinity before touching
Unity APIs — even on exception paths.
- **Incremental Saves:** shards expose `IsDirty`; layouts that don't require a full
snapshot receive only dirty blobs — plus any shard the layout reports it is *not* already
holding, so a restored shard or a save-as never commits an envelope over missing files. The
save envelope is cached per slot and invalidated by a `ShardStore` generation counter.
- **Integrity by Default:** envelope format v4 is little-endian, fully bounds-checked
on read, and gated by an xxHash3-64 checksum over everything past the checksum field
itself — the version, the magic and both counts are inside the hashed region, so a
flipped version bit cannot steer the reader to a different decoder. Corruption throws
`SaveCorruptedException` before a single byte is parsed.
- **Hostile Saves, Not Just Corrupt Ones:** a save file is treated as untrusted input.
Storage keys are confined to the save root by full-path normalization; a compressed
payload's declared output length is bounded by the format's maximum expansion before
anything is reserved; envelope counts are checked against the remaining bytes up front.
None of it is behind a scripting define. Where you need to detect *deliberate* edits
rather than accidental damage, `AesCbcHmacTransform`'s keyed tag does what the
envelope's unkeyed checksum cannot — anyone can recompute a checksum.
- **Blob-Level Migrations:** `IShardMigration` transforms raw serialized bytes keyed by
the *stored type name*, so schema upgrades can reshape fields and rename types even
after the legacy class was deleted.
- **Crash-Safe File Storage:** atomic tmp/bak write dance with automatic backup
restore. Reads go through `AsyncReadManager` straight into unmanaged memory — no managed
intermediate and no thread blocked on the transfer. Writes stream from that same unmanaged buffer
on a pool thread, so nothing is copied and the main thread is never held.
- **Transform Chain:** `TransformStorage` decorates any storage backend with a
reversible `IStorageTransform` chain — zero changes to the manager or layouts.
Dependency-free `DeflateTransform`, `AesCbcHmacTransform` (AES-256-CBC + HMAC-SHA256,
encrypt-then-MAC) and `XorTransform` ship in the box; LZ4 and Zstandard are one-file
samples.

## Key Types

| Type                     | Description                                                              |
|--------------------------|--------------------------------------------------------------------------|
| `SaveManager`            | Orchestrates the pipeline: dirty snapshot, envelope cache, save/load     |
| `IDataShard`             | Atomic unit of save data with a stable `SerializableGuid` identity       |
| `ShardStore`             | Flat, GUID-indexed shard set with O(1) lookup and a generation counter   |
| `ISerializer`            | Object ⇄ bytes via `IBufferWriter<byte>` / `ReadOnlySpan<byte>`          |
| `ISaveLayout`            | How blobs map onto storage keys (single-file vs multi-file)              |
| `IIncrementalSaveLayout` | Layout capability — "which shard blobs do you already hold?"             |
| `IStorage`               | Physical medium: `FileStorage`, `PlayerPrefsStorage`, `CloudSaveStorage` |
| `SingleFileSaveLayout`   | One gather-written, checksummed file per slot (atomic snapshot)          |
| `MultiFileSaveLayout`    | Envelope + one checksummed file per shard; rewrites only dirty shards    |
| `TransformStorage`       | Storage decorator applying an `IStorageTransform` chain                  |
| `SaveSlotBrowser`        | Lists existing slots and decodes their envelope headers on demand        |
| `IStorageDescriptor`     | Serializable recipe for a backend, so one can be picked in the Inspector |
| `MigrationRegistry`      | Chains `IShardMigration` steps over raw blob bytes                       |
| `SerializableGuid`       | Blittable, Unity-serializable 128-bit identity                           |

## Quick Start

Define a shard — a plain class with a stable identity and (optionally) dirty tracking:

```csharp
using System;
using Saesentsessis.Persistence.Core;
using UnityEngine;

[Serializable]
[ShardSchema(1)]
public class PlayerShard : IDataShard
{
    [SerializeField] private SerializableGuid id;
    [SerializeField] public int level;
    [SerializeField] public float health;

    [NonSerialized] private bool _dirty = true;

    public PlayerShard(Guid guid) => id = guid;

    public SerializableGuid Identifier => id;
    public bool IsDirty => _dirty;
    public void ClearDirty() => _dirty = false;
    public void MarkDirty() => _dirty = true;
}
```

Wire up a pipeline and save:

```csharp
using Saesentsessis.Persistence;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Serialization;
using Saesentsessis.Persistence.Storage;

var storage = new FileStorage();                    // Application.persistentDataPath
var layout  = new SingleFileSaveLayout(storage);    // one checksummed file per slot
var manager = new SaveManager(new UnityJsonSerializer(), layout);

var store = new ShardStore();
store.Add(new PlayerShard(Guid.NewGuid()) { level = 3, health = 87.5f });

await manager.SaveAsync("slot-1", store);

var loaded = await manager.LoadAsStoreAsync("slot-1");
loaded.TryGet<PlayerShard>(playerId, out var player);
```

> [!WARNING]
> Shards must not be mutated while a save is in flight. With background serialization
> enabled, shard data is read on a thread-pool thread — a mid-save mutation is a data
> race, and its dirty flag would be lost by the post-save `ClearDirty` pass.

## Usage Guide

### Incremental Saves

Layouts declare `RequiresFullSnapshot`. When `false`, only shards whose `IsDirty`
returns `true` are serialized and handed to the layout; the rest of the persisted
state is untouched. Dirty flags are snapshotted synchronously at the moment
`SaveAsync` is called and cleared only after the write succeeds.

```csharp
var manager = new SaveManager(serializer, new MultiFileSaveLayout(new FileStorage()));

player.MarkDirty();
await manager.SaveAsync("slot-1", store);   // rewrites ONLY the player shard's file + the envelope
```

Dirtiness is not the whole rule, though — **a clean shard is still written when the layout
does not already hold its blob**. Two ordinary sequences reach that state: removing a shard
and later restoring it unmodified (the removal deleted its file), and loading one slot to
save it into another (loading clears every dirty flag). In both cases the envelope would
otherwise commit records pointing at files that were never written, and the save would fail
to load. Layouts report what they hold through `IIncrementalSaveLayout`; a layout that does
not implement it is assumed to hold nothing, which is safe but writes every shard every save.

`MultiFileSaveLayout` stores one envelope file per slot plus one file per shard
(`slot/<guid-hex>`), each framed with its own xxHash3-64 checksum. The envelope is
written last and acts as the commit point. Trade-off vs `SingleFileSaveLayout`:
per-file writes are atomic, but a crash mid-save can leave a mixed-generation state
across shards — acceptable because shards are independent by design. It tracks the
membership it last saw on disk, which both sweeps orphaned files after a shard is removed
and tells `SaveManager` when a restored one has to be written again; a slot it has neither
read nor written this session is unknown, so the first save through a fresh layout is a full
write.

### Schema Migrations

Migrations operate on **raw blob bytes**, before deserialization. The source side is
identified by the stored type *name* (the CLR type may no longer exist); the
destination is a concrete `Type` that does.

```csharp
public sealed class PlayerV1ToV2 : IShardMigration
{
    public string FromTypeName => "Game.Persistence.PlayerShard";
    public int FromVersion => 1;
    public Type ToType => typeof(PlayerShard);
    public int ToVersion => 2;

    public void Migrate(ReadOnlySpan<byte> src, IBufferWriter<byte> dst)
    {
        // Reshape the serialized payload however the wire format requires —
        // rename fields, split values, change types.
    }
}

var migrations = new MigrationRegistry();
migrations.Register(new PlayerV1ToV2());

var manager = new SaveManager(serializer, layout, migrations);
```

The registry chains steps until the version declared by `[ShardSchema]` is reached,
validates broken or cyclic chains, and runs each step through pooled ping-pong buffers.

#### Typed migrations

Reshaping raw bytes is the expert path. When you would rather write a migration in plain
C#, derive from `TypedShardMigration<TOld, TNew>` and implement a single `Convert`. The base
class deserializes the old shape, hands it to you, and reserializes the result — so a typed
step is still just an `IShardMigration` in the same chain, with no wire-format knowledge
required. `TOld` may be a plain, versioned snapshot class kept only for migration.

```csharp
public sealed class PlayerV1ToV2 : TypedShardMigration<PlayerShardV1, PlayerShard>
{
    public PlayerV1ToV2() : base(fromVersion: 1, toVersion: 2) { }

    protected override PlayerShard Convert(PlayerShardV1 old)
        => new PlayerShard(old.Identifier, points: old.Value);
}

migrations.Register(new PlayerV1ToV2());
```

The active serializer is supplied automatically when the registry reaches a `SaveManager`
(the migration calling `Migrate` without one throws). Override `FromTypeName` if the stored
name differs from `TOld`'s current name.

### Building a SaveManager

`SaveManagerBuilder` (with `MigrationRegistryBuilder`) is a fluent alternative to the
constructors — it accepts either a ready `MigrationRegistry` or a builder, and picks the
matching `ISaveLayout` / `IManagedSaveLayout` overload:

```csharp
var manager = new SaveManagerBuilder()
    .WithSerializer(new UnityJsonSerializer())
    .WithLayout(new SingleFileSaveLayout(new FileStorage()))
    .WithMigrations(new MigrationRegistryBuilder()
        .Add(new PlayerV1ToV2()))
    .Build();
```

> [!NOTE]
> `IManagedSaveLayout` / `IManagedStorage` are the GC-heap counterparts of `ISaveLayout` /
> `IStorage`, for backends that cannot hand out native memory. The interfaces and the
> `SaveManager` pipeline behind them are in place, but **no implementation ships yet** — choosing
> the managed path today means writing both a layout and a storage yourself. Unless you have a
> backend that forces it, stay on the `NativeArray` path, where everything below is provided.

### Importing Existing (Non-Shard) Saves

Schema migrations only apply to data that already has a save envelope. Adopting a save written
*before* this package — a plain `PlayerData` blob, a PlayerPrefs string, an ad-hoc JSON file —
goes through a separate one-shot import pipeline that runs **before** the load path and commits
a single normal save. Afterward the slot loads like any other.

You load the legacy data yourself; the package never parses a foreign format:

```csharp
public sealed class InventoryImporter : IShardImporter<LegacySave>
{
    // false if the mapping touches UnityEngine.Object state.
    public bool SupportsBackgroundImport => true;

    public void Import(LegacySave legacy, ICollection<IDataShard> sink)
    {
        sink.Add(new InventoryShard(
            SerializableGuidExtensions.Compute("player/inventory"), legacy.Items));
    }
}

var legacy = JsonUtility.FromJson<LegacySave>(File.ReadAllText(oldPath)); // your call, your format

var result = await new ShardImportPipelineBuilder(manager)
    .AddImporter(new InventoryImporter())   // importers and data are registered separately
    .AddImporter(new StatsImporter())       // several importers may share one legacy type
    .AddData(legacy)
    .AddDataRange(legacyEnemies)            // many payloads of one type share a single step
    .Build()                                // pairs them by type; throws if a payload has none
    .RunAsync("slot0");

if (result.Status == ImportStatus.SkippedExistingSave)
    store = await manager.LoadAsStoreAsync("slot0"); // already adopted on an earlier run
else
    store = result.Store;                            // fresh import, ready to use
```

- **Run-once by default.** If the slot already holds a save the run is a no-op
  (`SkippedExistingSave`), so this is safe to call on every startup. Set
  `ImportOptions.Overwrite = true` to deliberately re-import.
- **Your old save is never touched.** The pipeline does not read, move or delete the legacy
  source — only you do, once you are satisfied the import worked.
- **Parallel by default.** Importers reporting `SupportsBackgroundImport` are all scheduled onto
  the thread pool first, then the main-thread importers run *concurrently with them*; both groups
  are joined before the save.
- **Batched by type.** All payloads of one legacy type go through a single step, so importing 500
  records costs one scheduled task and one buffer — not 500. Your importer still maps one object at
  a time; the batching is internal.
- **Validated at `Build()`.** A payload type with no importer throws (naming the type); an importer
  with no payloads only logs a warning, since it is harmless.
- `SerializableGuidExtensions.Compute(key)` mints a reproducible id from a string, since legacy data
  usually has no GUID. Optional — use `Guid.NewGuid()` if identity is arbitrary.

### Choosing a Storage Backend

`FileStorage` is the default and has no size ceiling worth naming. The other two do:

| Backend              | Size ceiling                                    | Use it for                                      |
|----------------------|-------------------------------------------------|-------------------------------------------------|
| `FileStorage`        | 2 GB arena limit                                | anything — this is the default                  |
| `PlayerPrefsStorage` | ~1.5 KB of save data advised; hard cap on Apple | settings, a slot index, small flags             |
| `CloudSaveStorage`   | 1 GiB/file, **200 files/player**                | cloud sync; see [Cloud Storage](#cloud-storage) |

All three can enumerate their slots for a load-game screen, but `PlayerPrefsStorage` only on some
platforms and only with a postfix set — see [Listing PlayerPrefs slots](#listing-playerprefs-slots).

#### WebGL

`FileStorage` is the backend to use on WebGL, and it is handled specially because the platform
needs it to be. Emscripten's filesystem lives in RAM: a write is instantly visible and instantly
gone when the tab closes, unless something mirrors it into IndexedDB. Unity only does that on
application quit, which a browser has no reliable event for.

This package syncs after every write and delete, and pulls IndexedDB back in before the first read,
so a completed `WriteAsync` means durable rather than "in memory". Nothing to call and nothing to
configure — the plugin ships with the package.

> [!IMPORTANT]
> **Saves do not survive a redeploy unless you pin the path.**
> `Application.persistentDataPath` on WebGL is `/idbfs/<md5 of the page's directory URL>`, so
> serving the build from a new URL — which itch.io and most CI deploys do on *every* upload — gives
> you a different directory and orphans every existing save. The data is still in IndexedDB; the
> game is looking somewhere else.
>
> Pass a fixed root under `/idbfs/` to pin it:
> ```csharp
> var storage = new FileStorage("/idbfs/your-game-a7f3c1");   // any stable, unique id
> ```
> The package mounts that path itself, since Unity only mounts the one it computed. Choose the id
> once and never change it — changing it orphans saves exactly like the URL does. A root that is
> neither `persistentDataPath` nor under `/idbfs/` is memory-only, and logs a warning saying so.

#### PlayerPrefs value limits

PlayerPrefs stores a **string**, and the payload is base64 — 4 characters stored per 3 bytes of
save data. Every budget below is measured against the encoded string, so the usable payload is
three quarters of it. Unity's own guidance is to keep a value at **2 KB or smaller** and write
anything larger to a file, which is precisely what `FileStorage` does. That 2 KB stored budget is
about **1.5 KB of actual save data**.

| Platform          | What the platform enforces                                                           | What this package does |
|-------------------|--------------------------------------------------------------------------------------|------------------------|
| **tvOS**          | Apple warns at 512 KB and **terminates the app** at 1 MB                             | throws past 512 KB     |
| **iOS**           | iOS 13+ rejects a write of ≥ 4 MiB (`CFPreferences`/`NSUserDefaults`)                | throws past 4 MiB      |
| **Windows**       | registry bounded by available memory; Microsoft advises ≤ 2048 bytes                 | warns past 2 KB        |
| **Android**       | `SharedPreferences` bounded by Java string size; the whole XML is parsed into memory | warns past 2 KB        |
| **macOS / Linux** | no documented ceiling                                                                | warns past 2 KB        |
| **Web**           | IndexedDB, quota decided by the browser                                              | warns past 2 KB        |

Two different kinds of number, so two different reactions. The Apple limits are **real ceilings** —
exceeding them loses the write or kills the process — so `WriteAsync` throws an `IOException`
before PlayerPrefs is called, and that check is never gated. Everything else is guidance about
load performance, so it logs once per key under `ENABLE_PERSISTENCE_INTEGRITY_CHECKS` and never
throws; inventing a ceiling there would break projects that work today.

> [!NOTE]
> The Apple limits apply to the **whole defaults store**, not to one value, so passing the check is
> necessary rather than sufficient — several values under the cap still add up. Nothing readable
> from PlayerPrefs would let the check know. On those platforms, use `FileStorage` for save data of
> any size.

> [!NOTE]
> The 1 MB figure often quoted for Web builds belonged to the long-removed Unity Web Player and does
> not apply to WebGL. The real Web hazard is flushing, not size: PlayerPrefs reaches IndexedDB only
> when the filesystem is synced, and a browser tab closing may not give Unity time. `Options.FlushOnWrite`
> is on by default for that reason.

### Configuring a Backend from the Inspector

A storage is a live resource — it holds caches and a write gate, it is `IDisposable`, and its
constructor may touch main-thread-only Unity APIs. So what gets serialized is not the storage but a
**descriptor**: plain data that knows how to build one.

```csharp
public class SaveSettings : ScriptableObject
{
    [SerializeReference] public IStorageDescriptor storage;
    [SerializeReference] public ISaveLayoutDescriptor layout;
}

// At startup — Create() returns a fresh instance every call, and the caller owns it.
var storage = settings.storage.Create();
var layout  = settings.layout.Create(storage);   // the layout owns the storage from here
var manager = new SaveManager(new UnityJsonSerializer(), layout);
```

> [!NOTE]
> Unity's built-in Inspector shows a `[SerializeReference]` field but offers no way to *choose*
> which implementation goes in it. The package ships a picker for its own Save Viewer window, but
> that attribute is internal — it is a minimal in-box tool, not an API. For your own assets, either
> write a small `PropertyDrawer` over `TypeCache.GetTypesDerivedFrom<IStorageDescriptor>()`, or use
> a package such as [SerializeReference Extensions](https://github.com/mackysoft/Unity-SerializeReferenceExtensions).
> Everything below works regardless — the descriptors are ordinary serializable classes.

| Descriptor                                                          | Builds                                     |
|---------------------------------------------------------------------|--------------------------------------------|
| `FileStorageDescriptor`                                             | `FileStorage` (root directory, extension)  |
| `PlayerPrefsStorageDescriptor`                                      | `PlayerPrefsStorage` (postfix, options)    |
| `TransformStorageDescriptor`                                        | `TransformStorage` — **nests**             |
| `XorTransformDescriptor`, `AesCbcHmacTransformDescriptor`           | the shipped transforms                     |
| `SingleFileSaveLayoutDescriptor`, `MultiFileSaveLayoutDescriptor`   | the shipped layouts                        |

`TransformStorageDescriptor` holds descriptors of its own, so a whole
`TransformStorage(FileStorage, Deflate, Aes)` chain is assembled in the Inspector. Because it builds
transforms nothing else refers to, it takes ownership of them — disposing the storage releases the
chain. See [Ownership](#3-ownership--threading-contracts) for the hand-written case, which does not.

> [!IMPORTANT]
> `AesCbcHmacTransformDescriptor` takes a **path to a key file**, never key bytes. A key typed into
> an inspector field is written into the asset, and into whatever that asset is committed to. The
> file is read only when the transform is built (so it may not exist on the machine editing the
> asset) and the bytes are held in unmanaged memory and zeroed as soon as the subkeys are derived.

### Listing Save Slots

`SaveSlotBrowser` answers "what saves exist?" — enough for a load-game screen, and what the editor
viewer is built on:

```csharp
var storage = new FileStorage();
var layout  = new MultiFileSaveLayout(storage);
var browser = new SaveSlotBrowser(storage, layout);

var slots = new List<SaveSlotInfo>();
await browser.PopulateAsync(slots);          // reads nothing — no save is opened

foreach (var slot in slots)
    Debug.Log($"{slot.Slot}: {slot.TotalBytes} bytes, {slot.KeyCount} files, {slot.ModifiedUtc}");

// Only for the slot the player actually highlights:
var header = await browser.ReadHeaderAsync(slots[0].Slot);

if (header.Status == SaveSlotStatus.Ok)
    Debug.Log($"{header.RecordCount} shards, written {header.WrittenUtc}");
```

**Two phases, on purpose.** `PopulateAsync` costs a directory walk and nothing more — sizes and
write times come from the listing itself. `ReadHeaderAsync` costs a full read of that slot. Listing
two hundred saves eagerly would read two hundred files to draw one screen, so decode headers lazily.

| Piece              | Role                                                                  |
|--------------------|-----------------------------------------------------------------------|
| `IListableStorage` | Optional storage capability — "which keys do you hold?"               |
| `StorageKeyInfo`   | One key: name, size, last-modified ticks                              |
| `ISlotKeyMapper`   | Layout capability — "which slot does this key belong to?"             |
| `SaveSlotBrowser`  | Composes the two; `SaveSlotInfo` per slot, `SaveSlotHeader` on demand |

Listing is a **capability, not a requirement**. Check `storage is IListableStorage`, or the browser's
`CanList`. `FileStorage` and `CloudSaveStorage` implement it; `TransformStorage` forwards to whatever
it wraps (so `CanList` can be true while the call still fails). **`PlayerPrefsStorage` implements it
on some platforms** — see below, because it is the one backend where `CanList` being true is not the
whole answer.

### Listing PlayerPrefs slots

Unity exposes no key enumeration for PlayerPrefs on any platform, so `PlayerPrefsStorage` reads the
store Unity itself writes to. Values still go through `PlayerPrefs`; the platform readers answer
*which keys exist* and nothing else.

| Platform                    | Store read                                     | Notes                                                   |
|-----------------------------|------------------------------------------------|---------------------------------------------------------|
| Windows (player + editor)   | Registry under `HKCU\Software\…`               | Editor and player use different branches; both handled  |
| macOS                       | `NSUserDefaults` property list                 | Binary `bplist00`; file name probed across known shapes |
| Linux                       | `~/.config/unity3d/<company>/<product>/prefs`  | Honours `XDG_CONFIG_HOME`                               |
| Android                     | `SharedPreferences` `<package>.v2.playerprefs` | **Requires the Android PlayerPrefs Reader sample**      |
| iOS / tvOS, WebGL, consoles | —                                              | `NotSupportedException`; use `FileStorage`              |

```csharp
var storage = new PlayerPrefsStorage(postfix: ".save");   // postfix is mandatory for listing
var browser = new SaveSlotBrowser(storage, layout);
await browser.PopulateAsync(slots);
```

> [!IMPORTANT]
> **A non-empty postfix is required, and listing without one throws.** PlayerPrefs is a shared
> namespace: with no postfix nothing separates your saves from Unity's own `unity.player_session_count`
> or the game's audio settings, and every key in the store would come back as a save slot.

Two consequences worth planning around. **`SizeBytes` and `ModifiedUtc` are 0** — measuring a size
would mean reading every save's full payload just to draw a list, and no prefs store records a
per-key modification time, so a slot list here shows names and nothing else. And on **Android the
plugin sample is mandatory**: import *Android PlayerPrefs Reader* from Package Manager and **rebuild
the APK**, since the helper is Java compiled into your build. Without it, `PopulateAsync` throws a
message saying so. It exists because JNI hands strings back with no span view, so filtering in C#
would allocate one string per *stored* key rather than per match; doing it in Java keeps that cost
proportional to the number of saves you actually have.

Both layouts implement `ISlotKeyMapper`, so the browser is normally built from the same storage and
layout you gave the `SaveManager`. Under `MultiFileSaveLayout` a slot's shard files are folded into
one entry, with `KeyCount` and `TotalBytes` covering the whole slot.

> [!NOTE]
> Header decoding consults no layout: every layout writes the envelope at offset 0 of the slot's own
> key. Reading through a transform chain reverses it on the way, so compressed and encrypted saves
> need nothing special. **Nothing but cancellation escapes `ReadHeaderAsync`** — every failure is
> reported through `SaveSlotStatus` (`Corrupted`, `Foreign`, `UnsupportedVersion`, `Missing`,
> `Unreadable`), because a browser has to survive one bad file in a folder. That includes a
> nonsensical timestamp: `WrittenUtc` never throws, and `HasTimestamp` is false when the stored
> value is not a date a `DateTime` can hold.

**Concurrency.** A browser needs no wiring to be safe against a concurrent save. Serialisation
happens one layer down, inside the storage, keyed by the resource itself — see
[Threading Contracts](#3-ownership--threading-contracts) — so it holds even when the writer is a
different storage instance in the same process, which is exactly what the Save Viewer is.

#### Save Viewer window

**Window/Saesentsessis/Persistence/Save Viewer** puts the same browser behind an inspector. Pick a
storage and a layout — the same [descriptors](#configuring-a-backend-from-the-inspector) you would
put on a settings asset — press Refresh, and click a slot to decode its envelope header.

The configuration is serialized on the window, so opening several viewers lets each point at a
different backend — matching a project that runs several `SaveManager`s — and it survives domain
reloads and editor restarts through the layout file. Resetting your window layout does clear it;
the configuration is cheap to redo, so that is an accepted trade rather than a bug.

### Compression / Encryption

Wrap any storage in a `TransformStorage`. `IStorageTransform`s apply in declaration order on write
and reverse order on read, so **compress first, then encrypt** — ciphertext does not compress:

```csharp
var storage = new TransformStorage(
    new FileStorage(),
    new DeflateTransform(),             // Apply runs in declaration order
    new AesCbcHmacTransform(key));      // Reverse runs in reverse order

var manager = new SaveManager(serializer, new SingleFileSaveLayout(storage));
```

**The unit of work is one storage key, not one save.** `SingleFileSaveLayout` writes a whole save
under one key, so a transform sees the entire packed buffer. `MultiFileSaveLayout` writes the
envelope plus one key per shard, so the transform runs *N+1* times on individual shard blobs — which
means worse compression ratios (small inputs) and, for encryption, per-file IV and tag overhead.

#### Shipped transforms

| Transform             | Dependency           | Notes                                                        |
|-----------------------|----------------------|--------------------------------------------------------------|
| `XorTransform`        | none                 | Obfuscation only, no security value. Its own inverse.        |
| `DeflateTransform`    | none                 | `System.IO.Compression`. See the IL2CPP caveat below.        |
| `AesCbcHmacTransform` | none                 | AES-256-CBC + HMAC-SHA256, encrypt-then-MAC.                 |
| `LZ4Transform`        | K4os.Compression.LZ4 | `Samples~/LZ4Compression`. Fast, modest ratio, pure managed. |
| `ZstdTransform`       | ZstdSharp.Port       | `Samples~/ZstdCompression`. Better ratio, slower.            |

All three compression transforms frame their output as `[originalLength:4 LE][compressed bytes]`.
LZ4 requires it (its decoder needs the output size up front); the others use it to size the output
buffer and to verify the result.

That prefix is **untrusted input** — it comes off disk, which means it comes from whoever last
edited the file. Reserving from it directly is how a few hundred bytes turn into a multi-gigabyte
allocation, so every decoder checks it against `TransformLimits` first:

- the declared length must be within the format's maximum expansion for the bytes actually present
  (Deflate 1032:1, LZ4 255:1, Zstd 32768:1), which keeps any reservation proportional to the file
  someone really wrote;
- `DeflateTransform` additionally never trusts the prefix at all. Deflate is self-terminating, so
  decompression is driven by the stream and each reservation is capped at `TransformLimits.MaxReservation`
  (2 MB). LZ4 and Zstd decode into an exactly-sized buffer and cannot grow incrementally, so for
  them the ratio bound is the whole defence.

> [!TIP]
> Chaining `AesCbcHmacTransform` after compression removes the exposure entirely. `Reverse` runs
> outermost-first, so the authentication tag is verified before a single compressed byte is
> examined, and forging that tag needs the key. The envelope's own xxHash3 does **not** help here —
> it is unkeyed, and it is checked only after the whole transform chain has already run.

`AesCbcHmacTransform` writes `[IV:16][ciphertext][HMAC:32]`. The tag covers `IV || ciphertext` and is
verified in constant time **before** anything is decrypted; a mismatch throws `SaveCorruptedException`.

> [!WARNING]
> On a shipped game, encryption here is **obfuscation, not secrecy** — the key travels inside the
> build and can be extracted. What the HMAC does buy is genuine tamper *detection*: the envelope's
> xxHash3 checksum is unkeyed, so anyone editing a save can simply recompute it, whereas forging the
> HMAC requires the key. Never use this to protect data that must stay secret from the player.

> [!NOTE]
> `AesGcm` is deliberately **not** used: it compiles on every platform but throws
> `PlatformNotSupportedException` at runtime on iOS, tvOS and WebGL.
> Under IL2CPP, AES is reached by reflection and gets stripped — preserve it in `Assets/link.xml`:
> ```xml
> <linker>
>   <assembly fullname="System.Core">
>     <type fullname="System.Security.Cryptography.AesManaged" preserve="all" />
>   </assembly>
> </linker>
> ```
> Unity also has a historical report of `DeflateStream` losing bytes under IL2CPP. Round-trip a save
> on your target platform before shipping `DeflateTransform`; the LZ4 sample is pure managed C# and
> carries no such risk.

#### Writing your own

```csharp
public sealed class MyTransform : IStorageTransform
{
    public void Apply(ReadOnlySpan<byte> src, IBufferWriter<byte> dst) { /* save direction */ }
    public void Reverse(ReadOnlySpan<byte> src, IBufferWriter<byte> dst) { /* load direction */ }
}
```

The contract is reversibility, not purity: `Reverse` must reconstruct the exact input of `Apply`,
but `Apply` need not be deterministic — an encrypting transform is expected to emit a fresh IV per
call. Implementations must be stateless between calls; `TransformStorage` runs one operation at a
time per instance, so they need not be thread-safe.

**Ownership:** a `TransformStorage` owns everything below it. Disposing one disposes the storage it
wraps *and* every transform in its chain, so `using var storage = …` releases the lot.

> [!IMPORTANT]
> **A transform instance belongs to exactly one storage.** Do not hand the same one to two chains.
> Transforms hold per-operation scratch state — the cipher's IV and arena, the decorator's own
> ping-pong buffers — so two storages driving one instance would interleave through it and corrupt
> both. Build a fresh transform per storage; `TransformStorageDescriptor` does exactly that on every
> `Create()`.

That rule is also what makes disposal unambiguous: there is only ever one owner, so a disposable
transform (`AesCbcHmacTransform`, and `XorTransform` — it holds a native pattern buffer) is released
exactly once and never leaks.

### Custom Serializers

Implement two methods — the pipeline owns all buffers:

```csharp
public sealed class MyBinarySerializer : ISerializer
{
    public bool SupportsBackgroundSerialization => true;

    public void Serialize(object value, Type type, IBufferWriter<byte> writer)
        => MyFormat.Write(value, type, writer);          // append into the arena

    public object Deserialize(ReadOnlySpan<byte> data, Type type)
        => MyFormat.Read(data, type);                    // read from the payload slice
}
```

Several ready-made implementations ship with the package — see
[Serialization Backends](#serialization-backends).

Return `false` from `SupportsBackgroundSerialization` if your serializer touches
`UnityEngine.Object` state — the pipeline will then keep it on the caller's thread.

## Serialization Backends

The core package ships only `UnityJsonSerializer` (`JsonUtility`, zero extra dependencies).
Five more backends are provided as **optional integrations** — none is a hard dependency, so
you pull in only what you use. Each maps `SerializableGuid` with no heap allocation where the
format allows (raw bytes for binary formats, a stack-formatted hex string for JSON).

| Backend             | Distribution       | GUID encoding            | Notes                                                                                   |
|---------------------|--------------------|--------------------------|-----------------------------------------------------------------------------------------|
| Unity `JsonUtility` | **built-in**       | two ulongs (JsonUtility) | Default. No dependencies.                                                               |
| Newtonsoft JSON     | in-runtime (gated) | hex string               | Auto-active when `com.unity.nuget.newtonsoft-json` is installed. Full contract control. |
| System.Text.Json    | Sample             | hex string (stack)       | `Utf8JsonWriter` is buffer-native. Reflection → IL2CPP `link.xml`.                      |
| MessagePack         | Sample             | raw 16 bytes             | Compact/fast. **Needs `mpc` generated resolvers for IL2CPP.**                           |
| MemoryPack          | Sample             | raw unmanaged            | Fastest; buffer-native both ways. Shards must be `[MemoryPackable] partial`.            |
| protobuf-net        | Sample             | two fixed64              | Contract-based; auto-maps public members. Positional wire format.                       |

> [!IMPORTANT]
> `UnityJsonSerializer` and `NewtonsoftJsonSerializer` serialize Unity-style **fields**, including
> private `[SerializeField]` ones — so the canonical shard shape (a private `id` behind a get-only
> `Identifier`) round-trips out of the box. The other backends default to **public** members; if your
> shards keep serialized state in private `[SerializeField]` fields, expose them publicly or follow
> the field-visibility note in that backend's sample README.

**In-runtime integrations** (Newtonsoft) live in gated assemblies that compile only when their UPM
package is present — install the package and the serializer type simply appears, no manual setup.

**Samples** are imported from the Package Manager (**Window → Package Manager → Unity Data Shards →
Samples**). Each carries its own asmdef and a README with the exact installation guide for its backend
DLL and any AOT caveats. They are copied into your project, so you can adapt them freely.

```csharp
// Newtonsoft (in-runtime, once the package is installed):
var manager = new SaveManager(new NewtonsoftJsonSerializer(), layout);
```

Beyond these, any `IBufferWriter<byte>`-capable serializer drops in via the `ISerializer` pattern
above — Odin Serializer, Ceras, and others are straightforward to wrap.

## Cloud Storage

`CloudSaveStorage` (in-runtime, gated on `com.unity.services.cloudsave`) backs `IStorage` with the
Unity Gaming Services **Cloud Save Files API**, so any layout — including `MultiFileSaveLayout` —
persists to the cloud.

```csharp
// Prerequisite: the app has initialized UGS and signed the player in.
await UnityServices.InitializeAsync();
await AuthenticationService.Instance.SignInAnonymouslyAsync();

var manager = new SaveManager(serializer, new SingleFileSaveLayout(new CloudSaveStorage()));
await manager.SaveAsync("slot-1", store);
```

- **Caller-initialized:** the storage never touches authentication; it throws a clear error if the
  player is not signed in. You control the UGS lifecycle and sign-in policy.
- **Multi-file support:** Cloud Save keys disallow `/`, so `CloudSaveStorage` remaps it to a
  reserved character (default `.`) when forming cloud keys. That character must not appear in your
  slot names (it is rejected if it does).

### Cloud Save quotas

Per player (there is no cap on the number of players):

| Quota              | Value   | Checked                                         |
|--------------------|---------|-------------------------------------------------|
| Total file storage | 1 GiB   | by UGS                                          |
| Single file size   | 1 GiB   | before upload, throws `IOException`             |
| Number of files    | **200** | by UGS                                          |
| Filename length    | 255     | on the incoming key, throws `ArgumentException` |

> [!WARNING]
> The **200-file cap is the one that bites, and it argues against `MultiFileSaveLayout` in the
> cloud.** That layout spends one file per shard plus one for the envelope, so a slot of N shards
> costs N+1 of the player's 200 — across every slot they own. Past roughly 199 shards in total the
> save cannot be stored at all, and this package is comfortable with shard counts well above that.
> `SingleFileSaveLayout` spends exactly one file per slot no matter how many shards it holds, and
> the size quotas are generous enough that a single-file save will never approach them. Pair
> multi-file with `FileStorage` locally and single-file with the cloud.

The two quotas a single call can see — file size and filename length — are enforced client-side so
you fail before spending the player's bandwidth. The running per-player totals are not visible
without an extra round trip, so UGS reports those as a `CloudSaveException` when they are hit.

## Performance

Every configuration below writes the same bytes; they differ in how much memory it takes to get
them there. Figures are for a save of **100 shards × 1 KB** (~105 KB on disk), counting every
buffer the pipeline allocates from `IDataShard` to the medium. "Copies" means payload-sized
memcpys, excluding the write to the medium itself.

| Serializer                  | Layout      | Storage              | Allocated  | Copies |
|-----------------------------|-------------|----------------------|------------|--------|
| buffer-native or Newtonsoft | single-file | `FileStorage`        | **105 KB** | **0**  |
| buffer-native or Newtonsoft | single-file | `CloudSaveStorage`   | 105 KB     | 0      |
| buffer-native or Newtonsoft | multi-file  | `FileStorage`        | 103 KB     | 0      |
| buffer-native or Newtonsoft | single-file | `PlayerPrefsStorage` | 383 KB     | 1×     |
| `UnityJsonSerializer`       | single-file | `FileStorage`        | 305 KB     | 1×     |
| `UnityJsonSerializer`       | multi-file  | `PlayerPrefsStorage` | 578 KB     | 2×     |

**Buffer-native** means `MemoryPack`, `MessagePack`, `System.Text.Json` or `protobuf-net` — anything
writing through `IBufferWriter<byte>`. `NewtonsoftJsonSerializer` joins them: it encodes UTF-8
straight into the arena. All five are byte-for-byte identical in cost.

Three rules cover most of it:

- **Single-file + `FileStorage` + a buffer-native serializer is the cheapest path**, and reaches the
  theoretical minimum: one buffer the size of the save, zero payload copies. The arena the shards
  are serialized into *is* the file handed to storage.
- **`UnityJsonSerializer` costs roughly 3× the alternatives.** `JsonUtility` returns a `string`, so
  every shard is copied to UTF-16 and encoded back. There is no streaming API to avoid it. It is a
  fine default for small saves and the wrong choice for large ones.
- **`PlayerPrefsStorage` adds ~2.67× the save size**, because `SetString` takes a `string` and
  base64 in UTF-16 is four chars per three bytes. Nothing in this package can improve on that — use
  `FileStorage` for payloads of any size.

One case where totals mislead: with `PlayerPrefsStorage`, **multi-file has a far lower peak** than
single-file. Single-file builds one string the size of the whole save; multi-file builds one small
string per shard and releases it. On iOS and tvOS that peak is what the platform's defaults-store
limit measures, so multi-file is the safer pairing there despite similar totals.

> [!NOTE]
> Per-combination numbers for all 36 serializer × layout × storage pairings, the theoretical minima
> they are measured against, and how each figure is derived are in
> [docs/performance-assesment.md](../../../docs/performance-assesment.md). The values there are computed from
> the source rather than profiled, so each is traceable to a specific allocation site.

## Technical Deep Dive

### 1. The Arena

`SaveManager` serializes every captured shard into one growable buffer
(`NativeList<byte>` on the unmanaged pipeline, a pooled `byte[]` on the managed one) through an
`IBufferWriter<byte>` facade. Blob boundaries are recorded as before/after write-length deltas
into a blittable `NativeArray<ShardBlobRange>`, which is **kept per slot and reused** rather than
allocated per save. The arena is pre-sized from the previous save's written length, so the steady
state never reallocates mid-serialization.

**The shards are serialized into their final position.** Before anything is written, the layout
declares how much room it needs in front of the data:

|                        | `HeaderReservation`                        | `BlobReservation`        |
|------------------------|--------------------------------------------|--------------------------|
| `SingleFileSaveLayout` | `ExactEncodedSize(envelope) + 4 + 24N + 4` | 0                        |
| `MultiFileSaveLayout`  | 0                                          | 8 (the per-file xxHash3) |

The manager advances the arena past those gaps as it serializes, so the layout only has to fill
them afterwards. Single-file writes its envelope, range block and payload length into the head and
hands storage **the same buffer** — the arena *is* the file. Multi-file writes each blob's hash
into the eight bytes ahead of it and hands storage a `GetSubArray` view spanning both. Neither
copies the payload.

> [!NOTE]
> **Writing your own `ISaveLayout`?** Both members are default interface implementations returning
> `0`, so a layout that ignores them behaves exactly as it did before they existed — there is
> nothing to add. Override them only to claim space you intend to fill. Two consequences if you do:
> blob offsets in `ranges` are **absolute within the buffer**, so they already include your header,
> and the buffer handed to `WriteAsync` arrives with the gaps present rather than needing to be
> assembled. Calling `SingleFileSaveLayout.WriteAsync` or `MultiFileSaveLayout.WriteAsync` directly
> with a buffer of your own is the one thing that changed: they now expect those reservations.

That the envelope must precede the payload never forced a copy; it only forced its encoded size to
be known exactly before serialization, which `EnvelopeCodec.ExactEncodedSize` supplies. (A *bound*
will not do — the slack would become a gap the format cannot express.)

**What a save actually allocates**, steady state, ignoring the medium:

|                        | per save                                |
|------------------------|-----------------------------------------|
| Arena                  | one buffer, the size of what is written |
| Blob ranges            | none — reused per slot                  |
| Dirty-set bitmask      | `N` bits                                |
| `SingleFileSaveLayout` | nothing                                 |
| `MultiFileSaveLayout`  | one envelope-sized buffer               |

The layouts still differ, just not in copies: single-file peaks at the whole save and commits it in
one atomic write; multi-file peaks the same but writes `N+1` files, so a crash can leave a
mixed-generation slot. See [Performance](#performance) for what each combination costs, and
[docs/performance-assesment.md](../../../docs/performance-assesment.md) for the derivation.

### 2. Envelope Format v4

```text
[Checksum:8] │ hashed region:
  [FormatVersion:4][Magic:4 "SHRD"][Timestamp:8][TypeCount:4][RecordCount:4]
  per type:   [nameLen:4][utf8 name][asmLen:4][utf8 asm][schemaVersion:4]
  per record: [guid:16][typeIndex:4]      ← one memcpy for the whole block
  (single-file layouts append [ranges][payload] here)
```

The fixed 32-byte header is written as raw struct memory, which makes the format
**little-endian only** — every Unity target is, and a big-endian host fails loudly
rather than emitting files nothing can read back. Variable-length fields still go
through `BinaryPrimitives`. Every field lands on its natural alignment.

The xxHash3-64 checksum covers everything past the 8-byte checksum field, including
the version, the magic and the type table: a corrupted type name is exactly as fatal
as a corrupted blob, and a flipped version bit cannot steer the reader to a different
decoder. On read the checksum is verified **before** any parsing; then the magic is
matched (so foreign data is rejected as foreign, not as a corrupt save), and because
*both* counts live in the fixed header, the decoder rejects impossible ones against
the remaining byte count before allocating anything.

> [!IMPORTANT]
> **Breaking in 0.4.0** — format **v3 cannot be read**. Its version field sat outside
> the checksummed region and its two counts were split across the variable-length type
> table. Saves written by 0.3.x are not upgradable; delete them or re-generate.

### 3. Ownership & Threading Contracts

- `IStorage.WriteAsync` does **not** copy: the caller guarantees the buffer stays
valid until the returned task completes, so the bytes go from the buffer to the medium
untouched. Since the shipped layouts write into the arena rather than assembling elsewhere, that
buffer is the one the serializer wrote into — `FileStorage` streams it straight from unmanaged
memory, and `CloudSaveStorage` wraps it in an `UnmanagedMemoryStream`. `PlayerPrefsStorage` is the
exception, and not by choice: `SetString` takes a `string`, so the payload is base64-encoded into
one.
- `IStorage.TryReadAsync` reports missing keys via a `Found` flag — no exception cost,
no extra `Exists` round trip.
- With background serialization, the pipeline hops to the thread pool for the CPU-heavy
work and always returns to the main thread before invoking layouts/storages — including
on exception and cancellation paths.
- `SaveManager`, `ShardStore`, storages and layouts implement `IDisposable`. Disposing a
`SaveManager` cascades to its layout and the storage that layout wraps, so a single
`using var manager = …` releases the whole chain; `ShardStore.Dispose` in turn disposes
any shard that is itself `IDisposable`, and `TransformStorage` disposes its transforms —
each belongs to exactly one storage, so ownership is never ambiguous. A `StorageReadResult`
reports a missing key as `Found == false`; when `Found` is true it owns a created
`NativeArray` and must be disposed by whoever consumes it.
- **Storage access is serialised per resource, process-wide.** Two `FileStorage` instances over one
directory name the same files, so the lock is keyed by the resolved absolute path rather than held
as a field on either instance — they meet on it without ever referencing each other. This is what
makes the Save Viewer safe to refresh while Play Mode saves: it builds its own storage and could
never have been handed the game's. Compiled in under `ENABLE_PERSISTENCE_SAFE_CONCURRENCY` and
always in the editor; a player build without the define has no viewer, so the only caller left is
your own code, which the define governs. Two *processes* over one directory remain out of scope.
- **One operation in flight per slot.** The pipeline arenas, `PooledArrayBufferWriter` and
`TransformStorage`'s ping-pong buffers are all single-operation by design, so overlapping
`SaveAsync`/`LoadAsync`/`DeleteAsync` calls on the *same* slot are a contract violation. Different
slots may run concurrently. What happens when the contract is broken depends on the build:
`ENABLE_PERSISTENCE_SAFE_CONCURRENCY` serialises the callers with a real per-slot mutex (and does
the same for `FileStorage`'s tmp/bak write sequence per key); otherwise the overlap costs nothing at
runtime, and under `ENABLE_PERSISTENCE_INTEGRITY_CHECKS` it throws immediately so the mistake
surfaces during development rather than as corruption later. Concurrency between two *processes*
sharing a save directory is out of scope and is not defended against.

### 4. Safety Checks (optional)

Two editor toggles under **Tools/Saesentsessis/Persistence** gate the pipeline's
non-essential checks behind scripting defines, so a shipping build can drop them:

| Menu item        | Define                                | Guards                                                                                                                                                          |
|------------------|---------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Integrity Checks | `ENABLE_PERSISTENCE_INTEGRITY_CHECKS` | Programmer-error asserts on your own data — buffer capacity, envelope count and string limits on **write**, value-type shard rejection, and the one-operation-per-slot detector. |
| Safe Concurrency | `ENABLE_PERSISTENCE_SAFE_CONCURRENCY` | Real per-slot and per-storage-key locking, plus concurrent collections inside the storage backends.                                                              |

Both default to **on** the first time the package loads in a project (recorded per-project,
so a deliberate opt-out sticks).

Two categories are **never** gated and run in every build, because stripping them would strip a
security or correctness boundary rather than an assert:

- validation of **untrusted data read from disk** — checksums, bounds, count plausibility,
  type-resolution, and the decompression limits described above;
- **caller-input validation on public entry points** — a null or empty slot, a null transform in a
  chain, and `FileStorage`'s path confinement (a key may not escape the save root, whether by `..`,
  by a rooted path, or by any other route the platform's own normalization would resolve outward).

## Async Backend (UniTask optional)

The pipeline's async surface is backend-agnostic. If **[UniTask](https://github.com/Cysharp/UniTask)**
is installed, the package auto-detects it (asmdef version define `PERSISTENCE_HAS_UNITASK`) and uses
`UniTask`/`UniTask<T>` for zero-allocation awaits — recommended. If it is **not** installed, the same
API compiles against `System.Threading.Tasks.Task`, with main-thread affinity provided by a
PlayerLoop-driven dispatcher (no `SynchronizationContext` dependency). Nothing to configure either
way; `SaveManager.SaveAsync`/`LoadAsync` return `UniTask` or `Task` accordingly.

## Requirements

- Unity **2022.3** or newer
- [`com.unity.collections`](https://docs.unity3d.com/Packages/com.unity.collections@latest) **2.1.4** or newer
- [`com.unity.burst`](https://docs.unity3d.com/Packages/com.unity.burst@latest) **1.8.0** or newer
- *(optional)* [`com.cysharp.unitask`](https://github.com/Cysharp/UniTask) **2.0.0** or newer — enables the UniTask backend
- *(optional)* [`com.unity.services.cloudsave`](https://docs.unity.com/ugs/manual/cloud-save/manual) **3.0.0** or newer — enables `CloudSaveStorage`, which uploads through that version's `SaveAsync(string, Stream)` overload

## Installation

### Method 1: OpenUPM (Recommended)

You can install this package via the [OpenUPM](https://openupm.com/) CLI:

```bash
openupm add com.saesentsessis.unity-data-shards
```

Or manually add the scoped registry to your `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.saesentsessis.unity-data-shards": "0.6.0"
  },
  "scopedRegistries": [
    {
      "name": "OpenUPM",
      "url": "https://package.openupm.com",
      "scopes": [
        "com.saesentsessis"
      ]
    }
  ]
}
```

It is also recommended (but not required) to install a `com.cysharp.unitask` package, as it provides zero-allocation
awaits workflow inside Unity context:

```json
{
  "dependencies": {
    "com.saesentsessis.unity-data-shards": "0.6.0",
    "com.cysharp.unitask": "2.0.0"
  },
  "scopedRegistries": [
    {
      "name": "OpenUPM",
      "url": "https://package.openupm.com",
      "scopes": [
        "com.saesentsessis",
        "com.cysharp"
      ]
    }
  ]
}
```

### Method 2: Unity package installer

1. Download the latest `.unitypackage` from [GitHub Releases page](https://github.com/Saesentsessis/Unity-Data-Shards/releases).
   - _Direct Link:_ [Unity-Data-Shards-Installer.unitypackage](https://github.com/Saesentsessis/Unity-Data-Shards/releases/download/0.6.0/Unity-Data-Shards-Installer.unitypackage)
2. Import the downloaded package into your Unity project.
3. The installer will automatically configure OpenUPM in your `manifest.json` file and install the package dependencies.

### Method 3: Manual installation

1. Open Unity and navigate to `Window` -> `Package Manager`.
2. Click on the `+` icon in the top left corner and select `Add package from git URL...`.
3. ```
   https://github.com/Saesentsessis/Unity-Data-Shards.git?path=Unity-Data-Shards/Assets/root
   ```
4. Click Add.

You can specify exact release version of this package like this:

```
https://github.com/Saesentsessis/Unity-Data-Shards.git?path=Unity-Data-Shards/Assets/root#0.6.0
```

You can repeat all steps for the optional dependency repository:
```
https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask
```

## Upcoming Changes

Rough sketches, not commitments — anything here may change shape or not ship at all.

- Storage integrations. All four are key→bytes stores that `IStorage` already fits; what each of
  them adds is conflict resolution between devices, which the interface has no concept of today.
  - [ ] Steam Cloud
  - [ ] Google Play Games (Saved Games)
  - [ ] Epic Online Services (Player Data Storage)
  - [ ] PlayFab (Entity Files)
- Serializer integrations.
  - [ ] Odin Serializer
  - [ ] FlatBuffers, via [FlatSharp](https://github.com/jamescourtney/FlatSharp)'s attribute-driven
    contracts — the official `flatc`-generated API is schema-first and exposes nothing an
    `ISerializer` could be implemented against.
- [ ] **Layout-driven serialization.** Invert the manager/layout relationship so a layout pulls one
  shard at a time instead of being handed a full arena. This is what would bring
  `MultiFileSaveLayout` from 47× its theoretical floor down to it — see
  [docs/performance-assesment.md](../../../docs/performance-assesment.md).
- [ ] **Import pipeline without a hard `SaveManager` dependency.** The pipeline should take objects
  and emit ready shards; persisting them through `SaveManager` becomes an optional final step.

## Contributing

Pull requests are welcome and genuinely wanted. The whole package is built around swappable
contracts, so the most valuable contributions are new implementations of them:

- **Serializer backends** (`ISerializer`) — anything with an `IBufferWriter<byte>`-shaped API:
  Odin Serializer, Ceras, FlatBuffers, Bond, a hand-rolled binary format.
- **Storage backends** (`IStorage` / `IManagedStorage`) — Steam Cloud, PlayFab, Epic Online
  Services, Google Play Games saved games, iCloud, a plain HTTP endpoint, an in-memory test double.
- **Storage transforms** (`IStorageTransform`) — compression, encryption, integrity, or anything
  else that is reversible.

That list is not a fence. Bug reports, bug fixes, performance work, new layouts, migrations,
tests, platform findings, typo corrections and documentation improvements are all appreciated —
if it makes the package better, open a PR or an
[issue](https://github.com/Saesentsessis/Unity-Data-Shards/issues).

A few things that make a PR easy to merge: keep it focused on one change, match the surrounding
code style, add tests for anything with behavior, and note the change in `CHANGELOG.md`. If you
are unsure whether an idea fits, open an issue first and ask — that is cheaper than writing code
in the wrong direction.

## Credits

This package was inspired by:

- **git-amend** — [Better Save/Load using Data Binding in Unity](https://youtu.be/z1sMhGIgfoo?si=EdouhvjAMAMoth8I)

## License

Licensed under the [MIT License](LICENSE).
