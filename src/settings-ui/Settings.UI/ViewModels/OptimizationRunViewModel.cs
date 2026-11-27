// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Storage;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Services;

namespace Kit.Settings.UI.ViewModels;

public sealed class OptimizationRunViewModel : INotifyPropertyChanged, ILiveLocalizable
{
    public OptimizationRunViewModel(OptimizationExecutionRecord record, string exportJson = null)
    {
        Record = record;
        _exportJson = exportJson;
        DetailRows = record.Items.Select(item => new OptimizationExecutionItemViewModel(item)).ToArray();
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public OptimizationExecutionRecord Record { get; }
    private string _exportJson;
    public string ExportJson => _exportJson ??= OptimizationHistoryStorage.ExportRecordJson(Record);
    public IReadOnlyList<OptimizationExecutionItemRecord> Details => Record.Items;
    public IReadOnlyList<OptimizationExecutionItemViewModel> DetailRows { get; }
    public string CompletedText => (Record.CompletedUtc ?? Record.StartedUtc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string StateLabel => Record.State switch
    {
        "completed" => "AIHub_Execution_Completed".GetLocalizedOrFallback("Completed"),
        "cancelled" => "AIHub_Execution_Cancelled".GetLocalizedOrFallback("Cancelled"),
        "failed" => "AIHub_Execution_Failed".GetLocalizedOrFallback("Stopped with errors"),
        _ => "AIHub_Execution_Interrupted".GetLocalizedOrFallback("Interrupted — verify files before retrying"),
    };
    public string Summary => Record.SkippedCount > 0
        ? "AIHub_Execution_SummaryWithSkipped".FormatLocalized(
            "{0} · {1:N0} deleted · {2:N0} moved · {3:N0} skipped · {4:N0} failed · {5:N0} remaining",
            StateLabel, Record.DeletedCount, Record.MovedCount, Record.SkippedCount, Record.FailedCount, Record.RemainingCount)
        : "AIHub_Execution_Summary".FormatLocalized(
            "{0} · {1:N0} deleted · {2:N0} moved · {3:N0} failed · {4:N0} remaining",
            StateLabel, Record.DeletedCount, Record.MovedCount, Record.FailedCount, Record.RemainingCount);
    public string DetailsSummary => string.Join(Environment.NewLine, new[]
    {
        "AIHub_Execution_Details".FormatLocalized("{0} freed · {1:N0}/{2:N0} item details retained. Latest 20 runs are kept locally.", new TempFileInfo { SizeInBytes = Record.FreedBytes }.SizeFormatted, Record.Items.Count, Record.ProcessedCount),
        Record.FailureReason ?? string.Empty,
        Record.HistoryError ?? string.Empty,
    });

    public void RefreshLocalization()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompletedText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailsSummary)));
        foreach (var row in DetailRows)
        {
            row.RefreshLocalization();
        }
    }
}

public sealed class OptimizationExecutionItemViewModel : INotifyPropertyChanged, ILiveLocalizable
{
    private readonly OptimizationExecutionItemRecord record;

    public OptimizationExecutionItemViewModel(OptimizationExecutionItemRecord record)
    {
        this.record = record;
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public string FileName => record.FileName;
    public string SourcePath => record.SourcePath;
    public string DestinationPath => record.DestinationPath;
    public string Reason => record.Reason;
    public string Summary => DescribeState(record.Action, record.State);

    public static string DescribeState(string action, string state) => state switch
    {
        "deleted" => "AIHub_ExecutionItem_Deleted".GetLocalizedOrFallback("Deleted"),
        "moved" => "AIHub_ExecutionItem_Moved".GetLocalizedOrFallback("Moved"),
        "succeeded" or "completed" => action == "move" ? "AIHub_ExecutionItem_Moved".GetLocalizedOrFallback("Moved") : "AIHub_ExecutionItem_Deleted".GetLocalizedOrFallback("Deleted"),
        "skipped" => "AIHub_ExecutionItem_Skipped".GetLocalizedOrFallback("Skipped (in use)"),
        "failed" => "AIHub_ExecutionItem_Failed".GetLocalizedOrFallback("Failed"),
        _ => "AIHub_ExecutionItem_Pending".GetLocalizedOrFallback("Not completed"),
    };

    public void RefreshLocalization() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
}
