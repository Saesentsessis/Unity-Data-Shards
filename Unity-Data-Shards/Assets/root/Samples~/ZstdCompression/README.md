# Zstandard Compression Transform

An `IStorageTransform` for Unity Data Shards backed by **[ZstdSharp](https://github.com/oleg-st/ZstdSharp)**
— a pure managed C# port of Zstandard (no native binaries). Better compression ratio than LZ4 at a
noticeably higher CPU cost in both directions.

## Choosing between this and the LZ4 sample

|                             | LZ4                                   | Zstd                                             |
|-----------------------------|---------------------------------------|--------------------------------------------------|
| Ratio                       | modest                                | **better**                                       |
| Compress / decompress speed | **fast**                              | slower                                           |
| Best for                    | save/load latency, frequent autosaves | large saves, cloud sync, bandwidth-bound storage |

If saves are small and written often, LZ4 is almost always the right pick. Reach for Zstd when the
bytes themselves cost you — `CloudSaveStorage` uploads, or saves measured in megabytes.

## Prerequisite

`ZstdSharp.Port` is not bundled. Install it via
**[NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity)**:

1. Install NuGetForUnity (`https://github.com/GlitchEnzo/NuGetForUnity.git?path=/src/NuGetForUnity`).
2. `NuGet ▸ Manage NuGet Packages`, search **ZstdSharp.Port**, install.
3. Ensure `ZstdSharp.dll` is **Auto Referenced** in the inspector.

## Usage

```csharp
using Saesentsessis.Persistence.Storage;
using Saesentsessis.Persistence.Storage.Zstd;

var storage = new TransformStorage(new FileStorage(), new ZstdTransform());
var manager = new SaveManager(new UnityJsonSerializer(), new SingleFileSaveLayout(storage));
```

Higher level, smaller output, slower (`3` is the library default):

```csharp
new ZstdTransform(level: 9)
```

Combined with encryption — **compress first, then encrypt**, since ciphertext does not compress:

```csharp
var storage = new TransformStorage(
    new FileStorage(),
    new ZstdTransform(),
    new AesCbcHmacTransform(key));
```

## Wire format

```text
[originalLength:4 LE][zstd frame]
```

A zstd frame usually records its own content size, but that field is *optional* in the format, so
this transform does not depend on it — the explicit prefix is authoritative and lets `Reverse` size
the output buffer in a single reservation.

## Notes

- **The unit of work is one storage key, not one save.** Under `MultiFileSaveLayout` the transform
  runs once per file on an individual shard blob; small blobs compress worse than one large one.
- `Compressor` / `Decompressor` are not thread-safe, so they are constructed per call.
  `TransformStorage` runs one operation at a time per instance, which keeps this within contract.
- A corrupt or truncated payload throws `SaveCorruptedException`, matching the rest of the pipeline.
