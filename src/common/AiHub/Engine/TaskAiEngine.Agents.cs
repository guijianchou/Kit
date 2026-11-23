namespace Kit.AiHub.Engine;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Kit.AiHub.Contract;
using Kit.AiHub.Models;

public sealed partial class TaskAiEngine
{
    private sealed record BatchWaiter(Guid RequestId, bool AllowSubagents);

    private sealed class AnalysisRun(Guid runId)
    {
        public Guid RunId { get; } = runId;
        public Guid CoordinatorId { get; } = Guid.NewGuid();
        public AiAgentResult[] Agents { get; set; } = [];
    }

    private void RefreshAdmissionBudget()
    {
        long version = Interlocked.Read(ref _stateVersion);
        AiHubConfig current = ReadState();
        ApplyAdmissionBudget(current, version);
    }

    private void ApplyAdmissionBudget(AiHubConfig config, long version)
    {
        lock (_schedulerLock)
        {
            // Never let a request's older read overwrite a more recent settings event.
            if (version != Interlocked.Read(ref _stateVersion))
            {
                return;
            }

            _concurrencyLimit = Math.Clamp(config.MaxConcurrentAnalysis, 1, 4);
            _subagentsEnabled = config.EnableSubagents;
            _maxAgentsPerTask = Math.Clamp(config.MaxAgentsPerTask, 1, 4);
            SignalCapacity();
        }
    }

    // Called under _schedulerLock. Prefer a task with fewer active workers; within
    // the same share, preserve admission order. An idle service lends all available
    // slots to one task, while newly waiting tasks receive the next released slots.
    // The host coordinator never acquires a slot while it waits for its workers.
    private LinkedListNode<BatchWaiter>? NextBatchWaiter()
    {
        LinkedListNode<BatchWaiter>? selected = null;
        int smallestShare = int.MaxValue;
        for (LinkedListNode<BatchWaiter>? node = _waitingBatches.First; node is not null; node = node.Next)
        {
            int running = _runningByRequest.GetValueOrDefault(node.Value.RequestId);
            int taskLimit = _subagentsEnabled && node.Value.AllowSubagents ? _maxAgentsPerTask : 1;
            if (running >= taskLimit)
            {
                continue;
            }
            if (running < smallestShare)
            {
                selected = node;
                smallestShare = running;
                if (running == 0)
                {
                    break;
                }
            }
        }

        return selected;
    }

    private static AiTaskResult<TOutput> WithRunMetadata<TOutput>(AiTaskResult<TOutput> result, AnalysisRun run)
        where TOutput : class => new()
        {
            RunId = run.RunId,
            CoordinatorId = run.CoordinatorId,
            Agents = Array.AsReadOnly(run.Agents.Select(agent => agent.State == "queued"
                ? agent with { State = "not-started", ErrorCode = result.ErrorCode } : agent).ToArray()),
            IsSuccess = result.IsSuccess,
            ErrorCode = result.ErrorCode,
            ErrorMessage = result.ErrorMessage,
            Diagnostics = result.Diagnostics,
            FailureRoute = result.FailureRoute,
            Payload = result.Payload,
            CompletedBatches = result.CompletedBatches,
            TotalBatches = result.TotalBatches,
            UsedModel = result.UsedModel,
            UsedRoute = result.UsedRoute,
            Elapsed = result.Elapsed,
        };
}
