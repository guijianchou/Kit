using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Kit.AIHubLib.Models;

/// <summary>Lifecycle stage of a scan or audit.</summary>
public enum ScanPhase
{
    Idle,
    Scanning,
    SelectingTargets,
    ExecutionPending,
    Failed,
}

/// <summary>One reported stage of a workflow.</summary>
public sealed class OptimizationStep
{
    public OptimizationStep(string title) => Title = title;

    public string Title { get; }

    /// <summary>0 while pending, 0.5 while running, 1 once complete.</summary>
    public double CompletionFraction { get; private set; }

    public bool IsDone => CompletionFraction >= 1;

    public void MarkStarted() => CompletionFraction = Math.Max(CompletionFraction, 0.5);

    public void MarkDone() => CompletionFraction = 1;

    /// <summary>Returns the stage to pending so a new run starts from zero.</summary>
    public void Reset() => CompletionFraction = 0;
}

/// <summary>
/// Weighted progress for a staged workflow.
/// </summary>
/// <remarks>
/// Mirrors the original app: named stages whose completion fractions average into a
/// 0-100 figure. Stage sets differ per workflow, so callers supply the titles.
/// </remarks>
public sealed class ScanProgressModel
{
    private readonly List<OptimizationStep> _steps;

    /// <summary>Creates a model with the optimization workflow stages.</summary>
    public ScanProgressModel()
        : this(new[] { "Preparing scan", "Scan cache locations", "Scan Downloads", "Finish" })
    {
    }

    /// <summary>Creates a model with caller-supplied stage titles.</summary>
    public ScanProgressModel(IEnumerable<string> stageTitles)
    {
        _steps = stageTitles.Select(title => new OptimizationStep(title)).ToList();
        if (_steps.Count == 0)
        {
            _steps.Add(new OptimizationStep("Working"));
        }
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

    public void Reset()
    {
        foreach (OptimizationStep step in _steps)
        {
            step.Reset();
        }
    }

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
