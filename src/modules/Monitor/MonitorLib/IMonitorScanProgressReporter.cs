// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Receives Monitor scan progress snapshots.
/// </summary>
public interface IMonitorScanProgressReporter
{
    /// <summary>
    /// Reports a progress snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot to report.</param>
    /// <param name="force">True to bypass reporter throttling.</param>
    void Report(MonitorScanProgressSnapshot snapshot, bool force = false);
}
