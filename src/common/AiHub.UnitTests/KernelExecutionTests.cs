namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;
using Kit.AiHub.Models;
using Kit.AiHub.UnitTests.Fixtures;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class KernelExecutionTests
{
    [TestMethod]
    public async Task ProcessDrainsBothPipesBeforeWaitingForExit()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        KernelProcessResult result = await KernelProcessRunner.RunAsync(fixture.Process("drain"), null,
            TimeSpan.FromSeconds(10), CancellationToken.None, 256 * 1024, 256 * 1024);
        Assert.AreEqual(KernelProcessFailure.None, result.Failure);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(131072, result.StandardOutput.Length);
        Assert.AreEqual(131072, result.StandardError.Length);
    }

    [TestMethod]
    public async Task CancellationDuringBlockedInputTerminatesTheProcessTree()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        Task<KernelProcessResult> running = KernelProcessRunner.RunAsync(fixture.Process("block-stdin"), new string('x', 2 * 1024 * 1024),
            TimeSpan.FromSeconds(20), cancellation.Token);
        await fixture.WaitForFileAsync("children.json");
        var elapsed = Stopwatch.StartNew();
        cancellation.Cancel();
        KernelProcessResult result = await running;
        Assert.AreEqual(KernelProcessFailure.Cancelled, result.Failure);
        Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        fixture.AssertChildrenExited();
    }

    [TestMethod]
    public async Task TimeoutTerminatesTheProcessTree()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        KernelProcessResult result = await KernelProcessRunner.RunAsync(fixture.Process("spawn"), null,
            TimeSpan.FromSeconds(3), CancellationToken.None);
        Assert.AreEqual(KernelProcessFailure.Timeout, result.Failure);
        fixture.AssertChildrenExited();
    }

    [TestMethod]
    public async Task DetachedDescendantCannotHoldTheOutputPipesOpen()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        KernelProcessResult result = await KernelProcessRunner.RunAsync(fixture.Process("detach"), null,
            TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.AreEqual(KernelProcessFailure.None, result.Failure);
        fixture.AssertChildrenExited();
    }

    [TestMethod]
    public async Task OutputLimitsTerminateTheWriterAndPreserveExitClassification()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        foreach (string scenario in new[] { "flood-output", "flood-error" })
        {
            var elapsed = Stopwatch.StartNew();
            KernelProcessResult limited = await KernelProcessRunner.RunAsync(fixture.Process(scenario), null,
                TimeSpan.FromSeconds(10), CancellationToken.None, 1024, 1024);
            Assert.AreEqual(KernelProcessFailure.OutputLimit, limited.Failure);
            Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        }

        KernelProcessResult rejected = await KernelProcessRunner.RunAsync(fixture.Process("nonzero"), null,
            TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.AreEqual(KernelProcessFailure.None, rejected.Failure);
        Assert.AreEqual(7, rejected.ExitCode);
    }

    [TestMethod]
    public async Task BothKernelsReturnTheFinalPayloadAndActualModel()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        foreach (string kernel in new[] { "codex", "pi" })
        {
            KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync(kernel, KernelFixture.Target(), "synthetic input", 10);
            Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
            Assert.AreEqual("{\"issues\":[]}", result.Output);
            Assert.AreEqual("fixture-actual", result.UsedModel);
            Assert.AreEqual(0, Directory.GetDirectories(fixture.RequestRoot).Length);
            using JsonDocument capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.RequestRoot, "capture.json")));
            Assert.IsTrue(capture.RootElement.GetProperty("KeyMatches").GetBoolean());
            Assert.IsTrue(capture.RootElement.GetProperty("InheritedKeysAbsent").GetBoolean());
            Assert.IsFalse(capture.RootElement.GetProperty("SecretInArguments").GetBoolean());
            Assert.IsFalse(capture.RootElement.GetProperty("SecretInConfig").GetBoolean());
            string config = capture.RootElement.GetProperty("Config").GetString()!;
            if (kernel == "codex")
            {
                Assert.IsTrue(config.Contains("model_reasoning_effort = \"max\"", StringComparison.Ordinal));
                Assert.IsTrue(config.Contains("shell_tool = false", StringComparison.Ordinal));
                Assert.IsTrue(config.Contains("approval_policy = \"never\"", StringComparison.Ordinal));
                using JsonDocument catalog = JsonDocument.Parse(capture.RootElement.GetProperty("Catalog").GetString()!);
                Assert.IsFalse(catalog.RootElement.GetProperty("models")[0].GetProperty("use_responses_lite").GetBoolean());
                Assert.AreEqual("capability", catalog.RootElement.GetProperty("models")[0].GetProperty("retained").GetString());
            }
            else
            {
                using JsonDocument models = JsonDocument.Parse(config);
                Assert.AreEqual("openai-responses", models.RootElement.GetProperty("providers").GetProperty("kit").GetProperty("api").GetString());
                string[] arguments = capture.RootElement.GetProperty("Args").EnumerateArray().Select(value => value.GetString()!).ToArray();
                Assert.IsTrue(arguments.Contains("--no-tools") && arguments.Contains("--no-extensions") && arguments.Contains("--no-context-files"));
                Assert.AreEqual("max", arguments[Array.IndexOf(arguments, "--thinking") + 1]);
                Assert.IsTrue(capture.RootElement.GetProperty("SessionHome").GetString()!.StartsWith(fixture.RequestRoot, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [TestMethod]
    [DataRow("codex", "gpt-6-luna", "max")]
    [DataRow("codex", "gpt-6.1-sol", "max")]
    [DataRow("codex", "future-provider-model", "low")]
    [DataRow("codex", "future-provider-model", "high")]
    [DataRow("codex", "future-provider-model", "max")]
    [DataRow("pi", "gpt-6-luna", "max")]
    [DataRow("pi", "gpt-6.1-sol", "high")]
    [DataRow("pi", "future-provider-model", "low")]
    public async Task ModelAndEffortReachTheNativeKernelUnchanged(string kernel, string model, string effort)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        AiTargetSettings target = KernelFixture.Target();
        target.Model = model;
        target.Effort = effort;
        string? diagnostic = null;

        KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync(kernel, target, "synthetic input", 10,
            reportDiagnostic: message =>
            {
                if (message.StartsWith("AI route configured:", StringComparison.Ordinal))
                {
                    diagnostic = message;
                }
            });

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.IsNotNull(diagnostic);
        StringAssert.Contains(diagnostic, $"model=\"{model}\"");
        StringAssert.Contains(diagnostic, $"configuredEffort={effort}, nativeEffortInput={effort}");
        Assert.IsFalse(diagnostic.Contains(target.ApiKey, StringComparison.Ordinal));
        Assert.IsFalse(diagnostic.Contains(target.BaseUrl, StringComparison.Ordinal));
        Assert.IsFalse(diagnostic.Contains("synthetic input", StringComparison.Ordinal));
        Assert.AreEqual(0, Directory.GetDirectories(fixture.RequestRoot).Length);
        using JsonDocument capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.RequestRoot, "capture.json")));
        Assert.AreEqual(model, capture.RootElement.GetProperty("Model").GetString());
        Assert.IsTrue(capture.RootElement.GetProperty("KeyMatches").GetBoolean());
        Assert.IsTrue(capture.RootElement.GetProperty("InheritedKeysAbsent").GetBoolean());
        Assert.IsFalse(capture.RootElement.GetProperty("SecretInArguments").GetBoolean());
        Assert.IsFalse(capture.RootElement.GetProperty("SecretInConfig").GetBoolean());
        string config = capture.RootElement.GetProperty("Config").GetString()!;
        if (kernel == "codex")
        {
            Assert.IsTrue(config.Contains($"model_reasoning_effort = \"{effort}\"", StringComparison.Ordinal));
            Assert.IsTrue(config.Contains("wire_api = \"responses\"", StringComparison.Ordinal));
            using JsonDocument catalog = JsonDocument.Parse(capture.RootElement.GetProperty("Catalog").GetString()!);
            Assert.IsFalse(catalog.RootElement.GetProperty("models")[0].GetProperty("use_responses_lite").GetBoolean());
            Assert.AreEqual("capability", catalog.RootElement.GetProperty("models")[0].GetProperty("retained").GetString());
        }
        else
        {
            string[] arguments = capture.RootElement.GetProperty("Args").EnumerateArray().Select(value => value.GetString()!).ToArray();
            Assert.AreEqual(effort, arguments[Array.IndexOf(arguments, "--thinking") + 1]);
            using JsonDocument models = JsonDocument.Parse(config);
            Assert.AreEqual("openai-responses", models.RootElement.GetProperty("providers").GetProperty("kit").GetProperty("api").GetString());
        }
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task NativeRetryDiagnosticsRemainSafeAndDoNotTurnRecoveryIntoFailure(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        var diagnostics = new ConcurrentQueue<string>();
        KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync(kernel, KernelFixture.Target("recovered"),
            "synthetic input", 10, diagnostics.Enqueue);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.IsTrue(diagnostics.Any(value => value.Contains("event=error, source=stdout", StringComparison.Ordinal) && value.Contains("reason=StreamDisconnected", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Any(value => value.Contains("event=retry, source=stderr", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Any(value => value.Contains("event=completed", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Any(value => value.StartsWith("AI route finished:", StringComparison.Ordinal) && value.Contains("code=None", StringComparison.Ordinal) && value.Contains("exitCode=0", StringComparison.Ordinal)));
        string messages = string.Join('\n', diagnostics);
        Assert.IsFalse(messages.Contains("synthetic-fixture-secret", StringComparison.Ordinal));
        Assert.IsFalse(messages.Contains("synthetic.invalid", StringComparison.Ordinal));
        Assert.IsFalse(messages.Contains("synthetic input", StringComparison.Ordinal));
        Assert.IsFalse(messages.Contains("{\"issues\":[]}", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task NonzeroNativeExitRetainsSafeHttpStatusAndExitCode(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        var diagnostics = new ConcurrentQueue<string>();
        KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync(kernel, KernelFixture.Target("nonzero-transport"),
            "synthetic input", 10, diagnostics.Enqueue);

        Assert.AreEqual(AiErrorCode.EndpointFailed, result.ErrorCode);
        Assert.IsTrue(result.CanFailover);
        Assert.IsTrue(diagnostics.Any(value => value.Contains("httpStatus=503, reason=ServerError", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Any(value => value.Contains("exitCode=7", StringComparison.Ordinal)));
        Assert.IsFalse(string.Join('\n', diagnostics).Contains("synthetic-fixture-secret", StringComparison.Ordinal));
        Assert.IsFalse(string.Join('\n', diagnostics).Contains("synthetic.invalid", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task CancellationKeepsDiagnosticsReportedBeforeTheProcessExited(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var diagnostics = new ConcurrentQueue<string>();
        var observedError = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<KernelExecutionResult> running = fixture.Dispatcher.ExecuteAsync(kernel, KernelFixture.Target("error-then-wait"),
            "synthetic input", 10, message =>
            {
                diagnostics.Enqueue(message);
                if (message.Contains("event=error, source=stdout", StringComparison.Ordinal))
                {
                    observedError.TrySetResult(true);
                }
            }, cancellation.Token);
        await observedError.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(running.IsCompleted, "The native error must be reported while its process is still waiting.");
        cancellation.Cancel();
        KernelExecutionResult result = await running;

        Assert.AreEqual(AiErrorCode.Cancelled, result.ErrorCode);
        Assert.IsTrue(diagnostics.Any(value => value.Contains("reason=StreamDisconnected", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Any(value => value.Contains("cancellationSource=upstream", StringComparison.Ordinal)));
        Assert.AreEqual(0, Directory.GetDirectories(fixture.RequestRoot).Length);
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task FailingDiagnosticSubscriberDoesNotChangeNativeResult(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync(kernel, KernelFixture.Target("recovered"),
            "synthetic input", 10, _ => throw new InvalidOperationException("synthetic callback failure"));
        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
    }

    [TestMethod]
    public async Task DiagnosticLinesAreBoundedWithoutChangingCapturedOutput()
    {
        string output = new string('x', 20_000) + "\nfirst\r\nlast";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(output));
        using var reader = new StreamReader(stream);
        var lines = new ConcurrentQueue<string>();
        string captured = await KernelProcessRunner.ReadBoundedAsync(reader, 32_768, CancellationToken.None, line =>
        {
            lines.Enqueue(line);
            throw new InvalidOperationException("synthetic callback failure");
        });
        Assert.AreEqual(output, captured);
        CollectionAssert.AreEqual(new[] { "first", "last" }, lines.ToArray());
    }

    [TestMethod]
    public async Task RepeatedNativeErrorsHaveABoundedDiagnosticBudget()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        var diagnostics = new ConcurrentQueue<string>();
        KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync("codex", KernelFixture.Target("many-errors"),
            "synthetic input", 10, diagnostics.Enqueue);
        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.IsTrue(diagnostics.Count <= 130);
        Assert.IsTrue(diagnostics.Any(value => value.StartsWith("AI route finished:", StringComparison.Ordinal) && value.Contains("suppressedEvents=131", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("recovered", AiErrorCode.None)]
    [DataRow("exhausted", AiErrorCode.EndpointFailed)]
    [DataRow("incomplete", AiErrorCode.EndpointFailed)]
    [DataRow("auth-recovered", AiErrorCode.InvalidConfiguration)]
    [DataRow("tool-recovered", AiErrorCode.PolicyViolation)]
    public async Task PiNativeAssistantRetryOnlyRecoversTransportFailures(string scenario, AiErrorCode expected)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        var diagnostics = new ConcurrentQueue<string>();
        var result = await fixture.Dispatcher.ExecuteAsync("pi", KernelFixture.Target("pi-native-" + scenario), "synthetic input", 10, diagnostics.Enqueue);
        Assert.AreEqual(expected, result.ErrorCode);
        Assert.AreEqual(expected == AiErrorCode.None, result.IsSuccess);
        Assert.IsTrue(diagnostics.Any(value => value.Contains("event=retry, source=stdout", StringComparison.Ordinal)));
        Assert.IsFalse(string.Join('\n', diagnostics).Contains("synthetic-fixture-secret", StringComparison.Ordinal));
        Assert.AreEqual(1, Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length);
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task NativeActivityIsReportedBeforeCompletionWithoutStreamingPayloads(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset,
            $"Local\\KitAiHubFixture-{Path.GetFileName(fixture.Root)}-kit-gate-activity");
        using var cancellation = new CancellationTokenSource();
        var responding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new ConcurrentQueue<string>();
        Task<KernelExecutionResult> running = fixture.Dispatcher.ExecuteAsync(kernel, KernelFixture.Target("activity-wait"),
            "kit-gate-activity", 15, message =>
            {
                diagnostics.Enqueue(message);
                if (message.Contains("phase=responding", StringComparison.Ordinal))
                {
                    responding.TrySetResult();
                }
            }, cancellation.Token);
        try
        {
            await responding.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(running.IsCompleted);
            Assert.AreEqual(3, diagnostics.Count(message => message.Contains("event=activity,", StringComparison.Ordinal)));
            Assert.IsFalse(string.Join('\n', diagnostics).Contains("synthetic-private-content", StringComparison.Ordinal));
            release.Set();
            Assert.IsTrue((await running).IsSuccess);
        }
        finally
        {
            cancellation.Cancel();
            release.Set();
            await running;
        }
    }

    [TestMethod]
    public async Task ProbeRequiresExactlyAnEmptyIssuesArray()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        foreach (string kernel in new[] { "codex", "pi" })
        {
            Assert.IsTrue((await fixture.Dispatcher.TestConnectionAsync(KernelFixture.Target(), kernel)).Success);
            foreach (string scenario in new[] { "nonempty", "missing", "wrongtype", "duplicate", "invalid-json", "incomplete", "error" })
            {
                Assert.IsFalse((await fixture.Dispatcher.TestConnectionAsync(KernelFixture.Target(scenario), kernel)).Success);
            }
        }
    }

    [TestMethod]
    public async Task OnlyEndpointFailuresMayRequestFailoverAndErrorsRemainRedacted()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        foreach (string kernel in new[] { "codex", "pi" })
        {
            foreach (string scenario in new[] { "transport", "transport-timeout", "auth", "error", "incomplete", "tool" })
            {
                var diagnostics = new ConcurrentQueue<string>();
                KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync(kernel, KernelFixture.Target(scenario), "synthetic input", 10, diagnostics.Enqueue);
                Assert.IsFalse(result.IsSuccess);
                Assert.AreEqual(scenario is "transport" or "transport-timeout", result.CanFailover);
                Assert.IsFalse(result.ErrorMessage.Contains("synthetic-fixture-secret", StringComparison.Ordinal));
                if (scenario == "transport-timeout")
                {
                    Assert.AreEqual(AiErrorCode.Timeout, result.ErrorCode);
                }

                if (scenario == "auth")
                {
                    Assert.AreEqual(AiErrorCode.InvalidConfiguration, result.ErrorCode);
                }

                if (scenario == "tool")
                {
                    Assert.AreEqual(AiErrorCode.PolicyViolation, result.ErrorCode);
                    Assert.IsTrue(diagnostics.Any(value => value.Contains("event=tool", StringComparison.Ordinal)));
                }

                if (scenario == "transport")
                {
                    Assert.IsTrue(diagnostics.Any(value => value.Contains("event=turn.failed", StringComparison.Ordinal) && value.Contains("httpStatus=503", StringComparison.Ordinal)));
                }
            }
        }
    }

    [TestMethod]
    public async Task ModeAndTomlConfigurationAreValidatedBeforeExecution()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        Assert.IsFalse(RouteDispatcher.IsTargetCompatible("codex", KernelFixture.Target(mode: "chat")));
        Assert.IsTrue(RouteDispatcher.IsTargetCompatible("pi", KernelFixture.Target(mode: "chat")));
        KernelExecutionResult rejected = await fixture.Dispatcher.ExecuteAsync("codex", KernelFixture.Target(mode: "chat"), "input", 10);
        Assert.AreEqual(AiErrorCode.InvalidConfiguration, rejected.ErrorCode);
        Assert.IsFalse(rejected.CanFailover);
        Assert.IsFalse(Directory.Exists(fixture.RequestRoot));

        var target = KernelFixture.Target("quoted\\\"model");
        KernelExecutionResult escaped = await fixture.Dispatcher.ExecuteAsync("codex", target, "input", 10);
        Assert.IsTrue(escaped.IsSuccess);
        using JsonDocument capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.RequestRoot, "capture.json")));
        Assert.AreEqual(target.Model, capture.RootElement.GetProperty("Model").GetString());

        KernelExecutionResult chat = await fixture.Dispatcher.ExecuteAsync("pi", KernelFixture.Target(mode: "chat"), "input", 10);
        Assert.IsTrue(chat.IsSuccess);
        using JsonDocument piCapture = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.RequestRoot, "capture.json")));
        using JsonDocument piConfig = JsonDocument.Parse(piCapture.RootElement.GetProperty("Config").GetString()!);
        Assert.AreEqual("openai-completions", piConfig.RootElement.GetProperty("providers").GetProperty("kit").GetProperty("api").GetString());
    }

    [TestMethod]
    public async Task OutputFileLimitAndExecutionDeadlineNeverFailOver()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        KernelExecutionResult oversized = await fixture.Dispatcher.ExecuteAsync("codex", KernelFixture.Target("large-file"), "input", 10);
        Assert.AreEqual(AiErrorCode.InvalidPayload, oversized.ErrorCode);
        Assert.IsFalse(oversized.CanFailover);
        KernelExecutionResult timeout = await fixture.Dispatcher.ExecuteAsync("pi", KernelFixture.Target("timeout"), "input", 1);
        Assert.AreEqual(AiErrorCode.Timeout, timeout.ErrorCode);
        Assert.IsFalse(timeout.CanFailover);
        Assert.AreEqual(0, Directory.GetDirectories(fixture.RequestRoot).Length);
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public async Task CleanupFailureIsNotOverwrittenByRouteTimeout(string kernel)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        FileStream? lockedFile = null;
        try
        {
            KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync(kernel, KernelFixture.Target("timeout"), "input", 1, message =>
            {
                if (message.StartsWith("AI route configured:", StringComparison.Ordinal))
                {
                    string request = Directory.GetDirectories(fixture.RequestRoot).Single();
                    lockedFile = new FileStream(Path.Combine(request, "cleanup-lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                }
            }, CancellationToken.None);

            Assert.IsNotNull(lockedFile, "The fixture must prevent request-directory deletion.");
            Assert.AreEqual(AiErrorCode.ExecutionFailed, result.ErrorCode);
            Assert.IsFalse(result.CanFailover);
        }
        finally
        {
            lockedFile?.Dispose();
        }
    }

    [TestMethod]
    public async Task CancellationWhileWaitingForInstallationLeaseRemainsCancellation()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        string locks = Path.Combine(fixture.Manager.KernelRoot, ".locks");
        Directory.CreateDirectory(locks);
        await using var installation = new FileStream(Path.Combine(locks, "codex.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync("codex", KernelFixture.Target(), "input", 10, cancellation.Token);
        Assert.AreEqual(AiErrorCode.Cancelled, result.ErrorCode);
        Assert.IsFalse(result.CanFailover);
        Assert.IsFalse(Directory.Exists(fixture.RequestRoot));
    }

    [TestMethod]
    public async Task MissingKernelAndInvalidRequestsNeverFailOver()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        File.Delete(Path.Combine(fixture.Root, "kernels", "pi", "pi.exe"));
        KernelExecutionResult missing = await fixture.Dispatcher.ExecuteAsync("pi", KernelFixture.Target(), "input", 10);
        Assert.AreEqual(AiErrorCode.KernelNotInstalled, missing.ErrorCode);
        Assert.IsFalse(missing.CanFailover);

        KernelExecutionResult deadline = await fixture.Dispatcher.ExecuteAsync("codex", KernelFixture.Target(), "input", 0);
        Assert.AreEqual(AiErrorCode.InvalidPayload, deadline.ErrorCode);
        Assert.IsFalse(deadline.CanFailover);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        KernelExecutionResult cancellation = await fixture.Dispatcher.ExecuteAsync("codex", KernelFixture.Target(), "input", 10, cancelled.Token);
        Assert.AreEqual(AiErrorCode.Cancelled, cancellation.ErrorCode);
        Assert.IsFalse(cancellation.CanFailover);
        Assert.IsFalse(Directory.Exists(fixture.RequestRoot));
    }
}
