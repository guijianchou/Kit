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
        ScanId = string.Empty;
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
        int? recordCount,
        string? scanId = null,
        string? message = null)
    {
        ScanId = scanId ?? string.Empty;
        Phase = phase;
        FilesProcessed = filesProcessed;
        FilesTotal = filesTotal;
        CurrentDirectory = currentDirectory;
        StartedAt = startedAt;
        UpdatedAt = DateTimeOffset.UtcNow;
        CompletedAt = completedAt;
        RecordCount = recordCount;
        Message = message;
    }

    /// <summary>
    /// Gets or sets the unique scan identifier used to separate background and manual progress.
    /// </summary>
    public string ScanId { get; set; }

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
    /// Gets or sets the last time this progress snapshot was written.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets the scan completion time, if finished.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Gets or sets the final record count, if finished.
    /// </summary>
    public int? RecordCount { get; set; }

    /// <summary>
    /// Gets or sets a human-readable terminal failure message, if the scan failed.
    /// </summary>
    public string? Message { get; set; }
}
