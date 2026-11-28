// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Kit.AiHub.Contract;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library.Helpers;
using ManagedCommon;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.ViewModels;

public sealed partial class AIHubPageViewModel
{
    private RelayCommand _diagnoseSystemCommand;
    private OptimizationInventoryReport _lastDiagnosticInventory;
    private OptimizationDiagnosticResult _systemDiagnosis;
    private string _systemDiagnosisStatus = string.Empty;

    public string SystemDiagnosisTitle => "AIHub_SystemDiagnosisTitle".GetLocalizedOrFallback("System diagnosis");

    public string SystemDiagnosisActionLabel => "AIHub_SystemDiagnosisAction".GetLocalizedOrFallback("Analyze system");

    public string SystemDiagnosisDescription => "AIHub_SystemDiagnosisDescription".GetLocalizedOrFallback("Review storage, CPU and memory samples, startup entries and saved audit findings. The report includes suggested checks, tradeoffs and verification steps.");

    public bool CanDiagnoseSystem => !_disposed && CanSelectOptimizationItems;

    public ICommand DiagnoseSystemCommand => _diagnoseSystemCommand ??= new RelayCommand(() => _ = RunSystemDiagnosisAsync(), () => CanDiagnoseSystem);

    public bool HasSystemDiagnosis => _systemDiagnosis != null;

    public string SystemDiagnosisText => _systemDiagnosis?.Format(PreferChinese()) ?? string.Empty;

    public string SystemDiagnosisStatus
    {
        get => _systemDiagnosisStatus;
        private set => Set(ref _systemDiagnosisStatus, value);
    }

    private void RefreshDiagnosisCommands()
    {
        _diagnoseSystemCommand?.OnCanExecuteChanged();
        OnPropertyChanged(nameof(CanDiagnoseSystem));
    }

    internal static OptimizationStabilityEvidence BuildStabilityEvidence(AuditResult snapshot)
    {
        if (snapshot == null)
        {
            return null;
        }

        static DateTimeOffset? Utc(DateTime? value) => value is DateTime timestamp
            ? new DateTimeOffset(timestamp.Kind == DateTimeKind.Local ? timestamp.ToUniversalTime() : DateTime.SpecifyKind(timestamp, DateTimeKind.Utc))
            : null;
        DateTimeOffset? from = Utc(snapshot.ScanStart);
        DateTimeOffset? to = Utc(snapshot.ScanEnd);
        bool validWindow = from.HasValue && to.HasValue && to.Value > from.Value;
        return new()
        {
            PeriodDays = validWindow ? Math.Max(1, (int)Math.Ceiling((to.GetValueOrDefault() - from.GetValueOrDefault()).TotalDays)) : 0,
            ScanCount = 1,
            CriticalCount = snapshot.HighCount,
            WarningCount = snapshot.MediumCount,
            IsPartial = !validWindow || string.IsNullOrWhiteSpace(snapshot.CollectionProfile) ||
                snapshot.SkippedChannels?.Count > 0 || snapshot.LimitedChannels?.Count > 0 ||
                snapshot.AnalysisOutcome is not ("ok" or "skipped"),
            ObservedFromUtc = from,
            ObservedToUtc = to,
        };
    }

    private async Task RunSystemDiagnosisAsync()
    {
        if (!CanDiagnoseSystem)
        {
            return;
        }
        _optimizationCts?.Dispose();
        var operation = new CancellationTokenSource();
        _optimizationCts = operation;
        CancellationToken token = operation.Token;
        int finished = 0;
        bool chinese = PreferChinese();

        // Display ranges can exceed the evidence collected by the original scan.
        // Diagnose only that original snapshot's actual coverage and counters.
        OptimizationStabilityEvidence stability = BuildStabilityEvidence(_auditSourceSnapshot);
        var cachedInventory = _lastDiagnosticInventory;
        if (cachedInventory == null || DateTimeOffset.UtcNow - cachedInventory.CompletedUtc > TimeSpan.FromMinutes(2))
        {
            cachedInventory = null;
        }
        ResetScanProgress();
        IsOptimizing = true;
        ScanPhase = ScanPhase.Scanning;
        BeginStage("Preparing scan");
        SystemDiagnosisStatus = "AIHub_SystemDiagnosisCollecting".GetLocalizedOrFallback("Collecting system evidence…");
        OptimizationWorkflowStatus = SystemDiagnosisStatus;
        try
        {
            var service = new OptimizationDiagnosticService();
            OptimizationEvidenceSnapshot evidence = await Task.Run(() => service.CollectAsync(cachedInventory, stability, token), token);
            token.ThrowIfCancellationRequested();
            if (_disposed || _optimizationCts != operation)
            {
                return;
            }
            EndStage("Preparing scan");
            BeginStage("Finish");
            SystemDiagnosisStatus = "AIHub_SystemDiagnosisAnalyzing".GetLocalizedOrFallback("Analyzing system evidence…");
            OptimizationWorkflowStatus = SystemDiagnosisStatus;
            var progress = new Progress<AiTaskProgress>(update =>
            {
                if (_disposed || Volatile.Read(ref finished) != 0 || token.IsCancellationRequested || _optimizationCts != operation)
                {
                    return;
                }
                _optimizationClock.Report(update);
                OptimizationWorkflowStatus = AiTaskProgressText.Summary(update, chinese);
            });
            OptimizationDiagnosticResult result = await service.AnalyzeAsync(evidence, chinese ? "zh-CN" : "en-US", progress, token);
            Interlocked.Exchange(ref finished, 1);
            token.ThrowIfCancellationRequested();
            if (_disposed || _optimizationCts != operation)
            {
                return;
            }

            // A failed refresh must not replace a previously validated report.
            if (result.IsSuccess || _systemDiagnosis?.IsSuccess != true)
            {
                _systemDiagnosis = result;
                OnPropertyChanged(nameof(HasSystemDiagnosis));
                OnPropertyChanged(nameof(SystemDiagnosisText));
            }
            SystemDiagnosisStatus = result.IsSuccess
                ? "AIHub_SystemDiagnosisComplete".GetLocalizedOrFallback("System diagnosis complete. Review the report before choosing any changes.")
                : "AIHub_SystemDiagnosisFailed".FormatLocalized("AI diagnosis could not finish ({0}). The previous report is retained when available.", result.ErrorCode);
            OptimizationWorkflowStatus = SystemDiagnosisStatus;
            if (result.IsSuccess)
            {
                EndStage("Finish");
            }
            ScanPhase = result.IsSuccess ? ScanPhase.SelectingTargets : ScanPhase.Failed;
            Logger.LogInfo($"Optimization diagnosis finished: outcome={result.ErrorCode}, collectionMs={evidence.CollectionElapsedMilliseconds}, analysisMs={result.AnalysisElapsedMilliseconds}, evidenceCount={evidence.Evidence.Count}");
            ShowStatus(SystemDiagnosisStatus, result.IsSuccess ? InfoBarSeverity.Success : InfoBarSeverity.Warning, optimization: true);
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && _optimizationCts == operation)
            {
                SystemDiagnosisStatus = "AIHub_SystemDiagnosisCancelled".GetLocalizedOrFallback("Diagnosis cancelled. The previous report is retained.");
                OptimizationWorkflowStatus = SystemDiagnosisStatus;
                ScanPhase = ScanPhase.Idle;
            }
        }
        catch (Exception exception)
        {
            Logger.LogWarning($"System diagnosis failed: type={exception.GetType().Name}");
            if (!_disposed && _optimizationCts == operation)
            {
                SystemDiagnosisStatus = "AIHub_SystemDiagnosisFailed".FormatLocalized("AI diagnosis could not finish ({0}). The previous report is retained when available.", "ExecutionFailed");
                OptimizationWorkflowStatus = SystemDiagnosisStatus;
                ScanPhase = ScanPhase.Failed;
            }
        }
        finally
        {
            Interlocked.Exchange(ref finished, 1);
            if (!_disposed && _optimizationCts == operation)
            {
                IsOptimizing = false;
            }
        }
    }
}
