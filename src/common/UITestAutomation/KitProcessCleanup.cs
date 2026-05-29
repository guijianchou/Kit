// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Microsoft.PowerToys.UITest
{
    internal static class KitProcessCleanup
    {
        public static void KillKnownKitProcesses()
        {
            foreach (PowerToysModule module in Enum.GetValues<PowerToysModule>())
            {
                KillByExecutablePath(ModuleConfigData.Instance.GetModulePath(module));
            }
        }

        public static bool KillKnownKitProcessesByName(string processName, Action<Process, Exception>? onError = null)
        {
            var killedKnownName = false;
            foreach (PowerToysModule module in Enum.GetValues<PowerToysModule>())
            {
                var executablePath = ModuleConfigData.Instance.GetModulePath(module);
                var executableName = Path.GetFileNameWithoutExtension(executablePath);
                if (!string.Equals(executableName, processName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                killedKnownName = true;
                KillByExecutablePath(executablePath, onError);
            }

            return killedKnownName;
        }

        public static void KillByExecutablePath(string executablePath, Action<Process, Exception>? onError = null)
        {
            var resolvedPath = ResolveExecutablePath(executablePath);
            if (string.IsNullOrEmpty(resolvedPath))
            {
                return;
            }

            var processName = Path.GetFileNameWithoutExtension(resolvedPath);
            if (string.IsNullOrWhiteSpace(processName))
            {
                return;
            }

            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var processPath = process.MainModule?.FileName;
                        if (!PathMatches(processPath, resolvedPath))
                        {
                            continue;
                        }

                        process.Kill();
                        process.WaitForExit();
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        onError?.Invoke(process, ex);
                    }
                }
            }
        }

        private static string ResolveExecutablePath(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return string.Empty;
            }

            var expandedPath = Environment.ExpandEnvironmentVariables(executablePath);
            if (!Path.IsPathFullyQualified(expandedPath))
            {
                expandedPath = Path.Combine(
                    AppContext.BaseDirectory,
                    expandedPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            return Path.GetFullPath(expandedPath);
        }

        private static bool PathMatches(string? processPath, string expectedPath)
        {
            if (string.IsNullOrWhiteSpace(processPath) || string.IsNullOrWhiteSpace(expectedPath))
            {
                return false;
            }

            return string.Equals(
                Path.GetFullPath(processPath),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
