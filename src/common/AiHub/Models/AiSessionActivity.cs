namespace Kit.AiHub.Models;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Live state of one active Codex task or worker batch in the current session.</summary>
public sealed record AiActiveExecution
{
    public Guid RequestId { get; init; }
    public Guid AgentId { get; init; }
    public string PluginId { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Route { get; init; } = string.Empty;
    public string Effort { get; init; } = string.Empty;
    public int BatchIndex { get; init; }
    public int TotalBatches { get; init; }
    public int CompletedBatches { get; init; }
    public int FailedBatches { get; init; }
    public string Phase { get; init; } = "preparing";
    public int RetryCount { get; init; }
    public int? RetryLimit { get; init; }
    public int InputItemCount { get; init; }
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public long QueueMilliseconds { get; init; }
}

/// <summary>One timestamped Codex session activity log entry.</summary>
public sealed record AiSessionActivityEvent
{
    public long Sequence { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string EventType { get; init; } = string.Empty;
    public Guid RequestId { get; init; }
    public string PluginId { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Route { get; init; } = string.Empty;
    public string Effort { get; init; } = string.Empty;
    public int BatchIndex { get; init; }
    public int TotalBatches { get; init; }
    public string Phase { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public int? HttpStatusCode { get; init; }
    public int RetryCount { get; init; }
    public long? InputTokens { get; init; }
    public long? CachedInputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long ElapsedMilliseconds { get; init; }
}

/// <summary>Point-in-time snapshot of live Codex executions and current-session events.</summary>
public sealed record AiSessionActivitySnapshot
{
    public string SessionId { get; init; } = string.Empty;
    public DateTimeOffset SessionStartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;
    public int ActiveTaskCount { get; init; }
    public int ActiveWorkerCount { get; init; }
    public IReadOnlyList<AiActiveExecution> ActiveExecutions { get; init; } = Array.Empty<AiActiveExecution>();
    public IReadOnlyList<AiSessionActivityEvent> Events { get; init; } = Array.Empty<AiSessionActivityEvent>();
}

/// <summary>
/// Thread-safe, bounded real-time Codex session activity tracker.
/// Observational only: subscriber failures never affect AI execution.
/// </summary>
public sealed class AiSessionActivityTracker
{
    public const int MaximumEvents = 60;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, AiActiveExecution> _activeTasks = new();
    private readonly Dictionary<Guid, AiActiveExecution> _activeWorkers = new();
    private readonly Queue<AiSessionActivityEvent> _events = new();
    private long _nextSequence;

    public string SessionId { get; }

    public DateTimeOffset SessionStartedUtc { get; }

    /// <summary>Raised outside internal locks whenever active tasks or session events change.</summary>
    public event EventHandler? Changed;

    public AiSessionActivityTracker(string? sessionId = null)
    {
        SessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId.Trim();
        SessionStartedUtc = DateTimeOffset.UtcNow;
    }

    public void TaskStarted(Guid requestId, string pluginId, string taskId, string model, string effort, int inputItemCount)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _activeTasks[requestId] = new AiActiveExecution
            {
                RequestId = requestId,
                AgentId = Guid.Empty,
                PluginId = pluginId,
                TaskId = taskId,
                Model = model,
                Route = "main",
                Effort = effort,
                Phase = "preparing",
                InputItemCount = Math.Max(0, inputItemCount),
                StartedUtc = now,
                UpdatedUtc = now,
            };

            AppendEventLocked(new AiSessionActivityEvent
            {
                TimestampUtc = now,
                EventType = "task_started",
                RequestId = requestId,
                PluginId = pluginId,
                TaskId = taskId,
                Model = model,
                Route = "main",
                Effort = effort,
                Phase = "preparing",
            });
        }

        NotifyChanged();
    }

    public void TaskBatchPlanReady(Guid requestId, int totalBatches)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_activeTasks.TryGetValue(requestId, out AiActiveExecution? existing))
            {
                _activeTasks[requestId] = existing with
                {
                    TotalBatches = Math.Max(0, totalBatches),
                    UpdatedUtc = now,
                };
            }
        }

        NotifyChanged();
    }

    public void WorkerUpdated(
        Guid requestId,
        Guid agentId,
        string pluginId,
        string taskId,
        string model,
        string route,
        string effort,
        int batchIndex,
        int totalBatches,
        int inputItemCount,
        string phase,
        int retryCount = 0,
        int? retryLimit = null,
        long queueMilliseconds = 0,
        int? httpStatusCode = null,
        string reason = "")
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string normalizedRoute = route.ToLowerInvariant();
        string normalizedPhase = phase.ToLowerInvariant();
        bool shouldLogEvent;

        lock (_gate)
        {
            bool existed = _activeWorkers.TryGetValue(agentId, out AiActiveExecution? previous);
            DateTimeOffset startedUtc = existed && previous is not null ? previous.StartedUtc : now;
            int effectiveRetry = Math.Max(retryCount, previous?.RetryCount ?? 0);
            int? effectiveLimit = retryLimit ?? previous?.RetryLimit;

            _activeWorkers[agentId] = new AiActiveExecution
            {
                RequestId = requestId,
                AgentId = agentId,
                PluginId = pluginId,
                TaskId = taskId,
                Model = model,
                Route = normalizedRoute,
                Effort = effort,
                BatchIndex = batchIndex,
                TotalBatches = totalBatches,
                Phase = normalizedPhase,
                RetryCount = effectiveRetry,
                RetryLimit = effectiveLimit,
                InputItemCount = Math.Max(0, inputItemCount),
                StartedUtc = startedUtc,
                UpdatedUtc = now,
                QueueMilliseconds = Math.Max(0, queueMilliseconds),
            };

            if (_activeTasks.TryGetValue(requestId, out AiActiveExecution? task))
            {
                _activeTasks[requestId] = task with
                {
                    TotalBatches = Math.Max(task.TotalBatches, totalBatches),
                    Model = string.IsNullOrWhiteSpace(model) ? task.Model : model,
                    Route = string.IsNullOrWhiteSpace(normalizedRoute) ? task.Route : normalizedRoute,
                    Phase = normalizedPhase,
                    UpdatedUtc = now,
                };
            }

            // Avoid flooding the live log when Codex streams repeated delta lines in the same phase.
            shouldLogEvent = !existed
                || !string.Equals(previous!.Phase, normalizedPhase, StringComparison.Ordinal)
                || !string.Equals(previous.Route, normalizedRoute, StringComparison.Ordinal)
                || (normalizedPhase == "retrying" && effectiveRetry > previous.RetryCount);

            if (shouldLogEvent)
            {
                string eventType = normalizedPhase switch
                {
                    "queued" => "batch_queued",
                    "waiting" or "started" when !existed || previous?.Phase == "queued" => "batch_started",
                    "retrying" => "retry",
                    "failover" => "failover",
                    _ => "phase",
                };

                AppendEventLocked(new AiSessionActivityEvent
                {
                    TimestampUtc = now,
                    EventType = eventType,
                    RequestId = requestId,
                    PluginId = pluginId,
                    TaskId = taskId,
                    Model = model,
                    Route = normalizedRoute,
                    Effort = effort,
                    BatchIndex = batchIndex,
                    TotalBatches = totalBatches,
                    Phase = normalizedPhase,
                    RetryCount = effectiveRetry,
                    HttpStatusCode = httpStatusCode,
                    Reason = reason,
                });
            }
        }

        NotifyChanged();
    }

    public void WorkerFinished(
        Guid requestId,
        Guid agentId,
        string pluginId,
        string taskId,
        string model,
        string route,
        string effort,
        int batchIndex,
        int totalBatches,
        string outcome,
        string reason,
        int? httpStatusCode,
        int retryCount,
        long? inputTokens,
        long? cachedInputTokens,
        long? outputTokens,
        long elapsedMilliseconds)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string normalizedOutcome = outcome.ToLowerInvariant();
        string normalizedRoute = route.ToLowerInvariant();

        lock (_gate)
        {
            _activeWorkers.Remove(agentId);
            if (_activeTasks.TryGetValue(requestId, out AiActiveExecution? task))
            {
                bool succeeded = normalizedOutcome == "succeeded";
                bool failed = normalizedOutcome is "failed" or "timeout";
                _activeTasks[requestId] = task with
                {
                    CompletedBatches = succeeded ? task.CompletedBatches + 1 : task.CompletedBatches,
                    FailedBatches = failed ? task.FailedBatches + 1 : task.FailedBatches,
                    UpdatedUtc = now,
                };
            }

            string eventType = normalizedOutcome switch
            {
                "succeeded" => "batch_completed",
                "cancelled" => "batch_cancelled",
                _ => "batch_failed",
            };

            AppendEventLocked(new AiSessionActivityEvent
            {
                TimestampUtc = now,
                EventType = eventType,
                RequestId = requestId,
                PluginId = pluginId,
                TaskId = taskId,
                Model = model,
                Route = normalizedRoute,
                Effort = effort,
                BatchIndex = batchIndex,
                TotalBatches = totalBatches,
                Outcome = normalizedOutcome,
                Reason = reason,
                HttpStatusCode = httpStatusCode,
                RetryCount = Math.Max(0, retryCount),
                InputTokens = inputTokens,
                CachedInputTokens = cachedInputTokens,
                OutputTokens = outputTokens,
                ElapsedMilliseconds = Math.Max(0, elapsedMilliseconds),
            });
        }

        NotifyChanged();
    }

    public void TaskFinished(
        Guid requestId,
        string pluginId,
        string taskId,
        string model,
        string route,
        string outcome,
        string reason,
        int completedBatches,
        int totalBatches,
        long elapsedMilliseconds)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string normalizedOutcome = outcome.ToLowerInvariant();

        lock (_gate)
        {
            _activeTasks.Remove(requestId);
            Guid[] strayWorkers = _activeWorkers.Values
                .Where(worker => worker.RequestId == requestId)
                .Select(worker => worker.AgentId)
                .ToArray();
            foreach (Guid workerId in strayWorkers)
            {
                _activeWorkers.Remove(workerId);
            }

            string eventType = normalizedOutcome switch
            {
                "succeeded" => "task_completed",
                "partial" => "task_partial",
                "cancelled" => "task_cancelled",
                _ => "task_failed",
            };

            AppendEventLocked(new AiSessionActivityEvent
            {
                TimestampUtc = now,
                EventType = eventType,
                RequestId = requestId,
                PluginId = pluginId,
                TaskId = taskId,
                Model = model,
                Route = route.ToLowerInvariant(),
                BatchIndex = completedBatches,
                TotalBatches = totalBatches,
                Outcome = normalizedOutcome,
                Reason = reason,
                ElapsedMilliseconds = Math.Max(0, elapsedMilliseconds),
            });
        }

        NotifyChanged();
    }

    public AiSessionActivitySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            // Prefer per-worker entries when workers are active; fall back to task-level entries during prep.
            var active = new List<AiActiveExecution>(_activeWorkers.Count + _activeTasks.Count);
            var tasksWithWorkers = new HashSet<Guid>();
            foreach (AiActiveExecution worker in _activeWorkers.Values.OrderBy(item => item.StartedUtc).ThenBy(item => item.BatchIndex))
            {
                tasksWithWorkers.Add(worker.RequestId);
                active.Add(worker);
            }

            foreach (AiActiveExecution task in _activeTasks.Values.OrderBy(item => item.StartedUtc))
            {
                if (!tasksWithWorkers.Contains(task.RequestId))
                {
                    active.Add(task);
                }
            }

            return new AiSessionActivitySnapshot
            {
                SessionId = SessionId,
                SessionStartedUtc = SessionStartedUtc,
                CapturedUtc = DateTimeOffset.UtcNow,
                ActiveTaskCount = _activeTasks.Count,
                ActiveWorkerCount = _activeWorkers.Count,
                ActiveExecutions = Array.AsReadOnly(active.ToArray()),
                Events = Array.AsReadOnly(_events.Reverse().ToArray()),
            };
        }
    }

    private void AppendEventLocked(AiSessionActivityEvent entry)
    {
        _events.Enqueue(entry with { Sequence = ++_nextSequence });
        while (_events.Count > MaximumEvents)
        {
            _events.Dequeue();
        }
    }

    private void NotifyChanged()
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // Observational listeners never affect AI execution.
            }
        }
    }
}
