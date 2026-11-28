// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetMapLib;

namespace NetMap.UnitTests;

[TestClass]
public sealed class WorldMapTests
{
    [TestMethod]
    public void DetailedMapAddsRealGeometryAndSeparatelyEmbeddedCoastlines()
    {
        int overviewVertices = WorldMap.Countries.Sum(country => country.Rings.Sum(ring => ring.Length));
        int detailVertices = WorldMap.DetailedCountries.Sum(country => country.Rings.Sum(ring => ring.Length));
        Assert.IsTrue(detailVertices > overviewVertices * 3);
        Assert.IsTrue(WorldMap.DetailedCountries.Count > WorldMap.Countries.Count);
        Assert.IsTrue(WorldMap.Coastlines.Count > 1000);
        foreach (var ring in WorldMap.DetailedCountries.SelectMany(country => country.Rings))
        {
            Assert.IsTrue(ring.Length >= 4);
            Assert.AreEqual(ring[0], ring[^1], "Land rings remain closed, including islands and polygon holes.");
        }

        foreach (var point in WorldMap.DetailedCountries.SelectMany(country => country.Rings).Concat(WorldMap.Coastlines).SelectMany(line => line))
        {
            Assert.IsTrue(point.Longitude is >= -180 and <= 180 && point.Latitude is >= -90 and <= 90);
        }
    }

    [TestMethod]
    public void GeographicLabelsHaveBilingualSourceNamesAndValidCoordinates()
    {
        Assert.IsTrue(WorldMap.Cities.Count > 500);
        Assert.IsTrue(WorldMap.Cities.Any(city => city.Capital && city.CountryCode == "JP" && !string.IsNullOrWhiteSpace(city.ChineseName)));
        Assert.IsTrue(WorldMap.DetailedCountries.Any(country => country.Code == "CN" && !string.IsNullOrWhiteSpace(country.ChineseName)));
        foreach (var city in WorldMap.Cities)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(city.Name));
            Assert.IsTrue(city.Location.Longitude is >= -180 and <= 180 && city.Location.Latitude is >= -90 and <= 90);
        }

        Assert.AreSame(WorldMap.DetailedCountries, WorldMap.DetailedCountries, "Embedded geometry is parsed only once.");
    }
}
