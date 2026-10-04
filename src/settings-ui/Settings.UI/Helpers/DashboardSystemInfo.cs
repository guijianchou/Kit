// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LocalServerHub.Windows;
using Microsoft.Win32;

namespace Kit.Settings.UI.Helpers;

internal sealed record DashboardSample<T>(T Value, DateTimeOffset Time);

// Only one probe of each kind may run, including when a slow native read outlives
// a page's wait. Page cancellation must not invalidate data shared with a new page.
internal sealed class DashboardProbeCache<T>(TimeSpan lifetime)
{
    private readonly object gate = new();
    private Task<DashboardSample<T>>? pending;
    private long started;

    internal Task<DashboardSample<T>> ReadAsync(Func<Task<T>> read, bool force, CancellationToken token)
    {
        Task<DashboardSample<T>> current;
        lock (gate)
        {
            if (pending == null || (pending.IsCompleted &&
                (force || !pending.IsCompletedSuccessfully || Environment.TickCount64 - started >= lifetime.TotalMilliseconds)))
            {
                started = Environment.TickCount64;
                pending = Task.Run(async () => new DashboardSample<T>(await read().ConfigureAwait(false), DateTimeOffset.Now));

                // A caller may leave before a native read finishes or fails.
                _ = pending.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            current = pending;
        }

        return current.WaitAsync(TimeSpan.FromSeconds(5), token);
    }
}

internal sealed record DashboardAdapter(string Id, string Name, string Driver, string Vendor);

internal sealed record DashboardGpuReading(DashboardAdapter Adapter, GpuTelemetry? Telemetry);

internal sealed record DashboardSystemValue(string Text, double? Percent = null);

internal static class DashboardSystemInfo
{
    private static readonly DashboardProbeCache<Dictionary<string, string>> Metadata = new(TimeSpan.FromMinutes(10));
    private static readonly DashboardProbeCache<Dictionary<string, DashboardSystemValue>> Usage = new(TimeSpan.FromSeconds(10));
    private static readonly DashboardProbeCache<Dictionary<string, DashboardSystemValue>> Storage = new(TimeSpan.FromMinutes(1));
    private static readonly DashboardProbeCache<IReadOnlyList<DashboardAdapter>> Adapters = new(TimeSpan.FromMinutes(10));
    private static readonly DashboardProbeCache<IReadOnlyList<DashboardGpuReading>> Graphics = new(TimeSpan.FromSeconds(10));

    internal static Task<DashboardSample<Dictionary<string, DashboardSystemValue>>> ReadUsageAsync(bool force, CancellationToken token) =>
        Usage.ReadAsync(async () =>
        {
            var metadata = await Metadata.ReadAsync(() => Task.FromResult(ReadMetadata()), force, CancellationToken.None).ConfigureAwait(false);
            var details = metadata.Value.ToDictionary(item => item.Key, item => new DashboardSystemValue(item.Value));
            var inspector = new SystemResourceInspector();
            inspector.Sample();
            await Task.Delay(250).ConfigureAwait(false);
            var sample = inspector.Sample();
            details["LogicalProcessors"] = new(sample.LogicalProcessorCount.ToString(CultureInfo.CurrentCulture));
            details["CpuLoad"] = new(sample.CpuPercent is { } cpu ? $"{cpu:F0}%" : "—", sample.CpuPercent);
            details["Memory"] = sample.TotalMemoryBytes > 0
                ? new($"{sample.UsedMemoryBytes / 1073741824d:F1} / {sample.TotalMemoryBytes / 1073741824d:F1} GB", sample.UsedMemoryBytes * 100d / sample.TotalMemoryBytes)
                : new("—");
            details["Uptime"] = new(TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"d\d\ hh\:mm\:ss", CultureInfo.CurrentCulture));
            return details;
        }, force, token);

    internal static Task<DashboardSample<Dictionary<string, DashboardSystemValue>>> ReadStorageAsync(bool force, CancellationToken token) =>
        Storage.ReadAsync(() => Task.FromResult(ReadStorage()), force, token);

    internal static Task<DashboardSample<IReadOnlyList<DashboardGpuReading>>> ReadGraphicsAsync(bool force, CancellationToken token) =>
        Graphics.ReadAsync(async () =>
        {
            var inventory = await Adapters.ReadAsync(() => Task.FromResult(ReadAdapters()), force, CancellationToken.None).ConfigureAwait(false);
            GpuSnapshot? telemetry = null;
            if (inventory.Value.Any(adapter => adapter.Vendor == "NVIDIA"))
            {
                telemetry = await GpuInspector.SampleAsync().ConfigureAwait(false);
            }

            return MatchGraphics(inventory.Value, telemetry);
        }, force, token);

    internal static IReadOnlyList<DashboardGpuReading> MatchGraphics(IReadOnlyList<DashboardAdapter> adapters, GpuSnapshot? telemetry) =>
        adapters.Select(adapter =>
        {
            // Never attach another adapter's readings. Duplicate model names cannot
            // be disambiguated by the existing NVIDIA API, so retain identity only.
            var matches = telemetry?.Devices.Where(item => string.Equals(item.Name, adapter.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var sample = adapter.Vendor == "NVIDIA" && matches?.Length == 1 &&
                adapters.Count(item => string.Equals(item.Name, adapter.Name, StringComparison.OrdinalIgnoreCase)) == 1 ? matches[0] : null;
            return new DashboardGpuReading(adapter, sample);
        }).ToArray();

    internal static string? VendorForDevice(string id)
    {
        if (!id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (id.Contains("VEN_10DE&", StringComparison.OrdinalIgnoreCase))
        {
            return "NVIDIA";
        }

        if (id.Contains("VEN_1002&", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }

        if (id.Contains("VEN_8086&", StringComparison.OrdinalIgnoreCase))
        {
            return "Intel";
        }

        return null;
    }

    /// <summary>
    /// Converts a raw WMI or nvidia-smi DriverVersion string to a user-friendly display format.
    /// For NVIDIA: "31.0.15.6601" or "566.03" → "566" (the well-known 3-digit branch number).
    /// </summary>
    internal static string FormatDriverVersion(string rawVersion, string vendor)
    {
        if (string.IsNullOrEmpty(rawVersion) || rawVersion == "—")
        {
            return rawVersion ?? "—";
        }

        if (string.Equals(vendor, "NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            var parts = rawVersion.Split('.');
            if (parts.Length >= 4)
            {
                string combined = parts[2] + parts[3];
                if (combined.Length >= 5)
                {
                    return combined[^5..^2]; // "156601" → "566"
                }
            }
            else if (parts.Length >= 1 && int.TryParse(parts[0], out int branch) && branch >= 100 && branch <= 999)
            {
                return parts[0]; // "566.03" → "566"
            }
        }

        return rawVersion;
    }

    private static IReadOnlyList<DashboardAdapter> ReadAdapters()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, DriverVersion FROM Win32_VideoController WHERE ConfigManagerErrorCode = 0");
        searcher.Options.Timeout = TimeSpan.FromSeconds(2);
        using var results = searcher.Get();
        var adapters = new List<DashboardAdapter>();
        foreach (ManagementObject device in results)
        {
            using (device)
            {
                string id = device["PNPDeviceID"]?.ToString() ?? string.Empty;
                string? vendor = VendorForDevice(id);
                if (vendor == null)
                {
                    continue;
                }

                string rawDriver = device["DriverVersion"]?.ToString() ?? "—";
                adapters.Add(new(id, device["Name"]?.ToString() ?? vendor, FormatDriverVersion(rawDriver, vendor), vendor));
            }
        }

        return adapters.DistinctBy(adapter => adapter.Id).OrderBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(adapter => adapter.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Dictionary<string, string> ReadMetadata()
    {
        const string windowsKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var details = new Dictionary<string, string>
        {
            ["Device"] = Environment.MachineName + " · " + RuntimeInformation.OSArchitecture,
        };
        string ReadRegistry(string key, string name) => Registry.GetValue(key, name, null)?.ToString()?.Trim() ?? string.Empty;
        try
        {
            string build = ReadRegistry(windowsKey, "CurrentBuildNumber");
            string edition = ReadRegistry(windowsKey, "ProductName");
            if (int.TryParse(build, out int number) && number >= 22000 && edition.StartsWith("Windows 10", StringComparison.Ordinal))
            {
                edition = "Windows 11" + edition[10..];
            }

            details["Windows"] = $"{edition} {ReadRegistry(windowsKey, "DisplayVersion")}\n{build}.{ReadRegistry(windowsKey, "UBR")}";
            details["Processor"] = ReadRegistry(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            details["Windows"] = RuntimeInformation.OSDescription;
            details["Processor"] = RuntimeInformation.OSArchitecture.ToString();
        }

        return details;
    }

    private static Dictionary<string, DashboardSystemValue> ReadStorage()
    {
        var disks = new Dictionary<string, DashboardSystemValue>();
        foreach (var disk in DriveInfo.GetDrives())
        {
            try
            {
                if (disk.DriveType == DriveType.Fixed && disk.IsReady)
                {
                    long total = disk.TotalSize;
                    long used = total - disk.TotalFreeSpace;
                    disks[disk.Name] = total > 0 ? new($"{used / 1073741824d:F0} / {total / 1073741824d:F0} GB", used * 100d / total) : new("—");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                disks[disk.Name] = new("—");
            }
        }

        return disks;
    }

    // ────────────────────────────────────────────────────────────────
    // Split data methods for multi-frequency polling
    // ────────────────────────────────────────────────────────────────

    /// <summary>CPU load + memory — 1-second cache for fast telemetry.</summary>
    private static readonly DashboardProbeCache<Dictionary<string, DashboardSystemValue>> CpuMemory = new(TimeSpan.FromSeconds(1));

    internal static Task<DashboardSample<Dictionary<string, DashboardSystemValue>>> ReadCpuMemoryAsync(bool force, CancellationToken token) =>
        CpuMemory.ReadAsync(async () =>
        {
            var details = new Dictionary<string, DashboardSystemValue>();
            var inspector = new SystemResourceInspector();
            inspector.Sample();
            await Task.Delay(250).ConfigureAwait(false);
            var sample = inspector.Sample();
            details["LogicalProcessors"] = new(sample.LogicalProcessorCount.ToString(CultureInfo.CurrentCulture));
            details["CpuLoad"] = new(sample.CpuPercent is { } cpu ? $"{cpu:F0}%" : "—", sample.CpuPercent);
            details["Memory"] = sample.TotalMemoryBytes > 0
                ? new($"{sample.UsedMemoryBytes / 1073741824d:F1} / {sample.TotalMemoryBytes / 1073741824d:F1} GB", sample.UsedMemoryBytes * 100d / sample.TotalMemoryBytes)
                : new("—");
            return details;
        }, force, token);

    /// <summary>Static system metadata (device, OS, processor) — uses existing 10-minute cache.</summary>
    internal static Task<DashboardSample<Dictionary<string, string>>> ReadStaticInfoAsync(bool force, CancellationToken token) =>
        Metadata.ReadAsync(() => Task.FromResult(ReadMetadata()), force, token);

    /// <summary>GPU adapter inventory — public wrapper around existing 10-minute cache.</summary>
    internal static Task<DashboardSample<IReadOnlyList<DashboardAdapter>>> ReadAdaptersInfoAsync(bool force, CancellationToken token) =>
        Adapters.ReadAsync(() => Task.FromResult(ReadAdapters()), force, token);

    /// <summary>GPU telemetry only — 1-second cache, reuses long-lived adapter cache for matching.</summary>
    private static readonly DashboardProbeCache<IReadOnlyList<DashboardGpuReading>> GpuTelemetryFast = new(TimeSpan.FromSeconds(1));

    internal static Task<DashboardSample<IReadOnlyList<DashboardGpuReading>>> ReadGpuTelemetryOnlyAsync(bool force, CancellationToken token) =>
        GpuTelemetryFast.ReadAsync(async () =>
        {
            var inventory = await Adapters.ReadAsync(() => Task.FromResult(ReadAdapters()), false, CancellationToken.None).ConfigureAwait(false);
            GpuSnapshot? telemetry = null;
            if (inventory.Value.Any(adapter => adapter.Vendor == "NVIDIA"))
            {
                telemetry = await GpuInspector.SampleAsync().ConfigureAwait(false);
            }

            return MatchGraphics(inventory.Value, telemetry);
        }, force, token);
}
