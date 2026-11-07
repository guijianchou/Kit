// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Storage;
using Kit.Settings.UI.Helpers;

namespace Kit.Settings.UI.ViewModels;

public sealed class OptimizationRunViewModel
{
    public OptimizationRunViewModel(OptimizationExecutionRecord record, string exportJson = null)
    {
        Record = record;
        _exportJson = exportJson;
    }

    public OptimizationExecutionRecord Record { get; }
    private string _exportJson;
    public string ExportJson => _exportJson ??= OptimizationHistoryStorage.ExportRecordJson(Record);
    public IReadOnlyList<OptimizationExecutionItemRecord> Details => Record.Items;
    public string CompletedText => (Record.CompletedUtc ?? Record.StartedUtc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string StateLabel => Record.State switch
    {
        "completed" => "AIHub_Execution_Completed".GetLocalizedOrFallback("Completed"),
        "cancelled" => "AIHub_Execution_Cancelled".GetLocalizedOrFallback("Cancelled"),
        "failed" => "AIHub_Execution_Failed".GetLocalizedOrFallback("Stopped with errors"),
        _ => "AIHub_Execution_Interrupted".GetLocalizedOrFallback("Interrupted — verify files before retrying"),
    };
    public string Summary => "AIHub_Execution_Summary".FormatLocalized(
        "{0} · {1:N0} deleted · {2:N0} moved · {3:N0} failed · {4:N0} remaining",
        StateLabel, Record.DeletedCount, Record.MovedCount, Record.FailedCount, Record.RemainingCount);
    public string DetailsSummary => string.Join(Environment.NewLine, new[]
    {
        "AIHub_Execution_Details".FormatLocalized("{0} freed · {1:N0}/{2:N0} item details retained. Latest 20 runs are kept locally.", new TempFileInfo { SizeInBytes = Record.FreedBytes }.SizeFormatted, Record.Items.Count, Record.ProcessedCount),
        Record.FailureReason ?? string.Empty,
        Record.HistoryError ?? string.Empty,
    });
}
