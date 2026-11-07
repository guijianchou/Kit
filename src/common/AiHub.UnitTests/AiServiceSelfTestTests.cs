namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;
using Kit.AiHub.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AiServiceSelfTestTests
{
    [TestMethod]
    public async Task ProbeAcceptsOnlyThePingRecordAndNeverPermitsActions()
    {
        var engine = new FakeEngine(Report());
        SelfTestResult result = await AiServiceSelfTest.RunAsync(engine);

        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.AreEqual(1, result.FindingCount);
        Assert.AreEqual(2, engine.Options!.BatchSize);
        Assert.AreEqual("ping", engine.Inputs[0].Note);
        Assert.AreEqual("ignore me", engine.Inputs[1].Note);
        Assert.IsTrue(engine.SchemaAccepted);
        Assert.AreEqual(0, engine.Schema!.AllowedActions.Count);
        Assert.IsFalse(engine.Schema.ValidateOutput(Report(), new HashSet<string> { "item-000002" }), "The ping reference must belong to the supplied batch.");
    }

    [TestMethod]
    [DataRow("wrong-item")]
    [DataRow("wrong-key")]
    [DataRow("wrong-severity")]
    [DataRow("uppercase-severity")]
    [DataRow("empty")]
    [DataRow("extra-finding")]
    [DataRow("action")]
    [DataRow("missing-language")]
    [DataRow("multiline")]
    [DataRow("oversize")]
    [DataRow("null-finding")]
    [DataRow("null-actions")]
    public async Task InvalidProbeIsRejectedBySchemaAndPostValidation(string defect)
    {
        AiTaskReport report = defect switch
        {
            "wrong-item" => Report(Finding(itemId: "item-000002")),
            "wrong-key" => Report(Finding(key: "audit")),
            "wrong-severity" => Report(Finding(severity: "medium")),
            "uppercase-severity" => Report(Finding(severity: "Low")),
            "missing-language" => Report(Finding(titleZh: string.Empty)),
            "multiline" => Report(Finding(description: "probe\nreceived")),
            "oversize" => Report(Finding(description: new string('x', 513))),
            "null-actions" => new() { Findings = [Finding()], Actions = null! },
            _ => Report(),
        };
        switch (defect)
        {
            case "empty":
                report.Findings.Clear();
                break;
            case "extra-finding":
                report.Findings.Add(Finding(itemId: "item-000002"));
                break;
            case "null-finding":
                report.Findings[0] = null!;
                break;
            case "action":
                report.Actions.Add(new()
                {
                    ItemId = "item-000001",
                    ActionType = "skip",
                    TargetRelativePath = string.Empty,
                    ReasonEn = "No action.",
                    ReasonZh = "无需操作。",
                });
                break;
        }

        // Deliberately return success from the fake transport even for invalid output.
        var engine = new FakeEngine(report);
        SelfTestResult result = await AiServiceSelfTest.RunAsync(engine);
        Assert.IsFalse(engine.SchemaAccepted, defect);
        Assert.IsFalse(result.Success, defect);
        Assert.AreEqual(0, result.FindingCount);
        StringAssert.Contains(result.ErrorMessage, "instructions were not followed");
    }

    [TestMethod]
    public async Task DisabledServiceDoesNotDispatchTheProbe()
    {
        var engine = new FakeEngine(Report()) { IsEnabled = false };
        SelfTestResult result = await AiServiceSelfTest.RunAsync(engine);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, engine.Calls);
    }

    private static AiTaskReport Report(AiFinding? finding = null) => new() { Findings = [finding ?? Finding()], Actions = [] };

    private static AiFinding Finding(string itemId = "item-000001", string key = "self-test", string severity = "low", string titleZh = "自检响应", string description = "The probe was received.") => new()
    {
        ItemId = itemId,
        Key = key,
        Severity = severity,
        TitleEn = "Probe response",
        TitleZh = titleZh,
        DescriptionEn = description,
        DescriptionZh = "已收到探测记录。",
        RecommendationEn = "No action is needed.",
        RecommendationZh = "无需操作。",
    };

    private sealed class FakeEngine(AiTaskReport report) : IAiTaskEngine
    {
        public bool IsEnabled { get; init; } = true;
        public string ActiveKernel => "fixture";
        public event EventHandler<AiHubStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public int Calls { get; private set; }
        public bool SchemaAccepted { get; private set; }
        public IReadOnlyList<SelfTestInput> Inputs { get; private set; } = [];
        public AiTaskOptions? Options { get; private set; }
        public AiTaskSchema<SelfTestInput, AiTaskReport>? Schema { get; private set; }
        public AiReadiness GetReadiness() => new(IsEnabled ? AiReadinessLevel.Unverified : AiReadinessLevel.Disabled, ActiveKernel, string.Empty);
        public Task<AiReadiness> ProbeReadinessAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose()
        {
        }

        public Task<AiTaskResult<TOutput>> ExecuteTaskAsync<TInput, TOutput>(string pluginId, string taskId, IReadOnlyList<TInput> items,
            AiTaskSchema<TInput, TOutput> schema, AiTaskOptions? options = null, CancellationToken cancellationToken = default)
            where TOutput : class
        {
            Assert.AreEqual(SelfTestPolicy.PluginId, pluginId);
            Assert.AreEqual(SelfTestPolicy.TaskId, taskId);
            Calls++;
            Inputs = items.Cast<SelfTestInput>().ToArray();
            Options = options;
            Schema = (AiTaskSchema<SelfTestInput, AiTaskReport>)(object)schema;
            SchemaAccepted = Schema.ValidateOutput(report, new HashSet<string>(StringComparer.Ordinal) { "item-000001", "item-000002" });
            return Task.FromResult(AiTaskResult.Success((TOutput)(object)report, "fixture-model", "fixture-route", TimeSpan.Zero));
        }
    }
}
