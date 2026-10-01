// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using MaxMind.GeoIP2;

namespace NetMapLib;

public sealed class GeoDatabase : IDisposable
{
    private readonly DatabaseReader? asn;
    private readonly DatabaseReader? city;
    private readonly NetMapOptions options;
    private readonly Func<string, CancellationToken, Task<GeoInfo>> online;
    private readonly ConcurrentDictionary<string, GeoInfo> cache = new();
    private readonly SemaphoreSlim lookupGate = new(1);
    private DateTimeOffset nextLookup;
    private int onlineRequests;
    private bool rateLimited;

    public GeoDatabase(NetMapOptions options)
        : this(options, (ip, token) => NetworkProbe.LookupGeographyAsync(ip, options, token))
    {
    }

    internal GeoDatabase(NetMapOptions options, Func<string, CancellationToken, Task<GeoInfo>> online)
    {
        this.options = options;
        this.online = online;
        asn = Open(string.IsNullOrWhiteSpace(options.AsnDatabase) ? AsnDatabaseUpdater.LocalPath : options.AsnDatabase, "ASN");
        city = Open(options.CityDatabase, "City");
    }

    public string Status => $"{(asn == null ? "0" : "1")}{(city == null ? "0" : "1")}";

    public GeoInfo Lookup(string ip)
    {
        if (cache.TryGetValue(ip, out var cached))
        {
            return cached;
        }

        var result = LookupLocal(ip);
        if (cache.Count >= 512)
        {
            return result with { LookupStatus = "RateLimited" };
        }

        string status = result.Asn != null && result.Latitude != null ? "Ready" : !IsPublicAddress(ip) ? "Private" : options.OnlineLookup ? "Pending" : "LocalUnavailable";
        result = result with { LookupStatus = status, Source = result.Asn != null || result.Latitude != null ? "MMDB" : string.Empty };
        if (cache.Count < 512)
        {
            cache.TryAdd(ip, result);
        }

        return result;
    }

    public async Task<GeoInfo> LookupAsync(string ip, CancellationToken cancellationToken)
    {
        var local = Lookup(ip);
        if (local.LookupStatus != "Pending")
        {
            return local;
        }

        await lookupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            local = Lookup(ip);
            if (local.LookupStatus != "Pending")
            {
                return local;
            }

            // Cache per session; no repeated query for every MTR sample or identity poll.
            if (rateLimited || onlineRequests >= 256)
            {
                return cache[ip] = local with { LookupStatus = "RateLimited" };
            }

            var delay = nextLookup - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            onlineRequests++;
            var result = await online(ip, cancellationToken).ConfigureAwait(false);
            rateLimited = result.LookupStatus == "RateLimited";
            nextLookup = DateTimeOffset.UtcNow.AddSeconds(1);
            bool sameCountry = local.CountryCode.Length == 0 || result.CountryCode.Length == 0 || local.CountryCode == result.CountryCode;
            return cache[ip] = local with
            {
                Asn = local.Asn ?? result.Asn,
                Organization = local.Asn != null ? local.Organization : result.Organization,
                City = local.City.Length > 0 || !sameCountry ? local.City : result.City,
                Region = local.Region.Length > 0 || !sameCountry ? local.Region : result.Region,
                CountryCode = local.CountryCode.Length > 0 ? local.CountryCode : result.CountryCode,
                Latitude = local.Latitude ?? (sameCountry ? result.Latitude : null),
                Longitude = local.Longitude ?? (sameCountry ? result.Longitude : null),
                LookupStatus = result.LookupStatus,
                Source = result.Source.Length == 0 ? local.Source : local.Source.Length == 0 ? result.Source : local.Source + " + " + result.Source,
            };
        }
        finally
        {
            lookupGate.Release();
        }
    }

    internal static bool IsPublicAddress(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address) || IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 &&
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && (bytes[1] == 168 || (bytes[1] == 0 && bytes[2] is 0 or 2))) &&
                !(bytes[0] == 198 && (bytes[1] is 18 or 19 || (bytes[1] == 51 && bytes[2] == 100))) &&
                !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        }

        return (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 1 && bytes[2] == 0x0d && bytes[3] == 0xb8);
    }

    private GeoInfo LookupLocal(string ip)
    {
        long? number = null;
        string organization = string.Empty;
        try
        {
            if (asn != null && asn.TryAsn(ip, out var result))
            {
                number = result.AutonomousSystemNumber;
                organization = result.AutonomousSystemOrganization ?? string.Empty;
            }
        }
        catch (Exception ex) when (ex is IOException or MaxMind.Db.InvalidDatabaseException or ArgumentException)
        {
            // A corrupt optional database must not invalidate an observed egress.
        }

        try
        {
            if (city != null && city.TryCity(ip, out var result))
            {
                return new(number, organization, result.City.Name ?? string.Empty, result.Country.IsoCode ?? string.Empty, result.Location.Latitude, result.Location.Longitude, result.Location.AccuracyRadius, Region: result.MostSpecificSubdivision.Name ?? string.Empty);
            }
        }
        catch (Exception ex) when (ex is IOException or MaxMind.Db.InvalidDatabaseException or ArgumentException)
        {
            // Country-only observations remain usable without city data.
        }

        return new(number, organization);
    }

    public void Dispose()
    {
        asn?.Dispose();
        city?.Dispose();
        lookupGate.Dispose();
    }

    private static DatabaseReader? Open(string path, string type)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var locales = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? new[] { "zh-CN", "en" } : new[] { "en" };
            var reader = new DatabaseReader(path, locales);
            if (reader.Metadata.DatabaseType.EndsWith(type, StringComparison.Ordinal))
            {
                return reader;
            }

            reader.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or MaxMind.Db.InvalidDatabaseException)
        {
            // Optional local data may be unavailable; do not log database paths.
        }

        return null;
    }
}
