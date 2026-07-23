# protobuf-net Serializer

An `ISerializer` for Unity Data Shards backed by **protobuf-net** — a contract-based Protocol Buffers
implementation for plain C# objects (unlike Google.Protobuf, no `.proto` files or codegen). The
`SerializableGuid` surrogate stores the id as two raw fixed64 values.

## Prerequisite

Install protobuf-net (v3+) as an auto-referenced managed plugin, e.g. via
[NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity):

```
NuGet → Manage NuGet Packages → search "protobuf-net" → Install
```

Ensure `protobuf-net.dll` (and `protobuf-net.Core.dll`) is marked **Auto Referenced**.

## Usage

```csharp
using Saesentsessis.Persistence;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Serialization.ProtobufNet;
using Saesentsessis.Persistence.Storage;

var serializer = new ProtobufNetSerializer();
var manager = new SaveManager(serializer, new SingleFileSaveLayout(new FileStorage()));
```

Shard types do **not** need `[ProtoContract]` attributes — this serializer auto-registers each shard
type's public instance fields and read/write properties on first use.

> [!WARNING]
> Protobuf is positional: field numbers are assigned from member metadata order. If you reorder,
> remove, or change the type of a shard's public members between builds, old saves will decode
> incorrectly — treat such changes as a schema migration (`IShardMigration`). For explicit control,
> annotate your shards with `[ProtoContract]` / `[ProtoMember(n)]` and construct the serializer with
> your own `RuntimeTypeModel`.

## IL2CPP note

protobuf-net uses reflection. On IL2CPP builds, preserve your shard types and members from managed
stripping with a `link.xml` or `[Preserve]` attributes.
