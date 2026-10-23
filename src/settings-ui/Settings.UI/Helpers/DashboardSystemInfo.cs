// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Kit.Settings.UI.Helpers;

internal sealed record GpuTelemetry(
    string Name,
    string DriverVersion,
    int? UtilizationPercent,
    long? UsedMemoryMiB,
    long? TotalMemoryMiB,
    int? TemperatureCelsius);

internal sealed record GpuSnapshot(
    IReadOnlyList<GpuTelemetry> Devices,
    string? Error)
{
    public GpuTelemetry? Primary => Devices.Count > 0 ? Devices[0] : null;
}

internal static class GpuInspector
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromSeconds(3);
    private static (GpuSnapshot Snapshot, DateTime ExpiresAt)? _cachedFailure;
    private static (GpuSnapshot Snapshot, DateTime ExpiresAt)? _cachedSuccess;

    private static GpuSnapshot CacheFailure(GpuSnapshot failure)
    {
        _cachedFailure = (failure, DateTime.UtcNow.Add(FailureCacheDuration));
        return failure;
    }

    public static async Task<GpuSnapshot> SampleAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedFailure is { } cachedFail && DateTime.UtcNow < cachedFail.ExpiresAt)
        {
            return cachedFail.Snapshot;
        }

        if (_cachedSuccess is { } cachedSucc && DateTime.UtcNow < cachedSucc.ExpiresAt)
        {
            return cachedSucc.Snapshot;
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = "nvidia-smi",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("--query-gpu=name,driver_version,utilization.gpu,memory.used,memory.total,temperature.gpu");
        startInfo.ArgumentList.Add("--format=csv,noheader,nounits");

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        Process process;
        try
        {
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                return CacheFailure(new([], "nvidia-smi did not start."));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or IOException)
        {
            return CacheFailure(new([], "NVIDIA telemetry unavailable: nvidia-smi was not found or could not start."));
        }

        try
        {
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            string error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                string failureMessage = string.IsNullOrWhiteSpace(error)
                    ? $"nvidia-smi exited with code {process.ExitCode}."
                    : error.Trim();
                return CacheFailure(new([], failureMessage));
            }

            string output = await outputTask.ConfigureAwait(false);
            List<GpuTelemetry> devices = [];
            foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParse(line, out GpuTelemetry? telemetry) && telemetry is not null)
                {
                    devices.Add(telemetry);
                }
            }

            if (devices.Count == 0)
            {
                return CacheFailure(new([], "nvidia-smi returned no readable GPU telemetry."));
            }

            _cachedFailure = null;
            var snapshot = new GpuSnapshot(devices, null);
            _cachedSuccess = (snapshot, DateTime.UtcNow.Add(SuccessCacheDuration));
            return snapshot;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CacheFailure(new([], "NVIDIA telemetry probe timed out."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return CacheFailure(new([], $"NVIDIA telemetry failed: {ex.Message}"));
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using CancellationTokenSource cleanupTimeout = new();
                    cleanupTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                    try
                    {
                        await process.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            process.Dispose();
        }
    }

    private static bool TryParse(string line, out GpuTelemetry? telemetry)
    {
        string[] fields = line.Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length < 6 || string.IsNullOrWhiteSpace(fields[0]))
        {
            telemetry = null;
            return false;
        }

        telemetry = new GpuTelemetry(
            fields[0],
            fields[1],
            ParseInt(fields[2]),
            ParseLong(fields[3]),
            ParseLong(fields[4]),
            ParseInt(fields[5]));
        return true;
    }

    private static int? ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : null;

    private static long? ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
            ? result
            : null;
}

internal sealed record SystemResourceSnapshot(
    double? CpuPercent,
    long TotalMemoryBytes,
    long AvailableMemoryBytes,
    int LogicalProcessorCount)
{
    public long UsedMemoryBytes => Math.Max(0, TotalMemoryBytes - AvailableMemoryBytes);
}

internal sealed class SystemResourceInspector
{
    private readonly Lock _gate = new();
    private ulong? _previousIdle;
    private ulong? _previousKernel;
    private ulong? _previousUser;

    public SystemResourceSnapshot Sample()
    {
        lock (_gate)
        {
            double? cpuPercent = null;
            if (GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
            {
                ulong idleTicks = idle.ToUInt64();
                ulong kernelTicks = kernel.ToUInt64();
                ulong userTicks = user.ToUInt64();

                if (_previousIdle is { } previousIdle
                    && _previousKernel is { } previousKernel
                    && _previousUser is { } previousUser
                    && idleTicks >= previousIdle
                    && kernelTicks >= previousKernel
                    && userTicks >= previousUser)
                {
                    ulong totalDelta = (kernelTicks - previousKernel) + (userTicks - previousUser);
                    ulong idleDelta = idleTicks - previousIdle;
                    if (totalDelta > 0)
                    {
                        cpuPercent = Math.Clamp(
                            (totalDelta - Math.Min(idleDelta, totalDelta)) * 100d / totalDelta,
                            0d,
                            100d);
                    }
                }

                _previousIdle = idleTicks;
                _previousKernel = kernelTicks;
                _previousUser = userTicks;
            }

            MemoryStatus memory = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            long totalMemory = 0;
            long availableMemory = 0;
            if (GlobalMemoryStatusEx(ref memory))
            {
                totalMemory = ToLong(memory.TotalPhysical);
                availableMemory = Math.Min(totalMemory, ToLong(memory.AvailablePhysical));
            }

            return new SystemResourceSnapshot(
                cpuPercent,
                totalMemory,
                availableMemory,
                Environment.ProcessorCount);
        }
    }

    private static long ToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

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
    /// For NVIDIA: "32.0.15.6636" → "566.36"; public versions such as "566.03" stay intact.
    /// </summary>
    internal static string FormatDriverVersion(string rawVersion, string vendor)
    {
        if (string.IsNullOrEmpty(rawVersion) || rawVersion == "—")
        {
            return rawVersion ?? "—";
        }

        if (string.Equals(vendor, "NVIDIA", StringComparison.OrdinalIgnoreCase) &&
            Version.TryParse(rawVersion, out var version) && version.Revision is >= 0 and <= 9999)
        {
            // The last digit of the Windows build and its four-digit revision encode
            // NVIDIA's public version. Numeric parsing also restores leading zeroes.
            string digits = ((version.Build % 10 * 10000) + version.Revision).ToString("D5", CultureInfo.InvariantCulture);
            return digits[..3] + "." + digits[3..];
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
