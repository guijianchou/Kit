// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;

namespace NetMapLib;

public sealed record MapCoordinate(double Longitude, double Latitude);

public sealed record MapCountry(string Code, string Name, MapCoordinate Label, IReadOnlyList<MapCoordinate[]> Rings, string ChineseName = "");

public static class WorldMap
{
    private static readonly Lazy<IReadOnlyList<MapCountry>> Data = new(Load);

    public static IReadOnlyList<MapCountry> Countries => Data.Value;

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
        var countries = new List<MapCountry>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var rings = new List<MapCoordinate[]>();
            foreach (var ring in entry.GetProperty("rings").EnumerateArray())
            {
                rings.Add(ring.EnumerateArray().Select(point => new MapCoordinate(point[0].GetDouble(), point[1].GetDouble())).ToArray());
            }

            var label = entry.GetProperty("label");
            countries.Add(new(entry.GetProperty("code").GetString()!, entry.GetProperty("name").GetString()!, new(label[0].GetDouble(), label[1].GetDouble()), rings, entry.TryGetProperty("nameZh", out var chinese) ? chinese.GetString() ?? string.Empty : string.Empty));
        }

        return countries;
    }
}
