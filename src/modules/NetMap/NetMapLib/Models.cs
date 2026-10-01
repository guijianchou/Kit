// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace NetMapLib;

public enum ProxyMode
{
    System,
    Explicit,
    SystemRoute,
}

public enum ProbeError
{
    None,
    Timeout,
    Dns,
    Connection,
    Tls,
    InvalidResponse,
    Http,
    InvalidProxy,
}

public enum ProbeHealth
{
    Unknown,
    Good,
    Warning,
    Error,
}

public sealed record NetMapOptions(ProxyMode Mode = ProxyMode.System, string ProxyAddress = "", string AsnDatabase = "", string CityDatabase = "", bool OnlineLookup = true)
{
    public bool IsValid => Enum.IsDefined(Mode) &&
        (string.IsNullOrEmpty(ProxyAddress) ? Mode != ProxyMode.Explicit : TryGetProxy(ProxyAddress, out _));

    public static bool TryGetProxy(string address, out Uri? proxy)
    {
        proxy = null;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks5") ||
            string.IsNullOrEmpty(uri.Host) || uri.Port < 1 || uri.Port > 65535 ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        proxy = uri;
        return true;
    }
}

public sealed record GeoInfo(long? Asn = null, string Organization = "", string City = "", string CountryCode = "", double? Latitude = null, double? Longitude = null, int? AccuracyKm = null, string LookupStatus = "", string Source = "", string Region = "");

public sealed record EgressIdentity(string Ip, string CountryCode, string Location, string Isp = "", string Region = "", string City = "");

public sealed record IdentityResult(EgressIdentity? Identity, ProbeError Error, DateTimeOffset Time, string Route, int? HttpStatus = null)
{
    public bool Success => Identity != null && Error == ProbeError.None;
}

public sealed record ChannelState(IdentityResult? Latest = null, IdentityResult? LastSuccess = null, GeoInfo? Geography = null, int ConsecutiveFailures = 0);

public sealed record ServiceResult(string Name, bool IsProxy, ProbeError Error, int? Status, DateTimeOffset Time, string Outcome = "", double? ElapsedMs = null, bool BodyIncomplete = false, EgressIdentity? Checkpoint = null, int ConsecutiveFailures = 0, double? LastKnownMs = null, EgressIdentity? LastKnownCheckpoint = null)
{
    public bool Failed => Error != ProbeError.None || Status >= 500 || Outcome is "TraceInvalid" or "HttpError";

    public ProbeHealth Health => Error != ProbeError.None || Status >= 500 || Outcome == "HttpError" ? ProbeHealth.Error :
        BodyIncomplete || Outcome is not ("TraceResponded" or "PageResponded") || ElapsedMs is null or > 75 ? ProbeHealth.Warning : ProbeHealth.Good;
}

public sealed record HopResult(int Ttl, string Ip, int Sent, int Received, double? LastMs, double? AverageMs, bool Reached, GeoInfo? Geography = null, double? BestMs = null, double? WorstMs = null, int ConsecutiveFailures = 0, double? LastKnownMs = null)
{
    public double NoResponsePercent => Sent == 0 ? 0 : (Sent - Received) * 100.0 / Sent;

    public ProbeHealth Health => Sent == 0 ? ProbeHealth.Unknown : LastMs == null ? ProbeHealth.Error : LastMs <= 75 ? ProbeHealth.Good : ProbeHealth.Warning;
}

public sealed record NetMapSnapshot(
    bool Running,
    long Generation,
    ChannelState Direct,
    ChannelState Proxy,
    IReadOnlyList<ServiceResult> Services,
    IReadOnlyList<HopResult> Hops,
    bool Stabilizing = false,
    bool RouteComplete = false,
    string DatabaseStatus = "00",
    bool Faulted = false,
    bool DiagnosticFailed = false)
{
    public static NetMapSnapshot Empty { get; } = new(false, 0, new(), new(), [], []);
}
