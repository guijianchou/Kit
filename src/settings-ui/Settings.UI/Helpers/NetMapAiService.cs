// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.Settings.UI.Helpers;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;
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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
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
    /// Deletes the temporary MTR trace record file if it exists.
    /// </summary>
    public static void DeleteLatestMtrSnapshot()
    {
        try
        {
            if (File.Exists(MtrTempFilePath))
            {
                File.Delete(MtrTempFilePath);
            }
        }
        catch
        {
            // Observational delete must not crash runtime
        }
    }

    /// <summary>
    /// The lightweight instruction for the route classification chain in Simplified Chinese.
    /// </summary>
    public const string RouteClassificationInstructionZh =
        "根据最新 MTR 跟踪结果分析跨境链路类型（如 CMI、CN2 GIA、163、9929、4837 等）。" +
        "语言规则：所有字段（lineType、summary、details）必须完全使用简体中文输出，严禁中英双语斜杠并列。" +
        "精简规则：" +
        "- lineType: 极简链路标签，如'移动 CMI'、'电信 CN2 GIA'、'联通 9929'、'163 骨干'，控制在 10 字以内，不要冗长前缀；" +
        "- summary: 卡片副标题极短短语，控制在 16 字以内（如'香港转接 · 约67ms'），严禁复述或包含 lineType；" +
        "- details: 1-2 句说明关键 IP、中继 ASN 与路径证据（详细分析内容放于此处）。";

    /// <summary>
    /// The lightweight instruction for the route classification chain in English.
    /// </summary>
    public const string RouteClassificationInstructionEn =
        "Analyze cross-border route type from MTR trace (e.g. CMI, CN2 GIA, 163, 9929, 4837, or BGP transit). " +
        "Output ALL fields STRICTLY in English only. Never use Chinese or bilingual slash text. " +
        "lineType: Short tag only (e.g. 'CMI', 'CN2 GIA', 'AS4837', '163 Net'), max 14 chars. " +
        "summary: Ultra-concise badge subtitle (e.g. 'Via HK · ~67ms'), max 22 chars, never repeat lineType. " +
        "details: 1-2 concise sentences with key IP, transit ASN, and latency evidence.";

    /// <summary>
    /// Returns the single lightweight instruction for the route classification chain based on current culture.
    /// </summary>
    public static string RouteClassificationInstruction =>
        GetRouteInstruction(CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves the route instruction according to UI language.
    /// </summary>
    public static string GetRouteInstruction(bool isChinese) =>
        isChinese ? RouteClassificationInstructionZh : RouteClassificationInstructionEn;

    /// <summary>
    /// Classifies the latest MTR trace through the shared AI Hub engine on the lightweight
    /// "route-analysis" chain with a single inline instruction.
    /// </summary>
    public static async Task<NetMapAiAnalysisResponse> AnalyzeRouteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return NetMapAiAnalysisResponse.Failed(NetMapViewModel.Text("AiNoMtrData") + " (" + ex.Message + ")");
        }

        if (traceData == null || traceData.Hops.Count == 0)
        {
            return NetMapAiAnalysisResponse.Failed(NetMapViewModel.Text("AiNoMtrData"));
        }

        bool isChinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        AiTaskResult<NetMapAiRouteResult> result;
        try
        {
            result = await AiHubEngine.Current.ExecuteTaskAsync(
                "NetMap",
                "route-analysis",
                new[] { traceData },
                CreateRouteSchema(),
                new AiTaskOptions
                {
                    Language = isChinese ? "zh-CN" : "en-US",
                    TimeoutSeconds = 120,
                    RouteTimeoutSeconds = 60,
                    InlineInstructions = GetRouteInstruction(isChinese),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return NetMapAiAnalysisResponse.Failed(NetMapViewModel.Text("AiCheckFailed") + ": " + ex.Message);
        }

        // The engine converts a caller stop into a cancelled failure result; re-throw it so
        // the view model can cancel silently instead of reporting an AI check error.
        cancellationToken.ThrowIfCancellationRequested();

        NetMapAiRouteResult? payload = result.Payload;
        if (!result.IsSuccess || payload is null)
        {
            return NetMapAiAnalysisResponse.Failed(MapErrorText(result.ErrorCode));
        }

        string cleanLineType = SanitizeLineType(payload.LineType, isChinese);
        string cleanSummary = SanitizeSummary(payload.Summary, cleanLineType, isChinese);
        string cleanDetails = SanitizeDetails(payload.Details, isChinese);

        return NetMapAiAnalysisResponse.Successful(cleanLineType, cleanSummary, cleanDetails);
    }

    /// <summary>
    /// Source-generated serialization metadata and output validation for the route chain.
    /// </summary>
    public static AiTaskSchema<NetMapMtrTraceData, NetMapAiRouteResult> CreateRouteSchema()
    {
        return new AiTaskSchema<NetMapMtrTraceData, NetMapAiRouteResult>
        {
            InputTypeInfo = NetMapAiJsonContext.Default.NetMapMtrTraceData,
            OutputTypeInfo = NetMapAiJsonContext.Default.NetMapAiRouteResult,
            ValidateOutput = static (route, _) =>
                !string.IsNullOrWhiteSpace(route.LineType) &&
                route.LineType.Length <= 40 &&
                !string.IsNullOrWhiteSpace(route.Summary) &&
                route.Summary.Length <= 120 &&
                (route.Details?.Length ?? 0) <= 600,
        };
    }

    private static string MapErrorText(AiErrorCode errorCode) => errorCode switch
    {
        AiErrorCode.HubDisabled => NetMapViewModel.Text("AiServiceDisabled"),
        AiErrorCode.InvalidConfiguration => NetMapViewModel.Text("AiNoEndpoint"),
        AiErrorCode.KernelNotInstalled => NetMapViewModel.Text("AiKernelMissing"),
        _ => NetMapViewModel.Text("AiCheckFailed"),
    };

    /// <summary>
    /// Selects the language appropriate portion if a model returned dual-language slash-separated text (e.g. "中文 / English").
    /// </summary>
    public static string SelectLanguageSegment(string text, bool isChinese)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        int slash = text.IndexOf(" / ", StringComparison.Ordinal);
        if (slash < 0)
        {
            slash = text.IndexOf('/');
        }

        if (slash > 0 && slash < text.Length - 1)
        {
            string part1 = text[..slash].Trim();
            string part2 = text[(slash + 1)..].TrimStart('/', ' ');
            if (part1.Length > 0 && part2.Length > 0)
            {
                bool part1HasCjk = ContainsCjk(part1);
                bool part2HasCjk = ContainsCjk(part2);
                if (part1HasCjk != part2HasCjk)
                {
                    return isChinese ? (part1HasCjk ? part1 : part2) : (!part1HasCjk ? part1 : part2);
                }

                return isChinese ? part1 : part2;
            }
        }

        return text.Trim();
    }

    /// <summary>
    /// Checks if a string contains Chinese/CJK characters.
    /// </summary>
    public static bool ContainsCjk(string text)
    {
        foreach (char c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sanitizes the route line type tag to ensure single-language brevity suitable for the card badge.
    /// </summary>
    public static string SanitizeLineType(string lineType, bool isChinese)
    {
        if (string.IsNullOrWhiteSpace(lineType))
        {
            return string.Empty;
        }

        string s = SelectLanguageSegment(lineType, isChinese);

        if (!isChinese)
        {
            // Normalize known carriers to short acronyms in English
            string[] cmiPatterns = ["China Mobile International (CMI)", "China Mobile International", "China Mobile CMI", "中国移动 CMI", "移动 CMI", "中国移动国际", "中国移动"];
            foreach (var p in cmiPatterns)
            {
                if (s.Equals(p, StringComparison.OrdinalIgnoreCase) || s.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                {
                    s = "CMI" + s[p.Length..];
                    break;
                }
            }

            string[] cn2GiaPatterns = ["China Telecom CN2 GIA", "中国电信 CN2 GIA", "电信 CN2 GIA"];
            foreach (var p in cn2GiaPatterns)
            {
                if (s.Equals(p, StringComparison.OrdinalIgnoreCase) || s.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                {
                    s = "CN2 GIA" + s[p.Length..];
                    break;
                }
            }

            string[] cn2Patterns = ["China Telecom CN2", "中国电信 CN2", "电信 CN2"];
            foreach (var p in cn2Patterns)
            {
                if (s.Equals(p, StringComparison.OrdinalIgnoreCase) || s.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                {
                    s = "CN2" + s[p.Length..];
                    break;
                }
            }

            string[] net163Patterns = ["China Telecom 163", "中国电信 163", "电信 163", "163 骨干网", "163 骨干"];
            foreach (var p in net163Patterns)
            {
                if (s.Equals(p, StringComparison.OrdinalIgnoreCase) || s.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                {
                    s = "163 Net" + s[p.Length..];
                    break;
                }
            }

            string[] cu9929Patterns = ["China Unicom 9929", "中国联通 9929", "联通 9929"];
            foreach (var p in cu9929Patterns)
            {
                if (s.Equals(p, StringComparison.OrdinalIgnoreCase) || s.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                {
                    s = "CU 9929" + s[p.Length..];
                    break;
                }
            }

            string[] cu4837Patterns = ["China Unicom 4837", "中国联通 4837", "联通 4837"];
            foreach (var p in cu4837Patterns)
            {
                if (s.Equals(p, StringComparison.OrdinalIgnoreCase) || s.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase))
                {
                    s = "CU 4837" + s[p.Length..];
                    break;
                }
            }
        }
        else
        {
            // Normalize in Chinese
            if (s.StartsWith("China Mobile International (CMI)", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("China Mobile CMI", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("中国移动国际 (CMI)", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("中国移动 CMI", StringComparison.OrdinalIgnoreCase))
            {
                s = "移动 CMI";
            }
            else if (s.StartsWith("China Telecom CN2 GIA", StringComparison.OrdinalIgnoreCase) ||
                     s.StartsWith("中国电信 CN2 GIA", StringComparison.OrdinalIgnoreCase))
            {
                s = "电信 CN2 GIA";
            }
            else if (s.StartsWith("China Telecom 163", StringComparison.OrdinalIgnoreCase) ||
                     s.StartsWith("中国电信 163", StringComparison.OrdinalIgnoreCase))
            {
                s = "电信 163";
            }
            else if (s.StartsWith("China Unicom 9929", StringComparison.OrdinalIgnoreCase) ||
                     s.StartsWith("中国联通 9929", StringComparison.OrdinalIgnoreCase))
            {
                s = "联通 9929";
            }
            else if (s.StartsWith("China Unicom 4837", StringComparison.OrdinalIgnoreCase) ||
                     s.StartsWith("中国联通 4837", StringComparison.OrdinalIgnoreCase))
            {
                s = "联通 4837";
            }
        }

        s = s.Trim();
        if (s.Length > 20)
        {
            s = s[..18].Trim() + "…";
        }

        return s;
    }

    public static string SanitizeSummary(string summary, string lineType) =>
        SanitizeSummary(summary, lineType, CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase));

    public static string SanitizeSummary(string summary, string lineType, bool isChinese)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return string.Empty;
        }

        string s = SelectLanguageSegment(summary, isChinese);

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
            "China Mobile CMI",
            "China Telecom CN2 GIA",
            "China Telecom CN2 GT",
            "China Telecom CN2",
            "China Telecom 163",
            "China Unicom 9929",
            "China Unicom 4837",
            "中国移动国际 (CMI)",
            "中国移动国际",
            "中国移动 CMI",
            "移动 CMI",
            "中国电信 CN2 GIA",
            "电信 CN2 GIA",
            "中国电信 CN2",
            "电信 CN2",
            "中国电信 163",
            "电信 163",
            "中国联通 9929",
            "联通 9929",
            "中国联通 4837",
            "联通 4837",
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
        if (s.Length > 28)
        {
            int cut = s.IndexOfAny([',', '，', ';', '；', '。', '.']);
            if (cut > 6 && cut <= 26)
            {
                s = s[..cut].Trim();
            }
            else
            {
                s = s[..26].Trim() + "…";
            }
        }

        if (s.Length > 0 && char.IsLower(s[0]))
        {
            s = char.ToUpperInvariant(s[0]) + s[1..];
        }

        return s;
    }

    /// <summary>
    /// Sanitizes the route details to ensure it follows the current UI language.
    /// </summary>
    public static string SanitizeDetails(string details, bool isChinese)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return string.Empty;
        }

        string s = SelectLanguageSegment(details, isChinese);
        return s.Trim();
    }
}
