// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Kit.AIHubLib.Models;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library.Helpers;

namespace Kit.Settings.UI.ViewModels;

/// <summary>Read-only space observations, deliberately separate from cleanup candidates.</summary>
public sealed class OptimizationInventoryViewModel : Observable
{
    private IReadOnlyList<OptimizationInventoryScopeViewModel> _rows = Array.Empty<OptimizationInventoryScopeViewModel>();
    private IReadOnlyList<OptimizationInventoryScopeViewModel> _filteredRows = Array.Empty<OptimizationInventoryScopeViewModel>();
    private OptimizationInventoryReport _report;
    private string _searchText = string.Empty;
    private int _filterIndex;
    private long? _observedBytes;
    private long _recentBytes;
    private long _olderBytes;

    public IReadOnlyList<OptimizationInventoryScopeViewModel> Rows => _rows;
    public IReadOnlyList<OptimizationInventoryScopeViewModel> FilteredRows => _filteredRows;
    public bool HasRows => _rows.Any(row => row.Scope.State != "missing");
    public bool HasFilteredRows => _filteredRows.Count > 0;
    public bool IsPartial => _report?.IsPartial == true;
    public long? ObservedBytes => _observedBytes;
    public string ObservedSizeText => FormatSize(_observedBytes);
    public string RecentSizeText => FormatSize(_observedBytes.HasValue ? _recentBytes : null);
    public string OldSizeText => FormatSize(_observedBytes.HasValue ? _olderBytes : null);
    public string CoverageSummary => _report is null ? string.Empty : "AIHub_Inventory_CoverageFormat".FormatLocalized(
        "{0:N0} measured · {1:N0} partial · {2:N0} unavailable · {3:N0} protected · {4:N0} not present · {5:N0} entries checked",
        _rows.Count(row => row.Scope.ObservedBytes.HasValue), _rows.Count(row => row.Scope.State == "partial"),
        _rows.Count(row => row.Scope.State == "unavailable"), _rows.Count(row => row.Scope.State == "protected"),
        _rows.Count(row => row.Scope.State == "missing"), _report.ExaminedEntries);
    public string SnapshotText => _report is null ? string.Empty : "AIHub_Inventory_SnapshotFormat".FormatLocalized(
        "Observed {0:g} · {1:N1} s · largest locations first", _report.CompletedUtc.ToLocalTime(), _report.ElapsedMilliseconds / 1000.0);
    public string FilterSummary => "AIHub_Inventory_FilterSummaryFormat".FormatLocalized(
        "{0:N0} of {1:N0} locations shown. Percentages use measured space, not drive capacity.", _filteredRows.Count, _rows.Count(row => row.Scope.State != "missing"));
    public IReadOnlyList<string> FilterOptions { get; } = new[]
    {
        "AIHub_Inventory_FilterAll".GetLocalizedOrFallback("All locations"),
        "AIHub_Inventory_FilterCache".GetLocalizedOrFallback("Cache locations"),
        "AIHub_Inventory_FilterAnalysis".GetLocalizedOrFallback("Analysis only"),
        "AIHub_Inventory_FilterManaged".GetLocalizedOrFallback("System managed"),
        "AIHub_Inventory_FilterProtected".GetLocalizedOrFallback("Protected"),
    };

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public int FilterIndex
    {
        get => _filterIndex;
        set
        {
            if (Set(ref _filterIndex, Math.Clamp(value, 0, 4)))
            {
                ApplyFilter();
            }
        }
    }

    public void Apply(OptimizationInventoryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var expanded = _rows.Where(row => row.IsExpanded).Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        _report = report;
        var measured = report.Scopes.Where(scope => scope.ObservedBytes.HasValue).ToArray();
        _observedBytes = measured.Length == 0 ? null : SumSizes(measured.Select(scope => scope.ObservedBytes!.Value));
        _recentBytes = SumSizes(measured.Select(scope => scope.RecentBytes));
        _olderBytes = SumSizes(measured.Select(scope => scope.OlderBytes));
        _rows = report.Scopes.Select(scope => new OptimizationInventoryScopeViewModel(scope, _observedBytes ?? 0)
        {
            IsExpanded = expanded.Contains(scope.Id),
        }).OrderByDescending(row => row.ObservedBytes.HasValue).ThenByDescending(row => row.ObservedBytes)
          .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        foreach (string property in new[]
        {
            nameof(Rows), nameof(HasRows), nameof(IsPartial), nameof(ObservedBytes), nameof(ObservedSizeText),
            nameof(RecentSizeText), nameof(OldSizeText), nameof(CoverageSummary), nameof(SnapshotText),
        })
        {
            OnPropertyChanged(property);
        }

        ApplyFilter();
    }

    internal static string FormatSize(long? bytes) => bytes.HasValue
        ? new TempFileInfo { SizeInBytes = Math.Max(0, bytes.Value) }.SizeFormatted
        : "AIHub_Inventory_NotMeasured".GetLocalizedOrFallback("Not measured");

    private static long SumSizes(IEnumerable<long> values) => values.Aggregate(0L, (total, value) =>
        value <= 0 ? total : value > long.MaxValue - total ? long.MaxValue : total + value);

    private void ApplyFilter()
    {
        string disposition = _filterIndex switch { 1 => "cache", 2 => "analysis-only", 3 => "system-managed", 4 => "protected", _ => string.Empty };
        var terms = _searchText.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        _filteredRows = _rows.Where(row => row.Scope.State != "missing"
            && (disposition.Length == 0 || row.Scope.Disposition == disposition)
            && terms.All(term => row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Path.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Reason.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Guidance.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.StateLabel.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.DispositionLabel.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        OnPropertyChanged(nameof(FilteredRows));
        OnPropertyChanged(nameof(HasFilteredRows));
        OnPropertyChanged(nameof(FilterSummary));
    }
}

public sealed class OptimizationInventoryScopeViewModel : Observable
{
    private bool _isExpanded;

    public OptimizationInventoryScopeViewModel(OptimizationInventoryScope scope, long totalBytes)
    {
        Scope = scope;
        SharePercent = scope.ObservedBytes.HasValue && totalBytes > 0 ? Math.Clamp(100.0 * scope.ObservedBytes.Value / totalBytes, 0, 100) : 0;
        TopFiles = scope.TopFiles.OrderByDescending(file => file.SizeInBytes).Take(5)
            .Select(file => new OptimizationInventoryFileViewModel(file)).ToArray();
    }

    public OptimizationInventoryScope Scope { get; }
    public string Id => Scope.Id;
    public string Name => PreferChinese ? Scope.NameZh : Scope.NameEn;
    public string Path => Scope.RootPath;
    public long? ObservedBytes => Scope.ObservedBytes;
    public bool HasMeasuredSize => Scope.ObservedBytes.HasValue;
    public string SizeText => OptimizationInventoryViewModel.FormatSize(Scope.ObservedBytes);
    public string FileSummary => HasMeasuredSize
        ? "AIHub_Inventory_FileCountFormat".FormatLocalized("{0:N0} files · {1:N1}% of observed space", Scope.FileCount, SharePercent)
        : "AIHub_Inventory_NotEnumerated".GetLocalizedOrFallback("Contents not enumerated");
    public double SharePercent { get; }
    public string Reason => PreferChinese ? Scope.ReasonZh : Scope.ReasonEn;
    public string Guidance => PreferChinese ? Scope.GuidanceZh : Scope.GuidanceEn;
    public IReadOnlyList<OptimizationInventoryFileViewModel> TopFiles { get; }
    public bool HasTopFiles => TopFiles.Count > 0;
    public bool HasScanNotes => Scope.SkippedEntries > 0 || Scope.AccessFailures > 0;
    public string ScanNotes => "AIHub_Inventory_ScanNotesFormat".FormatLocalized(
        "{0:N0} entries skipped · {1:N0} access failures. Sizes include only the entries that could be measured.", Scope.SkippedEntries, Scope.AccessFailures);
    public string AgeSummary => HasMeasuredSize ? "AIHub_Inventory_AgeSummaryFormat".FormatLocalized(
        "Modified within 7 days: {0} · 30 days or older: {1}", OptimizationInventoryViewModel.FormatSize(Scope.RecentBytes), OptimizationInventoryViewModel.FormatSize(Scope.OlderBytes)) : string.Empty;
    public string StateLabel => Scope.State switch
    {
        "complete" => "AIHub_Inventory_StateComplete".GetLocalizedOrFallback("Measured"),
        "partial" => "AIHub_Inventory_StatePartial".GetLocalizedOrFallback("Partial measurement"),
        "unavailable" => "AIHub_Inventory_StateUnavailable".GetLocalizedOrFallback("Unavailable"),
        "protected" => "AIHub_Inventory_StateProtected".GetLocalizedOrFallback("Protected · not scanned"),
        "missing" => "AIHub_Inventory_StateMissing".GetLocalizedOrFallback("Not present"),
        _ => "AIHub_Inventory_NotMeasured".GetLocalizedOrFallback("Not measured"),
    };
    public string DispositionLabel => Scope.Disposition switch
    {
        "cache" => "AIHub_Inventory_DispositionCache".GetLocalizedOrFallback("Cache · separate cleanup review"),
        "system-managed" => "AIHub_Inventory_DispositionManaged".GetLocalizedOrFallback("Use Windows or the owning app"),
        "protected" => "AIHub_Inventory_DispositionProtected".GetLocalizedOrFallback("Keep protected files"),
        _ => "AIHub_Inventory_DispositionAnalysis".GetLocalizedOrFallback("Analysis only"),
    };
    public string Glyph => Scope.Disposition == "protected" ? "\uE72E" : Scope.FamilyId switch
    {
        "development" => "\uE943",
        "packages" => "\uE7B8",
        "editors" => "\uE70F",
        "browsers" => "\uE774",
        "applications" => "\uE71D",
        _ => "\uE7F1",
    };
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value) && value && !HasBeenExpanded)
            {
                HasBeenExpanded = true;
                OnPropertyChanged(nameof(HasBeenExpanded));
            }
        }
    }

    public bool HasBeenExpanded { get; private set; }
    private static bool PreferChinese => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
}

public sealed class OptimizationInventoryFileViewModel
{
    public OptimizationInventoryFileViewModel(OptimizationInventoryFile file)
    {
        File = file;
    }

    public OptimizationInventoryFile File { get; }
    public string Name => File.Name;
    public string Path => File.Path;
    public string SizeText => OptimizationInventoryViewModel.FormatSize(File.SizeInBytes);
    public string ModifiedText => "AIHub_Inventory_ModifiedFormat".FormatLocalized("Modified {0:g}", File.LastModifiedUtc.ToLocalTime());
}
