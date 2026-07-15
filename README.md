# [Unity Package Template](https://github.com/Saesentsessis/Unity-Package-Template)

Unity Editor supports NPM-style packages through UPM (Unity Package Manager). Versioning and dependencies make packages far more flexible than classic plugins.

This template turns a fresh repository into a real Unity package: scaffold, multi-version tests, installer `.unitypackage`, GitHub Actions CI/CD, and OpenUPM-ready signed releases.

Based on [Ivan Murzak’s Unity-Package-Template](https://github.com/IvanMurzak/Unity-Package-Template). This fork is maintained by **Saesentsessis** under the MIT license.

Template copyright on `LICENSE` is held by Saesentsessis. After you run `init.ps1`, set the package `author` in `package.json` to your own credentials.

# Steps to make your package

### 1. Create a repository from this template

Use GitHub **Use this template** on [Saesentsessis/Unity-Package-Template](https://github.com/Saesentsessis/Unity-Package-Template), or fork and clone as you prefer.

### 2. Clone your new repository

### 3. Initialize the project

Run the initialization script to rename the package and replace all placeholders. Package id and assembly root are chosen **per repository** — never bake product identity into the template itself.

```powershell
./commands/init.ps1 `
  -PackageId "com.company.package" `
  -AssemblyName "MyPackage" `
  -PackageName "My Package" `
  -GitHubRepository "YourGitHubUsername/your-repo-name"
```

| Parameter | Placeholder(s) | Purpose |
|-----------|----------------|---------|
| `PackageId` | `YOUR_PACKAGE_ID`, `YOUR_PACKAGE_ID_LOWERCASE` | UPM name (`package.json`), test manifests, release `.tgz` prefix, `Installer.PackageId` |
| `AssemblyName` | `YOUR_ASSEMBLY_NAME` | Asmdef names/filenames, C# namespaces, CI `buildMethod` |
| `PackageName` | `YOUR_PACKAGE_NAME` (+ installer variants) | Display / descriptive text and installer folder naming only |

This script will:
- Rename directories and files (including `YOUR_ASSEMBLY_NAME.*.asmdef`).
- Replace the placeholders above in file contents.

### 4. Update `package.json`

Open `Unity-Package/Assets/root/package.json` and update:
- `description`
- `author`
- `keywords`
- `unity` (minimum supported Unity version)

### 5. Generate `.meta` files

#### Using the script

Open Unity projects so Unity generates `.meta` files.

**On Mac and Linux**:

```bash
./commands/open-all-projects-unix.sh
```

**On Windows**:

```powershell
./commands/open-all-projects-windows.ps1
```

#### Or manually

- Open Unity Hub.
- Add the `Installer` folder as a project.
- Add the `Unity-Package` folder as a project.
- Open both projects in the Unity Editor to generate `.meta` files.

### 6. Add files under `Unity-Package/Assets/root`

See [Unity package layout guidelines](https://docs.unity3d.com/Manual/cus-layout.html):

```text
  <root>
  ├── package.json
  ├── README.md
  ├── CHANGELOG.md
  ├── LICENSE.md
  ├── Third Party Notices.md
  ├── Editor
  │   ├── [company-name].[package-name].Editor.asmdef
  │   └── EditorExample.cs
  ├── Runtime
  │   ├── [company-name].[package-name].asmdef
  │   └── RuntimeExample.cs
  ├── Tests
  │   ├── Editor
  │   │   ├── [company-name].[package-name].Editor.Tests.asmdef
  │   │   └── EditorExampleTest.cs
  │   └── Runtime
  │        ├── [company-name].[package-name].Tests.asmdef
  │        └── RuntimeExampleTest.cs
  ├── Samples~
  │        ├── SampleFolder1
  │        ├── SampleFolder2
  │        └── ...
  └── Documentation~
       └── [package-name].md
```

# Optional steps

### 1. Version management

To update the package version across all files (`package.json`, `Installer.cs`, etc.):

```powershell
.\commands\bump-version.ps1 -NewVersion "1.0.1"
```

### 2. Setup CI/CD

Workflows ship as opt-in samples. The reusable job is already active at [`.github/workflows/test_unity_plugin.yml`](.github/workflows/test_unity_plugin.yml).

CI uses **GameCI** (`unity-test-runner@v4`, `unity-builder@v5`), a pinned disk-cleanup action, and a matrix covering:

| Unity version   | Test modes                          | Platforms (Linux images)   |
|-----------------|-------------------------------------|----------------------------|
| `2022.3.62f3`   | editmode, playmode, standalone      | `base`, `windows-mono`     |
| `2023.2.22f1`   | editmode, playmode, standalone      | `base`, `windows-mono`     |
| `6000.3.1f1`    | editmode, playmode, standalone      | `base`, `windows-mono`     |

The release pipeline also builds the installer `.unitypackage`, signs a UPM `.tgz` (hard gate), and publishes a GitHub Release with both assets.

PR workflows use the `pull_request` event (not `pull_request_target`), so secrets are only available to trusted workflow runs from the repository.

1. **Configure GitHub Secrets**

   Go to `Settings` > `Secrets and variables` > `Actions` > `New repository secret` and add:
   - `UNITY_EMAIL`: Your Unity account email.
   - `UNITY_PASSWORD`: Your Unity account password.
   - `UNITY_LICENSE`: Content of your `Unity_lic.ulf` file.
     - Windows: `C:/ProgramData/Unity/Unity_lic.ulf`
     - Mac: `/Library/Application Support/Unity/Unity_lic.ulf`
     - Linux: `~/.local/share/unity3d/Unity/Unity_lic.ulf`

   **Package signing secrets (Unity 6.3+ signed packages)**

   Starting with Unity 6.3, Package Manager warns about unsigned packages. The release workflow signs your package via Unity's UPM CLI, and **signing is a hard gate** — if these secrets are missing or misconfigured, the release fails and no GitHub Release is created. Add three more repository secrets:

   - `UPM_ORG_ID` — your Unity **organization ID**.
   - `UPM_SERVICE_ACCOUNT_KEY_ID` — a Unity Cloud **service-account key ID**.
   - `UPM_SERVICE_ACCOUNT_KEY_SECRET` — the matching **service-account key secret** (shown only once).

   How to obtain them (in the [Unity Cloud Dashboard](https://cloud.unity.com/)):

   1. Pick the **organization** that will own/sign your package (Unity Cloud → Organizations → note its **Organization ID** → that's `UPM_ORG_ID`).
   2. **Administration → Service Accounts → Create service account.** In its **Keys** section, **Create key** → record the **Key ID** (`UPM_SERVICE_ACCOUNT_KEY_ID`) and **Secret key** (`UPM_SERVICE_ACCOUNT_KEY_SECRET`) — the secret is shown only once.
   3. Give the service account the **"Package Manager Package Signer"** role for that organization.

   **Critical — three things must line up, or signing fails with "User does not have permission to sign package … with the provided credentials and organization":**

   - The **service account must belong to the same organization** as `UPM_ORG_ID`.
   - That **organization must be authorized to sign your package's namespace** (the reverse-domain name in `package.json`, e.g. `com.company.package`).
   - A brand-new org won't be authorized for a namespace until it **claims** it. To claim it, do a **one-time interactive sign in the Unity Editor**: Package Manager → select your package → Export → in the **Authoring Org** dropdown pick that organization → sign. After that one interactive sign, the CI service account can sign the same package automatically.

   > Tip: the Unity UPM CLI can only *sign* a namespace the org already owns — it cannot *claim* one. The interactive Editor sign above is the only way to establish the association.

   See [`docs/openupm-signing.md`](docs/openupm-signing.md) for the full setup, verification, and troubleshooting guide.

2. **Enable workflows**

   Rename the sample workflow files to enable them:
   - `.github/workflows/release.yml-sample` → `.github/workflows/release.yml`
   - `.github/workflows/test_pull_request.yml-sample` → `.github/workflows/test_pull_request.yml`

3. **Adjust Unity versions (if needed)**

   Edit the matrices in both enabled `.yml` files if your support matrix differs from the defaults above.

4. **Automatic deployment**

   The release workflow runs on push to `main` when `package.json` version does not already have a matching Git tag. It tests across the full matrix, exports the installer, signs the UPM package, then creates the GitHub Release atomically.

# Final polishing

- Replace this repository `README.md` with information about your package.
- Copy the package-facing `README.md` into `Unity-Package/Assets/root` as well (a stub is provided there for you to edit).

> Everything outside of the `root` folder is not included in the distributed UPM package. It can still be used for testing or documenting the repository.

### 1. Deploy to any registry you like

- [Deploy to OpenUPM](docs/Deploy-OpenUPM.md) (recommended)
- [Deploy using GitHub](docs/Deploy-GitHub.md)
- [Deploy to npmjs.com](docs/Deploy-npmjs.md)

### 2. Install your package into a Unity project

When your package is distributed, install it into **another** Unity project (not this template’s test project).

- [Install OpenUPM-CLI](https://github.com/openupm/openupm-cli#installation)
- Open a command line at the root of the Unity project (the folder that contains `Assets`)
- Run:

  ```bash
  openupm add YOUR_PACKAGE_ID_LOWERCASE
  ```

  (After `init.ps1`, replace with your real package name / id as documented by OpenUPM.)
