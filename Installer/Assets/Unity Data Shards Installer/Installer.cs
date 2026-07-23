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

namespace Saesentsessis.Persistence.Installer
{
    [InitializeOnLoad]
    public static partial class Installer
    {
        public const string PackageId = "com.saesentsessis.unity-data-shards";
        public const string Version = "0.2.1";

        static Installer()
        {
#if !IVAN_MURZAK_INSTALLER_PROJECT
            AddScopedRegistryIfNeeded(ManifestPath);
#endif
        }
    }
}