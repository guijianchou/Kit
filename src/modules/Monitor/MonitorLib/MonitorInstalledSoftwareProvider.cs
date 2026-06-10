// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security;
using Microsoft.Win32;

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Reads installed software names from Windows uninstall registry entries.
/// </summary>
public static class MonitorInstalledSoftwareProvider
{
    private static readonly string[] UninstallSubKeyPaths =
    {
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
        @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    /// <summary>
    /// Gets installed software display names visible to the current process.
    /// </summary>
    /// <returns>Installed software names.</returns>
    public static IReadOnlyList<string> GetInstalledSoftwareNames()
    {
        return GetInstalledSoftwareIndex().DisplayNames;
    }

    /// <summary>
    /// Gets installed software metadata visible to the current process.
    /// </summary>
    /// <returns>Installed software metadata.</returns>
    public static MonitorInstalledSoftwareIndex GetInstalledSoftwareIndex()
    {
        List<MonitorInstalledSoftwareEntry> entries = new();
        ReadEntries(Registry.CurrentUser, entries);
        ReadEntries(Registry.LocalMachine, entries);
        return new MonitorInstalledSoftwareIndex(entries);
    }

    private static void ReadEntries(RegistryKey rootKey, ICollection<MonitorInstalledSoftwareEntry> entries)
    {
        foreach (string subKeyPath in UninstallSubKeyPaths)
        {
            try
            {
                using RegistryKey? uninstallKey = rootKey.OpenSubKey(subKeyPath);
                if (uninstallKey is null)
                {
                    continue;
                }

                foreach (string applicationKeyName in uninstallKey.GetSubKeyNames())
                {
                    using RegistryKey? applicationKey = uninstallKey.OpenSubKey(applicationKeyName);
                    string? displayName = ReadStringValue(applicationKey, "DisplayName");
                    if (!string.IsNullOrWhiteSpace(displayName))
                    {
                        entries.Add(new MonitorInstalledSoftwareEntry(
                            displayName,
                            ReadStringValue(applicationKey, "DisplayVersion"),
                            ReadStringValue(applicationKey, "InstallDate"),
                            ReadStringValue(applicationKey, "InstallLocation")));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (SecurityException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string? ReadStringValue(RegistryKey? key, string valueName)
    {
        object? value = key?.GetValue(valueName);
        return value is null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
