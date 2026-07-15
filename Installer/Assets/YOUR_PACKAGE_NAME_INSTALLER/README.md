# YOUR_PACKAGE_NAME Installer

Editor scripts for this template’s `.unitypackage` installer. At install time they can add OpenUPM scoped registries and the package dependency to the host project’s `manifest.json`.

This file lives under the Installer Unity project, **not** inside the UPM package root (`Unity-Package/Assets/root`).

## Template usage

Follow the main guide: [Unity Package Template README](https://github.com/Saesentsessis/Unity-Package-Template/blob/main/README.md).

Per-repository setup:

```powershell
./commands/init.ps1 -PackageId "com.company.package" -PackageName "My Package" -GitHubRepository "YourGitHubUsername/your-repo-name"
```

Manual rename reference (if needed): [docs/Manual-Package-Rename.md](../../../docs/Manual-Package-Rename.md)

## CI export

Release CI calls `YOUR_PACKAGE_ID.Installer.PackageExporter.ExportPackage` via GameCI `unity-builder@v5` to produce `YOUR_PACKAGE_NAME_INSTALLER_FILE.unitypackage`.

## Deploy docs

- [Deploy to OpenUPM](../../../docs/Deploy-OpenUPM.md)
- [Deploy using GitHub](../../../docs/Deploy-GitHub.md)
- [Deploy to npmjs.com](../../../docs/Deploy-npmjs.md)
- [OpenUPM signing](../../../docs/openupm-signing.md)

Based on [Ivan Murzak’s Unity-Package-Template](https://github.com/IvanMurzak/Unity-Package-Template). Maintained by Saesentsessis under MIT.
