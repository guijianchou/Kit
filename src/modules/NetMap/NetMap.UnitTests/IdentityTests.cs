// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetMapLib;

namespace NetMap.UnitTests;

[TestClass]
public sealed class IdentityTests
{
    [TestMethod]
    public void ZoneUsesActualCountryAndDoesNotAssumeMainland()
    {
        var result = IdentityParser.ParseBilibili("""{"code":0,"data":{"addr":"203.0.113.1","country":"日本","city":"Tokyo"}}""");
        Assert.AreEqual("203.0.113.1", result.Ip);
        Assert.AreEqual(string.Empty, result.CountryCode);
        StringAssert.Contains(result.Location, "日本");
    }

    [TestMethod]
    public void TraceParsesCrLfAndIpv6WithoutJson()
    {
        var result = IdentityParser.ParseTrace("fl=1\r\nip=2001:db8::1\r\nloc=JP\r\ncolo=NRT\r\n");
        Assert.AreEqual("2001:db8::1", result.Ip);
        Assert.AreEqual("JP", result.CountryCode);
    }

    [DataTestMethod]
    [DataRow("<html>blocked</html>")]
    [DataRow("ip=1.2.3\nloc=JP")]
    [DataRow("ip=127.0.0.1\nloc=JP")]
    [DataRow("ip=203.0.113.1\nloc=JPN")]
    [DataRow("ip=203.0.113.1\nip=203.0.113.2\nloc=JP")]
    [DataRow("ip=fe80::1%4\nloc=JP")]
    public void TraceRejectsInvalidIdentities(string body)
    {
        Assert.ThrowsException<FormatException>(() => IdentityParser.ParseTrace(body));
    }

    [TestMethod]
    public void ZoneRejectsApiFailure()
    {
        Assert.ThrowsException<FormatException>(() => IdentityParser.ParseBilibili("""{"code":-403,"data":null}"""));
    }

    [TestMethod]
    public void ZonePreservesMainlandProvinceAndCity()
    {
        var result = IdentityParser.ParseBilibili("""{"code":0,"data":{"addr":"192.0.2.16","country":"中国","province":"江苏省","city":"南京市"}}""");
        Assert.AreEqual("CN", result.CountryCode);
        Assert.AreEqual("江苏省", result.Region);
        Assert.AreEqual("南京市", result.City);
    }

    [TestMethod]
    public void ConflictingGeoCountryCannotMoveObservedMainlandEgressAbroad()
    {
        Assert.AreEqual(WorldMap.Locate("CN", null), WorldMap.Locate("CN", new(CountryCode: "US", Latitude: 37, Longitude: -122)));
        Assert.AreEqual(new MapCoordinate(118.78, 32.06), WorldMap.Locate("CN", new(CountryCode: "CN", Latitude: 32.06, Longitude: 118.78)));
    }

    [DataTestMethod]
    [DataRow("[]")]
    [DataRow("null")]
    [DataRow("{\"code\":\"0\",\"data\":{}}")]
    public void ZoneWrongJsonShapeIsAParseFailure(string body)
    {
        Assert.ThrowsException<FormatException>(() => IdentityParser.ParseBilibili(body));
    }

    [TestMethod]
    public void InactiveProxyDraftCannotPersistCredentials()
    {
        Assert.IsFalse(new NetMapOptions(ProxyAddress: "http://user:secret@localhost:7890").IsValid);
    }

    [TestMethod]
    public async Task IdentityBodyIsBounded()
    {
        using var body = new StringContent(new string('x', 65537));
        await Assert.ThrowsExceptionAsync<FormatException>(() => NetworkProbe.ReadBoundedAsync(body, CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow("socks5://127.0.0.1:1080", true)]
    [DataRow("http://localhost:7890", true)]
    [DataRow("http://user:secret@localhost:7890", false)]
    [DataRow("http://localhost:7890/path", false)]
    [DataRow("socks5h://localhost:7890", false)]
    [DataRow("file:///C:/proxy", false)]
    public void ExplicitProxyValidatesWithoutPersistingCredentials(string value, bool valid)
    {
        Assert.AreEqual(valid, NetMapOptions.TryGetProxy(value, out _));
    }

    [TestMethod]
    public void MissingAndCorruptDatabasesDoNotBlockIdentity()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not a database");
            using var database = new GeoDatabase(new(AsnDatabase: path, CityDatabase: path + ".missing"));
            Assert.AreEqual("00", database.Status);
            Assert.IsNull(database.Lookup("203.0.113.1").Asn);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void OfflineMapHasSourcedCountryPointsAndUnknownStaysUnknown()
    {
        Assert.IsTrue(WorldMap.Countries.Count > 170);
        var japan = WorldMap.Locate("JP", null);
        Assert.IsNotNull(japan);
        Assert.IsTrue(japan.Longitude is > 120 and < 150);
        Assert.IsNull(WorldMap.Locate("XX", null));
        Assert.AreEqual(new MapCoordinate(135, 35), WorldMap.Locate("JP", new(Latitude: 35, Longitude: 135)));
    }
}
