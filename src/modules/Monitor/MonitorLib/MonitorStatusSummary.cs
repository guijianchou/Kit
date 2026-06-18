// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

public sealed record MonitorStatusSummary(
    MonitorScanStatus? OverallStatus,
    int TotalRuns,
    int SuccessRuns,
    int WarningRuns,
    int FailedRuns,
    IReadOnlyList<MonitorStatusDay> Days,
    string? LastMessage);
