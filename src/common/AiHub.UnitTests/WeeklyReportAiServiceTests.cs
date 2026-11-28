namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AiHub.Models;
using Kit.AiHub.Security;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class WeeklyReportAiServiceTests
{
    [TestMethod]
    public async Task EmbeddedDedicatedPolicyDeploysAndNeverOverwritesCustomContent()
    {
        using var fixture = new FixtureDirectory();
        var policies = new SecurityPolicyService(fixture.RootPath, fixture.PathFor("modules"));
        string path = policies.GetTaskPolicyPath(HubTaskCatalog.WeeklyReportId);
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(DefaultWeeklyReportPolicy.Instructions, await policies.LoadTaskAgentsPolicyAsync("aihub", HubTaskCatalog.WeeklyReportId));
        StringAssert.Contains(DefaultWeeklyReportPolicy.Instructions, "Level 1-3");
        StringAssert.Contains(DefaultWeeklyReportPolicy.Instructions, "Unknown, never zero/healthy");
        const string custom = "# Custom weekly policy\n保留用户模板。\n";
        await policies.SaveTaskPolicyAsync(HubTaskCatalog.WeeklyReportId, custom);
        byte[] original = File.ReadAllBytes(path);
        new SecurityPolicyService(fixture.RootPath, fixture.PathFor("modules")).EnsureDefaultPolicyExists();
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.AreEqual(custom, await policies.LoadTaskAgentsPolicyAsync("aihub", HubTaskCatalog.WeeklyReportId));
    }

    [TestMethod]
    public void IdentityChangesWithEffortContextCredentialsRouteOrderAndOwnPolicy()
    {
        var config = AiHubConfig.CreateDefault();
        string Identity() => WeeklyReportAiService.CreateIdentity(config, "global", "weekly", "security");
        var identities = new HashSet<string>(StringComparer.Ordinal) { Identity() };
        config.Targets[0].Effort = "max";
        Assert.IsTrue(identities.Add(Identity()));
        config.Targets[0].ContextWindowTokens = 1000000;
        Assert.IsTrue(identities.Add(Identity()));
        config.Targets[0].ApiKey = "synthetic-key";
        Assert.IsTrue(identities.Add(Identity()));
        config.Targets.Move(0, 1);
        Assert.IsTrue(identities.Add(Identity()));
        Assert.IsTrue(identities.Add(WeeklyReportAiService.CreateIdentity(config, "global", "changed weekly", "security")));
    }

    [TestMethod]
    public void BoundedEvidenceCountsAllEventsAndRetainsSevereAndDiverseProviderGroups()
    {
        var events = Enumerable.Range(0, 1000).Select(index => Event("Repeated", 3, index % 20)).ToList();
        events.AddRange(Enumerable.Range(0, 250).Select(index => Event("Provider" + index, 3, index) with { Message = new string('证', 1000) }));
        events.Add(Event("LateCritical", 1, 9999));
        var request = WeeklyReportAiService.BuildRequest(Input(events));
        Assert.IsTrue(WeeklyReportAiService.EncodedSize(request) <= WeeklyReportAiService.MaximumInputBytes);
        Assert.AreEqual(events.Count, request.ObservedCounts.Sum(item => item.Count));
        Assert.AreEqual(events.Count, request.Selection.ProvidedEvents);
        Assert.IsTrue(request.Selection.OmittedGroups > 0);
        Assert.AreEqual(request.Selection.TotalGroups, request.Selection.IncludedGroups + request.Selection.OmittedGroups);
        Assert.AreEqual(request.Evidence.Sum(item => item.Count), request.Selection.IncludedOccurrences);
        Assert.IsTrue(request.Evidence.Any(item => item.Provider == "LateCritical"));
        Assert.IsTrue(request.Evidence.Select(item => item.Provider).Distinct().Count() > 10);
        Assert.AreEqual(77, request.Metrics.Single().Current, "Host statistics must not be rewritten from the selected evidence.");
        Assert.IsFalse(request.CurrentComplete, "The request must preserve incomplete host coverage.");
    }

    [TestMethod]
    public void SchemaRequiresKnownEnvelopeEvidenceSeverityAndEveryBilingualField()
    {
        var valid = Output();
        var ids = new HashSet<string>(StringComparer.Ordinal) { "item-000001" };
        var refs = new HashSet<string>(StringComparer.Ordinal) { "statistics" };
        Assert.IsTrue(WeeklyReportAiService.ValidateOutput(valid, ids, refs));
        Assert.IsFalse(WeeklyReportAiService.ValidateOutput(valid with { InputRef = "other-input" }, ids, refs));
        Assert.IsFalse(WeeklyReportAiService.ValidateOutput(valid with { Findings = [valid.Findings[0] with { EvidenceRefs = ["invented-event"] }] }, ids, refs));
        Assert.IsFalse(WeeklyReportAiService.ValidateOutput(valid with { Findings = [valid.Findings[0] with { Priority = "Critical" }] }, ids, refs));
        Assert.IsFalse(WeeklyReportAiService.ValidateOutput(valid with { Findings = [valid.Findings[0] with { VerificationZh = string.Empty }] }, ids, refs));
        Assert.IsFalse(WeeklyReportAiService.ValidateOutput(valid with { AssessmentZh = "English only" }, ids, refs));
    }

    [TestMethod]
    public void ShortCredentialExpansionIsBudgetedAfterSanitizationWithoutChangingObservedTotals()
    {
        var events = Enumerable.Range(0, 160).Select(index => Event("Provider" + index, 2, index) with
        {
            Message = string.Concat(Enumerable.Repeat("pwd=x;", 45)),
        }).ToArray();
        var request = WeeklyReportAiService.BuildRequest(Input(events));
        var inputJson = JsonSerializer.SerializeToElement(request, WeeklyReportReviewJsonContext.Default.WeeklyReportAiInput);
        var engineJson = new DataSanitizer().SanitizeJson(inputJson);
        Assert.IsTrue(Encoding.UTF8.GetByteCount(engineJson.GetRawText()) <= WeeklyReportAiService.MaximumInputBytes,
            "The engine applies sanitization again before its 64 KB limit; the final expanded input must fit.");
        Assert.IsTrue(WeeklyReportAiService.EncodedSize(request) <= WeeklyReportAiService.MaximumInputBytes);
        Assert.AreEqual(events.Length, request.ObservedCounts.Sum(item => item.Count));
        Assert.AreEqual(events.Length, request.Selection.ProvidedEvents);
        Assert.IsTrue(request.Selection.IncludedGroups > 0);
        Assert.IsTrue(request.Selection.OmittedGroups > 0);
        Assert.AreEqual(request.Selection.TotalGroups, request.Selection.IncludedGroups + request.Selection.OmittedGroups);
        Assert.AreEqual(request.Evidence.Sum(item => item.Count), request.Selection.IncludedOccurrences);
        Assert.IsFalse(inputJson.GetRawText().Contains("pwd=x", StringComparison.Ordinal));
        StringAssert.Contains(request.Evidence[0].FirstSample, "[REDACTED_SECRET]");
    }

    [TestMethod]
    public void JsonContractRejectsOmittedFindingsAndUnknownHostFieldsButAllowsExplicitEmptyFindings()
    {
        const string missing = """{"inputRef":"item-000001","assessmentZh":"没有足够证据。","assessmentEn":"Evidence is insufficient."}""";
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize(missing, WeeklyReportReviewJsonContext.Default.WeeklyReportAiOutput));
        string explicitEmpty = missing[..^1] + ",\"findings\":[]}";
        var output = JsonSerializer.Deserialize(explicitEmpty, WeeklyReportReviewJsonContext.Default.WeeklyReportAiOutput)!;
        Assert.IsTrue(WeeklyReportAiService.ValidateOutput(output, new HashSet<string>(StringComparer.Ordinal) { "item-000001" }, new HashSet<string>(StringComparer.Ordinal) { "statistics" }));
        string extraHostField = explicitEmpty[..^1] + ",\"evidenceIndex\":[]}";
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize(extraHostField, WeeklyReportReviewJsonContext.Default.WeeklyReportAiOutput));

        var incompleteFinding = JsonSerializer.SerializeToNode(Output(), WeeklyReportReviewJsonContext.Default.WeeklyReportAiOutput)!;
        incompleteFinding["findings"]![0]!.AsObject().Remove("verificationEn");
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize(incompleteFinding.ToJsonString(), WeeklyReportReviewJsonContext.Default.WeeklyReportAiOutput));
    }

    [TestMethod]
    public void ExportedModelSchemaDeclaresAllOutputFieldsRequiredAndForbidsUnknownProperties()
    {
        var schema = WeeklyReportReviewJsonContext.Default.WeeklyReportAiOutput.GetJsonSchemaAsNode();
        var required = schema["required"]!.AsArray().Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(required.SetEquals(["inputRef", "assessmentZh", "assessmentEn", "findings"]));
        Assert.IsFalse(schema["additionalProperties"]!.GetValue<bool>());
        var findingSchema = schema["properties"]!["findings"]!["items"]!;
        var findingRequired = findingSchema["required"]!.AsArray().Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(findingRequired.SetEquals(["priority", "titleZh", "titleEn", "factsZh", "factsEn", "hypothesisZh", "hypothesisEn", "stepsZh", "stepsEn", "verificationZh", "verificationEn", "evidenceRefs"]));
        Assert.IsFalse(findingSchema["additionalProperties"]!.GetValue<bool>());
    }

    [TestMethod]
    public async Task OneIndependentEngineCallUsesDedicatedSchemaAndPreservesActualRoute()
    {
        int calls = 0;
        var review = await WeeklyReportAiService.ReviewCoreAsync(Input([]), (items, schema, options, token) =>
        {
            calls++;
            Assert.AreEqual(1, items.Count);
            Assert.AreEqual(1, options.BatchSize);
            Assert.IsNull(options.InlineInstructions, "The dedicated AGENTS.md must remain the task policy source.");
            Assert.IsFalse(options.AllowTimeoutFailover);
            Assert.IsFalse(options.AllowPartialResults);
            Assert.IsTrue(schema.ValidateOutput(Output(), new HashSet<string>(StringComparer.Ordinal) { "item-000001" }));
            return Task.FromResult(AiTaskResult.Success(Output(), "actual-provider-model", "Fallback", TimeSpan.FromSeconds(1)));
        }, null, CancellationToken.None);
        Assert.AreEqual(1, calls);
        Assert.AreEqual("actual-provider-model", review.UsedModel);
        Assert.AreEqual("Fallback", review.UsedRoute);
        Assert.AreEqual(Output().AssessmentEn, review.AssessmentEn);
        Assert.IsFalse(WeeklyReportAiService.MatchesRequestedRoute(review, "actual-provider-model"), "A fallback cannot replace the configured primary report.");
        Assert.IsFalse(WeeklyReportAiService.MatchesRequestedRoute(review with { UsedRoute = "Main" }, "gpt-6-astra"));
        Assert.IsTrue(WeeklyReportAiService.MatchesRequestedRoute(review with { UsedRoute = "Main" }, "actual-provider-model"));
    }

    [TestMethod]
    public async Task PartialFailureAndPreCancellationNeverBecomeCompletedReports()
    {
        int calls = 0;
        Task<AiTaskResult<WeeklyReportAiOutput>> Execute(IReadOnlyList<WeeklyReportAiInput> items,
            AiTaskSchema<WeeklyReportAiInput, WeeklyReportAiOutput> schema, AiTaskOptions options, CancellationToken token)
        {
            calls++;
            return Task.FromResult(new AiTaskResult<WeeklyReportAiOutput>
            {
                IsSuccess = false,
                ErrorCode = AiErrorCode.Timeout,
                Payload = Output(),
                UsedModel = "gpt-6-astra",
                UsedRoute = "Main",
            });
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WeeklyReportAiService.ReviewCoreAsync(Input([]), Execute, null, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => WeeklyReportAiService.ReviewCoreAsync(Input([]), Execute, null, cancelled.Token));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task EvidenceIndexComesFromSelectedHostRecordsAndRedactsSampleSecrets()
    {
        const string secret = "synthetic-weekly-password";
        var source = Event("KnownProvider", 2, 123) with { Message = "password=" + secret + @" path=C:\Users\PrivatePerson\data.txt" };
        var review = await WeeklyReportAiService.ReviewCoreAsync(Input([source]), (items, schema, options, token) =>
        {
            var output = Output() with { Findings = [Output().Findings[0] with { EvidenceRefs = [items[0].Evidence.Single().EvidenceRef] }] };
            return Task.FromResult(AiTaskResult.Success(output, "gpt-6-astra", "Main", TimeSpan.Zero));
        }, null, CancellationToken.None);
        var evidence = review.EvidenceIndex.Single(item => item.Kind == "current-event-group");
        Assert.AreEqual(review.Findings.Single().EvidenceRefs.Single(), evidence.EvidenceRef);
        Assert.AreEqual("KnownProvider", evidence.Provider);
        Assert.AreEqual(123, evidence.EventId);
        Assert.AreEqual(1, evidence.Count);
        Assert.IsFalse(evidence.FirstSample.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(evidence.FirstSample.Contains("PrivatePerson", StringComparison.Ordinal));
        StringAssert.Contains(evidence.FirstSample, "REDACTED_SECRET");
        Assert.IsTrue(review.EvidenceIndex.Any(item => item.Kind == "statistics"));
    }

    private static WindowsStabilityEvent Event(string provider, int level, int id) => new("System", provider, id, id,
        new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero), level, "Synthetic observed event", new Dictionary<string, string>(), []);

    private static WeeklyReportReviewInput Input(IReadOnlyList<WindowsStabilityEvent> events)
    {
        var end = new DateTimeOffset(2026, 11, 28, 0, 0, 0, TimeSpan.Zero);
        var data = new WindowsStabilityReportData("宿主统计", "Unknown", "覆盖不完整", "Machine",
            end.AddDays(-7), end, end.AddDays(-14), end.AddDays(-7),
            [new("events", "Observed events", 77, null, "events", "Observed lower bound")],
            [], [], [], [], [], [], [], ["Level 4 not available"], [])
        {
            CurrentComplete = false,
            PreviousComplete = false,
        };
        return new(WeeklyReportMode.Full, data, events, null);
    }

    private static WeeklyReportAiOutput Output() => new()
    {
        InputRef = "item-000001",
        AssessmentZh = "发现重复错误，覆盖不完整。",
        AssessmentEn = "Recurring errors were observed; coverage is incomplete.",
        Findings =
        [
            new()
            {
                Priority = "Medium",
                TitleZh = "重复错误",
                TitleEn = "Recurring errors",
                FactsZh = "宿主统计显示错误。",
                FactsEn = "Host statistics show errors.",
                HypothesisZh = "原因未知。",
                HypothesisEn = "The cause is unknown.",
                StepsZh = "先在事件查看器核对时间和组件，再根据确认的原因处理。",
                StepsEn = "First verify timestamps and components in Event Viewer, then address the confirmed cause.",
                VerificationZh = "下一次相同工作负载期间确认没有复发。",
                VerificationEn = "Check for recurrence during the next equivalent workload.",
                EvidenceRefs = ["statistics"],
            },
        ],
    };
}
