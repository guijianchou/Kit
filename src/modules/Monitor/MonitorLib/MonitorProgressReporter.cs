// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

internal static class MonitorProgressReporter
{
    public static void TryReport(IMonitorScanProgressReporter? progressReporter, MonitorScanProgressSnapshot snapshot, bool force = false)
    {
        try
        {
            progressReporter?.Report(snapshot, force);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
