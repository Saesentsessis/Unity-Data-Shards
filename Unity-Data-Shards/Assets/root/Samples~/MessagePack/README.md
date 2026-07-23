# MessagePack Serializer

An `ISerializer` for Unity Data Shards backed by **MessagePack-CSharp**, with a zero-allocation
`SerializableGuid` formatter that stores the id as a raw 16-byte payload.

## Prerequisite

Install MessagePack-CSharp for Unity (it is not bundled). Either:

- Import the `MessagePack.Unity` package from the
  [MessagePack-CSharp releases](https://github.com/MessagePack-CSharp/MessagePack-CSharp/releases), or
- Add it via git UPM URL:
  ```
  https://github.com/MessagePack-CSharp/MessagePack-CSharp.git?path=src/MessagePack.UnityClient/Assets/Scripts/MessagePack
  ```

Ensure the `MessagePack.dll` (and `MessagePack.Annotations.dll`) is **Auto Referenced**.

## Usage

```csharp
using Saesentsessis.Persistence;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Serialization.MessagePack;
using Saesentsessis.Persistence.Storage;

var serializer = new MessagePackShardSerializer();
var manager = new SaveManager(serializer, new SingleFileSaveLayout(new FileStorage()));
```

Your shard types must be MessagePack-serializable (`[MessagePackObject]` with `[Key]`s, or
configured for contractless/typeless resolution via a custom `MessagePackSerializerOptions`).

## Shard field visibility

MessagePack serializes the members its resolver sees — for the contractless resolver that means
**public** members. A shard with a **private `[SerializeField]` field** behind a get-only property
(e.g. a private `id`) will not round-trip that field. Either make serialized fields public / add
settable properties, or annotate the shard with `[MessagePackObject]` + `[Key(n)]` (including the
private members) and pass matching `MessagePackSerializerOptions`.

## AOT / IL2CPP note (important)

MessagePack relies on runtime IL generation, which **does not exist on IL2CPP**. You must run the
`mpc` code generator to emit a static resolver and register it before use:

```
dotnet tool install --global MessagePack.Generator
mpc -i ./YourUnityProject -o ./Assets/Generated/MessagePackResolver.cs
```

Then compose the generated resolver into the options you pass to `MessagePackShardSerializer`.
Without this step, saves/loads throw on device even though they work in the Editor.
