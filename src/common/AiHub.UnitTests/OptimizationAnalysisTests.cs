namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Engine;
using Kit.AiHub.Models;
using Kit.AiHub.Security;
using Kit.AiHub.Storage;
using Kit.AiHub.UnitTests.Fixtures;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class OptimizationAnalysisTests
{
    [TestMethod]
    public async Task CacheGroupsReviewOneScopeAndExpandOnlyItsOriginalSnapshot()
    {
        using var engine = new FakeEngine();
        var items = Enumerable.Range(0, 1200).Select(index =>
        {
            var item = Candidate("delete");
            item.CacheRoot = index < 1000 ? @"C:\private\cache-a" : @"C:\private\cache-b";
            return item;
        }).ToArray();
        var result = await new OptimizationAnalysisService(engine).AnalyzeAsync(items, "en-US");
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, engine.Inputs.Count);
        Assert.AreEqual(1000, engine.Inputs[0].FileCount);
        Assert.AreEqual(1200, result.Items.Count);
        Assert.IsTrue(result.Items.All(items.Contains));
        Assert.IsFalse(JsonSerializer.Serialize(engine.Inputs, AIHubLibJsonContext.Default.ListOptimizationItemInput).Contains("private"));
    }

    [TestMethod]
    public async Task DevelopmentReviewReceivesAgeAndEvidenceWithoutPaths()
    {
        using var engine = new FakeEngine();
        var item = Candidate("delete");
        item.DevelopmentRepository = @"C:\private\project";
        item.BuildManifestPath = @"C:\private\project\obj\Example.csproj.FileListAbsolute.txt";
        item.Category = "MSBuild outputs";
        await new OptimizationAnalysisService(engine).AnalyzeAsync([item], "en-US");
        Assert.AreEqual(60, engine.Inputs.Single().MinimumAgeMinutes);
        Assert.AreEqual("msbuild-output-list", engine.Inputs.Single().Evidence);
        Assert.IsFalse(JsonSerializer.Serialize(engine.Inputs, AIHubLibJsonContext.Default.ListOptimizationItemInput).Contains("private"));
    }

    [TestMethod]
    public async Task SharedEngineLoadsOptimizationPolicyAndReturnsTypedAdvice()
    {
        using var fixture = await KernelFixture.CreateAsync();
        var store = new AiHubSettingsStore(fixture.Root);
        var config = AiHubConfig.CreateDefault();
        config.IsEnabled = true;
        config.SelectedKernel = "codex";
        config.Targets[0] = KernelFixture.Target();
        config.Targets[0].Name = "Main";
        store.Save(config);
        using var engine = new TaskAiEngine(store, fixture.Manager, new SecurityPolicyService(fixture.Root));
        await fixture.SetResponseAsync("""
            {"recommendations":[{"itemId":"item-000001","action":"move","targetRelative":"Documents","risk":"low","reasonEn":"Matches the document category.","reasonZh":"符合文档分类。"}]}
            """);
        var progress = new CapturedProgress();
        var result = await new OptimizationAnalysisService(engine).AnalyzeAsync([Candidate("move")], "en-US", progress);
        Assert.IsTrue(result.IsSuccess, result.Describe(false) + " " + string.Join("; ", progress.Messages));
        Assert.AreEqual(1, result.Items.Count);
        string capture = await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(fixture.RequestRoot, "capture.json"));
        StringAssert.Contains(capture, "Mandatory AI review contract");
        Assert.IsFalse(capture.Contains("private-name", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CombinedScansUseUniqueEnvelopeIdsAndOnlyMetadata()
    {
        using var engine = new FakeEngine();
        var items = new[] { Candidate("move"), Candidate("delete") };
        var progress = new Progress<AiTaskProgress>();
        var result = await new OptimizationAnalysisService(engine).AnalyzeAsync(items, "zh-CN", progress);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, result.Items.Count);
        CollectionAssert.AreEqual(new[] { "item-000001", "item-000002" }, engine.Inputs.Select(i => i.ItemId).ToArray());
        Assert.AreEqual("move", engine.Inputs[0].AllowedAction);
        Assert.AreEqual("delete", engine.Inputs[1].AllowedAction);
        Assert.AreSame(progress, engine.Options!.Progress);
        Assert.IsTrue(engine.Options.AllowPartialResults);
        string json = JsonSerializer.Serialize(engine.Inputs, AIHubLibJsonContext.Default.ListOptimizationItemInput);
        Assert.IsFalse(json.Contains("private-name", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("Users", StringComparison.Ordinal));
        Assert.IsTrue(items.All(i => i.IsSelected));
        Assert.AreEqual("Documents", items[0].TargetRelativePath);
        Assert.AreEqual(RiskLevel.Medium, items[1].Risk, "AI cannot downgrade local risk.");
    }

    [TestMethod]
    [DataRow("wrongAction")]
    [DataRow("traversal")]
    [DataRow("wrongCategory")]
    [DataRow("unknownId")]
    [DataRow("duplicate")]
    [DataRow("missing")]
    [DataRow("nullEntry")]
    [DataRow("invalidRisk")]
    [DataRow("missingReason")]
    [DataRow("multilineReason")]
    [DataRow("longReason")]
    [DataRow("deleteTarget")]
    public async Task InvalidRecommendationsNeverEnableActions(string scenario)
    {
        using var engine = new FakeEngine
        {
            Edit = output =>
            {
                var first = output.Recommendations[0];
                switch (scenario)
                {
                    case "wrongAction": first.Action = "delete"; first.TargetRelative = null; break;
                    case "traversal": first.TargetRelative = "../Documents"; break;
                    case "wrongCategory": first.TargetRelative = "Music"; break;
                    case "unknownId": first.ItemId = "item-999999"; break;
                    case "duplicate": output.Recommendations[1] = first; break;
                    case "missing": output.Recommendations.RemoveAt(1); break;
                    case "nullEntry": output.Recommendations[0] = null!; break;
                    case "invalidRisk": first.Risk = "safe"; break;
                    case "missingReason": first.ReasonZh = string.Empty; break;
                    case "multilineReason": first.ReasonEn = "line\nline"; break;
                    case "longReason": first.ReasonEn = new string('x', 513); break;
                    case "deleteTarget": output.Recommendations[1].TargetRelative = "cache"; break;
                }
            },
        };
        var items = new[] { Candidate("move"), Candidate("delete") };
        var result = await new OptimizationAnalysisService(engine).AnalyzeAsync(items, "en-US");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(AiErrorCode.InvalidPayload, result.ErrorCode);
        Assert.AreEqual(0, result.Items.Count);
        Assert.IsTrue(items.All(i => !i.IsSelected));
        Assert.IsTrue(items.All(i => i.ReviewState == "failed"), "Unapproved snapshots stay visible with a failure state.");
    }

    [TestMethod]
    public async Task SkipAndHighRiskAreExcludedWithoutChangingLocalActions()
    {
        using var engine = new FakeEngine
        {
            Edit = output =>
            {
                output.Recommendations[0].Action = "skip";
                output.Recommendations[0].TargetRelative = null;
                output.Recommendations[1].Risk = "high";
            },
        };
        var items = new[] { Candidate("move"), Candidate("delete") };
        var result = await new OptimizationAnalysisService(engine).AnalyzeAsync(items, "en-US");
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, result.SkippedCount);
        Assert.AreEqual(0, result.Items.Count);
        Assert.AreEqual("move", items[0].Action);
        Assert.IsTrue(items.All(i => i.ReviewState == "skipped"));
        Assert.IsTrue(items.All(i => !string.IsNullOrWhiteSpace(i.ReasonEn)));
        Assert.AreEqual("delete", items[1].Action);
    }

    [TestMethod]
    public async Task LocalHighRiskCannotBeApprovedByLowRiskAdvice()
    {
        using var engine = new FakeEngine();
        var item = Candidate("delete");
        item.Risk = RiskLevel.High;
        var result = await new OptimizationAnalysisService(engine).AnalyzeAsync([item], "en-US");
        Assert.AreEqual(0, result.Items.Count);
        Assert.IsFalse(item.IsSelected);
    }

    [TestMethod]
    public void MissingRiskCannotDeserializeAsImplicitLowRisk()
    {
        const string json = """
            {"itemId":"item-000001","action":"delete","targetRelative":null,"reasonEn":"Cache","reasonZh":"缓存"}
            """;
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize(json, AIHubLibJsonContext.Default.OptimizationRecommendation));
    }

    [TestMethod]
    public async Task FailedOrPartialAnalysisDoesNotFallBackToLocalApproval()
    {
        using var engine = new FakeEngine { Failure = AiErrorCode.Timeout };
        var item = Candidate("move");
        var result = await new OptimizationAnalysisService(engine).AnalyzeAsync(new[] { item }, "en-US");
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(AiErrorCode.Timeout, result.ErrorCode);
        Assert.IsFalse(item.IsSelected);
        Assert.AreEqual(0, result.Items.Count);
    }

    [TestMethod]
    public async Task EmptyScanDoesNotCallAiAndCancellationDoesNotApprove()
    {
        using var engine = new FakeEngine();
        var service = new OptimizationAnalysisService(engine);
        Assert.IsTrue((await service.AnalyzeAsync([], "en-US")).IsSuccess);
        Assert.AreEqual(0, engine.Calls);
        using var cancellation = new CancellationTokenSource();
        engine.Edit = _ => cancellation.Cancel();
        var item = Candidate("delete");
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.AnalyzeAsync(new[] { item }, "en-US", cancellationToken: cancellation.Token));
        Assert.IsFalse(item.IsSelected);
    }

    [TestMethod]
    public async Task BatchSchemaRejectsCrossBatchReferencesAndMergesInAnyOrder()
    {
        using var engine = new FakeEngine { Edit = output => output.Recommendations.Reverse() };
        var result = await new OptimizationAnalysisService(engine).AnalyzeAsync(new[] { Candidate("move"), Candidate("delete") }, "en-US");
        Assert.AreEqual("move", result.Items[0].Action);
        var output = new OptimizationRecommendationContainer { Recommendations = [Recommendation(engine.Inputs[1])] };
        Assert.IsFalse(engine.Schema!.ValidateOutput(output, new HashSet<string> { "item-000001" }));
        var merged = engine.Schema.MergeBatches!([output, new() { Recommendations = [Recommendation(engine.Inputs[0])] }]);
        Assert.IsTrue(engine.Schema.ValidateOutput(merged, new HashSet<string> { "item-000001", "item-000002" }));
    }

    [TestMethod]
    public async Task TimedOutBatchesPreserveOnlyValidatedRecommendations()
    {
        using var engine = new FakeEngine
        {
            Failure = AiErrorCode.Timeout,
            Partial = true,
            Edit = output => output.Recommendations.RemoveAt(1),
        };
        var items = new[] { Candidate("move"), Candidate("delete") };
        var result = await new OptimizationAnalysisService(engine).AnalyzeAsync(items, "en-US");
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(1, result.Items.Count);
        Assert.AreSame(items[0], result.Items[0]);
        Assert.AreEqual("approved", items[0].ReviewState);
        Assert.AreEqual("failed", items[1].ReviewState);
        Assert.IsFalse(items[1].IsSelected);
        Assert.AreEqual(1, result.UnreviewedCount);
        StringAssert.Contains(result.Describe(false), "1 actionable");
        engine.Edit = output =>
        {
            output.Recommendations.RemoveAt(1);
            output.Recommendations[0].ItemId = "unknown";
        };
        Assert.AreEqual(0, (await new OptimizationAnalysisService(engine).AnalyzeAsync(items, "en-US")).Items.Count);
    }

    [TestMethod]
    public async Task DevelopmentFoldersShareReviewWithoutBroadeningSnapshotOrEvidence()
    {
        using var engine = new FakeEngine();
        var items = Enumerable.Range(0, 268).Select(index =>
        {
            var item = Candidate("delete");
            item.CacheRoot = $@"C:\private\project\module{index}\Debug";
            item.DevelopmentRepository = @"C:\private\project";
            item.Category = "C++ precompiled headers";
            return item;
        }).ToArray();
        var result = await new OptimizationAnalysisService(engine).AnalyzeAsync(items, "en-US");
        Assert.AreEqual(1, engine.Inputs.Count);
        Assert.AreEqual(268, result.Items.Count);
        Assert.IsTrue(result.Items.All(items.Contains));
        items[0].BuildManifestPath = @"C:\private\project\obj\Example.csproj.FileListAbsolute.txt";
        await new OptimizationAnalysisService(engine).AnalyzeAsync(items, "en-US");
        Assert.AreEqual(2, engine.Inputs.Count, "Different evidence cannot share a review decision.");
    }

    private static TempFileInfo Candidate(string action) => new()
    {
        ItemId = "item-000001",
        FilePath = action == "move" ? @"C:\Users\Fixture\Downloads\private-name.pdf" : @"C:\Users\Fixture\Temp\private-name.tmp",
        FileName = "private-name",
        Action = action,
        Category = action == "move" ? "Documents" : "Temporary files",
        TargetRelativePath = action == "move" ? "Documents" : null,
        SizeInBytes = 123,
        LastModified = DateTime.UtcNow.AddDays(-10),
        Risk = action == "move" ? RiskLevel.Low : RiskLevel.Medium,
    };

    private static OptimizationRecommendation Recommendation(OptimizationItemInput input) => new()
    {
        ItemId = input.ItemId,
        Action = input.AllowedAction,
        TargetRelative = input.AllowedAction == "move" ? input.CategoryHint : null,
        Risk = "low",
        ReasonEn = "Matches the supplied category.",
        ReasonZh = "符合提供的分类。",
    };

    private sealed class FakeEngine : IAiTaskEngine
    {
        public bool IsEnabled => true;
        public string ActiveKernel => "fixture";
        public event EventHandler<AiHubStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public Action<OptimizationRecommendationContainer>? Edit { get; set; }
        public AiErrorCode Failure { get; set; }
        public bool Partial { get; set; }
        public int Calls { get; private set; }
        public List<OptimizationItemInput> Inputs { get; private set; } = [];
        public AiTaskOptions? Options { get; private set; }
        public AiTaskSchema<OptimizationItemInput, OptimizationRecommendationContainer>? Schema { get; private set; }
        public AiReadiness GetReadiness() => throw new NotSupportedException();
        public Task<AiReadiness> ProbeReadinessAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose()
        {
        }

        public Task<AiTaskResult<TOutput>> ExecuteTaskAsync<TInput, TOutput>(string pluginId, string taskId, IReadOnlyList<TInput> items,
            AiTaskSchema<TInput, TOutput> schema, AiTaskOptions? options = null, CancellationToken cancellationToken = default)
            where TOutput : class
        {
            Assert.AreEqual("aihub", pluginId);
            Assert.AreEqual(HubTaskCatalog.OptimizationId, taskId);
            Calls++;
            Inputs = items.Cast<OptimizationItemInput>().ToList();
            Options = options;
            Schema = (AiTaskSchema<OptimizationItemInput, OptimizationRecommendationContainer>)(object)schema;
            var output = new OptimizationRecommendationContainer { Recommendations = Inputs.Select(Recommendation).ToList() };
            Edit?.Invoke(output);
            return Task.FromResult(new AiTaskResult<TOutput>
            {
                IsSuccess = Failure == AiErrorCode.None,
                ErrorCode = Failure,
                CompletedBatches = Partial ? 1 : 0,
                TotalBatches = Partial ? 2 : 0,
                Payload = (TOutput)(object)output,
            });
        }
    }

    private sealed class CapturedProgress : IProgress<AiTaskProgress>
    {
        public List<string> Messages { get; } = [];

        public void Report(AiTaskProgress value) => Messages.Add(value.Stage + ": " + value.StatusMessage);
    }
}
