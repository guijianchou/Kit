namespace Kit.AiHub.UnitTests;

using Kit.AIHubLib.Models;
using Kit.AIHubLib.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class OptimizationHistoryTests
{
    [TestMethod]
    public async Task HistoryKeepsTwentyRunsAndFiveHundredDetailsWithExactCounters()
    {
        using var fixture = new FixtureDirectory();
        var storage = new OptimizationHistoryStorage(fixture.PathFor("State"));
        Guid latest = Guid.Empty;
        for (int index = 0; index < 22; index++)
        {
            var record = new OptimizationExecutionRecord
            {
                State = "completed", TotalCount = 700, ProcessedCount = 700, SucceededCount = 700,
                Items = Enumerable.Range(0, 501).Select(item => new OptimizationExecutionItemRecord { FileName = item.ToString(), State = "succeeded" }).ToList(),
            };
            latest = record.RunId;
            await storage.SaveAsync(record);
        }

        var history = await storage.LoadAsync();
        Assert.AreEqual(20, history.Count);
        Assert.AreEqual(latest, history[0].RunId);
        Assert.IsTrue(history.All(record => record.Items.Count == 500 && record.DetailsTruncated && record.ProcessedCount == 700));
    }

    [TestMethod]
    public async Task UnfinishedRecordIsReportedAsInterruptedWithoutReplay()
    {
        using var fixture = new FixtureDirectory();
        var storage = new OptimizationHistoryStorage(fixture.PathFor("State"));
        var record = new OptimizationExecutionRecord { TotalCount = 10, ProcessedCount = 2, SucceededCount = 2 };
        await storage.SaveAsync(record);
        var recovered = (await storage.LoadAsync()).Single();
        Assert.AreEqual("interrupted", recovered.State);
        Assert.AreEqual(8, recovered.RemainingCount);
        StringAssert.Contains(recovered.FailureReason!, "no actions were retried");
        Assert.AreEqual("running", record.State, "Storage loading must not mutate an executor's live record.");
    }

    [TestMethod]
    public async Task CorruptHistoryIsPreservedInsteadOfSilentlyOverwritten()
    {
        using var fixture = new FixtureDirectory();
        string state = fixture.PathFor("State");
        Directory.CreateDirectory(state);
        string path = Path.Combine(state, "optimization_history.json");
        File.WriteAllText(path, "invalid history");
        var storage = new OptimizationHistoryStorage(state);
        await Assert.ThrowsExactlyAsync<System.Text.Json.JsonException>(() => storage.SaveAsync(new OptimizationExecutionRecord()));
        Assert.AreEqual("invalid history", File.ReadAllText(path));
    }

    [TestMethod]
    public async Task LoadingPhysicallyPrunesRunsOutsideRollingThirtyDays()
    {
        using var fixture = new FixtureDirectory();
        string state = fixture.PathFor("State");
        DateTime now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var storage = new OptimizationHistoryStorage(state, () => now);
        var expired = new OptimizationExecutionRecord { StartedUtc = now.AddDays(-29), State = "completed" };
        var boundary = new OptimizationExecutionRecord { StartedUtc = now.AddDays(-28), State = "completed" };
        await storage.SaveAsync(expired);
        await storage.SaveAsync(boundary);
        now = now.AddDays(2);
        Assert.AreEqual(boundary.RunId, (await storage.LoadAsync()).Single().RunId);
        string path = Path.Combine(state, "optimization_history.json");
        Assert.IsFalse(File.ReadAllText(path).Contains(expired.RunId.ToString(), StringComparison.OrdinalIgnoreCase));
        await storage.SaveAsync(expired);
        Assert.AreEqual(boundary.RunId, (await storage.LoadAsync()).Single().RunId);
        Assert.IsFalse(File.ReadAllText(path).Contains(expired.RunId.ToString(), StringComparison.OrdinalIgnoreCase));
    }
}
