namespace Kit.AiHub.UnitTests;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
                Assert.IsTrue(config.Contains("model_reasoning_effort = \"xhigh\"", StringComparison.Ordinal));
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
                KernelExecutionResult result = await fixture.Dispatcher.ExecuteAsync(kernel, KernelFixture.Target(scenario), "synthetic input", 10);
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
