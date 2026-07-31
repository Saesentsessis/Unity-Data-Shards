# LZ4 Compression Transform

An `IStorageTransform` for Unity Data Shards backed by **[K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4)**
— a pure managed C# LZ4 implementation (MIT). Fast both ways with a modest ratio: the usual right
trade for save data, where load latency matters more than a few extra kilobytes on disk.

Unlike the built-in `DeflateTransform`, this is pure C# with no reliance on Unity's BCL compression
stack, so it carries none of that path's IL2CPP risk.

## Prerequisite

`K4os.Compression.LZ4` is not bundled. Install it via
**[NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity)**:

1. Install NuGetForUnity (`https://github.com/GlitchEnzo/NuGetForUnity.git?path=/src/NuGetForUnity`).
2. `NuGet ▸ Manage NuGet Packages`, search **K4os.Compression.LZ4**, install.
3. Ensure `K4os.Compression.LZ4.dll` is **Auto Referenced** in the inspector.

## Usage

```csharp
using K4os.Compression.LZ4;
using Saesentsessis.Persistence.Storage;
using Saesentsessis.Persistence.Storage.LZ4;

var storage = new TransformStorage(new FileStorage(), new LZ4Transform());
var manager = new SaveManager(new UnityJsonSerializer(), new SingleFileSaveLayout(storage));
```

Combined with encryption — **compress first, then encrypt**, since ciphertext does not compress:

```csharp
var storage = new TransformStorage(
    new FileStorage(),
    new LZ4Transform(),                 // Apply runs in declaration order
    new AesCbcHmacTransform(key));      // Reverse runs in reverse order
```

Pick a level with the constructor; `L00_FAST` is the default:

```csharp
new LZ4Transform(LZ4Level.L12_MAX)      // smaller output, much slower compression
```

## Wire format

```text
[originalLength:4 LE][lz4 block]
```

The length prefix is **mandatory**: `LZ4Codec.Decode` needs the exact output size up front, and a raw
LZ4 block carries no length of its own.

## Notes

- **The unit of work is one storage key, not one save.** Under `SingleFileSaveLayout` the transform
  sees the whole packed save; under `MultiFileSaveLayout` it runs once per file and sees an
  individual shard blob. Small blobs compress worse — expect a poorer ratio on multi-file layouts.
- Incompressible input can make LZ4 output slightly *larger* than the input. That is normal and
  handled: the buffer is reserved via `LZ4Codec.MaximumOutputSize`.
- A corrupt or truncated payload throws `SaveCorruptedException`, matching the rest of the pipeline.
