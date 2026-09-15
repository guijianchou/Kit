namespace Kit.AiHub.UnitTests;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;
using Kit.AiHub.Models;
using Kit.AiHub.Security;
using Kit.AiHub.Serialization;
using Kit.AiHub.Storage;
using Kit.AiHub.UnitTests.Fixtures;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class EngineAndIpcTests
{
    private const string ValidReport = """
        {"findings":[{"itemId":"{{itemId}}","key":"health","severity":"low","titleEn":"Check health","titleZh":"Fixture title","descriptionEn":"A supplied fact","descriptionZh":"Fixture description","recommendationEn":"Review the service","recommendationZh":"Fixture recommendation"}],"actions":[]}
        """;

    [TestMethod]
    public async Task DisabledHubShortCircuitsBeforePolicyAndKernelAccess()
    {
        using var fixture = new FixtureDirectory();
        using var engine = AiHubEngine.Create(fixture.RootPath, Path.Combine(fixture.RootPath, "packages"));
        var result = await engine.ExecuteTaskAsync("../missing", "../task", new[] { new TestInput("sample", "secret", "path") }, Schema());
        Assert.AreEqual(AiErrorCode.HubDisabled, result.ErrorCode);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.RootPath, "kernels")));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.RootPath, "requests")));
    }

    [TestMethod]
    public async Task GenericTaskSanitizesDecodedInputAndAuditsTypedResult()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using var engine = await CreateEngineAsync(fixture);
        await fixture.SetResponseAsync(ValidReport);
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "private.txt");
        var result = await engine.ExecuteTaskAsync("Sample", "diagnostic", new[] { new TestInput("sample", "fixture-raw-secret", path) }, Schema());
        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual("item-000001", result.Payload!.Findings.Single().ItemId);
        using JsonDocument capture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.RequestRoot, "capture.json")));
        string prompt = capture.RootElement.GetProperty("Input").GetString()!;
        Assert.IsFalse(prompt.Contains("fixture-raw-secret", StringComparison.Ordinal));
        StringAssert.Contains(prompt, "item-000001");
        Assert.AreEqual("Main", result.UsedRoute);
    }

    [TestMethod]
    public async Task DynamicBatchesRetainStableIdsAndMergeValidatedReports()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using var engine = await CreateEngineAsync(fixture);
        await fixture.SetResponseAsync(ValidReport);
        var input = Enumerable.Range(0, 37).Select(index => new TestInput(index.ToString(), string.Empty, string.Empty)).ToArray();
        var result = await engine.ExecuteTaskAsync("Sample", "diagnostic", input, Schema());
        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual(3, result.Payload!.Findings.Count);
        CollectionAssert.AreEqual(new[] { "item-000001", "item-000016", "item-000031" }, result.Payload.Findings.Select(finding => finding.ItemId).ToArray());
    }

    [TestMethod]
    public async Task RecoverableMainFailureUsesFallbackAndReportsRoute()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using var engine = await CreateEngineAsync(fixture, "transport", true);
        await fixture.SetResponseAsync(ValidReport);
        var result = await engine.ExecuteTaskAsync("Sample", "diagnostic", new[] { new TestInput("sample", string.Empty, string.Empty) }, Schema());
        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual("Fallback", result.UsedRoute);
        Assert.AreEqual(2, Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length);
    }

    [TestMethod]
    public async Task AuthenticationFailureDoesNotSendInputToFallback()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using var engine = await CreateEngineAsync(fixture, "auth", true);
        await fixture.SetResponseAsync(ValidReport);
        var result = await engine.ExecuteTaskAsync("Sample", "diagnostic", new[] { new TestInput("sample", string.Empty, string.Empty) }, Schema());
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(1, Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length);
        Assert.IsFalse(result.ErrorMessage!.Contains("synthetic-fixture-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("{\"findings\":[],\"actions\":[],\"command\":\"start\"}")]
    [DataRow("{\"findings\":[{\"itemId\":\"other-item\"}],\"actions\":[]}")]
    [DataRow("{\"findings\":[],\"actions\":[{\"itemId\":\"{{itemId}}\",\"actionType\":\"restart\"}]}")]
    [DataRow("{\"findings\":[],\"actions\":[],\"isConfirmedByUser\":true}")]
    [DataRow("{\"findings\":[],\"actions\":[],\"actions\":[]}")]
    [DataRow("text before {\"findings\":[],\"actions\":[]}")]
    public async Task InvalidOutputIsRejectedWithoutFailover(string payload)
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using var engine = await CreateEngineAsync(fixture, fallback: true);
        await fixture.SetResponseAsync(payload);
        var result = await engine.ExecuteTaskAsync("Sample", "diagnostic", new[] { new TestInput("sample", string.Empty, string.Empty) }, Schema());
        Assert.AreEqual(AiErrorCode.PolicyViolation, result.ErrorCode);
        Assert.AreEqual(1, Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length);
    }

    [TestMethod]
    public async Task IndependentWorkerObservesSavedMasterSwitchAndCancelsItsTask()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using var engine = await CreateEngineAsync(fixture, "timeout");
        var running = engine.ExecuteTaskAsync("Sample", "diagnostic", new[] { new TestInput("sample", string.Empty, string.Empty) }, Schema(), new AiTaskOptions { TimeoutSeconds = 20 });
        await WaitForCaptureAsync(fixture);
        new AiHubSettingsStore(fixture.Root).Update(config => config.IsEnabled = false);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.AreEqual(AiErrorCode.HubDisabled, result.ErrorCode);
        Assert.IsFalse(engine.IsEnabled);
    }

    [TestMethod]
    public async Task IpcUsesTheSameEngineAndRetainsCorrelationForMalformedRequests()
    {
        using var fixture = new FixtureDirectory();
        using var engine = AiHubEngine.Create(fixture.RootPath, Path.Combine(fixture.RootPath, "modules"));
        using var handler = new AiHubIpcHandler(engine);
        Guid id = Guid.NewGuid();
        string request = JsonSerializer.Serialize(new AiHubIpcRequest { RequestId = id, Action = "get_status" }, AiHubJsonContext.Default.AiHubIpcRequest);
        string responseText = (await handler.HandleMessageAsync(request))!;
        var response = JsonSerializer.Deserialize(responseText, AiHubJsonContext.Default.AiHubIpcResponse)!;
        Assert.AreEqual(id, response.RequestId);
        Assert.IsTrue(response.IsSuccess);
        Assert.IsFalse(response.IsEnabled);

        string malformed = $$"""{"target":"AiHub","action":"ai_task_request","requestId":"{{id}}","items":"invalid"}""";
        response = JsonSerializer.Deserialize((await handler.HandleMessageAsync(malformed))!, AiHubJsonContext.Default.AiHubIpcResponse)!;
        Assert.AreEqual(id, response.RequestId);
        Assert.AreEqual(AiErrorCode.InvalidPayload, response.ErrorCode);
    }

    [TestMethod]
    public async Task IpcCancellationStopsOnlyTheMatchingRequest()
    {
        using KernelFixture fixture = await KernelFixture.CreateAsync();
        using var engine = await CreateEngineAsync(fixture, "timeout");
        using var handler = new AiHubIpcHandler(engine);
        Guid id = Guid.NewGuid();
        using JsonDocument item = JsonDocument.Parse("{\"state\":\"running\"}");
        string request = JsonSerializer.Serialize(new AiHubIpcRequest
        {
            RequestId = id, Action = "ai_task_request", PluginId = "Sample", TaskId = "diagnostic", Items = [item.RootElement.Clone()], TimeoutSeconds = 20,
        }, AiHubJsonContext.Default.AiHubIpcRequest);
        Task<string?> pending = handler.HandleMessageAsync(request);
        await WaitForCaptureAsync(fixture);
        string cancellation = JsonSerializer.Serialize(new AiHubIpcRequest { RequestId = id, Action = "ai_task_cancel" }, AiHubJsonContext.Default.AiHubIpcRequest);
        await handler.HandleMessageAsync(cancellation);
        var response = JsonSerializer.Deserialize((await pending.WaitAsync(TimeSpan.FromSeconds(8)))!, AiHubJsonContext.Default.AiHubIpcResponse)!;
        Assert.AreEqual(id, response.RequestId);
        Assert.AreEqual(AiErrorCode.Cancelled, response.ErrorCode);
    }

    private static AiTaskSchema<TestInput, AiTaskReport> Schema() => AiTaskReport.CreateSchema(EngineTestJsonContext.Default.TestInput);

    private static async Task<TaskAiEngine> CreateEngineAsync(KernelFixture fixture, string scenario = "success", bool fallback = false)
    {
        var store = new AiHubSettingsStore(fixture.Root);
        var config = AiHubConfig.CreateDefault();
        config.IsEnabled = true;
        config.Targets[0] = KernelFixture.Target(scenario);
        config.Targets[0].Name = "Main";
        if (fallback)
        {
            config.Targets[1] = KernelFixture.Target();
            config.Targets[1].Name = "Fallback";
        }

        store.Save(config);
        string packages = Path.Combine(fixture.Root, "modules");
        string task = Path.Combine(packages, "Sample", "Chains", "diagnostic");
        Directory.CreateDirectory(task);
        await File.WriteAllTextAsync(Path.Combine(task, "AGENTS.md"), "Return a report conforming to the host schema. Do not execute actions.");
        return new TaskAiEngine(store, fixture.Manager, new SecurityPolicyService(fixture.Root, packages));
    }

    private static async Task WaitForCaptureAsync(KernelFixture fixture)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!Directory.Exists(fixture.RequestRoot) || Directory.GetFiles(fixture.RequestRoot, "capture-*.json").Length == 0)
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}

internal sealed record TestInput(string Name, string ApiKey, string FilePath);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TestInput))]
internal sealed partial class EngineTestJsonContext : JsonSerializerContext
{
}
