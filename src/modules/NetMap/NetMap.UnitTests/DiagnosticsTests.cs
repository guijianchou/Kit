// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetMapLib;

namespace NetMap.UnitTests;

[TestClass]
public sealed class DiagnosticsTests
{
    [TestMethod]
    public void GeographyRequiresMatchingIpAndValidCoordinates()
    {
        const string body = """{"success":true,"ip":"8.8.8.8","country_code":"US","city":"Example","latitude":37.4,"longitude":-122.1,"connection":{"asn":15169,"org":"Example network"}}""";
        var result = NetworkProbe.ParseGeography(body, "8.8.8.8");
        Assert.AreEqual(15169L, result.Asn);
        Assert.AreEqual("ipwho.is", result.Source);
        Assert.AreEqual(37.4, result.Latitude);
        Assert.ThrowsException<FormatException>(() => NetworkProbe.ParseGeography(body, "1.1.1.1"));
        var invalid = NetworkProbe.ParseGeography(body.Replace("37.4", "999"), "8.8.8.8");
        Assert.IsNull(invalid.Latitude);
        Assert.IsNull(invalid.Longitude);
    }

    [TestMethod]
    public async Task MainlandRegionSurvivesOnlineParsingAndCache()
    {
        var result = NetworkProbe.ParseGeography("""{"success":true,"ip":"114.114.114.114","country_code":"CN","region":"江苏省","city":"南京","latitude":32.0616682,"longitude":118.7777846}""", "114.114.114.114");
        using var geo = new GeoDatabase(new(), (_, _) => Task.FromResult(result));
        var located = await geo.LookupAsync("114.114.114.114", CancellationToken.None);
        Assert.AreEqual("江苏省", located.Region);
        Assert.AreEqual("南京", located.City);
        Assert.AreEqual("CN", located.CountryCode);
        Assert.AreEqual(32.0616682, located.Latitude);
        Assert.AreEqual(located, geo.Lookup("114.114.114.114"));
    }

    [DataTestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("10.1.2.3")]
    [DataRow("192.168.1.1")]
    [DataRow("172.16.1.1")]
    [DataRow("100.64.0.1")]
    [DataRow("169.254.0.1")]
    [DataRow("203.0.113.1")]
    [DataRow("::1")]
    [DataRow("fd00::1")]
    [DataRow("2001:db8::1")]
    [DataRow("::ffff:192.168.1.1")]
    public async Task NonPublicAddressesNeverLeaveTheMachine(string ip)
    {
        using var geo = new GeoDatabase(new(), (_, _) => throw new AssertFailedException("Unexpected online lookup"));
        Assert.AreEqual("Private", (await geo.LookupAsync(ip, CancellationToken.None)).LookupStatus);
    }

    [TestMethod]
    public async Task OnlineLookupIsOptionalAndRepeatedIpsShareCachedResult()
    {
        int calls = 0;
        using var geo = new GeoDatabase(new(), (_, _) =>
        {
            calls++;
            return Task.FromResult(new GeoInfo(15169, "Example", Latitude: 37, Longitude: -122, LookupStatus: "Ready", Source: "ipwho.is"));
        });
        var results = await Task.WhenAll(geo.LookupAsync("8.8.8.8", CancellationToken.None), geo.LookupAsync("8.8.8.8", CancellationToken.None));
        Assert.AreEqual(1, calls);
        Assert.AreEqual(15169L, results[1].Asn);
        using var localOnly = new GeoDatabase(new(OnlineLookup: false), (_, _) => throw new AssertFailedException("Online disabled"));
        Assert.AreEqual("LocalUnavailable", (await localOnly.LookupAsync("8.8.8.8", CancellationToken.None)).LookupStatus);
    }

    [TestMethod]
    public async Task LookupFailureIsReportedAndNotRetriedEveryPoll()
    {
        int calls = 0;
        using var geo = new GeoDatabase(new(), (_, _) =>
        {
            calls++;
            return Task.FromResult(new GeoInfo(LookupStatus: "RateLimited"));
        });
        Assert.AreEqual("RateLimited", (await geo.LookupAsync("8.8.8.8", CancellationToken.None)).LookupStatus);
        Assert.AreEqual("RateLimited", (await geo.LookupAsync("8.8.8.8", CancellationToken.None)).LookupStatus);
        Assert.AreEqual("RateLimited", (await geo.LookupAsync("1.1.1.1", CancellationToken.None)).LookupStatus);
        Assert.AreEqual(1, calls);
    }

    [DataTestMethod]
    [DataRow(200, "<html>Welcome</html>", false, false, "PageResponded")]
    [DataRow(200, "<html>Sign in</html>", false, true, "SignIn")]
    [DataRow(200, "<title>Just a moment...</title>", false, false, "BrowserCheck")]
    [DataRow(403, "", true, false, "BrowserCheck")]
    [DataRow(403, "", false, false, "Restricted")]
    [DataRow(401, "", false, false, "AuthRequired")]
    [DataRow(429, "", false, false, "RateLimited")]
    [DataRow(503, "", false, false, "ServerError")]
    [DataRow(302, "", false, false, "Redirect")]
    public void WebsiteResponsesDoNotClaimModelOrAccountAvailability(int status, string body, bool challenge, bool signIn, string expected)
    {
        Assert.AreEqual(expected, NetworkProbe.ClassifyWebsite(status, body, challenge, signIn));
    }

    [TestMethod]
    public async Task DestinationTimeoutDoesNotInventExtraHops()
    {
        using var stop = new CancellationTokenSource();
        int sent = 0;
        IReadOnlyList<HopResult> latest = [];
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => RouteProbe.SampleAsync(
            rows =>
            {
                latest = rows;
                if (rows[0].Sent == 3)
                {
                    stop.Cancel();
                }
            },
            (ttl, _) =>
            {
                Assert.AreEqual(1, ttl);
                return Task.FromResult<(string, double?, bool)>(++sent == 2 ? (string.Empty, null, false) : ("8.8.8.8", 10, true));
            },
            TimeSpan.Zero,
            stop.Token));
        Assert.AreEqual(1, latest.Count);
        Assert.AreEqual(3, latest[0].Sent);
        Assert.AreEqual(2, latest[0].Received);
    }

    [TestMethod]
    public async Task MtrContinuesPastThreeRoundsAndCountsMissingReplies()
    {
        using var stop = new CancellationTokenSource();
        IReadOnlyList<HopResult> latest = [];
        int round = 0;
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => RouteProbe.SampleAsync(
            rows =>
            {
                latest = rows;
                if (rows.Count == 2 && rows[1].Sent == 4)
                {
                    stop.Cancel();
                }
            },
            (ttl, _) =>
            {
                if (ttl == 1)
                {
                    round++;
                }

                return Task.FromResult<(string, double?, bool)>(ttl == 1 && round % 2 == 0 ? (string.Empty, null, false) : (ttl == 1 ? "10.0.0.1" : "8.8.8.8", round * 10, ttl == 2));
            },
            TimeSpan.Zero,
            stop.Token));
        Assert.AreEqual(4, latest[0].Sent);
        Assert.AreEqual(50.0, latest[0].NoResponsePercent);
        Assert.AreEqual(0.0, latest[1].NoResponsePercent);
        Assert.AreEqual(20.0, latest[0].AverageMs);
        Assert.AreEqual(10.0, latest[0].BestMs);
        Assert.AreEqual(30.0, latest[0].WorstMs);
        Assert.IsNull(latest[0].LastMs);
    }
}
