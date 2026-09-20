// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.AIHubLib.Models;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>Lifecycle stage of an optimization scan.</summary>
public enum ScanPhase
{
    Idle,
    Scanning,
    SelectingTargets,
    ExecutionPending,
    Failed,
}

/// <summary>One reported stage of the optimization workflow.</summary>
public sealed class OptimizationStep
{
    public OptimizationStep(string title) => Title = title;

    public string Title { get; }

    /// <summary>0 while pending, 1 once complete.</summary>
    public double CompletionFraction { get; private set; }

    public bool IsDone => CompletionFraction >= 1;

    public void MarkStarted() => CompletionFraction = Math.Max(CompletionFraction, 0.5);

    public void MarkDone() => CompletionFraction = 1;

    /// <summary>Returns the stage to pending so a new scan starts from zero.</summary>
    public void Reset() => CompletionFraction = 0;
}

/// <summary>
/// Weighted progress for the optimization workflow.
/// </summary>
/// <remarks>
/// Mirrors the original OptimizationViewModel model: named stages with completion
/// fractions averaged into a 0-100 figure. The scan previously reported only
/// "Scanning..." and then success, so a long scan looked instant.
/// </remarks>
public sealed class ScanProgressModel
{
    private readonly List<OptimizationStep> _steps;

    public ScanProgressModel()
    {
        _steps = new List<OptimizationStep>
        {
            new OptimizationStep("Preparing scan"),
            new OptimizationStep("Scan cache locations"),
            new OptimizationStep("Scan Downloads"),
            new OptimizationStep("Finish"),
        };
    }

    public IReadOnlyList<OptimizationStep> Steps => _steps;

    /// <summary>Name of the stage currently running, or null when idle.</summary>
    public string? CurrentStage => _steps.FirstOrDefault(s => s.CompletionFraction is > 0 and < 1)?.Title;

    /// <summary>Average completion across stages, clamped to 0-100.</summary>
    public double Percent => Math.Clamp(
        Math.Floor(100 * _steps.Sum(s => s.CompletionFraction) / _steps.Count),
        0,
        100);

    public string PercentText => string.Format(CultureInfo.InvariantCulture, "{0:0}%", Percent);

    public int CompletedStageCount => _steps.Count(s => s.IsDone);

    public int TotalStageCount => _steps.Count;

    public string StageCountText => string.Format(
        CultureInfo.InvariantCulture,
        "{0}/{1} stages",
        CompletedStageCount,
        TotalStageCount);

    public bool IsComplete => _steps.All(s => s.IsDone);

    /// <summary>Marks a stage as running so the UI shows which one is active.</summary>
    public void StartStage(string title) => Find(title)?.MarkStarted();

    /// <summary>Marks a stage complete. Completing a later stage implies earlier ones.</summary>
    public void CompleteStage(string title)
    {
        int index = _steps.FindIndex(s => string.Equals(s.Title, title, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }

        for (int i = 0; i <= index; i++)
        {
            _steps[i].MarkDone();
        }
    }

    /// <summary>Resets every stage so a new scan starts from zero.</summary>
    public void Reset()
    {
        foreach (OptimizationStep step in _steps)
        {
            step.Reset();
        }
    }

    /// <summary>Marks the workflow finished, which sets every stage complete.</summary>
    public void CompleteAll()
    {
        foreach (OptimizationStep step in _steps)
        {
            step.MarkDone();
        }
    }

    private OptimizationStep? Find(string title) =>
        _steps.FirstOrDefault(s => string.Equals(s.Title, title, StringComparison.Ordinal));
}
