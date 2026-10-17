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
    /// The single lightweight instruction for the route classification chain. The engine
    /// wraps it with the host and plugin security policies, the JSON output schema and the
    /// untrusted input records, so no Chains/route-analysis/AGENTS.md file is deployed.
    /// </summary>
    public const string RouteClassificationInstruction =
        "根据最新 MTR 结果分析跨境链路类型（如 CMI、CN2、163，可包含移动、联通、电信线路）；" +
        "lineType 保持简短，summary 用极短短语且不复述 lineType，details 用 1-2 句说明关键 IP、ASN 与路径证据。";

    /// <summary>
    /// Classifies the latest MTR trace through the shared AI Hub engine on the lightweight
    /// "route-analysis" chain with a single inline instruction.
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
                    InlineInstructions = RouteClassificationInstruction,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return NetMapAiAnalysisResponse.Failed(NetMapViewModel.Text("AiCheckFailed") + ": " + ex.Message);
        }

        NetMapAiRouteResult? payload = result.Payload;
        if (!result.IsSuccess || payload is null)
        {
            return NetMapAiAnalysisResponse.Failed(MapErrorText(result.ErrorCode));
        }

        return NetMapAiAnalysisResponse.Successful(payload.LineType, SanitizeSummary(payload.Summary, payload.LineType), payload.Details);
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
