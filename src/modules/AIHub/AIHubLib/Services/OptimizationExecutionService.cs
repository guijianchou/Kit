using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Storage;

namespace Kit.AIHubLib.Services;

/// <summary>Runs only the supplied reviewed snapshot. History never authorizes a retry or rollback.</summary>
public sealed class OptimizationExecutionService
{
    private const int BatchSize = 128;
    private readonly CacheCleanupService _cache;
    private readonly DownloadOrganizerService _downloads;
    private readonly OptimizationHistoryStorage _history;
    // One optimizer executes per process, including while an old page is draining
    // cancellation. This process-lifetime semaphore never exposes AvailableWaitHandle.
    private static readonly SemaphoreSlim ExecutionGate = new(1, 1);

    public OptimizationExecutionService(CacheCleanupService cache, DownloadOrganizerService downloads, OptimizationHistoryStorage? history = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _history = history ?? new OptimizationHistoryStorage();
    }

    public Task<IReadOnlyList<OptimizationExecutionRecord>> LoadHistoryAsync(CancellationToken cancellationToken = default) => _history.LoadAsync(cancellationToken);

    public string ExportRecordJson(OptimizationExecutionRecord record) => OptimizationHistoryStorage.ExportRecordJson(record);

    public Task<OptimizationExecutionResult> ExecuteAsync(
        IReadOnlyList<TempFileInfo> items,
        IProgress<OptimizationExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        // Capture before yielding to the caller. Changes to a checkbox row or its path
        // after confirmation cannot replace the reviewed filesystem snapshot.
        var plan = items.Select(item => new PlannedItem(item, Snapshot(item)))
            .GroupBy(item => SourceIdentity(item.Snapshot.FilePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Snapshot.FileName.Equals(".nupkg.metadata", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray();
        return Task.Run(() => ExecuteCoreAsync(plan, progress, cancellationToken));
    }

    private async Task<OptimizationExecutionResult> ExecuteCoreAsync(
        IReadOnlyList<PlannedItem> plan, IProgress<OptimizationExecutionProgress>? progress, CancellationToken token)
    {
        var record = new OptimizationExecutionRecord { TotalCount = plan.Count };
        var succeeded = new List<TempFileInfo>();
        var failed = new List<OptimizationExecutionFailure>();
        var progressClock = Stopwatch.StartNew();
        long lastProgressMilliseconds = -100;
        long lastCheckpointMilliseconds = 0;
        string currentFileName = string.Empty;
        void ReportProgress(bool force = false)
        {
            long elapsed = progressClock.ElapsedMilliseconds;
            if (!force && elapsed - lastProgressMilliseconds < 100) return;
            lastProgressMilliseconds = elapsed;
            // Throttle before IProgress posts to a UI SynchronizationContext.
            try { progress?.Report(new(record.ProcessedCount, record.TotalCount, record.SucceededCount, record.FailedCount, currentFileName)); }
            catch (Exception) { }
        }
        bool entered = false;
        async Task SaveCheckpointAsync(bool initial)
        {
            if (!initial && progressClock.ElapsedMilliseconds - lastCheckpointMilliseconds < 1000) return;
            try
            {
                await _history.SaveAsync(record, initial ? token : CancellationToken.None).ConfigureAwait(false);
                lastCheckpointMilliseconds = progressClock.ElapsedMilliseconds;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                record.HistoryError = initial ? "Execution did not start because its local record could not be saved." :
                    "Execution stopped because its local checkpoint could not be saved.";
                throw;
            }
        }
        try
        {
            await ExecutionGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            OptimizationHistoryStorage.MarkActive(record.RunId);
            token.ThrowIfCancellationRequested();
            // An unwritable or corrupt history must be discovered before modifying files.
            await SaveCheckpointAsync(initial: true).ConfigureAwait(false);
            foreach (var batch in plan.Chunk(BatchSize))
            {
                token.ThrowIfCancellationRequested();
                var ignoredPaths = _cache.PrepareDevelopmentBatch(batch.Select(item => item.Snapshot)
                    .Where(item => item.IsActionable && item.Risk != RiskLevel.High).ToArray(), token);
                foreach (var planned in batch)
                {
                    token.ThrowIfCancellationRequested();
                    var item = planned.Snapshot;
                    bool success = false;
                    string? destination = null;
                    string? error = null;
                    try
                    {
                        if (!item.IsActionable || item.Risk == RiskLevel.High)
                        {
                            error = "Only reviewed, actionable items can execute; scan again.";
                        }
                        else if (item.Action == "delete")
                        {
                            // Require any NuGet marker to be explicitly selected and
                            // processed first; hidden side-effect deletions have no receipt.
                            (success, error) = _cache.CleanupItem(item, ignoredPaths, allowImplicitMarkerDeletion: false, token);
                        }
                        else
                        {
                            (success, destination, error) = _downloads.OrganizeItemDetailed(item, token);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                    {
                        error = exception.Message;
                    }

                    record.ProcessedCount++;
                    if (success)
                    {
                        succeeded.Add(planned.Original);
                        record.SucceededCount++;
                        if (item.Action == "move")
                        {
                            record.MovedCount++;
                        }
                        else
                        {
                            record.DeletedCount++;
                            record.FreedBytes += Math.Max(0, item.SizeInBytes);
                        }
                    }
                    else
                    {
                        record.FailedCount++;
                        failed.Add(new(planned.Original, error ?? "The action did not complete; scan again."));
                    }
                    if (record.Items.Count < OptimizationHistoryStorage.MaximumDetailedItems)
                    {
                        record.Items.Add(new OptimizationExecutionItemRecord
                        {
                            ItemId = item.ItemId,
                            FileName = item.FileName,
                            SourcePath = item.FilePath,
                            DestinationPath = destination,
                            Action = item.Action,
                            State = success ? "succeeded" : "failed",
                            Bytes = success ? Math.Max(0, item.SizeInBytes) : 0,
                            Reason = success ? null : error ?? "The action did not complete; scan again.",
                            CompletedUtc = DateTime.UtcNow,
                        });
                    }
                    currentFileName = item.FileName;
                    ReportProgress();
                }
                // Bound rewriting the retained history to once per second. The final
                // write below always records completion/cancellation regardless of cadence.
                await SaveCheckpointAsync(initial: false).ConfigureAwait(false);
            }
            record.State = token.IsCancellationRequested ? "cancelled" : record.FailedCount > 0 ? "failed" : "completed";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            record.State = "cancelled";
        }
        catch (Exception exception)
        {
            record.State = "failed";
            record.FailureReason = exception.Message;
        }
        finally
        {
            record.CompletedUtc = DateTime.UtcNow;
            try
            {
                await _history.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                record.HistoryError = exception.Message;
            }
            OptimizationHistoryStorage.MarkFinished(record.RunId);
            if (entered) ExecutionGate.Release();
            ReportProgress(force: true);
        }
        return new(record, succeeded, failed);
    }

    private static TempFileInfo Snapshot(TempFileInfo item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new TempFileInfo
        {
            ItemId = item.ItemId, FilePath = item.FilePath, FileName = item.FileName,
            SizeInBytes = item.SizeInBytes, LastModified = item.LastModified,
            Category = item.Category, CacheRoot = item.CacheRoot,
            DevelopmentRepository = item.DevelopmentRepository, BuildManifestPath = item.BuildManifestPath,
            ReviewState = item.ReviewState, Risk = item.Risk, IsSelected = item.IsSelected,
            Action = item.Action, TargetRelativePath = item.TargetRelativePath,
        };
    }

    private static string SourceIdentity(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { return path ?? string.Empty; }
        catch (NotSupportedException) { return path ?? string.Empty; }
    }

    private sealed record PlannedItem(TempFileInfo Original, TempFileInfo Snapshot);
}
