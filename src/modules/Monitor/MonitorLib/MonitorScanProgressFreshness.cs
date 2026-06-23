// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;

namespace Microsoft.PowerToys.Monitor;

public static class MonitorScanProgressFreshness
{
    public static string GetFreshProgressScanId(string progressPath, DateTimeOffset now)
    {
        return GetFreshProgressScanId(progressPath, now, MonitorScanProgressFileReporter.Read);
    }

    public static string GetFreshProgressScanId(string progressPath, DateTimeOffset now, Func<string, MonitorScanProgressSnapshot?> readSnapshot)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);

        if (string.IsNullOrWhiteSpace(progressPath) || !File.Exists(progressPath))
        {
            return string.Empty;
        }

        try
        {
            return GetFreshProgressScanId(readSnapshot(progressPath), now);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is JsonException)
        {
            return string.Empty;
        }
    }

    public static string GetFreshProgressScanId(MonitorScanProgressSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot == null ||
            string.IsNullOrWhiteSpace(snapshot.ScanId) ||
            IsTerminalScanPhase(snapshot.Phase) ||
            now - GetLastProgressAt(snapshot) >= MonitorStatusStore.StaleRunningScanTimeout)
        {
            return string.Empty;
        }

        return snapshot.ScanId;
    }

    private static DateTimeOffset GetLastProgressAt(MonitorScanProgressSnapshot snapshot)
    {
        if (snapshot.UpdatedAt != default)
        {
            return snapshot.UpdatedAt;
        }

        if (snapshot.CompletedAt.HasValue)
        {
            return snapshot.CompletedAt.Value;
        }

        return snapshot.StartedAt == default ? DateTimeOffset.UtcNow : snapshot.StartedAt;
    }

    private static bool IsTerminalScanPhase(string phase)
    {
        return string.Equals(phase, MonitorScanProgressPhase.Completed, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(phase, MonitorScanProgressPhase.Failed, StringComparison.OrdinalIgnoreCase);
    }
}
