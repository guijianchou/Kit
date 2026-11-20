// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.Settings.UI.Helpers;

using System;
using System.Diagnostics;
using Kit.AiHub.Contract;
using Microsoft.UI.Dispatching;

/// <summary>Keeps elapsed time moving between progress reports on the owning UI thread.</summary>
internal sealed class TaskProgressClock : IDisposable
{
    private readonly Stopwatch stopwatch = new();
    private readonly DispatcherQueueTimer timer;
    private readonly Action refresh;
    private AiTaskProgress progress;
    private long progressAtMilliseconds;
    private long progressElapsedMilliseconds;

    internal TaskProgressClock(DispatcherQueue dispatcher, Action refresh)
    {
        this.refresh = refresh;
        if (dispatcher is not null)
        {
            timer = dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.IsRepeating = true;
            timer.Tick += OnTick;
        }
    }

    internal bool HasStarted { get; private set; }

    internal bool HasProgress => progress is not null;

    internal long ElapsedMilliseconds => stopwatch.ElapsedMilliseconds;

    internal AiTaskProgress Progress
    {
        get
        {
            if (progress is null)
            {
                return new AiTaskProgress("Prepare", string.Empty, 0, 0) { ElapsedMilliseconds = ElapsedMilliseconds };
            }

            long sinceReport = ElapsedMilliseconds - progressAtMilliseconds;
            bool queued = progress.Activity == "queued" || (string.IsNullOrEmpty(progress.Activity) && progress.Stage == "Queue");
            return progress with
            {
                ElapsedMilliseconds = progressElapsedMilliseconds + sinceReport,
                QueueElapsedMilliseconds = queued ? (progress.QueueElapsedMilliseconds ?? 0) + sinceReport : progress.QueueElapsedMilliseconds,
            };
        }
    }

    internal void Start()
    {
        progress = null;
        progressAtMilliseconds = 0;
        progressElapsedMilliseconds = 0;
        HasStarted = true;
        stopwatch.Restart();
        timer?.Start();
    }

    internal void Report(AiTaskProgress value)
    {
        // A delayed report, retry or fallback must not move the displayed duration backwards.
        progressElapsedMilliseconds = Math.Max(Progress.ElapsedMilliseconds, value.ElapsedMilliseconds);
        progressAtMilliseconds = ElapsedMilliseconds;
        progress = value;
    }

    internal void Stop()
    {
        timer?.Stop();
        stopwatch.Stop();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (stopwatch.IsRunning)
        {
            refresh();
        }
    }

    public void Dispose()
    {
        Stop();
        if (timer is not null)
        {
            timer.Tick -= OnTick;
        }
    }
}
