# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.0] - 2026-07-23

### Added

- `TypedShardMigration<TOld, TNew>` — a typed migration tier that lets authors convert in plain C# (`protected abstract TNew Convert(TOld old)`) instead of reshaping raw serialized bytes. It is an adapter over `IShardMigration`, so the migration chain and `MigrationRegistry` are unchanged. The active serializer is bound automatically when the registry reaches a `SaveManager`, via the new `ISerializerAware` interface.
- `SaveManagerBuilder` and `MigrationRegistryBuilder` — fluent construction. `SaveManagerBuilder` accepts either a ready `MigrationRegistry` or a `MigrationRegistryBuilder` and selects the matching `ISaveLayout` / `IManagedSaveLayout` overload. `MigrationRegistry` also gained a bulk `IReadOnlyList<IShardMigration>` constructor.
- README "Typed migrations" and "Building a SaveManager" subsections documenting the above.
- `Saesentsessis.Persistence.Import` — a one-shot pipeline for adopting existing **non-shard** saves, separate from `MigrationRegistry` (foreign data has no envelope or checksum, so it cannot enter the load-time migration chain). `IShardImporter<TLegacy>` maps a caller-loaded legacy object onto shards; `ShardImportPipeline` / `ShardImportPipelineBuilder` schedule every `SupportsBackgroundImport` importer onto the thread pool first and run the main-thread importers concurrently with them, joining both groups before committing a single save. Skips by default when the slot already holds a save (`ImportOptions.Overwrite` opts into re-import). The legacy source is never read, moved or deleted.
- Importers and payloads are registered independently — `AddImporter<TLegacy>` / `AddData<TLegacy>` / `AddDataRange<TLegacy>` — and paired by legacy type at `Build()`. Several importers may share one legacy type; a payload type with no importer throws (naming every unmatched type), while an importer with no payloads only logs a warning. All generic construction happens at the statically-typed registration site, so the builder never reflects over types and stays IL2CPP/AOT-safe.
- Payloads of the same legacy type are batched into a single step, so a background import of N records costs one scheduled task and one pooled buffer instead of N. Duplicate ids are attributed to the exact importer and payload index.
- `PersistenceTask.WhenAll` — backend-agnostic join primitive used by the import pipeline.
- README "Importing Existing (Non-Shard) Saves" section documenting the above.

### Changed

- **BREAKING:** every namespace moved from `Persistence.*` to `Saesentsessis.Persistence.*`, matching the assembly definition names. Consumers must update their `using` directives (`using Persistence;` → `using Saesentsessis.Persistence;`, and likewise for `.Core`, `.Layout`, `.Storage`, `.Serialization`, `.Buffers`, `.Threading`, `.Import`). The `Samples~` serializers moved with it (`Saesentsessis.Persistence.Serialization.MemoryPack` and friends). No type names or behaviour changed.

## [0.2.1] - 2026-07-23

### Fixed

- `SerializableGuidExtensions.Compute` aliased the input as a `NativeArray<char>`; `char` is not a valid element type there, so the deterministic-id helper could not hash a key correctly. It now aliases the same memory as `ushort`, and the `string` overload delegates to the span overload instead of duplicating the logic.

## [0.2.0] - 2026-07-23

### Added

- Wrapped all of the `UniTask` calls inside a preprocessor directive with fallback to `System.Threading.Tasks`. `SaveManager.SaveAsync`/`LoadAsync` return `UniTask` or `Task` accordingly. Without UniTask, main-thread affinity is provided by a PlayerLoop-driven dispatcher.
- Backend-agnostic async pipeline surface support. If `UniTask` is installed, the package auto-detects it (`PERSISTENCE_HAS_UNITASK` added as an asmdef version-define on Runtime/CloudSave/Tests).
- A section inside [README.md - Async Backend (UniTask optional)](README.md#async-backend-unitask-optional) describing new behavior in more details.

### Removed

- Hard dependency on `com.cysharp.unitask` package from `package.json`. Dropped unused `UniTask` references from Newtonsoft asmdef. Non-destructive.

## [0.1.0] - 2026-07-17

### Added

- Initial preview release of Unity Data Shards.
- **Core abstractions** (`Persistence.Core`):
  - `IDataShard` — atomic unit of save data with a stable `SerializableGuid` identity and optional `IsDirty` / `ClearDirty()` tracking.
  - `ISerializer` — object ⇄ bytes over `IBufferWriter<byte>` / `ReadOnlySpan<byte>`; `SupportsBackgroundSerialization` flag opts serialization onto the thread pool.
  - `ISaveLayout` / `IManagedSaveLayout` — blob organization on storage (arena payload + `ShardBlobRange` index); `RequiresFullSnapshot` gates incremental saves.
  - `IStorage` / `IManagedStorage` — async key-value byte storage with `TryReadAsync` (missing keys report `Found == false`, no exception) and zero-copy `WriteAsync` (caller guarantees buffer lifetime until completion).
  - `ISaveTransform` — reversible byte transform (compression, encryption) chained at the storage boundary.
  - `IShardMigration` — blob-level schema migration step keyed by the *stored type name*, so legacy CLR types may be deleted; emits into a concrete destination `Type`.
  - `SerializableGuid` — blittable, Unity-serializable 128-bit identity with Burst-friendly equality and `Guid` interop.
  - `ShardSchemaAttribute` / `ShardSchemaHelper` — per-type schema versioning with a thread-safe cache.
  - `SaveCorruptedException` — thrown on checksum mismatch, truncation, or structurally impossible values.
- **Pipeline** (`Persistence`):
  - `SaveManager` — dirty-set snapshot into `NativeBitArray` before any await, per-slot envelope cache invalidated by the `ShardStore` generation counter (weakly referenced, evicted on `DeleteAsync`), background serialization with exception-safe main-thread affinity restore, per-slot arena size hints.
  - `ShardStore` — flat, GUID-indexed `IReadOnlyList<IDataShard>` with O(1) lookup, swap-back removal, membership `Generation` counter, and duplicate-id rejection in the copy constructor.
  - `MigrationRegistry` — chains blob migrations through pooled ping-pong buffers; validates broken chains, version overshoot, and cycles (64-step cap).
- **Arena buffer writers** (`Persistence.Buffers`):
  - `NativeListBufferWriter` — `IBufferWriter<byte>` over `NativeList<byte>` with a reusable `MemoryManager<byte>` bridge for `GetMemory` consumers.
  - `PooledArrayBufferWriter` — `ArrayPool<byte>`-backed managed counterpart.
- **Layout** (`Persistence.Layout`):
  - Envelope binary format **v3**: little-endian via `BinaryPrimitives`, deduplicated type table, fully bounds-checked decoding with sanity-capped counts.
  - `EnvelopeCodec` — single-pass writer (no size pre-measuring), xxHash3-64 checksum helpers (`ComputeChecksum` / `PatchChecksum` / `ValidateChecksum`) covering everything past the 12-byte prefix.
  - `SingleFileSaveLayout` — envelope + ranges + payload gather-written into one checksummed storage key per slot (atomic snapshot).
  - `MultiFileSaveLayout` — incremental layout: one envelope file per slot plus one file per shard (`slot/<guid-hex>`), each framed with an 8-byte xxHash3-64 prefix; only dirty shards' files are rewritten, the envelope is written last as the commit point.
  - `ShardBlobRange`, `SaveEnvelope`, `ShardRecord`, `SerializedType`, `SaveLayoutResult` / `ManagedSaveLayoutResult`.
- **Storage** (`Persistence.Storage`):
  - `FileStorage` — crash-safe atomic writes (tmp/bak dance with stale-backup recovery), reads via `AsyncReadManager` directly into unmanaged memory, per-key path cache, 2 GB file-size guard.
  - `PlayerPrefsStorage` — single-allocation base64 round-trip (exact decoded length from padding, `string.Create` + `Convert.TryToBase64Chars` encode).
  - `TransformStorage` — `IStorage` decorator applying an `ISaveTransform` chain through reused ping-pong arenas.
- **Serialization** (`Persistence.Serialization`):
  - `UnityJsonSerializer` — `JsonUtility`-backed serializer with single-pass UTF-8 encoding; background-capable for plain data types.
  - `SerializableGuid.TryFormatHex(Span<char>)` and `TryParse(ReadOnlySpan<char>)` — allocation-free hex format/parse used by the serializer integrations.
- **Serializer integrations** (optional, none a hard dependency):
  - `NewtonsoftJsonSerializer` — in-runtime, gated on `com.unity.nuget.newtonsoft-json` via asmdef version defines; full `JsonSerializerSettings` contract control, hex-string GUIDs.
  - Samples (`Samples~`, imported from Package Manager): **System.Text.Json** (`Utf8JsonWriter` buffer-native, stack-formatted hex GUIDs), **MessagePack** (raw 16-byte GUIDs; `mpc` resolvers required for IL2CPP), **MemoryPack** (buffer-native both ways, raw unmanaged GUIDs; shards must be `[MemoryPackable] partial`), **protobuf-net** (auto public-member mapping, fixed64 GUID surrogate). Each ships its own asmdef + install README.
- **Cloud storage** (optional, `Persistence.Storage.CloudSave`):
  - `CloudSaveStorage` — `IStorage` over the UGS Cloud Save Files API, gated on `com.unity.services.cloudsave`. Caller-initialized (no auth handling; guards against a signed-out player); remaps `/` in keys to a reserved character so `MultiFileSaveLayout` works on the cloud.
- **Editor**: `SerializableGuid` property drawer with regenerate and copy-to-clipboard buttons.
- **Tests**: round-trips (0–1000 shards, both storage backends), incremental-save dirty accounting, envelope cache reuse/invalidation, background-serialization round-trip, blob migration with type rename, broken/cyclic chain detection, codec truncation fuzzing at every byte offset, whole-file bit-flip checksum sweep, `FileStorage` crash-recovery scenarios.
- Dependencies: `com.cysharp.unitask` 2.3.3, `com.unity.collections` 2.1.4, `com.unity.burst` 1.8.0; Unity 2022.3+.

[0.3.0]: https://github.com/Saesentsessis/Unity-Data-Shards/compare/0.2.1...0.3.0
[0.2.1]: https://github.com/Saesentsessis/Unity-Data-Shards/compare/0.2.0...0.2.1
[0.2.0]: https://github.com/Saesentsessis/Unity-Data-Shards/compare/0.1.0...0.2.0
[0.1.0]: https://github.com/Saesentsessis/Unity-Data-Shards/releases/tag/0.1.0
