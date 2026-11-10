namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;
using Kit.AiHub.Models;
using Kit.AiHub.UnitTests.Fixtures;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class NativeRouteDiagnosticsTests
{
    [TestMethod]
    [DataRow("codex", "responses-vNext", "ultra", "Main", 512000)]
    [DataRow("codex", "responses", "minimal", "Fallback", 64000)]
    [DataRow("pi", "openai-completions", "medium", "Main", 128000)]
    [DataRow("pi", "provider.native-api", "off", "Fallback", 4096)]
    public async Task NativeSettingsAndPerRouteContextReachTheIsolatedCli(string kernel, string mode, string effort, string route, int context)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        AiTargetSettings target = KernelFixture.Target(mode: mode);
        target.Name = route;
        target.Effort = effort;
        target.ContextWindowTokens = context;

        Assert.IsTrue(RouteDispatcher.IsTargetCompatible(kernel, target));
        KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync(kernel, target, "synthetic input", 10);
        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual(context, result.Diagnostics!.ContextWindowTokens);
        using JsonDocument capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.RequestRoot, "capture.json")));
        string config = capture.RootElement.GetProperty("Config").GetString()!;
        if (kernel == "codex")
        {
            StringAssert.Contains(config, $"wire_api = \"{mode}\"");
            StringAssert.Contains(config, $"model_reasoning_effort = \"{effort}\"");
            StringAssert.Contains(config, $"model_context_window = {context}");
            StringAssert.Contains(config, "approval_policy = \"never\"");
        }
        else
        {
            using JsonDocument models = JsonDocument.Parse(config);
            JsonElement provider = models.RootElement.GetProperty("providers").GetProperty("kit");
            Assert.AreEqual(mode, provider.GetProperty("api").GetString());
            Assert.AreEqual(context, provider.GetProperty("models")[0].GetProperty("contextWindow").GetInt32());
            Assert.IsTrue(provider.GetProperty("models")[0].GetProperty("maxTokens").GetInt32() <= context);
            string[] arguments = capture.RootElement.GetProperty("Args").EnumerateArray().Select(value => value.GetString()!).ToArray();
            Assert.AreEqual(effort, arguments[Array.IndexOf(arguments, "--thinking") + 1]);
            Assert.IsTrue(arguments.Contains("--no-tools"));
        }
    }

    [TestMethod]
    [DataRow("responses\n[tools]")]
    [DataRow("--execute")]
    [DataRow("unsafe/value")]
    [DataRow("")]
    public void NativeIdentifiersStillRejectConfigurationInjection(string value)
    {
        AiTargetSettings target = KernelFixture.Target();
        target.Mode = value;
        Assert.IsFalse(RouteDispatcher.IsTargetCompatible("codex", target));
        target.Mode = "responses";
        target.Effort = value;
        Assert.IsFalse(RouteDispatcher.IsTargetCompatible("pi", target));
    }

    [TestMethod]
    [DataRow("codex", "catalog")]
    [DataRow("pi", "execute")]
    public async Task NativeConfigurationRejectionReportsItsPhaseWithoutLeakingRawErrors(string kernel, string phase)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync(kernel, KernelFixture.Target(mode: "fixture-unsupported"), "synthetic input", 10);

        Assert.AreEqual(AiErrorCode.InvalidConfiguration, result.ErrorCode);
        Assert.IsFalse(result.CanFailover);
        Assert.AreEqual(phase, result.Diagnostics!.Phase);
        Assert.AreEqual("UnsupportedNativeConfiguration", result.Diagnostics.Reason);
        Assert.AreEqual(9, result.Diagnostics.ExitCode);
        Assert.AreEqual("None", result.Diagnostics.ProcessFailure);
        StringAssert.Contains(result.ErrorMessage, "native kernel rejected");
        AssertSafe(result.ErrorMessage + JsonSerializer.Serialize(result.Diagnostics));
        Assert.AreEqual(0, Directory.GetDirectories(fixture.RequestRoot).Length);
    }

    [TestMethod]
    public async Task MalformedNativeCatalogRetainsItsProcessExitCode()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync("codex", KernelFixture.Target(mode: "fixture-malformed-catalog"), "synthetic input", 10);

        Assert.AreEqual(AiErrorCode.InvalidPayload, result.ErrorCode);
        Assert.AreEqual("catalog", result.Diagnostics!.Phase);
        Assert.AreEqual(0, result.Diagnostics.ExitCode);
        Assert.AreEqual("None", result.Diagnostics.ProcessFailure);
        Assert.IsFalse(result.CanFailover);
    }

    [TestMethod]
    [DataRow("codex", 2, 1000L, null)]
    [DataRow("pi", 1, 1050L, 50L)]
    public async Task NativeRetriesAndUsageAreCollectedWithoutALogSubscriberAndWithoutEchoDoubleCounting(string kernel, int retries, long inputTokens, long? cacheWriteTokens)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync(kernel, KernelFixture.Target("native-usage"), "synthetic input", 10);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        AiRouteDiagnostics diagnostics = result.Diagnostics!;
        Assert.AreEqual("complete", diagnostics.Phase);
        Assert.AreEqual("NativeRetryRecovered", diagnostics.Reason);
        Assert.AreEqual(retries, diagnostics.NativeRetryCount);
        Assert.AreEqual(3, diagnostics.NativeRetryLimit);
        Assert.AreEqual(503, diagnostics.HttpStatusCode);
        Assert.AreEqual(inputTokens, diagnostics.InputTokens);
        Assert.AreEqual(250L, diagnostics.CachedInputTokens);
        Assert.AreEqual(cacheWriteTokens, diagnostics.CacheWriteTokens);
        Assert.AreEqual(100L, diagnostics.OutputTokens);
        Assert.AreEqual(0, diagnostics.ExitCode);
        Assert.IsTrue(diagnostics.ElapsedMilliseconds > 0);
        AssertSafe(JsonSerializer.Serialize(diagnostics));
        Assert.AreEqual(1, Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length);
    }

    [TestMethod]
    public async Task CodexReconnectionEventsExposeAttemptAndLimitWithoutPayloads()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        var events = new ConcurrentQueue<string>();
        KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync("codex", KernelFixture.Target("native-usage"), "synthetic input", 10, events.Enqueue);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(events.Any(value => value.Contains("event=retry, source=stdout", StringComparison.Ordinal) &&
            value.Contains("retryAttempt=2, retryLimit=3", StringComparison.Ordinal)));
        AssertSafe(string.Join('\n', events));
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task MissingNativeUsageRemainsUnknown(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync(kernel, KernelFixture.Target(), "synthetic input", 10);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Diagnostics!.InputTokens);
        Assert.IsNull(result.Diagnostics.OutputTokens);
        Assert.IsNull(result.Diagnostics.CachedInputTokens);
        Assert.AreEqual(0, result.Diagnostics.NativeRetryCount);
        Assert.AreEqual("None", result.Diagnostics.Reason);
    }

    [TestMethod]
    public async Task UsageInLargePiAssistantMessagesStillCountsOnce()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        await fixture.SetResponseAsync("{\"notes\":\"" + new string('x', 20000) + "\"}");
        KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync("pi", KernelFixture.Target("native-usage"), "synthetic input", 10);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1050L, result.Diagnostics!.InputTokens);
        Assert.AreEqual(100L, result.Diagnostics.OutputTokens);
    }

    [TestMethod]
    [DataRow("codex", "account-unavailable", "AccountUnavailable", 404, true)]
    [DataRow("pi", "account-unavailable", "AccountUnavailable", 404, true)]
    [DataRow("codex", "model-not-found", "ModelNotFound", 404, true)]
    [DataRow("pi", "model-not-found", "ModelNotFound", 404, true)]
    [DataRow("codex", "auth", "Authentication", 401, true)]
    [DataRow("pi", "auth", "Authentication", 401, true)]
    [DataRow("codex", "forbidden", "AccessDenied", 403, true)]
    [DataRow("pi", "forbidden", "AccessDenied", 403, true)]
    [DataRow("pi", "rate-limit", "RateLimited", 429, true)]
    [DataRow("codex", "quota", "QuotaExceeded", 429, true)]
    public async Task FailuresDistinguishProviderAvailabilityFromInvalidConfiguration(string kernel, string scenario, string reason, int status, bool canFailover)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync(kernel, KernelFixture.Target(scenario), "synthetic input", 10);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(canFailover, result.CanFailover);
        Assert.AreEqual(canFailover ? AiErrorCode.EndpointFailed : AiErrorCode.InvalidConfiguration, result.ErrorCode);
        Assert.AreEqual("execute", result.Diagnostics!.Phase);
        Assert.AreEqual(reason, result.Diagnostics.Reason);
        Assert.AreEqual(status, result.Diagnostics.HttpStatusCode);
        StringAssert.Contains(result.ErrorMessage, $"HTTP {status}");
        AssertSafe(result.ErrorMessage + JsonSerializer.Serialize(result.Diagnostics));
        Assert.AreEqual(1, Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length);
    }

    [TestMethod]
    public async Task ExhaustedNativeRetriesRetainTheFinalFailureAndCount()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync("pi", KernelFixture.Target("pi-native-exhausted"), "synthetic input", 10);

        Assert.AreEqual(AiErrorCode.EndpointFailed, result.ErrorCode);
        Assert.AreEqual(3, result.Diagnostics!.NativeRetryCount);
        Assert.AreEqual(3, result.Diagnostics.NativeRetryLimit);
        Assert.AreEqual("ServerError", result.Diagnostics.Reason);
        StringAssert.Contains(result.ErrorMessage, "Native retries: 3/3");
    }

    private static void AssertSafe(string text)
    {
        Assert.IsFalse(text.Contains("synthetic-fixture-secret", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("synthetic.invalid", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("synthetic input", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("secret-model", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("secret-account", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("{\"issues\":[]}", StringComparison.Ordinal));
    }
}
