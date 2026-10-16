// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.Settings.UI.Helpers;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Engine;
using Kit.AiHub.Models;
using Kit.AiHub.Storage;
using Kit.Settings.UI.ViewModels;
using NetMapLib;

public sealed record NetMapMtrHopItem(
    int Ttl,
    string Ip,
    string Location,
    string Asn,
    double LossPercent,
    double? LastMs,
    double? AvgMs,
    double? BestMs,
    double? WorstMs);

public sealed record NetMapMtrTraceData(
    DateTimeOffset Timestamp,
    string TargetIp,
    string TargetLocation,
    string TargetAsn,
    string DirectIp,
    string DirectLocation,
    string DirectAsn,
    IReadOnlyList<NetMapMtrHopItem> Hops);

public sealed record NetMapAiRouteResult(
    string LineType,
    string Summary,
    string Details);

public sealed record NetMapAiAnalysisResponse(
    bool Success,
    string LineType,
    string Summary,
    string Details,
    string? ErrorMessage)
{
    public static NetMapAiAnalysisResponse Successful(string lineType, string summary, string details) =>
        new(true, lineType, summary, details, null);

    public static NetMapAiAnalysisResponse Failed(string error) =>
        new(false, string.Empty, string.Empty, string.Empty, error);
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NetMapMtrTraceData))]
[JsonSerializable(typeof(NetMapAiRouteResult))]
public sealed partial class NetMapAiJsonContext : JsonSerializerContext
{
}

public static class NetMapAiService
{
    private static readonly string StorageDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kit",
        "NetMap");

    public static string MtrTempFilePath => Path.Combine(StorageDirectory, "mtr-latest.json");

    /// <summary>
    /// Overwrites the temporary MTR trace record file with the latest snapshot data.
    /// </summary>
    public static void SaveLatestMtrSnapshot(NetMapSnapshot snapshot)
    {
        try
        {
            if (snapshot.Hops.Count == 0 && snapshot.Proxy.LastSuccess == null)
            {
                return;
            }

            Directory.CreateDirectory(StorageDirectory);

            var hopItems = new List<NetMapMtrHopItem>(snapshot.Hops.Count);
            foreach (var hop in snapshot.Hops)
            {
                string loc = NetMapViewModel.LocationText(null, hop.Geography);
                string asn = hop.Geography?.Asn is { } asnNum ? $"AS{asnNum} {hop.Geography.Organization}" : string.Empty;
                hopItems.Add(new NetMapMtrHopItem(
                    hop.Ttl,
                    hop.Ip,
                    loc,
                    asn,
                    Math.Round(hop.NoResponsePercent, 1),
                    hop.LastMs.HasValue ? Math.Round(hop.LastMs.Value, 1) : null,
                    hop.AverageMs.HasValue ? Math.Round(hop.AverageMs.Value, 1) : null,
                    hop.BestMs.HasValue ? Math.Round(hop.BestMs.Value, 1) : null,
                    hop.WorstMs.HasValue ? Math.Round(hop.WorstMs.Value, 1) : null));
            }

            var proxyIdentity = snapshot.Proxy.LastSuccess?.Identity;
            var directIdentity = snapshot.Direct.LastSuccess?.Identity;

            var data = new NetMapMtrTraceData(
                DateTimeOffset.UtcNow,
                proxyIdentity?.Ip ?? string.Empty,
                NetMapViewModel.LocationText(proxyIdentity, snapshot.Proxy.Geography),
                snapshot.Proxy.Geography?.Asn is { } pAsn ? $"AS{pAsn} {snapshot.Proxy.Geography.Organization}" : string.Empty,
                directIdentity?.Ip ?? string.Empty,
                NetMapViewModel.LocationText(directIdentity, snapshot.Direct.Geography),
                snapshot.Direct.Geography?.Asn is { } dAsn ? $"AS{dAsn} {snapshot.Direct.Geography.Organization}" : string.Empty,
                hopItems);

            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(data, NetMapAiJsonContext.Default.NetMapMtrTraceData);
            File.WriteAllBytes(MtrTempFilePath, jsonBytes);
        }
        catch
        {
            // Observational write must not crash runtime
        }
    }

    /// <summary>
    /// Invokes the underlying Kit AI service to analyze the MTR route trace.
    /// </summary>
    public static async Task<NetMapAiAnalysisResponse> AnalyzeRouteAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(MtrTempFilePath))
        {
            return NetMapAiAnalysisResponse.Failed(NetMapViewModel.Text("AiNoMtrData"));
        }

        NetMapMtrTraceData? traceData;
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(MtrTempFilePath, cancellationToken).ConfigureAwait(false);
            traceData = JsonSerializer.Deserialize(bytes, NetMapAiJsonContext.Default.NetMapMtrTraceData);
        }
        catch (Exception ex)
        {
            return NetMapAiAnalysisResponse.Failed(NetMapViewModel.Text("AiNoMtrData") + " (" + ex.Message + ")");
        }

        if (traceData == null || traceData.Hops.Count == 0)
        {
            return NetMapAiAnalysisResponse.Failed(NetMapViewModel.Text("AiNoMtrData"));
        }

        var settingsStore = new AiHubSettingsStore();
        AiHubConfig config;
        try
        {
            config = settingsStore.Load();
        }
        catch (Exception ex)
        {
            return NetMapAiAnalysisResponse.Failed("Failed to load AI settings: " + ex.Message);
        }

        if (!config.IsEnabled)
        {
            return NetMapAiAnalysisResponse.Failed(NetMapViewModel.Text("AiServiceDisabled"));
        }

        var target = config.GetMainTarget();
        if (target == null || !target.IsActive || string.IsNullOrWhiteSpace(target.BaseUrl))
        {
            return NetMapAiAnalysisResponse.Failed(NetMapViewModel.Text("AiNoEndpoint"));
        }

        bool isChinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        string prompt = BuildPrompt(traceData, isChinese);

        string output = string.Empty;
        bool executed = false;

        // Try RouteDispatcher kernel first
        try
        {
            using var kernelManager = new KernelManagerService();
            var dispatcher = new RouteDispatcher(kernelManager);
            var kernelResult = await dispatcher.ExecuteAsync(config.SelectedKernel, target, prompt, 60, cancellationToken).ConfigureAwait(false);
            if (kernelResult.IsSuccess && !string.IsNullOrWhiteSpace(kernelResult.Output))
            {
                output = kernelResult.Output;
                executed = true;
            }
            else if (kernelResult.CanFailover && config.GetFallbackTarget() is { IsActive: true } fallback)
            {
                var fallbackResult = await dispatcher.ExecuteAsync(config.SelectedKernel, fallback, prompt, 60, cancellationToken).ConfigureAwait(false);
                if (fallbackResult.IsSuccess && !string.IsNullOrWhiteSpace(fallbackResult.Output))
                {
                    output = fallbackResult.Output;
                    executed = true;
                }
            }
        }
        catch
        {
            // Proceed to direct HTTP fallback
        }

        // Direct HTTP fallback if kernel was not ready or failed
        if (!executed)
        {
            try
            {
                output = await ExecuteDirectHttpAsync(target, prompt, cancellationToken).ConfigureAwait(false);
                executed = true;
            }
            catch (Exception ex)
            {
                if (config.GetFallbackTarget() is { IsActive: true } fallback)
                {
                    try
                    {
                        output = await ExecuteDirectHttpAsync(fallback, prompt, cancellationToken).ConfigureAwait(false);
                        executed = true;
                    }
                    catch
                    {
                        return NetMapAiAnalysisResponse.Failed(NetMapViewModel.Text("AiCheckFailed") + ": " + ex.Message);
                    }
                }
                else
                {
                    return NetMapAiAnalysisResponse.Failed(NetMapViewModel.Text("AiCheckFailed") + ": " + ex.Message);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return NetMapAiAnalysisResponse.Failed(NetMapViewModel.Text("AiCheckFailed"));
        }

        var parsed = ParseResponse(output, isChinese);
        return NetMapAiAnalysisResponse.Successful(parsed.LineType, parsed.Summary, parsed.Details);
    }

    private static string BuildPrompt(NetMapMtrTraceData data, bool isChinese)
    {
        var sb = new StringBuilder();
        sb.AppendLine(isChinese
            ? "你是一个专业的网络路由分析专家。请根据以下从中国大陆客户端到代理节点的 MTR 链路追踪记录，分析其跨境链路类型。"
            : "You are a professional network routing analysis expert. Analyze the following MTR route trace from a client in Mainland China to a proxy node and determine the cross-border transit line type.");
        sb.AppendLine();
        sb.AppendLine("## Target & Egress Information");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Direct (Local): {data.DirectIp} ({data.DirectLocation}, {data.DirectAsn})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Proxy (Target): {data.TargetIp} ({data.TargetLocation}, {data.TargetAsn})");
        sb.AppendLine();
        sb.AppendLine("## MTR Hop Records");
        sb.AppendLine("TTL | Router IP | Location | ASN | Loss% | Avg Latency");
        foreach (var hop in data.Hops)
        {
            string ip = string.IsNullOrEmpty(hop.Ip) ? "*" : hop.Ip;
            string loc = string.IsNullOrEmpty(hop.Location) ? "-" : hop.Location;
            string asn = string.IsNullOrEmpty(hop.Asn) ? "-" : hop.Asn;
            string avg = hop.AvgMs.HasValue ? $"{hop.AvgMs.Value} ms" : "N/A";
            sb.AppendLine(CultureInfo.InvariantCulture, $"{hop.Ttl} | {ip} | {loc} | {asn} | {hop.LossPercent}% | {avg}");
        }

        sb.AppendLine();
        sb.AppendLine("## Routing Knowledge");
        sb.AppendLine("- CMI: China Mobile International (AS58453), typically routes via 223.120.*, 223.118.*, 223.119.* or 58.218.*, entering HK/Tokyo/Singapore direct peering.");
        sb.AppendLine("- CN2 GIA: China Telecom Next Generation Carrier (AS4809), dual-direction 59.43.* hops domestically and at international gateway.");
        sb.AppendLine("- CN2 GT: China Telecom Next Generation Carrier (AS4809), enters via 202.97 domestically and transfers to 59.43 at international gateway.");
        sb.AppendLine("- 163 骨干网: China Telecom Standard Backbone (AS4134), standard 202.97.* international transit hops.");
        sb.AppendLine("- CU169 / 4837: China Unicom Standard Backbone (AS4837), 219.158.* hops.");
        sb.AppendLine("- CU Premium / 9929: China Unicom Industrial Backbone (AS9929 / AS10099).");
        sb.AppendLine("- BGP / 国际直连 / Other: Direct peering or standard overseas transit.");
        sb.AppendLine();
        sb.AppendLine("## Required Output");
        sb.AppendLine("Return strictly one single JSON object with no Markdown formatting or code fences, following this format:");
        sb.AppendLine("{");
        sb.AppendLine("  \"lineType\": \"CMI / CN2 GIA / CN2 GT / 163 / 4837 / 9929 / BGP\",");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  \"summary\": \"Extremely short phrase under {(isChinese ? "10 字" : "5 words")} (e.g. '{(isChinese ? "223.120.* 直连香港" : "223.120.* HK Direct")}'). Do NOT repeat the lineType name, do NOT write a full sentence.\",");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  \"details\": \"1-2 concise sentences in {(isChinese ? "Simplified Chinese" : "English")} explaining key router IPs, transit ASNs and path evidence.\"");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static async Task<string> ExecuteDirectHttpAsync(AiTargetSettings target, string prompt, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(50) };
        string baseUrl = target.BaseUrl.Trim().TrimEnd('/');
        string url = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{baseUrl}/chat/completions"
            : $"{baseUrl}/v1/chat/completions";

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(target.ApiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", target.ApiKey.Trim());
        }

        var payload = new
        {
            model = target.Model.Trim(),
            messages = new object[]
            {
                new { role = "system", content = "You are a network routing analysis assistant. Return strictly one valid JSON object without Markdown formatting or code fences." },
                new { role = "user", content = prompt },
            },
            temperature = 0.2,
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(responseText);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
            {
                return content.GetString() ?? string.Empty;
            }
        }

        return responseText;
    }

    public static NetMapAiRouteResult ParseResponse(string text, bool isChinese)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new NetMapAiRouteResult(isChinese ? "未知链路" : "Unknown", isChinese ? "未返回有效分析" : "No analysis returned", string.Empty);
        }

        string cleaned = text.Trim();
        if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[7..];
        }
        else if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            cleaned = cleaned[3..];
        }

        if (cleaned.EndsWith("```", StringComparison.Ordinal))
        {
            cleaned = cleaned[..^3];
        }

        cleaned = cleaned.Trim();
        int start = cleaned.IndexOf('{');
        int end = cleaned.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            cleaned = cleaned.Substring(start, end - start + 1);
        }

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            string lineType = root.TryGetProperty("lineType", out var lt) ? lt.GetString() ?? string.Empty : string.Empty;
            string summary = root.TryGetProperty("summary", out var sm) ? sm.GetString() ?? string.Empty : string.Empty;
            string details = root.TryGetProperty("details", out var dt) ? dt.GetString() ?? string.Empty : string.Empty;

            if (!string.IsNullOrWhiteSpace(lineType))
            {
                string compactSummary = SanitizeSummary(summary, lineType);
                return new NetMapAiRouteResult(lineType, compactSummary, details);
            }
        }
        catch
        {
            // JSON parse fallback
        }

        // Heuristic detection if model answered in text
        string detected = isChinese ? "BGP / 国际直连" : "BGP / Direct";
        if (text.Contains("CN2 GIA", StringComparison.OrdinalIgnoreCase))
        {
            detected = "CN2 GIA";
        }
        else if (text.Contains("CN2 GT", StringComparison.OrdinalIgnoreCase))
        {
            detected = "CN2 GT";
        }
        else if (text.Contains("CN2", StringComparison.OrdinalIgnoreCase))
        {
            detected = "CN2";
        }
        else if (text.Contains("CMI", StringComparison.OrdinalIgnoreCase))
        {
            detected = "CMI";
        }
        else if (text.Contains("9929", StringComparison.OrdinalIgnoreCase) || text.Contains("CUG", StringComparison.OrdinalIgnoreCase))
        {
            detected = isChinese ? "联通 9929" : "CU 9929";
        }
        else if (text.Contains("4837", StringComparison.OrdinalIgnoreCase) || text.Contains("169", StringComparison.OrdinalIgnoreCase))
        {
            detected = isChinese ? "联通 4837" : "CU 4837";
        }
        else if (text.Contains("163", StringComparison.OrdinalIgnoreCase) || text.Contains("4134", StringComparison.OrdinalIgnoreCase))
        {
            detected = isChinese ? "163 骨干网" : "163 Backbone";
        }

        string trimmedSummary = text.Length > 200 ? text[..200] + "..." : text;
        string sanitizedFallback = SanitizeSummary(trimmedSummary, detected);
        return new NetMapAiRouteResult(detected, sanitizedFallback, text);
    }

    public static string SanitizeSummary(string summary, string lineType)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return string.Empty;
        }

        string s = summary.Trim();

        // Strip conversational AI prefixes
        string[] prefixes =
        [
            "The route most likely uses ",
            "The route appears to use ",
            "The route uses ",
            "Route uses ",
            "Traffic routes via ",
            "This route is ",
            "This is a ",
            "该链路主要使用 ",
            "该链路属于 ",
            "该链路使用 ",
            "该链路为 ",
            "该路由属于 ",
            "该路由为 ",
            "路由经由 ",
            "链路经由 ",
        ];

        foreach (var prefix in prefixes)
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..].Trim();
                break;
            }
        }

        // Clean up common long carrier names if repeated at the start
        string[] carrierPrefixes =
        [
            "China Mobile International (CMI)",
            "China Mobile International",
            "China Telecom CN2 GIA",
            "China Telecom CN2 GT",
            "China Telecom CN2",
            "China Telecom 163",
            "China Unicom 9929",
            "China Unicom 4837",
            "中国移动国际",
            "中国电信 CN2",
            "中国电信 163",
            "中国联通 9929",
            "中国联通 4837",
        ];

        foreach (var carrier in carrierPrefixes)
        {
            if (s.StartsWith(carrier, StringComparison.OrdinalIgnoreCase))
            {
                s = s[carrier.Length..].TrimStart(' ', ':', '-', '·', '—');
                break;
            }
        }

        // Strip leading lineType repeats (e.g. "CMI: ...", "CMI - ...", "CMI ...", "CMI for ...")
        if (!string.IsNullOrEmpty(lineType))
        {
            if (s.StartsWith(lineType + ":", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith(lineType + " -", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith(lineType + " —", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith(lineType + " ·", StringComparison.OrdinalIgnoreCase))
            {
                s = s[(lineType.Length + 1)..].TrimStart('-', ':', '—', '·', ' ');
            }
            else if (s.StartsWith(lineType + " for ", StringComparison.OrdinalIgnoreCase))
            {
                s = s[(lineType.Length + 5)..].Trim();
            }
            else if (s.StartsWith(lineType + " ", StringComparison.OrdinalIgnoreCase))
            {
                s = s[(lineType.Length + 1)..].Trim();
            }
        }

        if (s.StartsWith("for cross-border transit to ", StringComparison.OrdinalIgnoreCase))
        {
            s = s["for cross-border transit to ".Length..].Trim();
        }
        else if (s.StartsWith("for cross-border transit via ", StringComparison.OrdinalIgnoreCase))
        {
            s = "Via " + s["for cross-border transit via ".Length..].Trim();
        }
        else if (s.StartsWith("for cross-border transit", StringComparison.OrdinalIgnoreCase))
        {
            s = s["for cross-border transit".Length..].TrimStart(' ', ':', '-');
        }

        // Cap length to keep it concise and single-line
        if (s.Length > 32)
        {
            int cut = s.IndexOfAny([',', '，', ';', '；', '。', '.']);
            if (cut > 6 && cut <= 30)
            {
                s = s[..cut].Trim();
            }
            else
            {
                s = s[..30].Trim() + "…";
            }
        }

        if (s.Length > 0 && char.IsLower(s[0]))
        {
            s = char.ToUpperInvariant(s[0]) + s[1..];
        }

        return s;
    }
}
