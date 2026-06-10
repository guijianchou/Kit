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
        public readonly record struct CleanupResult(bool MatchedKnownExecutable, bool KilledAnyProcess, bool FailedAnyProcess)
        {
            public bool HandledAnyProcess => KilledAnyProcess || FailedAnyProcess;
        }

        public static void KillKnownKitProcesses()
        {
            foreach (PowerToysModule module in Enum.GetValues<PowerToysModule>())
            {
                KillByExecutablePath(
                    ModuleConfigData.Instance.GetModulePath(module),
                    (process, ex) => Console.WriteLine($"[KitProcessCleanup] Failed to terminate process {process.ProcessName} (ID: {process.Id}): {ex.Message}"));
            }
        }

        public static CleanupResult KillKnownKitProcessesByName(string processName, Action<Process, Exception>? onError = null)
        {
            var matchedKnownExecutable = false;
            var killedAnyProcess = false;
            var failedAnyProcess = false;
            foreach (PowerToysModule module in Enum.GetValues<PowerToysModule>())
            {
                var executablePath = ModuleConfigData.Instance.GetModulePath(module);
                var executableName = Path.GetFileNameWithoutExtension(executablePath);
                if (!string.Equals(executableName, processName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matchedKnownExecutable = true;
                var result = KillByExecutablePath(executablePath, onError);
                killedAnyProcess |= result.KilledAnyProcess;
                failedAnyProcess |= result.FailedAnyProcess;
            }

            return new CleanupResult(matchedKnownExecutable, killedAnyProcess, failedAnyProcess);
        }

        public static CleanupResult KillByExecutablePath(string executablePath, Action<Process, Exception>? onError = null)
        {
            var resolvedPath = ResolveExecutablePath(executablePath);
            if (string.IsNullOrEmpty(resolvedPath))
            {
                return default;
            }

            var processName = Path.GetFileNameWithoutExtension(resolvedPath);
            if (string.IsNullOrWhiteSpace(processName))
            {
                return default;
            }

            var killedAnyProcess = false;
            var failedAnyProcess = false;
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
                        killedAnyProcess = true;
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        failedAnyProcess = true;
                        onError?.Invoke(process, ex);
                    }
                }
            }

            return new CleanupResult(killedAnyProcess || failedAnyProcess, killedAnyProcess, failedAnyProcess);
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
