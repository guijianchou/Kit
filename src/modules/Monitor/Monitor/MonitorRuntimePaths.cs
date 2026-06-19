// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;

namespace Microsoft.PowerToys.Monitor;

internal static class MonitorRuntimePaths
{
    private const string MonitorProgressFileName = "scan-progress.json";
    private const string MonitorStatusDatabaseFileName = "monitor-status.db";

    public static string ResolveScanId(string? scanId)
    {
        return string.IsNullOrWhiteSpace(scanId) ? CreateScanId() : scanId;
    }

    public static string CreateScanId()
    {
        return Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }

    public static string ResolveDownloadsPath(string? downloadsPath)
    {
        if (!string.IsNullOrWhiteSpace(downloadsPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(downloadsPath));
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Downloads");
    }

    public static string ResolveSettingsPath(string? settingsPath)
    {
        if (!string.IsNullOrWhiteSpace(settingsPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(settingsPath));
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Kit", "Monitor", "settings.json");
    }

    public static string ResolveProgressPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Kit", "Monitor", MonitorProgressFileName);
    }

    public static string ResolveStatusDatabasePath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Kit", "Monitor", MonitorStatusDatabaseFileName);
    }

    public static string ResolveCsvPath(string downloadsPath, string csvPath)
    {
        string expandedCsvPath = Environment.ExpandEnvironmentVariables(csvPath);
        return Path.IsPathRooted(expandedCsvPath) ? Path.GetFullPath(expandedCsvPath) : Path.Combine(downloadsPath, expandedCsvPath);
    }
}
