// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Microsoft.UI.Dispatching;
using NetMapLib;

namespace Kit.Settings.UI.ViewModels;

public sealed class DashboardDetail : INotifyPropertyChanged
{
    private string label = string.Empty;
    private string value = string.Empty;
    private double? percent;

    public DashboardDetail(string label, string value, double? percent = null, UsageCategory usageCategory = UsageCategory.Default)
    {
        this.label = label;
        this.value = value;
        this.percent = percent;
        UsageCategory = usageCategory;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label
    {
        get => label;
        set
        {
            if (label != value)
            {
                label = value;
                OnPropertyChanged(nameof(Label));
            }
        }
    }

    public string Value
    {
        get => value;
        set
        {
            if (this.value != value)
            {
                this.value = value;
                OnPropertyChanged(nameof(Value));
            }
        }
    }

    public double? Percent
    {
        get => percent;
        set
        {
            if (percent != value)
            {
                percent = value;
                OnPropertyChanged(nameof(Percent));
                OnPropertyChanged(nameof(HasUsage));
                OnPropertyChanged(nameof(UsageValue));
            }
        }
    }

    public bool HasUsage => Percent.HasValue && double.IsFinite(Percent.Value);

    public double UsageValue => HasUsage ? Math.Clamp(Percent!.Value, 0, 100) : 0;

    public UsageCategory UsageCategory { get; }

    public void Update(string newValue, double? newPercent = null)
    {
        Value = newValue;
        Percent = newPercent;
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class DashboardGpuCard : INotifyPropertyChanged
{
    private string name;
    private IReadOnlyList<DashboardDetail> details;
    private string status;

    public DashboardGpuCard(string name, IReadOnlyList<DashboardDetail> details, string status)
    {
        this.name = name;
        this.details = details;
        this.status = status;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => name;
        set
        {
            if (name != value)
            {
                name = value;
                OnPropertyChanged(nameof(Name));
            }
        }
    }

    public IReadOnlyList<DashboardDetail> Details
    {
        get => details;
        set
        {
            if (details != value)
            {
                details = value;
                OnPropertyChanged(nameof(Details));
            }
        }
    }

    public string Status
    {
        get => status;
        set
        {
            if (status != value)
            {
                status = value;
                OnPropertyChanged(nameof(Status));
            }
        }
    }

    public string StatusResourceKey { get; set; } = string.Empty;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public partial class DashboardViewModel
{
    private static readonly string[] SystemDetailKeys = ["Device", "Windows", "Processor", "LogicalProcessors", "CpuLoad", "Memory", "Uptime"];
    private readonly Dictionary<string, DashboardSystemValue> systemValues = new();
    private readonly Dictionary<string, DashboardDetail> systemDetailMap = new();
    private CancellationTokenSource? overviewCancellation;
    private CancellationTokenSource? systemCancellation;
    private CancellationTokenSource? telemetryCancellation;
    private CancellationTokenSource? storageCancellation;

    // Distinct polling timers:
    // Uptime: 1s
    // Memory, CPU load, VRAM usage, GPU temp: 1s (GPU process spawn throttled to 3s in GpuInspector)
    // Storage: 5m
    // Network overview: 10s
    private DispatcherQueueTimer? uptimeTimer;
    private DispatcherQueueTimer? telemetryTimer;
    private DispatcherQueueTimer? storageTimer;
    private DispatcherQueueTimer? overviewTimer;
    private bool overviewActive;
    private long activityGeneration;
    private long lastOverviewRefresh;
    private long lastSystemRefresh;
    private DateTimeOffset?[]? systemSampleTimes;
    private DateTime? overviewSampleTime;
    private string systemStatusResourceKey = string.Empty;
    private string overviewStatusResourceKey = string.Empty;

    public IReadOnlyList<DashboardDetail> SystemDetails { get; private set; } = [];

    public IReadOnlyList<DashboardGpuCard> GraphicsCards { get; private set; } = [];

    public IReadOnlyList<DashboardDetail> StorageDetails { get; private set; } = [];

    public string SystemStatus { get; private set; } = string.Empty;

    public bool CanRefreshSystem => overviewActive && systemCancellation == null;

    public NetMapCard DirectEgress { get; private set; } = new("Direct", new(), true);

    public NetMapCard ProxyEgress { get; private set; } = new("Proxy", new(), true);

    public string OverviewStatus { get; private set; } = string.Empty;

    public bool CanRefreshOverview => overviewActive && overviewCancellation == null;

    public static string EgressIp(NetMapCard card) => card.Health == ProbeHealth.Error ? "N/A" : card.Ip;

    public static string EgressStatus(NetMapCard card) => card.Health == ProbeHealth.Good
        ? string.Empty
        : card.Status;

    private void RefreshOverviewLocalization()
    {
        if (SystemDetails.Count == SystemDetailKeys.Length)
        {
            for (int index = 0; index < SystemDetailKeys.Length; index++)
            {
                SystemDetails[index].Label = ResourceLoader.GetString("Dashboard_" + SystemDetailKeys[index]);
            }
        }
        else
        {
            foreach (var entry in systemDetailMap)
            {
                entry.Value.Label = ResourceLoader.GetString("Dashboard_" + entry.Key);
            }
        }

        if (StorageDetails.Count == 1 && StorageDetails[0].Value == "—" && !StorageDetails[0].HasUsage)
        {
            StorageDetails[0].Label = ResourceLoader.GetString("Dashboard_Storage");
        }

        string[] graphicsKeys = ["Driver", "VideoMemory", "GpuLoad", "Temperature"];
        foreach (var card in GraphicsCards)
        {
            if (card.Details.Count == 0)
            {
                card.Name = ResourceLoader.GetString("Dashboard_GpuNotDetected");
            }

            for (int index = 0; index < card.Details.Count && index < graphicsKeys.Length; index++)
            {
                card.Details[index].Label = ResourceLoader.GetString("Dashboard_" + graphicsKeys[index]);
            }

            if (!string.IsNullOrEmpty(card.StatusResourceKey))
            {
                card.Status = ResourceLoader.GetString(card.StatusResourceKey);
            }
        }

        if (!string.IsNullOrEmpty(systemStatusResourceKey))
        {
            SystemStatus = systemStatusResourceKey == "Dashboard_SystemSampleTimes" && systemSampleTimes != null
                ? string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString(systemStatusResourceKey),
                    systemSampleTimes[0]!.Value.ToString("T", CultureInfo.CurrentCulture),
                    systemSampleTimes[1]!.Value.ToString("T", CultureInfo.CurrentCulture),
                    systemSampleTimes[2]!.Value.ToString("T", CultureInfo.CurrentCulture))
                : ResourceLoader.GetString(systemStatusResourceKey);
            OnPropertyChanged(nameof(SystemStatus));
        }

        if (!string.IsNullOrEmpty(overviewStatusResourceKey))
        {
            OverviewStatus = overviewStatusResourceKey == "Dashboard_OverviewUpdated" && overviewSampleTime.HasValue
                ? string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString(overviewStatusResourceKey), overviewSampleTime.Value.ToString("T", CultureInfo.CurrentCulture))
                : ResourceLoader.GetString(overviewStatusResourceKey);
            OnPropertyChanged(nameof(OverviewStatus));
        }

        DirectEgress = DirectEgress.Relocalize();
        ProxyEgress = ProxyEgress.Relocalize();
        OnPropertyChanged(nameof(DirectEgress));
        OnPropertyChanged(nameof(ProxyEgress));
    }

    public void SetOverviewActive(bool active)
    {
        active &= !isDisposed;
        if (overviewActive == active)
        {
            return;
        }

        overviewActive = active;
        long generation = ++activityGeneration;
        if (!overviewActive)
        {
            uptimeTimer?.Stop();
            telemetryTimer?.Stop();
            storageTimer?.Stop();
            overviewTimer?.Stop();

            overviewCancellation?.Cancel();
            overviewCancellation = null;
            systemCancellation?.Cancel();
            systemCancellation = null;
            telemetryCancellation?.Cancel();
            telemetryCancellation = null;
            storageCancellation?.Cancel();
            storageCancellation = null;
        }
        else
        {
            // 1. Uptime timer: 1s interval (zero-cost tick read)
            if (uptimeTimer == null)
            {
                uptimeTimer = dispatcher.CreateTimer();
                uptimeTimer.Interval = TimeSpan.FromSeconds(1);
                uptimeTimer.Tick += (_, _) => UpdateUptimeTick();
            }

            // 2. Fast telemetry timer: 1s interval (CPU load, Memory, GPU VRAM & Temp)
            if (telemetryTimer == null)
            {
                telemetryTimer = dispatcher.CreateTimer();
                telemetryTimer.Interval = TimeSpan.FromSeconds(1);
                telemetryTimer.Tick += async (_, _) => await RefreshFastTelemetryAsync();
            }

            // 3. Storage timer: 5 minutes interval (disk space changes slowly)
            if (storageTimer == null)
            {
                storageTimer = dispatcher.CreateTimer();
                storageTimer.Interval = TimeSpan.FromMinutes(5);
                storageTimer.Tick += async (_, _) => await RefreshStorageAsync();
            }

            // 4. Network overview timer: 10s interval
            if (overviewTimer == null)
            {
                overviewTimer = dispatcher.CreateTimer();
                overviewTimer.Interval = TimeSpan.FromSeconds(10);
                overviewTimer.Tick += async (_, _) =>
                {
                    RefreshUpdateRelativeTime();
                    await RefreshOverviewAsync();
                };
            }

            uptimeTimer.Start();
            telemetryTimer.Start();
            storageTimer.Start();
            overviewTimer.Start();

            // Present the retained snapshot first. Short visits to another page
            // should not restart network probes or rebuild GPU/storage cards.
            dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!overviewActive || generation != activityGeneration || isDisposed)
                {
                    return;
                }

                if (lastOverviewRefresh == 0 || Stopwatch.GetElapsedTime(lastOverviewRefresh) >= TimeSpan.FromSeconds(10))
                {
                    _ = RefreshOverviewAsync();
                }

                if (lastSystemRefresh == 0 || Stopwatch.GetElapsedTime(lastSystemRefresh) >= TimeSpan.FromMinutes(1))
                {
                    _ = RefreshSystemAsync();
                }
            });
        }

        OnPropertyChanged(nameof(CanRefreshOverview));
        OnPropertyChanged(nameof(CanRefreshSystem));
    }

    private void UpdateUptimeTick()
    {
        if (isDisposed || !overviewActive)
        {
            return;
        }

        string uptimeText = TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"d\d\ hh\:mm\:ss", CultureInfo.CurrentCulture);
        systemValues["Uptime"] = new(uptimeText);
        if (systemDetailMap.TryGetValue("Uptime", out var uptimeDetail))
        {
            uptimeDetail.Update(uptimeText, null);
        }
        else
        {
            UpdateSystemDetails();
        }
    }

    private async Task RefreshFastTelemetryAsync()
    {
        if (isDisposed || !overviewActive || telemetryCancellation != null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        telemetryCancellation = cancellation;

        try
        {
            // The timer controls freshness; reuse only in-flight probes so cache timing cannot skip a tick.
            await Task.WhenAll(
                ReadFastPart(() => DashboardSystemInfo.ReadCpuMemoryAsync(force: true, cancellation.Token), values =>
                {
                    foreach (var kv in values)
                    {
                        systemValues[kv.Key] = kv.Value;
                    }

                    UpdateSystemDetails();
                }, cancellation),
                ReadFastPart(() => DashboardSystemInfo.ReadGpuTelemetryOnlyAsync(force: true, cancellation.Token), values =>
                {
                    ApplyGraphicsReadings(values);
                }, cancellation)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            dispatcher.TryEnqueue(() =>
            {
                cancellation.Dispose();
                if (telemetryCancellation == cancellation)
                {
                    telemetryCancellation = null;
                }
            });
        }
    }

    private async Task ReadFastPart<T>(Func<Task<DashboardSample<T>>> read, Action<T> apply, CancellationTokenSource cancellation)
    {
        try
        {
            var result = await read().ConfigureAwait(false);
            dispatcher.TryEnqueue(() =>
            {
                try
                {
                    if (!isDisposed && overviewActive && !cancellation.IsCancellationRequested)
                    {
                        apply(result.Value);
                    }
                }
                catch (ObjectDisposedException)
                {
                }
            });
        }
        catch
        {
        }
    }

    private async Task RefreshStorageAsync(bool force = false)
    {
        if (isDisposed || !overviewActive || storageCancellation != null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        storageCancellation = cancellation;

        try
        {
            var sample = await DashboardSystemInfo.ReadStorageAsync(force, cancellation.Token).ConfigureAwait(false);
            dispatcher.TryEnqueue(() =>
            {
                try
                {
                    if (!isDisposed && overviewActive && !cancellation.IsCancellationRequested)
                    {
                        ApplyStorageDetails(sample.Value);
                    }
                }
                catch (ObjectDisposedException)
                {
                }
            });
        }
        catch
        {
        }
        finally
        {
            dispatcher.TryEnqueue(() =>
            {
                cancellation.Dispose();
                if (storageCancellation == cancellation)
                {
                    storageCancellation = null;
                }
            });
        }
    }

    private void ApplyStorageDetails(Dictionary<string, DashboardSystemValue> values)
    {
        if (values.Count == 0)
        {
            StorageDetails = [new DashboardDetail(ResourceLoader.GetString("Dashboard_Storage"), "—")];
            OnPropertyChanged(nameof(StorageDetails));
            return;
        }

        if (StorageDetails.Count == values.Count && StorageDetails.All(item => values.ContainsKey(item.Label)))
        {
            // In-place update from old to new values
            foreach (var item in StorageDetails)
            {
                if (values.TryGetValue(item.Label, out var val))
                {
                    item.Update(val.Text, val.Percent);
                }
            }

            return;
        }

        StorageDetails = values.Select(value => new DashboardDetail(value.Key, value.Value.Text, value.Value.Percent, UsageCategory.Disk)).ToArray();
        OnPropertyChanged(nameof(StorageDetails));
    }

    private void ApplyGraphicsReadings(IReadOnlyList<DashboardGpuReading> values)
    {
        if (values.Count == 0)
        {
            GraphicsCards = [new DashboardGpuCard(ResourceLoader.GetString("Dashboard_GpuNotDetected"), [], string.Empty)];
            OnPropertyChanged(nameof(GraphicsCards));
            return;
        }

        if (GraphicsCards.Count == values.Count && GraphicsCards[0].Details.Count == 4)
        {
            bool match = true;
            for (int i = 0; i < values.Count; i++)
            {
                string expectedName = $"GPU {i + 1} · {values[i].Adapter.Name}";
                if (GraphicsCards[i].Name != expectedName)
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                // In-place update from old to new values without recreating progress bars
                for (int i = 0; i < values.Count; i++)
                {
                    var card = GraphicsCards[i];
                    var val = values[i];
                    var sample = val.Telemetry;

                    // Details[0]: Driver
                    card.Details[0].Update(val.Adapter.Driver);

                    // Details[1]: VideoMemory
                    string vramText = sample?.TotalMemoryMiB > 0 && sample.UsedMemoryMiB.HasValue
                        ? $"{sample.UsedMemoryMiB / 1024d:F1} / {sample.TotalMemoryMiB / 1024d:F1} GB"
                        : "—";
                    double? vramPercent = sample?.TotalMemoryMiB > 0
                        ? sample.UsedMemoryMiB * 100d / sample.TotalMemoryMiB
                        : null;
                    card.Details[1].Update(vramText, vramPercent);

                    // Details[2]: GpuLoad
                    string loadText = sample?.UtilizationPercent is { } load ? $"{load}%" : "—";
                    card.Details[2].Update(loadText, sample?.UtilizationPercent);

                    // Details[3]: Temperature
                    string tempText = sample?.TemperatureCelsius is { } temperature ? $"{temperature} °C" : "—";
                    card.Details[3].Update(tempText, sample?.TemperatureCelsius is { } tempVal ? Math.Clamp(tempVal, 0d, 100d) : null);

                    card.StatusResourceKey = sample != null
                        ? string.Empty
                        : val.Adapter.Vendor == "NVIDIA" ? "Dashboard_GpuUnavailable" : "Dashboard_GpuUnsupported";
                    card.Status = string.IsNullOrEmpty(card.StatusResourceKey) ? string.Empty : ResourceLoader.GetString(card.StatusResourceKey);
                }

                return;
            }
        }

        GraphicsCards = values.Select((value, index) =>
        {
            var sample = value.Telemetry;
            string statusKey = sample != null ? string.Empty : value.Adapter.Vendor == "NVIDIA" ? "Dashboard_GpuUnavailable" : "Dashboard_GpuUnsupported";
            return new DashboardGpuCard($"GPU {index + 1} · {value.Adapter.Name}", new[]
            {
                new DashboardDetail(ResourceLoader.GetString("Dashboard_Driver"), value.Adapter.Driver),
                new DashboardDetail(ResourceLoader.GetString("Dashboard_VideoMemory"),
                    sample?.TotalMemoryMiB > 0 && sample.UsedMemoryMiB.HasValue ? $"{sample.UsedMemoryMiB / 1024d:F1} / {sample.TotalMemoryMiB / 1024d:F1} GB" : "—",
                    sample?.TotalMemoryMiB > 0 ? sample.UsedMemoryMiB * 100d / sample.TotalMemoryMiB : null, UsageCategory.Gpu),
                new DashboardDetail(ResourceLoader.GetString("Dashboard_GpuLoad"), sample?.UtilizationPercent is { } load ? $"{load}%" : "—", sample?.UtilizationPercent, UsageCategory.Gpu),
                new DashboardDetail(ResourceLoader.GetString("Dashboard_Temperature"), sample?.TemperatureCelsius is { } temperature ? $"{temperature} °C" : "—", sample?.TemperatureCelsius is { } tempVal ? Math.Clamp(tempVal, 0d, 100d) : null, UsageCategory.Temperature),
            }, string.IsNullOrEmpty(statusKey) ? string.Empty : ResourceLoader.GetString(statusKey)) { StatusResourceKey = statusKey };
        }).ToArray();

        OnPropertyChanged(nameof(GraphicsCards));
    }

    public async Task RefreshSystemAsync(bool force = false)
    {
        if (!CanRefreshSystem)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        systemCancellation = cancellation;
        systemStatusResourceKey = "Dashboard_SystemLoading";
        SystemStatus = ResourceLoader.GetString(systemStatusResourceKey);
        OnPropertyChanged(nameof(SystemStatus));
        OnPropertyChanged(nameof(CanRefreshSystem));

        try
        {
            // Publish each part as it arrives; a slow GPU/disk cannot hide CPU and RAM.
            var times = await Task.WhenAll(
                ReadPart(() => DashboardSystemInfo.ReadUsageAsync(force, cancellation.Token), values =>
                {
                    foreach (var value in values)
                    {
                        systemValues[value.Key] = value.Value;
                    }

                    UpdateSystemDetails();
                }),
                ReadPart(() => DashboardSystemInfo.ReadStorageAsync(force, cancellation.Token), values =>
                {
                    ApplyStorageDetails(values);
                }),
                ReadPart(() => DashboardSystemInfo.ReadGraphicsAsync(force, cancellation.Token), values =>
                {
                    ApplyGraphicsReadings(values);
                })).ConfigureAwait(false);

            Publish(() =>
            {
                systemSampleTimes = times;
                if (times.All(time => time.HasValue))
                {
                    lastSystemRefresh = Stopwatch.GetTimestamp();
                }
                systemStatusResourceKey = times.All(time => time.HasValue) ? "Dashboard_SystemSampleTimes" : "Dashboard_SystemPartial";
                SystemStatus = times.All(time => time.HasValue)
                    ? string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString("Dashboard_SystemSampleTimes"),
                        times[0]!.Value.ToString("T", CultureInfo.CurrentCulture), times[1]!.Value.ToString("T", CultureInfo.CurrentCulture), times[2]!.Value.ToString("T", CultureInfo.CurrentCulture))
                    : ResourceLoader.GetString("Dashboard_SystemPartial");
                OnPropertyChanged(nameof(SystemStatus));
            });
        }
        finally
        {
            if (!dispatcher.TryEnqueue(() =>
            {
                cancellation.Dispose();
                if (systemCancellation == cancellation)
                {
                    systemCancellation = null;
                    OnPropertyChanged(nameof(CanRefreshSystem));
                }
            }))
            {
                cancellation.Dispose();
            }
        }

        async Task<DateTimeOffset?> ReadPart<T>(Func<Task<DashboardSample<T>>> read, Action<T> apply)
        {
            try
            {
                var result = await read().ConfigureAwait(false);
                Publish(() => apply(result.Value));
                return result.Time;
            }
            catch (Exception)
            {
                return null;
            }
        }

        void Publish(Action update) => dispatcher.TryEnqueue(() =>
        {
            try
            {
                if (!isDisposed && overviewActive && !cancellation.IsCancellationRequested && systemCancellation == cancellation)
                {
                    update();
                }
            }
            catch (ObjectDisposedException)
                {
                }
        });
    }

    private static UsageCategory GetSystemDetailCategory(string key) => key switch
    {
        "CpuLoad" => UsageCategory.Cpu,
        "Memory" => UsageCategory.Memory,
        _ => UsageCategory.Default,
    };

    private void UpdateSystemDetails()
    {
        // If items are already populated, update existing items in-place!
        if (SystemDetails.Count == SystemDetailKeys.Length && systemDetailMap.Count == SystemDetailKeys.Length)
        {
            foreach (var key in SystemDetailKeys)
            {
                if (systemValues.TryGetValue(key, out var val) && systemDetailMap.TryGetValue(key, out var detail))
                {
                    detail.Update(val.Text, val.Percent);
                }
            }

            return;
        }

        // Initial build or structure mismatch
        var list = new List<DashboardDetail>();
        systemDetailMap.Clear();
        foreach (var key in SystemDetailKeys)
        {
            if (systemValues.TryGetValue(key, out var val))
            {
                var detail = new DashboardDetail(ResourceLoader.GetString("Dashboard_" + key), val.Text, val.Percent, GetSystemDetailCategory(key));
                list.Add(detail);
                systemDetailMap[key] = detail;
            }
        }

        SystemDetails = list;
        OnPropertyChanged(nameof(SystemDetails));
    }

    public async Task RefreshOverviewAsync()
    {
        if (!CanRefreshOverview)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        overviewCancellation = cancellation;
        overviewStatusResourceKey = "Dashboard_OverviewLoading";
        OverviewStatus = ResourceLoader.GetString(overviewStatusResourceKey);
        OnPropertyChanged(nameof(CanRefreshOverview));
        OnPropertyChanged(nameof(OverviewStatus));

        // Read-only, bounded observations. Do not start NetMap's monitoring session.
        Task network = Task.Run(async () =>
        {
            NetMapOptions options;
            try
            {
                var settings = SettingsUtils.Default.GetSettings<NetMapSettings>(NetMapSettings.ModuleName).Properties;
                options = new(settings?.AsnDatabase?.Value ?? string.Empty, settings?.CityDatabase?.Value ?? string.Empty, settings?.OnlineLookup?.Value ?? true);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                options = new();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                options = new();
            }

            var probe = new NetworkProbe();
            async Task ObserveAsync(bool proxy)
            {
                IdentityResult result;
                try
                {
                    result = await probe.ObserveAsync(proxy, options, cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    result = new(null, ProbeError.Connection, DateTimeOffset.Now, proxy ? "SystemProxy" : "Direct");
                }

                Publish(() =>
                {
                    var card = new NetMapCard(proxy ? "Proxy" : "Direct", new(result, result.Success ? result : null), true);
                    if (proxy)
                    {
                        ProxyEgress = card;
                        OnPropertyChanged(nameof(ProxyEgress));
                    }
                    else
                    {
                        DirectEgress = card;
                        OnPropertyChanged(nameof(DirectEgress));
                    }
                });
            }

            await Task.WhenAll(ObserveAsync(false), ObserveAsync(true)).ConfigureAwait(false);
        }, cancellation.Token);

        try
        {
            await network.ConfigureAwait(false);
            Publish(() =>
            {
                lastOverviewRefresh = Stopwatch.GetTimestamp();
                overviewSampleTime = DateTime.Now;
                overviewStatusResourceKey = "Dashboard_OverviewUpdated";
                OverviewStatus = string.Format(CultureInfo.CurrentCulture,
                    ResourceLoader.GetString(overviewStatusResourceKey), overviewSampleTime.Value.ToString("T", CultureInfo.CurrentCulture));
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            Publish(() =>
            {
                DirectEgress = new("Direct", new(new(null, ProbeError.Connection, DateTimeOffset.Now, "Direct")), true);
                ProxyEgress = new("Proxy", new(new(null, ProbeError.Connection, DateTimeOffset.Now, string.Empty)), true);
                OnPropertyChanged(nameof(DirectEgress));
                OnPropertyChanged(nameof(ProxyEgress));
                overviewStatusResourceKey = "Dashboard_OverviewUnavailable";
                OverviewStatus = ResourceLoader.GetString(overviewStatusResourceKey);
            });
        }
        finally
        {
            if (!dispatcher.TryEnqueue(() =>
            {
                cancellation.Dispose();
                if (overviewCancellation != cancellation)
                {
                    return;
                }

                overviewCancellation = null;
                OnPropertyChanged(nameof(CanRefreshOverview));
                OnPropertyChanged(nameof(OverviewStatus));
            }))
            {
                cancellation.Dispose();
            }
        }

        void Publish(Action update) => dispatcher.TryEnqueue(() =>
        {
            try
            {
                if (!isDisposed && overviewActive && !cancellation.IsCancellationRequested && overviewCancellation == cancellation)
                {
                    update();
                }
            }
            catch (ObjectDisposedException)
                {
                }
        });
    }
}
