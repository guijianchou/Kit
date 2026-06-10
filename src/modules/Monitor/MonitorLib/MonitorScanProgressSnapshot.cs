// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Serializable Monitor scan progress state.
/// </summary>
public sealed class MonitorScanProgressSnapshot
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MonitorScanProgressSnapshot"/> class.
    /// </summary>
    public MonitorScanProgressSnapshot()
    {
        Phase = string.Empty;
        CurrentDirectory = string.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MonitorScanProgressSnapshot"/> class.
    /// </summary>
    public MonitorScanProgressSnapshot(
        string phase,
        int filesProcessed,
        int filesTotal,
        string currentDirectory,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt,
        int? recordCount)
    {
        Phase = phase;
        FilesProcessed = filesProcessed;
        FilesTotal = filesTotal;
        CurrentDirectory = currentDirectory;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        RecordCount = recordCount;
    }

    /// <summary>
    /// Gets or sets the current scan phase.
    /// </summary>
    public string Phase { get; set; }

    /// <summary>
    /// Gets or sets the number of files processed in the current scan.
    /// </summary>
    public int FilesProcessed { get; set; }

    /// <summary>
    /// Gets or sets the total number of files expected in the current scan.
    /// </summary>
    public int FilesTotal { get; set; }

    /// <summary>
    /// Gets or sets the directory currently being scanned.
    /// </summary>
    public string CurrentDirectory { get; set; }

    /// <summary>
    /// Gets or sets the scan start time.
    /// </summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// Gets or sets the scan completion time, if finished.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Gets or sets the final record count, if finished.
    /// </summary>
    public int? RecordCount { get; set; }
}
