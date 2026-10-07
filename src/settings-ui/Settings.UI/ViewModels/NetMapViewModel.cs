// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.Library.Interfaces;
using Kit.Settings.UI.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using NetMapLib;

namespace Kit.Settings.UI.ViewModels;

public sealed partial class NetMapHealthStyleConverter : IValueConverter
{
    public ResourceDictionary Styles { get; set; } = new();

    public object Convert(object value, Type targetType, object parameter, string language) => Styles[(value is ProbeHealth health ? health : ProbeHealth.Unknown).ToString() + parameter];

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class NetMapCard
{
    public NetMapCard(string title, ChannelState state, bool running)
    {
        Title = NetMapViewModel.Text(title);
        var identity = state.LastSuccess?.Identity;
        Ip = state.ConsecutiveFailures >= 3 ? "N/A" : identity?.Ip ?? "—";
        Location = state.ConsecutiveFailures >= 3 ? "N/A" : NetMapViewModel.LocationText(identity, state.Geography);
        Health = !running || state.Latest == null ? ProbeHealth.Unknown : state.Latest.Success ? ProbeHealth.Good : ProbeHealth.Error;

        Asn = state.ConsecutiveFailures >= 3 ? "N/A" : state.Geography?.Asn is { } number ? $"AS{number} · {state.Geography.Organization}" : NetMapViewModel.Text("Asn_" + (string.IsNullOrEmpty(state.Geography?.LookupStatus) ? "NotFound" : state.Geography.LookupStatus));
        GeographySource = NetMapViewModel.Text("GeoSource") + " " + (string.IsNullOrEmpty(state.Geography?.Source) ? "—" : state.Geography.Source);
        Timestamp = state.LastSuccess == null ? NetMapViewModel.Text("NoSuccess") : NetMapViewModel.Text("LastSuccess") + " " + state.LastSuccess.Time.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        Status = !running ? NetMapViewModel.Text("Stopped") : state.Latest == null ? NetMapViewModel.Text("Waiting") : state.Latest.Success ? NetMapViewModel.Text("Responded") : NetMapViewModel.ErrorText(state.Latest.Error, state.Latest.HttpStatus);
        if (state.ConsecutiveFailures > 0)
        {
            Status += $" · {state.ConsecutiveFailures}/3";
        }
        Route = state.Latest == null ? string.Empty : NetMapViewModel.Text("Route_" + state.Latest.Route);
        Source = title == "Direct" ? "api.bilibili.com/x/web-interface/zone" : "1.1.1.1/cdn-cgi/trace";
        IsStale = state.LastSuccess != null && (!running || state.Latest?.Success != true);
        Freshness = IsStale ? NetMapViewModel.Text("LastKnown") : string.Empty;
    }

    public string Title { get; }

    public ProbeHealth Health { get; }

    public string Ip { get; }

    public string Location { get; }

    public string Asn { get; }

    public string GeographySource { get; }

    public string Timestamp { get; }

    public string Status { get; }

    public string Route { get; }

    public string Source { get; }

    public string Freshness { get; }

    public bool IsStale { get; }
}

public sealed class NetMapServiceRow(string name) : Observable
{
    private ServiceResult? result;
    private bool running;
    private int upstreamFailures;

    public string Name { get; } = name;

    public string Endpoint { get; } = NetworkProbe.ServiceEndpoint(name).AbsoluteUri;

    public string Proxy => NetMapViewModel.ServiceText(result);

    public ProbeHealth Health => !running ? ProbeHealth.Unknown : result?.Health ?? (upstreamFailures > 0 ? ProbeHealth.Error : ProbeHealth.Unknown);

    public string Status => NetMapViewModel.Text(!running ? "Stopped" : result == null ? (upstreamFailures > 0 ? "ProxyUnavailable" : "Waiting") : Health == ProbeHealth.Error ? "Disconnected" :
        Health == ProbeHealth.Good ? "Connected" : result.Outcome is "TraceResponded" or "PageResponded" && !result.BodyIncomplete ? "Slow" : "NeedsAttention");

    public string Latency => result?.ConsecutiveFailures >= 3 || (result == null && upstreamFailures >= 3) ? "N/A" :
        (result?.ElapsedMs ?? result?.LastKnownMs) is { } elapsed ? elapsed.ToString("F1", CultureInfo.CurrentCulture) + " ms" : "—";

    public void Update(ServiceResult? value, bool active, int failures)
    {
        if (result == value && running == active && upstreamFailures == failures)
        {
            return;
        }

        result = value;
        running = active;
        upstreamFailures = failures;
        OnPropertyChanged(nameof(Proxy));
        OnPropertyChanged(nameof(Health));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Latency));
    }
}

public sealed class NetMapHopRow(int ttl) : Observable
{
    private HopResult? result;
    private bool running;

    public string Ttl { get; } = ttl.ToString(CultureInfo.CurrentCulture);

    public string Ip => result?.ConsecutiveFailures >= 3 ? "N/A" : string.IsNullOrEmpty(result?.Ip) ? "*" : result.Ip;

    public string Location => Ip is "*" or "N/A" ? "—" : NetMapViewModel.LocationText(null, result?.Geography);

    public string Loss => result?.Sent > 0 ? result.NoResponsePercent.ToString("F1", CultureInfo.CurrentCulture) + "%" : "—";

    public string Last => result?.ConsecutiveFailures >= 3 ? "N/A" : Format(result?.LastMs ?? result?.LastKnownMs);

    public ProbeHealth Health => running ? result?.Health ?? ProbeHealth.Unknown : ProbeHealth.Unknown;

    public string Rtt => Format(result?.AverageMs);

    public string MapLabel => $"{Ttl} · {Last} ms · {Loss}";

    public string Detail => $"{Ttl}: {Ip} · {Location} · " + string.Format(CultureInfo.CurrentCulture, NetMapViewModel.Text("HopDetail"), result?.Sent ?? 0, result?.NoResponsePercent ?? 0, Last, Rtt) +
        " · " + NetMapViewModel.Text("BestWorst") + " " + Format(result?.BestMs) + " / " + Format(result?.WorstMs) + " ms" +
        (result?.Geography?.Asn is { } asn ? $" · AS{asn} {result.Geography.Organization}" : string.Empty) +
        (result?.LastMs == null && result?.LastKnownMs != null ? " · " + NetMapViewModel.Text("LastKnown") : string.Empty);

    public void Update(HopResult value, bool active)
    {
        if (result == value && running == active)
        {
            return;
        }

        result = value;
        running = active;
        OnPropertyChanged(nameof(Ip));
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(Loss));
        OnPropertyChanged(nameof(Last));
        OnPropertyChanged(nameof(Rtt));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(MapLabel));
        OnPropertyChanged(nameof(Health));
    }

    private static string Format(double? value) => value?.ToString("F1", CultureInfo.CurrentCulture) ?? "—";
}

public sealed class NetMapViewModel : Observable, IDisposable
{
    private static readonly string[] ServiceNames = ["Claude", "ChatGPT", "Gemini", "Google"];
    private readonly ISettingsRepository<GeneralSettings> general;
    private readonly DispatcherQueue dispatcher;
    private readonly NetMapSession session = new();
    private NetMapOptions options;
    private NetMapSnapshot snapshot = NetMapSnapshot.Empty;
    private bool pageActive;
    private bool disposed;
    private string message = string.Empty;
    private int proxyMode;
    private string proxyAddress = string.Empty;
    private string asnDatabase = string.Empty;
    private string cityDatabase = string.Empty;
    private bool onlineLookup;
    private CancellationTokenSource? updateCancellation;
    private bool isUpdating;
    private double updateProgress;
    private string updateStatus = string.Empty;

    public NetMapViewModel()
    {
        dispatcher = DispatcherQueue.GetForCurrentThread();
        general = SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default);
        var settings = SettingsUtils.Default.GetSettingsOrDefault<NetMapSettings>(NetMapSettings.ModuleName).Properties ?? new NetMapProperties();
        proxyMode = settings.ProxyMode?.Value ?? 0;
        proxyAddress = settings.ProxyAddress?.Value ?? string.Empty;
        asnDatabase = settings.AsnDatabase?.Value ?? string.Empty;
        cityDatabase = settings.CityDatabase?.Value ?? string.Empty;
        onlineLookup = settings.OnlineLookup?.Value ?? true;
        options = DraftOptions();
        general.SettingsChanged += OnGeneralChanged;
        session.Changed += OnSnapshot;
    }

    public event Action? MapChanged;

    public NetMapSnapshot Snapshot => snapshot;

    public NetMapCard Direct => new("Direct", snapshot.Direct, snapshot.Running);

    public NetMapCard Proxy => new("Proxy", snapshot.Proxy, snapshot.Running);

    public bool IsEnabled
    {
        get => general.SettingsConfig.Enabled.NetMap;
        set
        {
            if (value != IsEnabled)
            {
                general.SettingsConfig.Enabled.NetMap = value;
                RefreshEnabledState();
                ShellPage.SendDefaultIPCMessage(new OutGoingGeneralSettings(general.SettingsConfig).ToString());
            }
        }
    }

    public bool IsDetectionOn
    {
        get => snapshot.Running;
        set
        {
            if (value && IsEnabled && pageActive && !disposed && !IsUpdating)
            {
                if (!options.IsValid)
                {
                    Message = Text("InvalidProxy");
                    OnPropertyChanged();
                    return;
                }

                Message = string.Empty;
                session.Start(options);
            }
            else
            {
                session.Stop();
            }

            ApplySnapshot(session.Current);
        }
    }

    public bool CanStart => IsEnabled && !IsDetectionOn && !IsUpdating;

    public bool CanUpdate => IsEnabled && !IsUpdating;

    public bool IsUpdating => isUpdating;

    public double UpdateProgress
    {
        get => updateProgress;
        private set => Set(ref updateProgress, value);
    }

    public string UpdateStatus
    {
        get => updateStatus;
        private set => Set(ref updateStatus, value);
    }

    public string RunStatus => snapshot.Faulted ? Text("UnexpectedFailure") : snapshot.Running ? Text(snapshot.Stabilizing ? "Stabilizing" : "Running") : Text("Stopped");

    public string EvidenceNote => snapshot.Direct.Latest?.Success == true && snapshot.Proxy.Latest?.Success == true && snapshot.Direct.Latest.Identity!.Ip == snapshot.Proxy.Latest.Identity!.Ip ? Text("SameEgress") : Text("RouteBoundary");

    public string RouteStatus => !snapshot.Running ? Text("Stopped") : snapshot.DiagnosticFailed ? Text("DiagnosticFailed") : snapshot.Stabilizing ? Text("Stabilizing") : snapshot.Proxy.LastSuccess?.Identity?.Ip.Contains(':') == true ? Text("Ipv6Route") : snapshot.RouteComplete ? Text("RouteComplete") : snapshot.Hops.Count == 0 ? Text("NoRoute") : Text("RouteSampling");

    public string RouteTarget => Text("ThisDevice") + " → " + (snapshot.Proxy.LastSuccess?.Identity?.Ip ?? Text("WaitingProxy"));

    public string DatabaseStatus => Text("Database_" + snapshot.DatabaseStatus) + " · " + Text(options.OnlineLookup ? "OnlineEnabled" : "OnlineDisabled");

    public string ServiceTimestamp => snapshot.Services.Count == 0 ? Text("NoServices") : Text("MeasuredAt") + " " + snapshot.Services.Max(item => item.Time).ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public IReadOnlyList<NetMapServiceRow> Services { get; } = ServiceNames.Select(name => new NetMapServiceRow(name)).ToArray();

    public ObservableCollection<NetMapHopRow> Hops { get; } = new();

    public ProbeHealth TargetHealth => !snapshot.Running ? ProbeHealth.Unknown : snapshot.DiagnosticFailed || snapshot.Proxy.ConsecutiveFailures > 0 ? ProbeHealth.Error :
        snapshot.Hops.LastOrDefault(hop => hop.Ip == snapshot.Proxy.LastSuccess?.Identity?.Ip)?.Health ?? (snapshot.Hops.Count >= 24 ? ProbeHealth.Error : ProbeHealth.Unknown);

    public string TargetLatency
    {
        get
        {
            var target = snapshot.Hops.LastOrDefault(hop => hop.Ip == snapshot.Proxy.LastSuccess?.Identity?.Ip);
            if (target == null)
            {
                return snapshot.Proxy.ConsecutiveFailures >= 3 || (snapshot.Hops.Count >= 24 && snapshot.Hops[^1].Sent >= 3) ? Text("TargetLatency") + " · N/A" : Text("TargetPending");
            }

            string last = target.ConsecutiveFailures >= 3 ? "N/A" : (target.LastMs ?? target.LastKnownMs)?.ToString("F1", CultureInfo.CurrentCulture) ?? "—";
            return Text("TargetLatency") + " · " + string.Format(CultureInfo.CurrentCulture, Text("HopDetail"), target.Sent, target.NoResponsePercent, last, target.AverageMs?.ToString("F1", CultureInfo.CurrentCulture) ?? "—") +
                (target.LastMs == null && target.LastKnownMs != null ? " · " + Text("LastKnown") : string.Empty);
        }
    }

    public int ProxyModeIndex
    {
        get => proxyMode;
        set => Set(ref proxyMode, value);
    }

    public string ProxyAddress
    {
        get => proxyAddress;
        set => Set(ref proxyAddress, value);
    }

    public string AsnDatabase
    {
        get => asnDatabase;
        set => Set(ref asnDatabase, value);
    }

    public string CityDatabase
    {
        get => cityDatabase;
        set => Set(ref cityDatabase, value);
    }

    public bool OnlineLookup
    {
        get => onlineLookup;
        set => Set(ref onlineLookup, value);
    }

    public string Message
    {
        get => message;
        private set => Set(ref message, value);
    }

    public static string Text(string key) => ResourceLoaderInstance.ResourceLoader.GetString("NetMap_" + key);

    public static string LocationText(EgressIdentity? identity, GeoInfo? geography)
    {
        string code = string.IsNullOrEmpty(identity?.CountryCode) ? geography?.CountryCode ?? string.Empty : identity.CountryCode;
        if (!string.IsNullOrEmpty(geography?.CountryCode) && code != geography.CountryCode)
        {
            // Do not attach an overseas city to an observed mainland egress (or vice versa).
            geography = null;
        }

        bool chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
        string countryName = code;
        if (code.Length == 2)
        {
            try
            {
                if (code == "CN")
                {
                    countryName = Text("MainlandChina");
                }
                else if (chinese)
                {
                    string? name = WorldMap.Countries.FirstOrDefault(country => country.Code == code)?.ChineseName;
                    countryName = string.IsNullOrEmpty(name) ? code : name;
                }
                else
                {
                    countryName = new RegionInfo(code).EnglishName;
                }
            }
            catch (ArgumentException)
            {
                countryName = code;
            }
        }

        string region = geography?.Region ?? string.Empty;
        string city = geography?.City ?? string.Empty;
        if (chinese && !string.IsNullOrEmpty(identity?.Region))
        {
            region = identity.Region;
            city = identity.City;
        }

        if (region.TrimEnd('省', '市') == city.TrimEnd('省', '市'))
        {
            city = string.Empty;
        }

        string location = string.Join(" · ", new[] { countryName, region, city }.Where(part => !string.IsNullOrWhiteSpace(part)).Distinct(StringComparer.OrdinalIgnoreCase));
        if (location.Length > 0)
        {
            return location;
        }

        return !string.IsNullOrEmpty(identity?.Location) ? identity.Location : Text(geography?.LookupStatus switch
        {
            "Private" => "LocPrivate",
            "Pending" => "LocPending",
            "RateLimited" => "LocLimited",
            _ => "UnknownLocation",
        });
    }

    public static string ErrorText(ProbeError error, int? status) => error == ProbeError.Http ? "HTTP " + status : Text("Error_" + error);

    public async Task UpdateAsnAsync()
    {
        if (!CanUpdate || !pageActive || disposed)
        {
            return;
        }

        if (!options.IsValid)
        {
            UpdateStatus = Text("InvalidProxy");
            return;
        }

        using var source = new CancellationTokenSource();
        updateCancellation = source;
        isUpdating = true;
        OnPropertyChanged(nameof(IsUpdating));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(CanStart));
        IsDetectionOn = false;
        UpdateProgress = 0;
        UpdateStatus = Text("UpdateWorking");
        var progress = new Progress<double>(value =>
        {
            if (ReferenceEquals(updateCancellation, source) && !source.IsCancellationRequested)
            {
                UpdateProgress = value;
            }
        });
        try
        {
            await session.Completion.WaitAsync(source.Token);
            var result = await Task.Run(() => new AsnDatabaseUpdater(options).UpdateAsync(progress, source.Token));
            UpdateProgress = 100;
            UpdateStatus = Text(result.AlreadyCurrent ? "UpdateCurrent" : "UpdateDone") + " · " + result.Version + "\nSHA-256: " + result.Sha256;
        }
        catch (OperationCanceledException)
        {
            UpdateStatus = Text(source.IsCancellationRequested ? "UpdateCancelled" : "UpdateTimeout");
        }
        catch (InvalidDataException ex)
        {
            string code = ex.Message is "HashUnavailable" or "HashMismatch" or "TooLarge" ? ex.Message : "InvalidData";
            UpdateStatus = Text("Update" + code);
        }
        catch (HttpRequestException)
        {
            UpdateStatus = Text("UpdateConnection");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or MaxMind.Db.InvalidDatabaseException)
        {
            UpdateStatus = Text("UpdateInvalidData");
        }
        catch (Exception)
        {
            // Keep paths, redirect query strings and server response bodies out of the UI and logs.
            UpdateStatus = Text("UpdateFailed");
        }
        finally
        {
            updateCancellation = null;
            isUpdating = false;
            OnPropertyChanged(nameof(IsUpdating));
            OnPropertyChanged(nameof(CanUpdate));
            OnPropertyChanged(nameof(CanStart));
        }
    }

    public void CancelDataUpdate() => updateCancellation?.Cancel();

    public void ApplySettings()
    {
        var draft = DraftOptions();
        if (!draft.IsValid)
        {
            Message = Text("InvalidProxy");
            return;
        }

        IsDetectionOn = false;
        options = draft;
        var settings = new NetMapSettings
        {
            Properties = new NetMapProperties
            {
                ProxyMode = new IntProperty((int)options.Mode),
                ProxyAddress = new StringProperty(options.ProxyAddress),
                AsnDatabase = new StringProperty(options.AsnDatabase),
                CityDatabase = new StringProperty(options.CityDatabase),
                OnlineLookup = new BoolProperty(options.OnlineLookup),
            },
        };
        var outgoing = new SndModuleSettings<SndNetMapSettings>(new SndNetMapSettings { Settings = settings });
        ShellPage.SendDefaultIPCMessage(JsonSerializer.Serialize(outgoing, SettingsSerializationContext.Default.SndModuleSettingsSndNetMapSettings));
        Message = Text("SettingsApplied");
    }

    public void SetPageActive(bool active)
    {
        pageActive = active;
        if (!active)
        {
            CancelDataUpdate();
        }
    }

    public void RefreshEnabledState()
    {
        if (disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanUpdate));
        if (!IsEnabled)
        {
            CancelDataUpdate();
            IsDetectionOn = false;
        }
    }

    public void Dispose()
    {
        CancelDataUpdate();
        disposed = true;
        general.SettingsChanged -= OnGeneralChanged;
        session.Changed -= OnSnapshot;
        session.Dispose();
    }

    internal static string ServiceText(ServiceResult? result)
    {
        if (result == null)
        {
            return "—";
        }

        string kind = string.IsNullOrEmpty(result.Outcome) ? "Responded" : result.Outcome;
        var identity = result.ConsecutiveFailures >= 3 ? null : result.Checkpoint ?? result.LastKnownCheckpoint;
        string checkpoint = identity != null ? $"\n{identity.Ip} · {LocationText(identity, null)}" : string.Empty;
        string detail = result.Error != ProbeError.None ? ErrorText(result.Error, result.Status) : $"{Text(kind)} · HTTP {result.Status}";
        if (result.ConsecutiveFailures > 0)
        {
            detail += $" · {result.ConsecutiveFailures}/3";
        }

        if (result.LastKnownMs != null || result.LastKnownCheckpoint != null)
        {
            detail += " · " + Text("LastKnown");
        }

        return detail + (result.BodyIncomplete ? " · " + Text("BodyIncomplete") : string.Empty) + checkpoint;
    }

    private NetMapOptions DraftOptions() => new((ProxyMode)proxyMode, proxyAddress.Trim(), asnDatabase.Trim(), cityDatabase.Trim(), onlineLookup);

    private void OnGeneralChanged(GeneralSettings settings) => dispatcher.TryEnqueue(RefreshEnabledState);

    private void OnSnapshot(NetMapSnapshot value) => dispatcher.TryEnqueue(() =>
    {
        // A queued callback may belong to an already stopped/replaced session.
        if (!disposed && ReferenceEquals(value, session.Current))
        {
            ApplySnapshot(value);
        }
    });

    private void ApplySnapshot(NetMapSnapshot value)
    {
        var previous = snapshot;
        snapshot = value;
        if (previous.Direct != value.Direct || previous.Running != value.Running)
        {
            OnPropertyChanged(nameof(Direct));
        }

        if (previous.Proxy != value.Proxy || previous.Running != value.Running)
        {
            OnPropertyChanged(nameof(Proxy));
        }

        OnPropertyChanged(nameof(IsDetectionOn));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(RunStatus));
        OnPropertyChanged(nameof(EvidenceNote));
        OnPropertyChanged(nameof(RouteStatus));
        OnPropertyChanged(nameof(RouteTarget));
        OnPropertyChanged(nameof(DatabaseStatus));
        if (!ReferenceEquals(previous.Services, value.Services) || previous.Running != value.Running || previous.Proxy.ConsecutiveFailures != value.Proxy.ConsecutiveFailures)
        {
            foreach (var row in Services)
            {
                row.Update(value.Services.FirstOrDefault(item => item.Name == row.Name && item.IsProxy), value.Running, value.Proxy.ConsecutiveFailures);
            }

            OnPropertyChanged(nameof(ServiceTimestamp));
        }

        if (previous.Generation != value.Generation)
        {
            Hops.Clear();
        }

        for (int i = 0; i < value.Hops.Count; i++)
        {
            if (i == Hops.Count)
            {
                Hops.Add(new NetMapHopRow(value.Hops[i].Ttl));
            }

            Hops[i].Update(value.Hops[i], value.Running);
        }

        while (Hops.Count > value.Hops.Count)
        {
            Hops.RemoveAt(Hops.Count - 1);
        }

        OnPropertyChanged(nameof(TargetLatency));
        OnPropertyChanged(nameof(TargetHealth));
        MapChanged?.Invoke();
    }
}
