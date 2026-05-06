// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Writes Monitor scan progress to a JSON file using temp-file replacement.
/// </summary>
public sealed class MonitorScanProgressFileReporter : IMonitorScanProgressReporter
{
    private readonly string _progressPath;
    private readonly TimeSpan _minimumInterval;
    private DateTimeOffset _lastWrite = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="MonitorScanProgressFileReporter"/> class.
    /// </summary>
    /// <param name="progressPath">The progress JSON path.</param>
    /// <param name="minimumInterval">The minimum interval between non-forced writes.</param>
    public MonitorScanProgressFileReporter(string progressPath, TimeSpan minimumInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(progressPath);

        _progressPath = progressPath;
        _minimumInterval = minimumInterval < TimeSpan.Zero ? TimeSpan.Zero : minimumInterval;
    }

    /// <inheritdoc />
    public void Report(MonitorScanProgressSnapshot snapshot, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!force && _lastWrite != DateTimeOffset.MinValue && now - _lastWrite < _minimumInterval)
        {
            return;
        }

        string? directoryName = Path.GetDirectoryName(_progressPath);
        if (!string.IsNullOrEmpty(directoryName))
        {
            Directory.CreateDirectory(directoryName);
        }

        string temporaryPath = Path.Combine(
            directoryName ?? string.Empty,
            Path.GetFileName(_progressPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, MonitorJsonSerializationContext.Default.MonitorScanProgressSnapshot));
            if (File.Exists(_progressPath))
            {
                File.Replace(temporaryPath, _progressPath, null);
            }
            else
            {
                File.Move(temporaryPath, _progressPath);
            }

            _lastWrite = now;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Reads a progress snapshot from disk.
    /// </summary>
    /// <param name="progressPath">The progress JSON path.</param>
    /// <returns>The progress snapshot.</returns>
    public static MonitorScanProgressSnapshot Read(string progressPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(progressPath);

        return JsonSerializer.Deserialize(File.ReadAllText(progressPath), MonitorJsonSerializationContext.Default.MonitorScanProgressSnapshot)
            ?? throw new InvalidDataException("Monitor progress file did not contain a valid snapshot.");
    }
}
