# System.Text.Json Serializer

An `ISerializer` for Unity Data Shards backed by **System.Text.Json**, with a zero-allocation
`SerializableGuid` ⇄ hex-string converter.

## Prerequisite

System.Text.Json is not bundled with Unity. Install it as an auto-referenced managed plugin, e.g.
via [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity):

```
NuGet → Manage NuGet Packages → search "System.Text.Json" → Install
```

Ensure the imported `System.Text.Json.dll` (and its dependencies) is marked **Auto Referenced** in
its plugin importer so this sample's assembly can see it.

## Usage

```csharp
using Saesentsessis.Persistence;
using Saesentsessis.Persistence.Layout;
using Saesentsessis.Persistence.Serialization.SystemTextJson;
using Saesentsessis.Persistence.Storage;

var serializer = new SystemTextJsonSerializer();
var manager = new SaveManager(serializer, new SingleFileSaveLayout(new FileStorage()));
```

Pass a configured `JsonSerializerOptions` to the constructor for custom naming policies,
converters, etc. — the `SerializableGuid` converter is added automatically if you don't supply one.

## Shard field visibility

Unlike `UnityJsonSerializer` (which serializes private `[SerializeField]` fields like `JsonUtility`),
System.Text.Json serializes **public** members. This serializer enables `IncludeFields`, so public
shard fields round-trip — but a **private `[SerializeField]` field** (e.g. a private `id` behind a
get-only `Identifier`) will not deserialize with the defaults.

Two options:
- Make serialized shard fields public, or expose a `{ get; set; }` property; or
- Pass a `JsonSerializerOptions` with a `DefaultJsonTypeInfoResolver` modifier that adds the private
  fields you need (System.Text.Json 8+):

```csharp
var options = new JsonSerializerOptions
{
    IncludeFields = true,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver
    {
        Modifiers =
        {
            info =>
            {
                foreach (var f in info.Type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                    if (f.IsDefined(typeof(UnityEngine.SerializeField), false))
                    {
                        var prop = info.CreateJsonPropertyInfo(f.FieldType, f.Name);
                        prop.Get = f.GetValue;
                        prop.Set = f.SetValue;
                        info.Properties.Add(prop);
                    }
            }
        }
    }
};
var serializer = new SystemTextJsonSerializer(options);
```

## IL2CPP note

This serializer uses reflection (Unity cannot run the System.Text.Json source generator). On IL2CPP
builds, preserve your shard types and their members from managed stripping with a `link.xml` or
`[Preserve]` attributes, or serialization will fail at runtime.
