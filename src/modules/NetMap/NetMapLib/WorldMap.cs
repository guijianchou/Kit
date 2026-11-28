// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;

namespace NetMapLib;

public sealed record MapCoordinate(double Longitude, double Latitude);

public sealed record MapCountry(string Code, string Name, MapCoordinate Label, IReadOnlyList<MapCoordinate[]> Rings, string ChineseName = "", int LabelRank = 6);

public sealed record MapCity(string Name, string ChineseName, string CountryCode, MapCoordinate Location, int Rank, bool Capital);

public static class WorldMap
{
    private static readonly Lazy<IReadOnlyList<MapCountry>> Data = new(Load);
    private static readonly Lazy<MapDetail> Detail = new(LoadDetail);

    public static IReadOnlyList<MapCountry> Countries => Data.Value;

    public static IReadOnlyList<MapCountry> DetailedCountries => Detail.Value.Countries;

    public static IReadOnlyList<MapCoordinate[]> Coastlines => Detail.Value.Coastlines;

    public static IReadOnlyList<MapCity> Cities => Detail.Value.Cities;

    public static MapCoordinate? Locate(string code, GeoInfo? geography)
    {
        if (geography is { Longitude: >= -180 and <= 180, Latitude: >= -90 and <= 90 } &&
            (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(geography.CountryCode) || code == geography.CountryCode))
        {
            return new(geography.Longitude.Value, geography.Latitude.Value);
        }

        var country = Countries.FirstOrDefault(item => item.Code == code);
        return country?.Label;
    }

    private static IReadOnlyList<MapCountry> Load()
    {
        using var stream = typeof(WorldMap).Assembly.GetManifestResourceStream("NetMapLib.Data.world.json")!;
        using var document = JsonDocument.Parse(stream);
        return ReadCountries(document.RootElement);
    }

    private static IReadOnlyList<MapCountry> ReadCountries(JsonElement entries)
    {
        var countries = new List<MapCountry>();
        foreach (var entry in entries.EnumerateArray())
        {
            var rings = new List<MapCoordinate[]>();
            foreach (var ring in entry.GetProperty("rings").EnumerateArray())
            {
                rings.Add(ring.EnumerateArray().Select(point => new MapCoordinate(point[0].GetDouble(), point[1].GetDouble())).ToArray());
            }

            var label = entry.GetProperty("label");
            countries.Add(new(entry.GetProperty("code").GetString()!, entry.GetProperty("name").GetString()!, new(label[0].GetDouble(), label[1].GetDouble()), rings, entry.TryGetProperty("nameZh", out var chinese) ? chinese.GetString() ?? string.Empty : string.Empty, entry.TryGetProperty("rank", out var rank) ? rank.GetInt32() : 6));
        }

        return countries;
    }

    private static MapDetail LoadDetail()
    {
        using var stream = typeof(WorldMap).Assembly.GetManifestResourceStream("NetMapLib.Data.world-detail.json")!;
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var coastlines = root.GetProperty("coastlines").EnumerateArray()
            .Select(line => line.EnumerateArray().Select(point => new MapCoordinate(point[0].GetDouble(), point[1].GetDouble())).ToArray()).ToArray();
        var cities = root.GetProperty("cities").EnumerateArray().Select(city => new MapCity(
            city.GetProperty("name").GetString()!,
            city.GetProperty("nameZh").GetString()!,
            city.GetProperty("code").GetString()!,
            new(city.GetProperty("label")[0].GetDouble(), city.GetProperty("label")[1].GetDouble()),
            city.GetProperty("rank").GetInt32(),
            city.GetProperty("capital").GetBoolean())).ToArray();
        return new(ReadCountries(root.GetProperty("countries")), coastlines, cities);
    }

    private sealed record MapDetail(IReadOnlyList<MapCountry> Countries, IReadOnlyList<MapCoordinate[]> Coastlines, IReadOnlyList<MapCity> Cities);
}
