# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.5.0] - 2026-07-31

### Changed

- **BREAKING:** an `IStorageTransform` instance now belongs to exactly one `TransformStorage`, which disposes it. Sharing one between chains is no longer supported — transforms carry per-operation scratch state (the cipher's IV and arena, the decorator's ping-pong buffers), so two storages driving one instance interleave through it. Build a fresh transform per storage. Callers who disposed their own transforms should stop.
- **BREAKING:** `ISaveTransform` renamed to `IStorageTransform`. The old name described the wrong unit of work: a transform runs once **per storage key**, not per save, so a `MultiFileSaveLayout` save of N shards invokes it N+1 times on individual shard blobs. Migration is one line per implementation; both method signatures are unchanged. Its docs are corrected to match, including that the contract is reversibility rather than purity — `Apply` may be non-deterministic, so an encrypting transform can emit a fresh IV per call.
- **BREAKING:** `SaveManager.DeleteAsync` is now `async`. Cache eviction and the pipeline delete have to sit in one gated section, or a concurrent save repopulates the cache for a slot being deleted. Unchanged for `await`ing callers.
- **BREAKING:** `SaveEnvelope.Create` and the envelope's count/timestamp setters are `internal`. They hand out pooled arrays behind a `[Conditional]` bounds assert — defensible for our own code, not for a public API.
- `MultiFileSaveLayout` deletes the shard files a membership change orphans, instead of leaving them until the slot is deleted. It remembers the membership last seen on disk — from a read *or* a write, so load/remove/save is self-cleaning — and sweeps **after** the envelope is committed, since leaking a file is recoverable and dangling a committed envelope over a deleted shard is not. An unchanged membership rents nothing.
- Concurrency is now governed by **two locks at two scopes**, where 0.4.0 had none. `SlotGate` (per `SaveManager`, keyed by slot) makes one-operation-per-slot an enforced contract and guards that manager's envelope cache. `StorageGate` (static, keyed by resolved absolute path) guards the filesystem, which is process-global: two `FileStorage` instances over one directory name the same files, and a lock held on either could never see the other — so they meet on resource identity instead. Reads take it as well as writes, since a read overlapping the tmp/bak rename can have the file move out from under the handle. Keys compare case-insensitively everywhere.
  - `SlotGate` compiles to nothing without `ENABLE_PERSISTENCE_SAFE_CONCURRENCY`, degrading to a `[Conditional]` overlap detector under `ENABLE_PERSISTENCE_INTEGRITY_CHECKS`. `StorageGate` also compiles in whenever `UNITY_EDITOR` is defined, because the Save Viewer can refresh mid-save. Two *processes* over one directory remain out of scope.
- `PlayerPrefsStorage` flushes after every write by default (`Options.FlushOnWrite`). Unity's opportunistic flush could otherwise drop a save the caller was told had succeeded; `PlayerPrefs.Save()` is a synchronous main-thread write of the whole blob, so it is opt-out.
- `ShardStore` logs an error when Unity deserialization produces two shards sharing an identifier — an Inspector-duplicated element, the only route past the duplicate rejection in `Add`. It logs rather than throws because an exception out of `OnAfterDeserialize` cannot be caught by the caller and takes the Inspector with it.

### Added

- **Save-slot listing** — `IListableStorage` reports which keys a storage holds (`StorageKeyInfo`: key, size, last-modified ticks), `ISlotKeyMapper` maps a key back to its slot, and `SaveSlotBrowser` composes the two into `SaveSlotInfo` per slot plus an on-demand `SaveSlotHeader`. Enough for an in-game "load game" screen. Implemented by `FileStorage` and `CloudSaveStorage`, forwarded by `TransformStorage`, and by `PlayerPrefsStorage` on some platforms (below).
  - An **optional capability**, detected with `storage is IListableStorage` — folding it into `IStorage` would have broken every existing implementation. Both layouts implement `ISlotKeyMapper`.
  - **Two-phase on purpose:** `PopulateAsync` reads nothing, while `ReadHeaderAsync` costs a full read of the slot — a folder of two hundred saves would otherwise read two hundred files to draw one screen. Header decoding needs no layout, since every layout writes the envelope at offset 0 of the slot key, and a transform chain reverses on the way.
  - **Nothing but cancellation escapes `ReadHeaderAsync`**: corruption, I/O failure and a rejected payload all report through `SaveSlotStatus`, including the new `Unreadable` (bytes that never arrived, as against `Corrupted` — arrived and did not decode). Timestamps are total, so a hostile tick count reads as "no timestamp" (`HasTimestamp` false) rather than throwing out of a save list.
- **Save Viewer window** (`Tools/Saesentsessis/Persistence/Save Viewer`) — lists a storage's slots with size, key count and last-modified, and decodes the selected slot's envelope header. A thin shell over `SaveSlotBrowser`, so everything it shows is available at runtime. Storage and layout are picked as descriptors on the window itself, so several windows can each point at a different backend, and the configuration survives domain reloads and restarts.
- **`PlayerPrefsStorage` can list its slots** on Windows (player and editor), macOS, Linux and Android. Unity exposes no key enumeration, so each platform reads the store Unity itself writes to: the registry under `HKCU\Software\…`, the `NSUserDefaults` binary plist, `~/.config/unity3d/…/prefs`, and `SharedPreferences` named `<package>.v2.playerprefs`. Values still go through `PlayerPrefs`.
  - **A non-empty postfix is required**, and listing without one throws: PlayerPrefs is a shared namespace, so with no postfix every unrelated setting would come back as a save slot.
  - **Sizes and timestamps report 0.** Measuring a size means reading every save's full payload to draw a list, and no prefs store records a per-key modification time.
  - **Not supported:** iOS/tvOS, WebGL and consoles, which throw `NotSupportedException` naming the platform. Use `FileStorage` there — on consoles that is the right backend regardless, since certification wants explicit save-data mount and commit.
- **Sample: Android PlayerPrefs Reader** — a Java helper (no `.aar`, no dependency, no manifest entry) that `PlayerPrefsStorage` **requires** to list slots on Android. It applies the postfix filter on the Java side of JNI, so only matching keys cross: one managed string per *match* rather than per stored key. There is deliberately no managed fallback, and the missing-plugin error notes the APK must be rebuilt after importing.
- **Configuration descriptors** (`Saesentsessis.Persistence.Configuration`) — a serializable, inspector-editable recipe for a backend. `IStorageDescriptor`, `ITransformDescriptor` and `ISaveLayoutDescriptor` each expose a single `Create()`, implemented for both layouts, `FileStorage`, `PlayerPrefsStorage`, `TransformStorage`, `XorTransform` and `AesCbcHmacTransform`. A storage is a live resource — caches, gates, `IDisposable`, a constructor that touches main-thread Unity APIs — so what serializes is the recipe, not the object. `TransformStorageDescriptor` nests, so a whole `TransformStorage(FileStorage, Deflate, Aes)` chain is assembled through `[SerializeReference]` fields; `Create()` returns a fresh instance the caller owns, and disposing it releases the chain.
  - `AesCbcHmacTransformDescriptor` takes a **path to a key file**, not key bytes: a key typed into an inspector field lands in the asset and in whatever that asset is committed to. The file is read only when the transform is built, and the bytes are zeroed once subkeys are derived.
- **Storage transforms** — `TransformStorage` decorates any backend with a reversible chain:
  - `DeflateTransform` — compression over `System.IO.Compression`, no third-party dependency, and no intermediate buffer in either direction.
  - `AesCbcHmacTransform` — AES-256-CBC with an HMAC-SHA256 tag in encrypt-then-MAC order, framed `[IV:16][ciphertext][HMAC:32]`. The tag covers `IV || ciphertext` and is verified in constant time *before* anything is decrypted; a mismatch throws `SaveCorruptedException`. Independent cipher and MAC subkeys from one master key, plus a PBKDF2 passphrase overload. `AesGcm` was rejected: it compiles everywhere but throws `PlatformNotSupportedException` at runtime on iOS, tvOS and WebGL.
  - `XorTransform` — dependency-free byte masking. Its own inverse; obfuscation only, no security value.
  - All compression transforms share a `[originalLength:4 LE][compressed]` framing, and check the declared length against the public `TransformLimits` before reserving anything.
- Two samples, each with its own asmdef and install README: **LZ4 Compression Transform** (fast, modest ratio) and **Zstandard Compression Transform** (better ratio, slower). Both pure managed C#, so neither ships native binaries.
- `IIncrementalSaveLayout` — an optional layout capability reporting which shard blobs it already holds for a slot, so `SaveManager` can write a clean shard whose blob is missing. Implemented by `MultiFileSaveLayout`. A layout that does not implement it is assumed to hold nothing: safe, but it writes every shard on every save, which is announced once per layout in the editor and development builds.
- `PooledArrayBufferWriter` takes an opt-in `clearOnRelease`, zeroing the backing array before it returns to the pool — on **growth** as well as dispose, since growth is where an array escapes mid-lifetime. `AesCbcHmacTransform` sets it, so decrypted save data never reaches the next renter.
- `PlayerPrefsStorage` enforces per-platform value budgets against the base64 string it actually stores. **tvOS** (Apple warns at 512 KB, terminates at 1 MB) and **iOS** (13+ rejects ≥ 4 MiB) are real ceilings, so a write past them throws `IOException` before PlayerPrefs is called. Everywhere else has no documented ceiling, so Unity's 2 KB recommendation is logged once per key under `ENABLE_PERSISTENCE_INTEGRITY_CHECKS` and never throws. Note the Apple limits cover the whole defaults store, so passing is necessary rather than sufficient.
- `CloudSaveStorage` checks the two Cloud Save quotas a single call can see: a payload over 1 GiB and a key over the 255-character filename limit are both rejected before the upload spends the player's bandwidth. The per-player totals (1 GiB overall, **200 files**) need a round trip, so UGS reports those.

### Fixed

- **CRITICAL:** an incremental save could commit an envelope referencing shard files that were never written, producing a save that failed to load. Only dirty shards were serialized, on the assumption that the layout still held the rest — which the layout was never asked to confirm. **Loading one slot and saving it into another** hit this in 0.4.0: loading clears every dirty flag, so the destination received an envelope with no blobs at all. Capture now asks the layout what it holds (`IIncrementalSaveLayout`) and writes any shard it cannot account for. An unchanged shard set is still settled by one ordered pass, so the steady state is unaffected.
- **CRITICAL:** `FileStorage` did not confine a storage key to the save root. `Path.Combine` returns its second argument verbatim when rooted, so a key like `C:\Windows\evil` simply *became* the path being read or written, and because `Path.Combine` does not normalize, `a/../../evil` survived intact. The `../` rejection that existed was `[Conditional("ENABLE_PERSISTENCE_INTEGRITY_CHECKS")]`, so it compiled away in any build without that define — including CI and most release configurations. Confinement is now decided on `Path.GetFullPath` of the combined path, compared ordinally against a root ending in a separator, and is **never** conditional.
- Both layouts sized their envelope buffer with a flat allowance and reallocated past it. Records are 20 bytes each, so `SingleFileSaveLayout`'s 1 KB ran out at roughly **47 shards** — at the worst moment, since the payload is reserved last, doubling a payload-sized arena with the old one still alive. `MultiFileSaveLayout` had the same flaw in the scratch it reuses for the envelope. Both now reserve exactly, via the new `EnvelopeCodec.MaxEncodedSize`.
- `StorageReadResult.Found` is derived from `Data.IsCreated` rather than stored separately. A zero-length payload used to report `Found == true` beside an uncreated `NativeArray`, which throws under `ENABLE_UNITY_COLLECTIONS_CHECKS` as soon as a caller honors the documented "you own it, dispose it" contract.
- `SaveManager`'s null/empty slot check is no longer `[Conditional]`. Validating caller input on a public entry point is not an assert about our own data: with the define off, a null slot failed deep in a backend with a message that said nothing about the real mistake.
- `SaveManager.Dispose` returns its cached envelopes' pooled arrays to the pool; it previously cleared the cache and dropped them.
- `FileStorage.ExistsAsync` and `DeleteAsync` performed their filesystem calls synchronously despite returning a task. Both now go through the thread pool.
- `PlayerPrefsStorage.Dispose` threw a `NullReferenceException` in the default configuration: the key cache is only allocated when a postfix is set.

### Documentation

- **Corrected:** single-file packing was described as "a straight concatenation with no re-copy". `SingleFileSaveLayout` copies the whole payload once, into a second arena as large as itself, so peak unmanaged memory during a save is about twice the payload. That is structural — the envelope must precede the payload and its size is unknown until serialization ends, while `IStorage.WriteAsync` takes one contiguous array — and it buys the atomic whole-slot write. `MultiFileSaveLayout` moves the same bytes but never holds more than one blob. Both READMEs now compare them directly, and "two allocations per save" is qualified as the pipeline's two, with the layout's buffer named separately.
- **Corrected:** the README no longer claims the two safety defines gate path confinement or caller-input validation.
- Both READMEs gain a **Contributing** section, plus new sections on compression/encryption, configuring a backend from the Inspector, choosing a storage backend per platform, listing PlayerPrefs slots, and the Cloud Save quotas — including that the **200-file per-player cap argues against `MultiFileSaveLayout` in the cloud**, since a slot of N shards costs N+1 of the 200.


## [0.4.0] - 2026-07-28

### Changed

- **BREAKING:** envelope format **v4**. The header is now a fixed 32-byte block — `[Checksum:8][FormatVersion:4][Magic:4][TimestampUtc:8][TypeCount:4][RecordCount:4]` — followed by the type table and the record block. Only the checksum sits outside the hashed region, so the version and the magic are now covered by it; every field lands on its natural alignment; and both counts precede all variable-length data.
- **BREAKING:** format **v3 is no longer readable**. Saves written by 0.3.x are refused with `SaveCorruptedExceptionReason.UnsupportedVersion` and cannot be upgraded — delete or re-generate them. v3 placed the version *outside* the checksummed region (so it could be altered undetected) and split `TypeCount` from `RecordCount` across the variable-length type table (so neither the body size nor the record offset was knowable up front). Both were corrected by a clean break rather than a compatibility shim; `SaveEnvelopeV3` is deleted.
- **BREAKING:** the envelope format is now explicitly **little-endian only**. The header and the record block are transferred as raw struct memory, so their in-memory layout is the wire layout. Every Unity target is little-endian; a big-endian host now throws `PlatformNotSupportedException` instead of silently writing unreadable files. Variable-length fields still go through `BinaryPrimitives`.
- The record block is written and read as a single memcpy instead of field-by-field, and the header as one 32-byte store. `ShardRecord` is pinned to a 20-byte wire stride via `Pack = 4` rather than an undersized explicit `Size`, which no runtime is obliged to honor — a Mono/IL2CPP disagreement there would have made editor-written saves unreadable in a player build.
- `SaveEnvelope.Types`/`Records` now slice to the logical counts, so a pooled backing array's tail can no longer leak into a save or to a consumer.
- Wire types (`SaveEnvelopeHeader`, `SaveEnvelopeHeaderExtensions`) are `internal`: they are format details, not API.

### Added

- A `"SHRD"` magic tag in the header, validated before the version, so data that was never a Data Shards save is reported as `SaveCorruptedExceptionReason.InvalidMagic` rather than as a corrupt or unsupported save. It is also readable as ASCII in a hex dump.
- `SaveCorruptedExceptionReason.InvalidMagic`.
- An up-front plausibility check: because both counts now precede the body, the decoder rejects counts larger than the remaining bytes could possibly describe *before* allocating any array — a hostile count in a few dozen bytes of input can no longer drive a multi-megabyte allocation.
- `[Conditional("ENABLE_PERSISTENCE_INTEGRITY_CHECKS")]` guards on `SaveEnvelope.Create`, rejecting a logical count larger than its backing array (which would otherwise publish adjacent pool memory into the file).
- Codec tests: wire-layout size assertions (guarding IL2CPP divergence), magic emission and rejection, foreign-buffer rejection, unsupported-version rejection, hostile-count rejection, empty-envelope round-trip, and per-offset truncation fuzzing.

### Removed

- `SaveEnvelopeV3` and the v3 read path, along with the now-unused `ReadLong`/`ReadULong` decoders.
- The dead `TimestampUtc` write in the header factory — the pipeline stamps it at write time, which is the only correct moment given envelopes are cached and reused across saves.

## [0.3.1] - 2026-07-25

### Added

- Toggleable integrity safety checks inside pipeline. All optional validation may be disabled by toggling `Tools/Saesentsessis/Persistence/Integrity Checks` menu item.
- Toggleable safe concurrency checks inside pipeline. All safe concurrency may be disabled by toggling `Tools/Saesentsessis/Persistence/Safe Concurrency` menu item.
- `FileStorage` now validates rootDirectory, fileExtension and keys, so they don't point out of the root directory (a key containing `../` is rejected). Gated under `ENABLE_PERSISTENCE_INTEGRITY_CHECKS`.
- `IStorage`, `ISaveLayout`, `IManagedSaveLayout`, `StorageReadResult`, `ShardStore` and `SaveManager` now implement `IDisposable`, so a whole pipeline can be released deterministically.
- `SaveManager` now rejects a null or empty `slot` up front on `SaveAsync`/`LoadAsync`/`ExistsAsync`/`DeleteAsync`, instead of failing deep inside a storage backend.
- `SaveManager` now detects a record/blob-range id mismatch on load and throws `SaveCorruptedException` (`CorruptedLayout`) rather than deserializing a shard against the wrong blob.
- `SaveCorruptedException` carries a `SaveCorruptedExceptionReason` enum describing why the save was rejected (checksum mismatch, unsupported version, truncation, count overflow, missing/too-large file, corrupted layout, …).

### Fixed

- **CRITICAL:** `SerializedTypeHelper.Resolve` was able to pass **any arbitrary type** when shards were deserialized, potentially causing **severe damage** to the application. Now instantiation is hard bounded to be derived from `IDataShard`.

### Changed

- `SerializedTypeHelper.Resolve` no longer mutates strings when hitting a new type not present inside the cache. All string data mutation is now sitting inside `SerializedType.ToString` method. No extra allocations was required.
- `UnsafeStringUtils` is now an internal class, because CLR does not allow string mutation by design. You should avoid its usage and move to a safer alternative as `string.Create()`.
- **BREAKING (custom layouts):** `SaveEnvelope` now exposes `Types` and `Records` as `ReadOnlySpan<>` properties over private backing arrays, built through the new `SaveEnvelope.Create` factory, rather than as public mutable array fields. Code that reads `envelope.Types` / `envelope.Records` is unaffected; code that assigned those fields directly must use `Create`.

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

[0.5.0]: https://github.com/Saesentsessis/Unity-Data-Shards/compare/0.4.0...0.5.0
[0.4.0]: https://github.com/Saesentsessis/Unity-Data-Shards/compare/0.3.1...0.4.0
[0.3.1]: https://github.com/Saesentsessis/Unity-Data-Shards/compare/0.3.0...0.3.1
[0.3.0]: https://github.com/Saesentsessis/Unity-Data-Shards/compare/0.2.1...0.3.0
[0.2.1]: https://github.com/Saesentsessis/Unity-Data-Shards/compare/0.2.0...0.2.1
[0.2.0]: https://github.com/Saesentsessis/Unity-Data-Shards/compare/0.1.0...0.2.0
[0.1.0]: https://github.com/Saesentsessis/Unity-Data-Shards/releases/tag/0.1.0
