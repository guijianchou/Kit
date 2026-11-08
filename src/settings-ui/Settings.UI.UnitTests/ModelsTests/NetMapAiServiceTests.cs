// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Models;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetMapLib;

namespace ModelsTests;

[TestClass]
public class NetMapAiServiceTests
{
    [TestMethod]
    public void SanitizeSummary_CompactsVerboseSentencesAndRemovesRepeats()
    {
        string verbose = "The route most likely uses China Mobile International (CMI) for cross-border transit to Singapore via Hong Kong.";
        string sanitized = NetMapAiService.SanitizeSummary(verbose, "CMI");
        Assert.AreEqual("Singapore via Hong Kong.", sanitized);

        string withPrefix = "该链路主要使用 223.120.* 直连香港出口";
        string sanitizedZh = NetMapAiService.SanitizeSummary(withPrefix, "CMI");
        Assert.AreEqual("223.120.* 直连香港出口", sanitizedZh);
    }

    [TestMethod]
    public void RouteSchema_ValidatesModelResult()
    {
        var schema = NetMapAiService.CreateRouteSchema();
        var itemIds = new HashSet<string>(StringComparer.Ordinal);

        Assert.IsTrue(schema.ValidateOutput(new NetMapAiRouteResult("CMI", "223.120.* 直连香港", "AS58453 承载，无绕美。"), itemIds));
        Assert.IsFalse(schema.ValidateOutput(new NetMapAiRouteResult(string.Empty, "223.120.* 直连香港", "详情"), itemIds));
        Assert.IsFalse(schema.ValidateOutput(new NetMapAiRouteResult("CMI", string.Empty, "详情"), itemIds));
        Assert.IsFalse(schema.ValidateOutput(new NetMapAiRouteResult("CMI", new string('x', 121), "详情"), itemIds));
        Assert.IsFalse(schema.ValidateOutput(new NetMapAiRouteResult("CMI", "短句", new string('x', 601)), itemIds));
    }

    [TestMethod]
    public void RouteClassificationInstruction_IsSingleLineAndLanguageAware()
    {
        string instructionZh = NetMapAiService.GetRouteInstruction(true);
        Assert.IsFalse(instructionZh.Contains('\n'));
        Assert.IsTrue(instructionZh.Length is > 0 and <= 512);

        string instructionEn = NetMapAiService.GetRouteInstruction(false);
        Assert.IsFalse(instructionEn.Contains('\n'));
        Assert.IsTrue(instructionEn.Length is > 0 and <= 512);

        string current = NetMapAiService.RouteClassificationInstruction;
        Assert.IsFalse(current.Contains('\n'));
        Assert.IsTrue(current.Length is > 0 and <= 512);
    }

    [TestMethod]
    public void SelectLanguageSegment_PicksCorrectSideForBilingualText()
    {
        string bilingual = "中国移动 CMI / China Mobile CMI";
        Assert.AreEqual("China Mobile CMI", NetMapAiService.SelectLanguageSegment(bilingual, isChinese: false));
        Assert.AreEqual("中国移动 CMI", NetMapAiService.SelectLanguageSegment(bilingual, isChinese: true));

        string summaryBilingual = "香港转接，约 67 ms / Via Hong Kong, approx 67 ms";
        Assert.AreEqual("Via Hong Kong, approx 67 ms", NetMapAiService.SelectLanguageSegment(summaryBilingual, isChinese: false));
        Assert.AreEqual("香港转接，约 67 ms", NetMapAiService.SelectLanguageSegment(summaryBilingual, isChinese: true));
    }

    [TestMethod]
    public void SanitizeLineType_NormalizesAndFollowsLanguage()
    {
        // Bilingual slash input
        string bilingual = "中国移动 CMI / China Mobile CMI";
        Assert.AreEqual("CMI", NetMapAiService.SanitizeLineType(bilingual, isChinese: false));
        Assert.AreEqual("移动 CMI", NetMapAiService.SanitizeLineType(bilingual, isChinese: true));

        // Long carrier names
        Assert.AreEqual("CMI", NetMapAiService.SanitizeLineType("China Mobile International (CMI)", isChinese: false));
        Assert.AreEqual("CN2 GIA", NetMapAiService.SanitizeLineType("China Telecom CN2 GIA", isChinese: false));
        Assert.AreEqual("163 Net", NetMapAiService.SanitizeLineType("China Telecom 163", isChinese: false));
        Assert.AreEqual("CU 9929", NetMapAiService.SanitizeLineType("China Unicom 9929", isChinese: false));

        Assert.AreEqual("移动 CMI", NetMapAiService.SanitizeLineType("中国移动 CMI", isChinese: true));
        Assert.AreEqual("电信 CN2 GIA", NetMapAiService.SanitizeLineType("中国电信 CN2 GIA", isChinese: true));
        Assert.AreEqual("电信 163", NetMapAiService.SanitizeLineType("中国电信 163", isChinese: true));
        Assert.AreEqual("联通 9929", NetMapAiService.SanitizeLineType("中国联通 9929", isChinese: true));
    }

    [TestMethod]
    public void SanitizeDetails_SelectsCorrectLanguage()
    {
        string detailsBilingual = "关键 IP 223.120.3.197 (AS58453 CMI) / Key IP 223.120.3.197 (AS58453 CMI) via Hong Kong";
        Assert.AreEqual("Key IP 223.120.3.197 (AS58453 CMI) via Hong Kong", NetMapAiService.SanitizeDetails(detailsBilingual, isChinese: false));
        Assert.AreEqual("关键 IP 223.120.3.197 (AS58453 CMI)", NetMapAiService.SanitizeDetails(detailsBilingual, isChinese: true));
    }

    [TestMethod]
    public void RouteResult_JsonContext_RejectsUnmappedMembers()
    {
        string valid = """{"lineType":"CMI","summary":"Direct via AS58453","details":"223.120.* hops observed."}""";
        var parsed = JsonSerializer.Deserialize(valid, NetMapAiJsonContext.Default.NetMapAiRouteResult);
        Assert.IsNotNull(parsed);
        Assert.AreEqual("CMI", parsed.LineType);

        string unmapped = """{"lineType":"CMI","summary":"Direct","details":"d","extra":"unexpected"}""";
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize(unmapped, NetMapAiJsonContext.Default.NetMapAiRouteResult));
    }

    [TestMethod]
    public void TraceData_SourceGeneratedJson_RoundTrip()
    {
        var hops = new List<NetMapMtrHopItem>
        {
            new(1, "192.168.1.1", "局域网", string.Empty, 0.0, 1.2, 1.5, 0.8, 3.1),
            new(2, "223.120.2.1", "香港", "AS58453 CMI", 0.0, 18.5, 19.0, 18.2, 22.0),
        };

        var trace = new NetMapMtrTraceData(
            DateTimeOffset.UtcNow,
            "1.1.1.1",
            "香港",
            "AS13335 Cloudflare",
            "114.114.114.114",
            "中国上海",
            "AS4134 中国电信",
            hops);

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(trace, NetMapAiJsonContext.Default.NetMapMtrTraceData);
        Assert.IsNotNull(bytes);
        Assert.IsTrue(bytes.Length > 0);

        var deserialized = JsonSerializer.Deserialize(bytes, NetMapAiJsonContext.Default.NetMapMtrTraceData);
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("1.1.1.1", deserialized.TargetIp);
        Assert.AreEqual("114.114.114.114", deserialized.DirectIp);
        Assert.AreEqual(2, deserialized.Hops.Count);
        Assert.AreEqual("223.120.2.1", deserialized.Hops[1].Ip);
        Assert.AreEqual("AS58453 CMI", deserialized.Hops[1].Asn);
    }

    [TestMethod]
    public void NetMapCard_PreservesAiRouteProperties()
    {
        var channel = new ChannelState();
        var cardWithAi = new NetMapCard("Proxy", channel, running: true, aiRoute: "CMI", aiRouteSummary: "Direct peering", aiRouteDetails: "AS58453");

        Assert.AreEqual("CMI", cardWithAi.AiRoute);
        Assert.AreEqual("Direct peering", cardWithAi.AiRouteSummary);
        Assert.AreEqual("AS58453", cardWithAi.AiRouteDetails);

        var cardDefault = new NetMapCard("Direct", channel, running: true);
        Assert.IsNull(cardDefault.AiRoute);
        Assert.IsNull(cardDefault.AiRouteSummary);
        Assert.IsNull(cardDefault.AiRouteDetails);
    }

    [TestMethod]
    public void DeleteLatestMtrSnapshot_DeletesExistingSnapshotFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Kit-NetMapAiServiceTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "mtr-latest.json");
            File.WriteAllText(path, "{}");
            Assert.IsTrue(File.Exists(path));

            NetMapAiService.DeleteLatestMtrSnapshot(path);
            Assert.IsFalse(File.Exists(path));

            // Calling it when file doesn't exist must not throw.
            NetMapAiService.DeleteLatestMtrSnapshot(path);
        }
        finally
        {
            // Remove only the unique directory owned by this test.
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task AnalyzeRouteAsync_PreCancelledTokenPropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => NetMapAiService.AnalyzeRouteAsync(cts.Token));
    }

    [TestMethod]
    [DataRow(false, "en-US")]
    [DataRow(true, "zh-CN")]
    public async Task AnalyzeRouteAsync_DispatchesTraceAndSanitizesSuccess(bool isChinese, string language)
    {
        var trace = SyntheticTrace();
        var engine = new CapturingEngine
        {
            Result = AiTaskResult.Success(
                new NetMapAiRouteResult("中国移动 CMI / China Mobile CMI", "香港转接 / Via Hong Kong", "测试证据 / Test evidence"),
                "fixture",
                "Main",
                TimeSpan.FromSeconds(1)),
        };

        var response = await NetMapAiService.AnalyzeRouteAsync(trace, engine, isChinese);

        Assert.IsTrue(response.Success);
        Assert.AreEqual(isChinese ? "移动 CMI" : "CMI", response.LineType);
        Assert.AreEqual(isChinese ? "香港转接" : "Via Hong Kong", response.Summary);
        Assert.AreEqual(isChinese ? "测试证据" : "Test evidence", response.Details);
        Assert.IsNull(response.ErrorMessage);
        Assert.AreEqual("NetMap", engine.PluginId);
        Assert.AreEqual("route-analysis", engine.TaskId);
        Assert.AreSame(trace, engine.Inputs[0]);
        Assert.AreEqual(1, engine.Inputs.Count);
        Assert.IsNotNull(engine.Options);
        Assert.AreEqual(language, engine.Options.Language);
        Assert.AreEqual(NetMapAiService.GetRouteInstruction(isChinese), engine.Options.InlineInstructions);
        Assert.AreEqual(600, engine.Options.TimeoutSeconds);
        Assert.AreEqual(300, engine.Options.RouteTimeoutSeconds);
        Assert.IsNotNull(engine.Options.Progress);
        Assert.IsNotNull(engine.Schema);
        Assert.AreSame(NetMapAiJsonContext.Default.NetMapAiRouteResult, engine.Schema.OutputTypeInfo);
    }

    [TestMethod]
    [DataRow(AiErrorCode.Timeout, "AiTimeout")]
    [DataRow(AiErrorCode.EndpointFailed, "AiEndpointFailed")]
    [DataRow(AiErrorCode.PolicyViolation, "AiPolicyViolation")]
    [DataRow(AiErrorCode.InvalidPayload, "AiInvalidResponse")]
    [DataRow(AiErrorCode.HostUnavailable, "AiServiceUnavailable")]
    [DataRow(AiErrorCode.KernelNotInstalled, "AiKernelMissing")]
    [DataRow(AiErrorCode.HubDisabled, "AiServiceDisabled")]
    [DataRow(AiErrorCode.InvalidConfiguration, "AiNoEndpoint")]
    public async Task AnalyzeRouteAsync_UsesLocalizedFailureWithoutProviderText(AiErrorCode code, string resourceKey)
    {
        var engine = new CapturingEngine
        {
            Result = AiTaskResult.Failed<NetMapAiRouteResult>(code, "provider detail must never reach the UI"),
        };

        var response = await NetMapAiService.AnalyzeRouteAsync(SyntheticTrace(), engine, false);

        Assert.IsFalse(response.Success);
        Assert.AreEqual(NetMapViewModel.Text(resourceKey), response.ErrorMessage);
        Assert.IsFalse(string.IsNullOrWhiteSpace(response.ErrorMessage));
        Assert.AreEqual(string.Empty, response.Details);
    }

    [TestMethod]
    public async Task AnalyzeRouteAsync_ReportsTypedNativeFailureDetailsWithoutProviderText()
    {
        var engine = new CapturingEngine
        {
            Result = new AiTaskResult<NetMapAiRouteResult>
            {
                ErrorCode = AiErrorCode.EndpointFailed,
                ErrorMessage = "provider detail must never reach the UI",
                Diagnostics = new AiRouteDiagnostics
                {
                    Phase = "execute",
                    Reason = "RateLimited",
                    HttpStatusCode = 429,
                    NativeRetryCount = 4,
                    NativeRetryLimit = 4,
                    CancellationSource = "none",
                    ProcessFailure = "None",
                    ExitCode = 1,
                    ContextWindowTokens = 256000,
                    ElapsedMilliseconds = 12000,
                },
            },
        };

        var response = await NetMapAiService.AnalyzeRouteAsync(SyntheticTrace(), engine, false);

        Assert.IsFalse(response.Success);
        StringAssert.StartsWith(response.ErrorMessage, NetMapViewModel.Text("AiEndpointFailed"));
        StringAssert.Contains(response.ErrorMessage, "phase=execute, reason=RateLimited, httpStatus=429");
        StringAssert.Contains(response.ErrorMessage, "nativeRetries=4/4");
        StringAssert.Contains(response.ErrorMessage, "contextTokens=256000");
        Assert.IsFalse(response.ErrorMessage.Contains("provider detail", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AnalyzeRouteAsync_RejectsArbitraryTextInsideTypedDiagnosticFields()
    {
        const string secret = "https://private-endpoint.example/sk-secret";
        var engine = new CapturingEngine
        {
            Result = new AiTaskResult<NetMapAiRouteResult>
            {
                ErrorCode = AiErrorCode.ExecutionFailed,
                ErrorMessage = secret,
                Diagnostics = new AiRouteDiagnostics
                {
                    Phase = secret,
                    Reason = secret,
                    CancellationSource = secret,
                    ProcessFailure = secret,
                    HttpStatusCode = 9999,
                },
            },
        };

        var response = await NetMapAiService.AnalyzeRouteAsync(SyntheticTrace(), engine, false);

        Assert.IsFalse(response.Success);
        Assert.IsFalse(response.ErrorMessage.Contains(secret, StringComparison.Ordinal));
        StringAssert.Contains(response.ErrorMessage, "phase=unknown, reason=Unknown, httpStatus=unknown");
    }

    [TestMethod]
    public async Task AnalyzeRouteAsync_CancelledResultPreservesCallerCancellation()
    {
        using var source = new CancellationTokenSource();
        var engine = new CapturingEngine
        {
            OnExecute = source.Cancel,
            Result = AiTaskResult.Failed<NetMapAiRouteResult>(AiErrorCode.Cancelled, "cancelled"),
        };

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => NetMapAiService.AnalyzeRouteAsync(SyntheticTrace(), engine, false, source.Token));
        Assert.AreEqual(1, engine.Calls);
    }

    [TestMethod]
    public async Task AnalyzeRouteAsync_PreCancelledTraceDoesNotDispatch()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var engine = new CapturingEngine();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => NetMapAiService.AnalyzeRouteAsync(SyntheticTrace(), engine, false, source.Token));
        Assert.AreEqual(0, engine.Calls);
    }

    [TestMethod]
    public async Task AnalyzeRouteAsync_EngineExceptionDoesNotExposeDetails()
    {
        var engine = new CapturingEngine { OnExecute = () => throw new InvalidOperationException("private provider configuration") };

        var response = await NetMapAiService.AnalyzeRouteAsync(SyntheticTrace(), engine, false);

        Assert.IsFalse(response.Success);
        Assert.AreEqual(NetMapViewModel.Text("AiCheckFailed"), response.ErrorMessage);
    }

    [TestMethod]
    public void RouteDiagnostic_PreservesOnlySafeFailureClassifications()
    {
        string status = "batch=1/1, AI native event: request=private-id, event=error, source=stderr, code=EndpointFailed, httpStatus=429, reason=RateLimited, elapsedMs=123, model=private-model, apiKey=private-key, message=private-output";

        Assert.AreEqual("event=error, code=EndpointFailed, httpStatus=429, reason=RateLimited", NetMapAiService.FormatRouteDiagnostic(status));
        Assert.AreEqual("phase=schema", NetMapAiService.FormatRouteDiagnostic("batch=1/1, AI batch rejected: phase=schema, exception=JsonException"));
    }

    [TestMethod]
    public void RouteDiagnostic_RejectsUnrecognizedValuesAndOversizedText()
    {
        Assert.AreEqual(string.Empty, NetMapAiService.FormatRouteDiagnostic("code=private-key, phase=private-output, httpStatus=9999, reason=https://private-endpoint"));
        Assert.AreEqual(string.Empty, NetMapAiService.FormatRouteDiagnostic(new string('x', 4097)));
        Assert.AreEqual("code=Timeout", NetMapAiService.FormatRouteDiagnostic("code=Timeout, code=private-key"));
        Assert.AreEqual(string.Empty, NetMapAiService.FormatRouteDiagnostic("code=6, httpStatus=-1"));
    }

    private static NetMapMtrTraceData SyntheticTrace() => new(
        DateTimeOffset.UtcNow,
        "192.0.2.2",
        "Test region",
        "AS64500",
        "198.51.100.2",
        "Test region",
        "AS64501",
        [new NetMapMtrHopItem(1, "192.0.2.1", "Test region", "AS64500", 0, 10, 10, 10, 10)]);

    private sealed class CapturingEngine : IAiTaskEngine
    {
        public bool IsEnabled => true;

        public string ActiveKernel => "fixture";

        public event EventHandler<AiHubStateChangedEventArgs> StateChanged
        {
            add
            {
            }

            remove
            {
            }
        }

        public AiTaskResult<NetMapAiRouteResult> Result { get; init; } = new();

        public Action OnExecute { get; init; }

        public int Calls { get; private set; }

        public string PluginId { get; private set; }

        public string TaskId { get; private set; }

        public IReadOnlyList<NetMapMtrTraceData> Inputs { get; private set; }

        public AiTaskOptions Options { get; private set; }

        public AiTaskSchema<NetMapMtrTraceData, NetMapAiRouteResult> Schema { get; private set; }

        public AiReadiness GetReadiness() => throw new NotSupportedException();

        public Task<AiReadiness> ProbeReadinessAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose()
        {
        }

        public Task<AiTaskResult<TOutput>> ExecuteTaskAsync<TInput, TOutput>(
            string pluginId,
            string taskId,
            IReadOnlyList<TInput> items,
            AiTaskSchema<TInput, TOutput> schema,
            AiTaskOptions options = null,
            CancellationToken cancellationToken = default)
            where TOutput : class
        {
            Calls++;
            PluginId = pluginId;
            TaskId = taskId;
            Inputs = (IReadOnlyList<NetMapMtrTraceData>)(object)items;
            Schema = (AiTaskSchema<NetMapMtrTraceData, NetMapAiRouteResult>)(object)schema;
            Options = options;
            options?.Progress?.Report(new AiTaskProgress("Execute", "private status must not be logged", 0, 1));
            OnExecute?.Invoke();
            return Task.FromResult((AiTaskResult<TOutput>)(object)Result);
        }
    }
}
