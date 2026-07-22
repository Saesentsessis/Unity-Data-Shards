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

Two native allocations per save, regardless of shard count. No exact-size buffer
contracts. No main-thread serialization stalls.

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
                                   │  + ShardBlobRange[] (id, offset, length)
                                   ▼
                              ISaveLayout  (single-file gather-write / multi-file)
                                   │  envelope codec v3 + xxHash3 checksum
                                   ▼
                          [TransformStorage]  (optional compression/encryption chain)
                                   ▼
                               IStorage  (FileStorage / PlayerPrefsStorage / custom)
```

## Core Features

- **Arena Pipeline:** all shard bytes land in one contiguous buffer indexed by
blittable `(id, offset, length)` ranges. A save performs **two** native allocations
total; disposal is two frees instead of one per shard.
- **`IBufferWriter<byte>` Serializer Contract:** no exact-size returns, no pre-measure
pass, no intermediate copies. Any writer-based serializer (MessagePack-CSharp,
`Utf8JsonWriter`, custom binary) plugs in directly.
- **Background Serialization:** serializers declaring `SupportsBackgroundSerialization`
run on the thread pool; the pipeline restores main-thread affinity before touching
Unity APIs — even on exception paths.
- **Incremental Saves:** shards expose `IsDirty`; layouts that don't require a full
snapshot receive only dirty blobs. The save envelope is cached per slot and invalidated
by a `ShardStore` generation counter.
- **Integrity by Default:** envelope format v3 is little-endian, fully bounds-checked
on read, and gated by an xxHash3-64 checksum over everything past the header.
Corruption throws `SaveCorruptedException` before a single byte is parsed.
- **Blob-Level Migrations:** `IShardMigration` transforms raw serialized bytes keyed by
the *stored type name*, so schema upgrades can reshape fields and rename types even
after the legacy class was deleted.
- **Crash-Safe File Storage:** atomic tmp/bak write dance with automatic backup
restore, reads through `AsyncReadManager` directly into unmanaged memory — no
thread-pool thread blocked on I/O, no managed intermediate.
- **Transform Chain:** `TransformStorage` decorates any storage backend with a
reversible `ISaveTransform` chain (compression, encryption) — zero changes to the
manager or layouts.

## Key Types

| Type                   | Description                                                              |
|------------------------|--------------------------------------------------------------------------|
| `SaveManager`          | Orchestrates the pipeline: dirty snapshot, envelope cache, save/load     |
| `IDataShard`           | Atomic unit of save data with a stable `SerializableGuid` identity       |
| `ShardStore`           | Flat, GUID-indexed shard set with O(1) lookup and a generation counter   |
| `ISerializer`          | Object ⇄ bytes via `IBufferWriter<byte>` / `ReadOnlySpan<byte>`          |
| `ISaveLayout`          | How blobs map onto storage keys (single-file vs multi-file)              |
| `IStorage`             | Physical medium: `FileStorage`, `PlayerPrefsStorage`, `CloudSaveStorage` |
| `SingleFileSaveLayout` | One gather-written, checksummed file per slot (atomic snapshot)          |
| `MultiFileSaveLayout`  | Envelope + one checksummed file per shard; rewrites only dirty shards    |
| `TransformStorage`     | Storage decorator applying an `ISaveTransform` chain                     |
| `MigrationRegistry`    | Chains `IShardMigration` steps over raw blob bytes                       |
| `SerializableGuid`     | Blittable, Unity-serializable 128-bit identity                           |

## Quick Start

Define a shard — a plain class with a stable identity and (optionally) dirty tracking:

```csharp
using System;
using Persistence.Core;
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
using Persistence;
using Persistence.Layout;
using Persistence.Serialization;
using Persistence.Storage;

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

`MultiFileSaveLayout` stores one envelope file per slot plus one file per shard
(`slot/<guid-hex>`), each framed with its own xxHash3-64 checksum. The envelope is
written last and acts as the commit point. Trade-off vs `SingleFileSaveLayout`:
per-file writes are atomic, but a crash mid-save can leave a mixed-generation state
across shards — acceptable because shards are independent by design.

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

### Compression / Encryption

Wrap any storage in a `TransformStorage`. Transforms apply in declaration order on
write and reverse order on read:

```csharp
var storage = new TransformStorage(new FileStorage(), new Lz4Transform(), new AesTransform());
var manager = new SaveManager(serializer, new SingleFileSaveLayout(storage));
```

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

| Backend            | Distribution        | GUID encoding            | Notes                                              |
|--------------------|---------------------|--------------------------|----------------------------------------------------|
| Unity `JsonUtility`| **built-in**        | two ulongs (JsonUtility) | Default. No dependencies.                           |
| Newtonsoft JSON    | in-runtime (gated)  | hex string               | Auto-active when `com.unity.nuget.newtonsoft-json` is installed. Full contract control. |
| System.Text.Json   | Sample              | hex string (stack)       | `Utf8JsonWriter` is buffer-native. Reflection → IL2CPP `link.xml`. |
| MessagePack         | Sample              | raw 16 bytes             | Compact/fast. **Needs `mpc` generated resolvers for IL2CPP.** |
| MemoryPack          | Sample              | raw unmanaged            | Fastest; buffer-native both ways. Shards must be `[MemoryPackable] partial`. |
| protobuf-net        | Sample              | two fixed64              | Contract-based; auto-maps public members. Positional wire format. |

> [!NOTE]
> `UnityJsonSerializer` and `NewtonsoftJsonSerializer` serialize Unity-style **fields**, including
> private `[SerializeField]` ones — so the canonical shard shape (a private `id` behind a get-only
> `Identifier`) round-trips out of the box. The other backends default to **public** members; if your
> shards keep serialized state in private `[SerializeField]` fields, expose them publicly or follow
> the field-visibility note in that backend's sample README.

**In-runtime integrations** (Newtonsoft) live in gated assemblies that compile only when their UPM
package is present — install the package and the serializer type simply appears, no manual setup.

**Samples** are imported from the Package Manager (**Window → Package Manager → Unity Data Shards →
Samples**). Each carries its own asmdef and a README with the exact install command for its backend
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

## Technical Deep Dive

### 1. The Arena

`SaveManager` serializes every captured shard into one growable buffer
(`NativeList<byte>` on the unmanaged pipeline, `ArrayPool<byte>` on the managed one)
through an `IBufferWriter<byte>` facade. Blob boundaries are recorded as before/after
write-length deltas into a blittable `ShardBlobRange[]`. The arena is pre-sized per
slot from the previous save's payload, so the steady state never reallocates
mid-serialization. Layouts receive `(envelope, payload, ranges)` and gather-write —
single-file packing is a straight concatenation with no re-copy.

### 2. Envelope Format v3

```text
[FormatVersion:4][Checksum:8] │ hashed region:
  [Timestamp:8][TypeCount:4]
  per type:   [nameLen:4][utf8 name][asmLen:4][utf8 asm][schemaVersion:4]
  [RecordCount:4]
  per record: [guid:16][typeIndex:4]
  (single-file layouts append [ranges][payload] here)
```

All primitives are written little-endian via `BinaryPrimitives` — endian-stable and
safe on unaligned addresses. The xxHash3-64 checksum covers everything past the
12-byte prefix, including the type table: a corrupted type name is exactly as fatal
as a corrupted blob. `FormatVersion` stays outside the hash so it can always be parsed
to pick a decoder. On read, the checksum is verified **before** any parsing; the
decoder additionally bounds-checks every advance and sanity-caps all counts.

### 3. Ownership & Threading Contracts

- `IStorage.WriteAsync` does **not** copy: the caller guarantees the buffer stays
valid until the returned task completes. This makes the whole-payload write zero-copy.
- `IStorage.TryReadAsync` reports missing keys via a `Found` flag — no exception cost,
no extra `Exists` round trip.
- With background serialization, the pipeline hops to the thread pool for the CPU-heavy
work and always returns to the main thread before invoking layouts/storages — including
on exception and cancellation paths.

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
    "com.cysharp.unitask": "2.3.3",
    "com.saesentsessis.unity-data-shards": "0.1.0"
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
   - _Direct Link:_ [Unity-Data-Shards-Installer.unitypackage](https://github.com/Saesentsessis/Unity-Data-Shards/releases/download/0.2.0/Unity-Data-Shards-Installer.unitypackage)
2. Import the downloaded package into your Unity project.
3. The installer will automatically configure OpenUPM in your `manifest.json` file and install the package dependencies.

### Method 3: Manual installation

1. Open Unity and navigate to `Window` -> `Package Manager`.
2. Click on the `+` icon in the top left corner and select `Add package from git URL...`.
3. Enter the following URL (dependency repository):
   ```
   https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask
   ```
4. Click Add.
5. Repeat all steps for the actual repository:
   ```
   https://github.com/Saesentsessis/Unity-Data-Shards.git?path=Unity-Data-Shards/Assets/root
   ```

You can specify exact release version of this package like this:

```
https://github.com/Saesentsessis/Unity-Data-Shards.git?path=Unity-Data-Shards/Assets/root#0.1.0
```

## Credits

This package was inspired by:

- **git-amend** — [Better Save/Load using Data Binding in Unity](https://youtu.be/z1sMhGIgfoo?si=EdouhvjAMAMoth8I)

## License

Licensed under the [MIT License](LICENSE).
