# Unity Data Shards - Installer

This folder contains the lightweight installer that ships as
`Unity-Data-Shards-Installer.unitypackage` on each
[GitHub Release](https://github.com/Saesentsessis/Unity-Data-Shards/releases).

## What it does

When the `.unitypackage` is imported into a Unity project, the installer runs
once (`[InitializeOnLoad]`) and edits the project's `Packages/manifest.json` to:

1. Add the OpenUPM scoped registry (`https://package.openupm.com`) with the
   scopes required to resolve `com.saesentsessis.unity-data-shards` and its
   dependencies (`com.saesentsessis`, `com.cysharp` for UniTask).
2. Add `com.saesentsessis.unity-data-shards` to the project `dependencies`
   at the version bundled with this installer.

The version bump is one-directional: the installer never downgrades a package
that is already present at a newer version (see `ShouldUpdateVersion`).

## Installation

1. Download `Unity-Data-Shards-Installer.unitypackage` from the latest
   [release](https://github.com/Saesentsessis/Unity-Data-Shards/releases).
2. Import it into your Unity project (`Assets > Import Package > Custom Package...`).
3. Unity Package Manager resolves and installs the package automatically.

For alternative installation methods (Git URL, OpenUPM CLI) see the repository
[README](https://github.com/Saesentsessis/Unity-Data-Shards#installation).
