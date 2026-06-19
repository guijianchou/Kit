// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Settings.UI.Services
{
    public sealed record MonitorManualScanProgressUpdate(
        string ScanId,
        bool IsVisible,
        bool IsRunning,
        bool IsIndeterminate,
        double ProgressValue,
        string Detail,
        bool ShouldStopTimer,
        bool ShouldRefreshStatus);
}
