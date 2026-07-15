/*
┌────────────────────────────────────────────────────────────────────────────┐
│  Author: Saesentsessis (https://github.com/Saesentsessis)                  │
│  Repository: GitHub (https://github.com/Saesentsessis/Unity-Package-Template) │
│  Copyright (c) 2025–2026 Saesentsessis                                     │
│  Licensed under the MIT License.                                           │
│  See the LICENSE file in the project root for more information.            │
└────────────────────────────────────────────────────────────────────────────┘
*/
#nullable enable
using UnityEditor;

namespace YOUR_PACKAGE_ID.Installer
{
    [InitializeOnLoad]
    public static partial class Installer
    {
        public const string PackageId = "YOUR_PACKAGE_ID_LOWERCASE";
        public const string Version = "1.0.0";

        static Installer()
        {
#if !IVAN_MURZAK_INSTALLER_PROJECT
            AddScopedRegistryIfNeeded(ManifestPath);
#endif
        }
    }
}