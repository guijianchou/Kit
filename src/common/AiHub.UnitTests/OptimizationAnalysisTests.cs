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
    [DataRow("Pictures", true)]
    [DataRow("Documents", false)]
    [DataRow("../Pictures", false)]
    [DataRow("pictures", false)]
    public async Task PictureRecommendationsMustKeepTheHostCategory(string target, bool expectedSuccess)
    {
        using var engine = new FakeEngine { Edit = output => output.Recommendations[0].TargetRelative = target };
        var item = Candidate("move");
        item.FilePath = @"C:\private-name\photo.JPG";
        item.FileName = "photo.JPG";
        item.Category = "Pictures";
        item.TargetRelativePath = "Pictures";
        var result = await new OptimizationAnalysisService(engine).AnalyzeAsync([item], "en-US");

        Assert.AreEqual(expectedSuccess, result.IsSuccess);
        Assert.AreEqual(expectedSuccess, item.IsSelected);
        Assert.AreEqual("Pictures", engine.Inputs.Single().CategoryHint);
        Assert.AreEqual("download-extension", engine.Inputs.Single().Evidence);
        Assert.AreEqual(10, engine.Inputs.Single().MinimumAgeMinutes);
        Assert.AreEqual("Pictures", item.TargetRelativePath, "AI must never redirect the host's picture destination.");
    }

    [TestMethod]
    [DataRow("Others", true)]
    [DataRow("Pictures", true)]
    [DataRow("Documents", true)]
    [DataRow("../Others", false)]
    [DataRow("ArbitraryFolder", false)]
    public async Task DownloadFallbackAllowsOthersOrKnownCategoryAndRejectsUnsafeTargets(string target, bool expectedSuccess)
    {
        using var engine = new FakeEngine { Edit = output => output.Recommendations[0].TargetRelative = target };
        var item = Candidate("move");
        item.FilePath = @"C:\private-name\asset.raw";
        item.FileName = "asset.raw";
        item.Category = DownloadOrganizerService.OthersCategory;
        item.TargetRelativePath = DownloadOrganizerService.OthersCategory;
        item.EvidenceKind = "download-fallback";
        var result = await new OptimizationAnalysisService(engine).AnalyzeAsync([item], "en-US");

        Assert.AreEqual(expectedSuccess, result.IsSuccess);
        Assert.AreEqual("download-fallback", engine.Inputs.Single().Evidence);
        Assert.AreEqual("Others", engine.Inputs.Single().CategoryHint);
        if (expectedSuccess)
        {
            Assert.IsTrue(item.IsSelected);
            Assert.AreEqual(target, item.Category);
            Assert.AreEqual(target, item.TargetRelativePath);
        }
        else
        {
            Assert.IsFalse(item.IsSelected);
            Assert.AreEqual("Others", item.TargetRelativePath);
        }
    }

    [TestMethod]
    public async Task TempResidualsAreGroupedByExtensionSoAiCanDeleteLogsAndSkipDocuments()
    {
        using var engine = new FakeEngine();
        engine.Edit = output =>
        {
            foreach (var rec in output.Recommendations)
            {
                var input = engine.Inputs.Single(i => i.ItemId == rec.ItemId);
                if (input.Extension == ".docx")
                {
                    rec.Action = "skip";
                    rec.TargetRelative = null;
                    rec.ReasonEn = "Possible user document in Temp; skip.";
                    rec.ReasonZh = "Temp 中疑似用户文档，建议跳过。";
                }
            }
        };
        var log1 = Candidate("delete");
        log1.FilePath = @"C:\private\Temp\a.log";
        log1.FileName = "a.log";
        log1.Category = "Temporary residuals";
        log1.CacheRoot = @"C:\private\Temp";
        log1.EvidenceKind = "temp-residual";
        var log2 = Candidate("delete");
        log2.FilePath = @"C:\private\Temp\b.log";
        log2.FileName = "b.log";
        log2.Category = "Temporary residuals";
        log2.CacheRoot = @"C:\private\Temp";
        log2.EvidenceKind = "temp-residual";
        var doc = Candidate("delete");
        doc.FilePath = @"C:\private\Temp\notes.docx";
        doc.FileName = "notes.docx";
        doc.Category = "Temporary residuals";
        doc.CacheRoot = @"C:\private\Temp";
        doc.EvidenceKind = "temp-residual";

        var result = await new OptimizationAnalysisService(engine).AnalyzeAsync([log1, log2, doc], "zh-CN");
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, engine.Inputs.Count, "Different residual extensions must be reviewed separately.");
        Assert.AreEqual(2, result.Items.Count);
        Assert.AreEqual(1, result.SkippedCount);
        Assert.AreEqual("approved", log1.ReviewState);
        Assert.AreEqual("approved", log2.ReviewState);
        Assert.AreEqual("skipped", doc.ReviewState);
        Assert.IsFalse(doc.IsSelected);
    }

    [TestMethod]
    public async Task ManyTempResidualExtensionsCollapseIntoBoundedSemanticBucketsWhileIsolatingDocuments()
    {
        using var engine = new FakeEngine();
        engine.Edit = output =>
        {
            foreach (var rec in output.Recommendations)
            {
                var input = engine.Inputs.Single(i => i.ItemId == rec.ItemId);
                if (input.Extension.Contains("documents-credentials", StringComparison.Ordinal) || input.Extension is ".docx" or ".pem")
                {
                    rec.Action = "skip";
                    rec.TargetRelative = null;
                    rec.ReasonEn = "Potential document or credential in Temp; skip.";
                    rec.ReasonZh = "Temp 中可能存在用户文档或凭据，建议跳过。";
                }
            }
        };

        string[] extensions =
        [
            ".log", ".dmp", ".txt", ".tlog",
            ".cab", ".msi", ".exe", ".zip",
            ".pdb", ".dll", ".obj", ".lib", ".winmd", ".mui", ".cache",
            ".png", ".jpg", ".ico",
            ".cs", ".cpp", ".h", ".json", ".ps1", ".md", ".xml", ".props", ".targets", ".mszyml", string.Empty,
            ".docx", ".pem",
        ];
        var items = extensions.Select((ext, index) =>
        {
            var item = Candidate("delete");
            item.FileName = $"file-{index}{ext}";
            item.FilePath = $@"C:\private\Temp\file-{index}{ext}";
            item.Category = "Temporary residuals";
            item.CacheRoot = @"C:\private\Temp";
            item.EvidenceKind = "temp-residual";
            return item;
        }).ToArray();

        var result = await new OptimizationAnalysisService(engine).AnalyzeAsync(items, "zh-CN");
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(6, engine.Inputs.Count, "31 distinct Temp extensions must collapse into 6 semantic buckets instead of fragmenting AI batches.");
        Assert.AreEqual(32, engine.Options!.BatchSize);
        Assert.AreEqual(extensions.Length - 2, result.Items.Count);
        Assert.AreEqual(2, result.SkippedCount);
        Assert.IsTrue(items.Where(i => i.FileName.EndsWith(".docx", StringComparison.Ordinal) || i.FileName.EndsWith(".pem", StringComparison.Ordinal)).All(i => i.ReviewState == "skipped"));
        Assert.IsTrue(items.Where(i => !i.FileName.EndsWith(".docx", StringComparison.Ordinal) && !i.FileName.EndsWith(".pem", StringComparison.Ordinal)).All(i => i.ReviewState == "approved"));
    }

    [TestMethod]
    public async Task CacheResidualsGroupByCacheRootWhileIsolatingDocuments()
    {
        using var engine = new FakeEngine();
        var f1 = Candidate("delete");
        f1.FilePath = @"C:\private\LocalLow\NVIDIA\PerDriverVersion\DXCache\a.bin";
        f1.FileName = "a.bin";
        f1.Category = "NVIDIA DXCache";
        f1.CacheRoot = @"C:\private\LocalLow\NVIDIA\PerDriverVersion\DXCache";
        f1.EvidenceKind = "cache-residual";
        var f2 = Candidate("delete");
        f2.FilePath = @"C:\private\LocalLow\NVIDIA\PerDriverVersion\DXCache\b.toc";
        f2.FileName = "b.toc";
        f2.Category = "NVIDIA DXCache";
        f2.CacheRoot = @"C:\private\LocalLow\NVIDIA\PerDriverVersion\DXCache";
        f2.EvidenceKind = "cache-residual";
        var f3 = Candidate("delete");
        f3.FilePath = @"C:\private\LocalLow\NVIDIA\PerDriverVersion\DXCache\unexpected.pdf";
        f3.FileName = "unexpected.pdf";
        f3.Category = "NVIDIA DXCache";
        f3.CacheRoot = @"C:\private\LocalLow\NVIDIA\PerDriverVersion\DXCache";
        f3.EvidenceKind = "cache-residual";

        var result = await new OptimizationAnalysisService(engine).AnalyzeAsync([f1, f2, f3], "en-US");
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, engine.Inputs.Count, "Dedicated cache roots should coalesce cache files into one bucket while isolating document extensions.");
        Assert.IsTrue(engine.Inputs.Any(i => i.FileCount == 2 && i.Extension.StartsWith("bucket:cache-data", StringComparison.Ordinal)));
        Assert.IsTrue(engine.Inputs.Any(i => i.FileCount == 1 && i.Extension == ".pdf"));
    }

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
        StringAssert.Contains(capture, "Review a fixed candidate snapshot");
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
