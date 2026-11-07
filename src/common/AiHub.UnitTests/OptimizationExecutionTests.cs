namespace Kit.AiHub.UnitTests;

using System.Text.Json;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.AIHubLib.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class OptimizationExecutionTests
{
    [TestMethod]
    public async Task ExecutionRecordsActualMoveDestinationAndOnlyApprovedUnchangedLeaves()
    {
        using var fixture = new FixtureDirectory();
        string temp = fixture.PathFor("Temp");
        string downloads = fixture.PathFor("Downloads");
        Old(Path.Combine(temp, "delete.tmp"));
        Old(Path.Combine(temp, "changed.tmp"));
        Old(Path.Combine(temp, "pending.tmp"));
        Old(Path.Combine(downloads, "report.pdf"));
        Old(Path.Combine(downloads, "Documents", "report.pdf"), "existing");
        var cache = new CacheCleanupService(fixture.PathFor("Local"), temp, CacheCleanupService.DeleteFile);
        var organizer = new DownloadOrganizerService(downloads);
        var items = (await cache.ScanAsync()).Concat(await organizer.ScanAsync()).ToList();
        foreach (var item in items)
        {
            item.ReviewState = item.FileName == "pending.tmp" ? "pending" : "approved";
        }

        File.AppendAllText(Path.Combine(temp, "changed.tmp"), "new content");
        var service = new OptimizationExecutionService(cache, organizer, new OptimizationHistoryStorage(fixture.PathFor("State")));

        var result = await service.ExecuteAsync(items);

        Assert.AreEqual("failed", result.Record.State);
        Assert.AreEqual(4, result.Record.ProcessedCount);
        Assert.AreEqual(2, result.Record.SucceededCount);
        Assert.AreEqual(2, result.Record.FailedCount);
        Assert.AreEqual(1, result.Record.DeletedCount);
        Assert.AreEqual(1, result.Record.MovedCount);
        Assert.AreEqual(0, result.Record.RemainingCount);
        Assert.AreEqual(2, result.SucceededItems.Count);
        Assert.AreEqual(2, result.FailedItems.Count);
        Assert.IsTrue(items.All(item => item.ExecutionError == string.Empty), "UI-owned objects are updated by the UI, not a background executor.");
        Assert.IsTrue(File.Exists(Path.Combine(temp, "pending.tmp")));
        Assert.IsTrue(File.Exists(Path.Combine(temp, "changed.tmp")));
        var moved = result.Record.Items.Single(item => item.Action == "move");
        Assert.AreEqual(Path.Combine(downloads, "Documents", "report_1.pdf"), moved.DestinationPath);
        Assert.IsTrue(File.Exists(moved.DestinationPath));
        Assert.AreEqual("existing", File.ReadAllText(Path.Combine(downloads, "Documents", "report.pdf")));
        var stored = (await service.LoadHistoryAsync()).Single();
        Assert.AreEqual(result.Record.RunId, stored.RunId);
        using var export = JsonDocument.Parse(service.ExportRecordJson(stored));
        Assert.AreEqual(4, export.RootElement.GetProperty("processedCount").GetInt32());
    }

    [TestMethod]
    public async Task CancellationKeepsCompletedReceiptAndDoesNotStartNextFile()
    {
        using var fixture = new FixtureDirectory();
        using var cancellation = new CancellationTokenSource();
        string temp = fixture.PathFor("Temp");
        foreach (string name in new[] { "first.tmp", "second.tmp", "third.tmp" })
        {
            Old(Path.Combine(temp, name));
        }

        int calls = 0;
        var cache = new CacheCleanupService(fixture.PathFor("Local"), temp, path =>
        {
            calls++;
            var deleted = CacheCleanupService.DeleteFile(path);
            cancellation.Cancel();
            return deleted;
        });
        var items = await cache.ScanAsync();
        foreach (var item in items)
        {
            item.ReviewState = "approved";
        }

        var service = new OptimizationExecutionService(cache, new DownloadOrganizerService(fixture.PathFor("Downloads")), new OptimizationHistoryStorage(fixture.PathFor("State")));

        var result = await service.ExecuteAsync(items, cancellationToken: cancellation.Token);

        Assert.AreEqual(1, calls);
        Assert.AreEqual("cancelled", result.Record.State);
        Assert.AreEqual(1, result.Record.ProcessedCount);
        Assert.AreEqual(2, result.Record.RemainingCount);
        Assert.AreEqual(1, result.SucceededItems.Count);
        Assert.AreEqual(2, items.Count(item => File.Exists(item.FilePath)));
        Assert.AreEqual("cancelled", (await service.LoadHistoryAsync()).Single().State);
    }

    [TestMethod]
    public async Task ExecutionCapturesConfirmedPathsBeforeReturningToCaller()
    {
        using var fixture = new FixtureDirectory();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        string temp = fixture.PathFor("Temp");
        Old(Path.Combine(temp, "one.tmp"));
        Old(Path.Combine(temp, "two.tmp"));
        string outside = fixture.PathFor("important.txt");
        Old(outside, "preserve me");
        int calls = 0;
        var cache = new CacheCleanupService(fixture.PathFor("Local"), temp, path =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Test did not release execution.");
                }
            }

            return CacheCleanupService.DeleteFile(path);
        });
        var items = await cache.ScanAsync();
        foreach (var item in items)
        {
            item.ReviewState = "approved";
        }

        string[] confirmed = items.Select(item => item.FilePath).ToArray();
        var service = new OptimizationExecutionService(cache, new DownloadOrganizerService(fixture.PathFor("Downloads")), new OptimizationHistoryStorage(fixture.PathFor("State")));
        Task<OptimizationExecutionResult> execution = service.ExecuteAsync(items);
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
            foreach (var item in items)
            {
                item.FilePath = outside;
                item.Action = "move";
                item.ReviewState = "pending";
            }
        }
        finally
        {
            release.Set();
        }

        var result = await execution;
        Assert.AreEqual(2, result.Record.SucceededCount);
        Assert.IsTrue(confirmed.All(path => !File.Exists(path)));
        Assert.AreEqual("preserve me", File.ReadAllText(outside));
        CollectionAssert.AreEquivalent(confirmed, result.Record.Items.Select(item => item.SourcePath).ToArray());
    }

    [TestMethod]
    public async Task UnwritableHistoryPreventsFilesystemActions()
    {
        using var fixture = new FixtureDirectory();
        string temp = fixture.PathFor("Temp");
        string source = Path.Combine(temp, "keep.tmp");
        Old(source);
        string blocked = fixture.PathFor("State");
        File.WriteAllText(blocked, "not a directory");
        int calls = 0;
        var cache = new CacheCleanupService(fixture.PathFor("Local"), temp, path =>
        {
            calls++;
            return CacheCleanupService.DeleteFile(path);
        });
        var items = await cache.ScanAsync();
        items.Single().ReviewState = "approved";
        var service = new OptimizationExecutionService(cache, new DownloadOrganizerService(fixture.PathFor("Downloads")), new OptimizationHistoryStorage(blocked));
        var result = await service.ExecuteAsync(items);
        Assert.AreEqual("failed", result.Record.State);
        Assert.AreEqual(0, calls);
        Assert.AreEqual(1, result.Record.RemainingCount);
        Assert.IsFalse(string.IsNullOrEmpty(result.Record.HistoryError));
        Assert.IsTrue(File.Exists(source));
    }

    [TestMethod]
    public async Task NugetExecutionDoesNotDeleteUnselectedRestoreMarker()
    {
        using var fixture = new FixtureDirectory();
        string user = fixture.PathFor("User");
        string package = Path.Combine(user, ".nuget", "packages", "sample", "1.0");
        string marker = Path.Combine(package, ".nupkg.metadata");
        string payload = Path.Combine(package, "lib", "sample.dll");
        Old(marker);
        Old(payload);
        var cache = new CacheCleanupService(fixture.PathFor("Local"), fixture.PathFor("Temp"), CacheCleanupService.DeleteFile, user);
        var items = await cache.ScanAsync();
        foreach (var item in items)
        {
            item.ReviewState = "approved";
        }

        var service = new OptimizationExecutionService(cache, new DownloadOrganizerService(fixture.PathFor("Downloads")), new OptimizationHistoryStorage(fixture.PathFor("State")));
        var partial = await service.ExecuteAsync(items.Where(item => item.FilePath == payload).ToList());
        Assert.AreEqual(0, partial.Record.SucceededCount);
        Assert.IsTrue(File.Exists(marker));
        Assert.IsTrue(File.Exists(payload));
        var complete = await service.ExecuteAsync(items);
        Assert.AreEqual(2, complete.Record.SucceededCount);
        Assert.AreEqual(marker, complete.Record.Items[0].SourcePath);
        Assert.IsFalse(File.Exists(marker));
        Assert.IsFalse(File.Exists(payload));
    }

    [TestMethod]
    public async Task DetailedReceiptLimitDoesNotTruncateCountsOrSuccessfulIdentities()
    {
        using var fixture = new FixtureDirectory();
        string temp = fixture.PathFor("Temp");
        for (int index = 0; index < 501; index++)
        {
            Old(Path.Combine(temp, $"{index}.tmp"));
        }

        var cache = new CacheCleanupService(fixture.PathFor("Local"), temp, CacheCleanupService.DeleteFile);
        var items = await cache.ScanAsync();
        foreach (var item in items)
        {
            item.ReviewState = "approved";
        }

        var service = new OptimizationExecutionService(cache, new DownloadOrganizerService(fixture.PathFor("Downloads")), new OptimizationHistoryStorage(fixture.PathFor("State")));
        var result = await service.ExecuteAsync(items, new ThrowingProgress());
        Assert.AreEqual("completed", result.Record.State);
        Assert.AreEqual(501, result.Record.ProcessedCount);
        Assert.AreEqual(501, result.SucceededItems.Count);
        Assert.AreEqual(500, result.Record.Items.Count);
        Assert.IsTrue(result.Record.DetailsTruncated);
        Assert.AreEqual(items.Sum(item => item.SizeInBytes), result.Record.FreedBytes);
        Assert.AreEqual(501, (await service.LoadHistoryAsync()).Single().ProcessedCount);
    }

    [TestMethod]
    public async Task DuplicateSourceIsExecutedOnceAndFinalProgressIsAlwaysReported()
    {
        using var fixture = new FixtureDirectory();
        string temp = fixture.PathFor("Temp");
        Old(Path.Combine(temp, "only.tmp"));
        int calls = 0;
        var cache = new CacheCleanupService(fixture.PathFor("Local"), temp, path =>
        {
            calls++;
            return CacheCleanupService.DeleteFile(path);
        });
        var item = (await cache.ScanAsync()).Single();
        item.ReviewState = "approved";
        var sameSource = new TempFileInfo { FilePath = item.FilePath, ReviewState = "approved" };
        var service = new OptimizationExecutionService(cache, new DownloadOrganizerService(fixture.PathFor("Downloads")), new OptimizationHistoryStorage(fixture.PathFor("State")));
        var progress = new CollectingProgress();
        var result = await service.ExecuteAsync(new[] { item, sameSource, item }, progress);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, result.Record.TotalCount);
        Assert.AreEqual(1, result.SucceededItems.Count);
        Assert.AreSame(item, result.SucceededItems.Single());
        Assert.IsTrue(progress.Values.Count > 0);
        Assert.AreEqual(result.Record.ProcessedCount, progress.Values[^1].ProcessedCount);
        Assert.AreEqual(result.Record.TotalCount, progress.Values[^1].TotalCount);
    }

    private static void Old(string path, string content = "cache")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
    }

    private sealed class ThrowingProgress : IProgress<OptimizationExecutionProgress>
    {
        public void Report(OptimizationExecutionProgress value) => throw new InvalidOperationException("An observer must not fail execution.");
    }

    private sealed class CollectingProgress : IProgress<OptimizationExecutionProgress>
    {
        public List<OptimizationExecutionProgress> Values { get; } = new();
        public void Report(OptimizationExecutionProgress value) => Values.Add(value);
    }
}
