# MemoryPack Serializer

An `ISerializer` for Unity Data Shards backed by **MemoryPack** — Cysharp's zero-encoding binary
serializer. `IBufferWriter<byte>`-native on write, span-native on read: the tightest performance
fit for the arena pipeline. The `SerializableGuid` formatter writes the id as raw unmanaged bytes.

## Prerequisite

Install MemoryPack for Unity (it is not bundled). Add both the runtime and its **source generator**
via git UPM URL (see the [MemoryPack README](https://github.com/Cysharp/MemoryPack#unity)):

```
https://github.com/Cysharp/MemoryPack.git?path=src/MemoryPack.Unity/Assets/Plugins/MemoryPack.Unity
```

Ensure `MemoryPack.Core.dll` is **Auto Referenced** and the Roslyn source generator is active.

## Requirement: shards must be `[MemoryPackable] partial`

MemoryPack is source-generated — it cannot serialize arbitrary POCOs. Every shard type you persist
with this serializer must be annotated:

```csharp
using MemoryPack;
using Persistence.Core;

[MemoryPackable]
[ShardSchema(1)]
public partial class PlayerShard : IDataShard
{
    public SerializableGuid Identifier { get; set; }
    public int Level;
    public float Health;

    [MemoryPackIgnore] public bool IsDirty => true;  // or your own tracking
}
```

## Usage

```csharp
using Persistence;
using Persistence.Layout;
using Persistence.Serialization.MemoryPack;
using Persistence.Storage;

var serializer = new MemoryPackShardSerializer();
var manager = new SaveManager(serializer, new SingleFileSaveLayout(new FileStorage()));
```

No extra AOT/IL2CPP steps are needed — MemoryPack does all its work at compile time.
