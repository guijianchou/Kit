// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NetMapLib;

public sealed class NetworkProbe
{
    public static readonly Uri DirectSource = new("https://api.bilibili.com/x/web-interface/zone");
    public static readonly Uri ProxySource = new("https://1.1.1.1/cdn-cgi/trace");
    private const int BodyLimit = 65536;

    public static Uri ServiceEndpoint(string name) => new(name switch
    {
        "Claude" => "https://api.anthropic.com/cdn-cgi/trace",
        "ChatGPT" => "https://chatgpt.com/cdn-cgi/trace",
        "Gemini" => "https://gemini.google.com/",
        "Google" => "https://www.google.com/",
        _ => throw new ArgumentException("Unknown service.", nameof(name)),
    });

    public async Task<IdentityResult> ObserveAsync(bool isProxy, NetMapOptions options, CancellationToken cancellationToken)
    {
        string route = isProxy ? options.Mode.ToString() : "Direct";
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var uri = isProxy ? ProxySource : DirectSource;
            using var client = CreateClient(uri, isProxy, options, out route);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new(null, ProbeError.Http, DateTimeOffset.Now, route, (int)response.StatusCode);
            }

            var body = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
            var identity = isProxy ? IdentityParser.ParseTrace(body) : IdentityParser.ParseBilibili(body);
            return new(identity, ProbeError.None, DateTimeOffset.Now, route);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(null, ProbeError.Timeout, DateTimeOffset.Now, route);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or FormatException or ArgumentException)
        {
            return new(null, Classify(ex), DateTimeOffset.Now, route);
        }
    }

    public Task<IReadOnlyList<ServiceResult>> CheckServicesAsync(NetMapOptions options, CancellationToken cancellationToken) => CheckServicesAsync(options, _ => { }, cancellationToken);

    public async Task<IReadOnlyList<ServiceResult>> CheckServicesAsync(NetMapOptions options, Action<ServiceResult> progress, CancellationToken cancellationToken)
    {
        string[] endpoints = ["Claude", "ChatGPT", "Gemini", "Google"];
        async Task<ServiceResult> CheckAsync(string name)
        {
            var result = await CheckServiceAsync(name, ServiceEndpoint(name), true, options, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            progress(result);
            return result;
        }

        return await Task.WhenAll(endpoints.Select(CheckAsync)).ConfigureAwait(false);
    }

    public static ProbeError Classify(Exception exception) => exception switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => ProbeError.Dns,
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => ProbeError.Tls,
        HttpRequestException => ProbeError.Connection,
        ArgumentException => ProbeError.InvalidProxy,
        _ => ProbeError.InvalidResponse,
    };

    public static async Task<GeoInfo> LookupGeographyAsync(string ip, NetMapOptions options, CancellationToken cancellationToken)
    {
        if (!GeoDatabase.IsPublicAddress(ip))
        {
            return new(LookupStatus: "Private");
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            string language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en";
            var uri = new Uri("https://ipwho.is/" + Uri.EscapeDataString(ip) + "?lang=" + language);
            using var client = CreateClient(uri, true, options, out _);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new(LookupStatus: response.StatusCode == HttpStatusCode.TooManyRequests ? "RateLimited" : "Unavailable");
            }

            return ParseGeography(await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false), ip);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(LookupStatus: "Unavailable");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            return new(LookupStatus: "Unavailable");
        }
    }

    internal static GeoInfo ParseGeography(string body, string expectedIp)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 12 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("ip", out var ip) || !IPAddress.TryParse(ip.GetString(), out var actual) || !actual.Equals(IPAddress.Parse(expectedIp)))
        {
            throw new FormatException("Invalid geography response.");
        }

        long? asn = null;
        string organization = string.Empty;
        if (root.TryGetProperty("connection", out var connection) && connection.ValueKind == JsonValueKind.Object)
        {
            if (connection.TryGetProperty("asn", out var number) && number.TryGetInt64(out long value) && value > 0)
            {
                asn = value;
            }

            organization = ReadGeoText(connection, "org");
        }

        double? latitude = root.TryGetProperty("latitude", out var lat) && lat.TryGetDouble(out double y) && y is >= -90 and <= 90 ? y : null;
        double? longitude = root.TryGetProperty("longitude", out var lon) && lon.TryGetDouble(out double x) && x is >= -180 and <= 180 ? x : null;
        if (latitude == null || longitude == null)
        {
            latitude = longitude = null;
        }

        var country = ReadGeoText(root, "country_code");
        if (country.Length != 2 || !country.All(char.IsAsciiLetterUpper))
        {
            country = string.Empty;
        }

        return new(asn, organization, ReadGeoText(root, "city"), country, latitude, longitude, LookupStatus: asn != null || latitude != null ? "Ready" : "NotFound", Source: "ipwho.is", Region: ReadGeoText(root, "region"));
    }

    internal static string ClassifyWebsite(int status, string body, bool challenge, bool signIn)
    {
        if (challenge || body.Contains("<title>Just a moment", StringComparison.OrdinalIgnoreCase) || body.Contains("id=\"challenge-form\"", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("id='challenge-form'", StringComparison.OrdinalIgnoreCase) || (status == 403 && body.Contains("_cf_chl_opt", StringComparison.Ordinal)))
        {
            return "BrowserCheck";
        }

        return status switch
        {
            401 => "AuthRequired",
            403 => "Restricted",
            451 => "Restricted",
            429 => "RateLimited",
            >= 500 => "ServerError",
            >= 300 and < 400 => "Redirect",
            >= 200 and < 300 => signIn ? "SignIn" : "PageResponded",
            _ => "HttpError",
        };
    }

    private static string ReadGeoText(JsonElement root, string key)
    {
        string value = root.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? string.Empty : string.Empty;
        return value[..Math.Min(value.Length, 160)];
    }

    public static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > BodyLimit)
        {
            throw new FormatException("Identity response too large.");
        }

        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[BodyLimit + 1];
        int count = 0;
        while (count < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return Encoding.UTF8.GetString(buffer, 0, count);
            }

            count += read;
        }

        throw new FormatException("Identity response too large.");
    }

    internal static HttpClient CreateClient(Uri target, bool isProxy, NetMapOptions options, out string route, bool followRedirects = false)
    {
        IWebProxy? proxy = null;
        route = isProxy ? "SystemRoute" : "Direct";
        if (isProxy && options.Mode == ProxyMode.Explicit)
        {
            if (!NetMapOptions.TryGetProxy(options.ProxyAddress, out var address))
            {
                throw new ArgumentException("Invalid proxy address.");
            }

            proxy = new WebProxy(address!);
            route = "Explicit";
        }
        else if (isProxy && options.Mode == ProxyMode.System)
        {
            // Get a fresh Windows proxy resolver, not HttpClient.DefaultProxy's cached instance.
            proxy = WebRequest.GetSystemWebProxy();
            route = proxy.IsBypassed(target) ? "SystemBypass" : "SystemProxy";
        }

        var handler = new SocketsHttpHandler
        {
            UseProxy = proxy != null,
            Proxy = proxy,
            AllowAutoRedirect = followRedirects,
            MaxAutomaticRedirections = 5,
            UseCookies = followRedirects,
            AutomaticDecompression = followRedirects ? DecompressionMethods.All : DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxResponseHeadersLength = followRedirects ? 64 : 16,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kit-NetMap/1.0");
        return client;
    }

    internal static async Task<ServiceResult> CheckServiceAsync(string name, Uri uri, bool isProxy, NetMapOptions options, CancellationToken cancellationToken, HttpClient? client = null)
    {
        ServiceResult? observed = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            long started = Stopwatch.GetTimestamp();
            using var http = client ?? CreateClient(uri, isProxy, options, out _, followRedirects: name is "Gemini" or "Google");
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            double responseMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            bool signIn = finalUri.Host == "accounts.google.com" || finalUri.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase) || finalUri.AbsolutePath.Contains("signin", StringComparison.OrdinalIgnoreCase);
            bool challenge = response.Headers.TryGetValues("cf-mitigated", out var mitigation) && mitigation.Any(value => string.Equals(value, "challenge", StringComparison.OrdinalIgnoreCase));
            observed = new(name, isProxy, ProbeError.None, (int)response.StatusCode, DateTimeOffset.Now, ClassifyWebsite((int)response.StatusCode, string.Empty, challenge, signIn), responseMs);
            cancellationToken.ThrowIfCancellationRequested();

            // A slow or interrupted body must not erase successful HTTP connectivity evidence.
            using var bodyDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            bodyDeadline.CancelAfter(TimeSpan.FromSeconds(2));
            using var stream = await response.Content.ReadAsStreamAsync(bodyDeadline.Token).ConfigureAwait(false);
            var buffer = new byte[BodyLimit];
            int count = 0;
            bool incomplete = false;
            try
            {
                while (count < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(count), bodyDeadline.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    count += read;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is OperationCanceledException or IOException or HttpRequestException)
            {
                incomplete = true;
            }

            string body = Encoding.UTF8.GetString(buffer, 0, count);
            string outcome = ClassifyWebsite((int)response.StatusCode, body, challenge, signIn);
            if ((name is "ChatGPT" or "Claude") && response.IsSuccessStatusCode && outcome != "BrowserCheck")
            {
                if (!incomplete && count < buffer.Length)
                {
                    try
                    {
                        return observed with { Outcome = "TraceResponded", Checkpoint = IdentityParser.ParseTrace(body) };
                    }
                    catch (FormatException)
                    {
                        // A 200 HTML page is not a valid trace checkpoint.
                    }
                }

                return observed with { Outcome = "TraceInvalid", BodyIncomplete = incomplete || count == buffer.Length };
            }

            return observed with { Outcome = outcome, BodyIncomplete = incomplete };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return observed != null ? observed with { BodyIncomplete = true, Outcome = (name is "ChatGPT" or "Claude") && observed.Status is >= 200 and < 300 ? "TraceInvalid" : observed.Outcome } : new(name, isProxy, ProbeError.Timeout, null, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException)
        {
            return observed != null ? observed with { BodyIncomplete = true, Outcome = (name is "ChatGPT" or "Claude") && observed.Status is >= 200 and < 300 ? "TraceInvalid" : observed.Outcome } : new(name, isProxy, Classify(ex), null, DateTimeOffset.Now);
        }
    }
}
