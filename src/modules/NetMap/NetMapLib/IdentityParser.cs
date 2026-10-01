// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json;

namespace NetMapLib;

public static class IdentityParser
{
    public static EgressIdentity ParseBilibili(string body)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 12 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code) ||
            code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var value) || value != 0 ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Invalid zone response.");
        }

        var ip = ReadText(data, "addr");
        var country = ReadText(data, "country");
        var province = ReadText(data, "province");
        var city = ReadText(data, "city");
        var countryCode = country switch
        {
            "中国" or "中国大陆" or "China" or "CN" => "CN",
            _ => string.Empty,
        };
        var location = string.Join(" · ", new[] { country, province, city }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        return new EgressIdentity(NormalizeIp(ip), countryCode, location, ReadText(data, "isp"), province, city);
    }

    public static EgressIdentity ParseTrace(string body)
    {
        string? ip = null;
        string? country = null;
        foreach (var line in body.Split('\n'))
        {
            int separator = line.IndexOf('=');
            if (separator < 1)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key == "ip")
            {
                if (ip != null)
                {
                    throw new FormatException("Duplicate IP.");
                }

                ip = value;
            }
            else if (key == "loc")
            {
                if (country != null)
                {
                    throw new FormatException("Duplicate location.");
                }

                country = value;
            }
        }

        if (country is not { Length: 2 } || !country.All(char.IsAsciiLetterUpper))
        {
            throw new FormatException("Invalid location.");
        }

        return new EgressIdentity(NormalizeIp(ip), country, country);
    }

    private static string NormalizeIp(string? value)
    {
        // Do not accept abbreviated IPv4, scoped addresses or HTML error pages.
        if (string.IsNullOrEmpty(value) || value.Contains('%') ||
            (!value.Contains(':') && value.Count(c => c == '.') != 3) ||
            !IPAddress.TryParse(value, out var ip) || IPAddress.IsLoopback(ip) ||
            ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
        {
            throw new FormatException("Invalid IP address.");
        }

        return ip.ToString();
    }

    private static string ReadText(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        string text = value.GetString() ?? string.Empty;
        return text[..Math.Min(text.Length, 160)];
    }
}
