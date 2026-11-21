// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Kit.Settings.UI.Helpers;

namespace Kit.Settings.UI.ViewModels;

public sealed partial class AIHubPageViewModel
{
    private int _auditResultViewIndex;
    private string _findingCategory;

    public int AuditResultViewIndex
    {
        get => _auditResultViewIndex;
        set
        {
            if (value is >= 0 and <= 1 && Set(ref _auditResultViewIndex, value))
            {
                RefreshAuditWorkspace();
            }
        }
    }

    public bool ShowAuditOverview => AuditResultViewIndex == 0 && HasAuditData;

    public bool ShowAuditFindings => AuditResultViewIndex == 1;

    public string AuditOverviewTabLabel => "AIHub_WorkspaceOverview".GetLocalizedOrFallback("System overview");

    public string AuditFindingsTabLabel => "AIHub_WorkspaceFindings".FormatLocalized("Findings ({0})", TotalFindings);

    public string AuditViewAllLabel => "AIHub_WorkspaceViewAll".FormatLocalized("View all {0} issues", TotalFindings);

    public string AuditBackToOverviewLabel => "AIHub_WorkspaceBack".GetLocalizedOrFallback("Back to overview");

    public string FindingSourceFilterLabel => "AIHub_Fluent_EventSource".GetLocalizedOrFallback("Event source");

    public string TaskDetailsLabel => "AIHub_WorkspaceTaskDetails".GetLocalizedOrFallback("Task details");

    public string AuditFindingsTitle => _findingCategory != null
        ? AuditCategories.FirstOrDefault(category => string.Equals(category.Category, _findingCategory, StringComparison.OrdinalIgnoreCase))?.Title ?? _findingCategory
        : "AIHub_WorkspaceAllIssues".GetLocalizedOrFallback("All issues");

    public string AuditFindingContext
    {
        get
        {
            var filters = new List<string>();
            if (_findingCategory != null)
            {
                filters.Add(AuditFindingsTitle);
            }

            if (_severityFilter != "All")
            {
                filters.Add(_severityFilter switch { "High" => AuditOverviewHighLabel, "Medium" => AuditOverviewMediumLabel, _ => AuditOverviewLowLabel });
            }

            if (_sourceFilter != "All")
            {
                filters.Add(_sourceFilter);
            }

            if (!string.IsNullOrWhiteSpace(_findingSearchText))
            {
                filters.Add(_findingSearchText.Trim());
            }

            return filters.Count > 0 ? string.Join(" · ", filters)
                : "AIHub_WorkspaceSortHint".GetLocalizedOrFallback("All issues · sorted by risk and occurrences");
        }
    }

    public void OpenAuditFindings(string category = null, string severity = "All")
    {
        ResetFindingFilters();
        _findingCategory = category;
        SetSeverityFilter(severity);
        AuditResultViewIndex = 1;
    }

    private void RefreshAuditWorkspace()
    {
        OnPropertyChanged(nameof(ShowAuditOverview));
        OnPropertyChanged(nameof(ShowAuditFindings));
        OnPropertyChanged(nameof(AuditFindingsTabLabel));
        OnPropertyChanged(nameof(AuditViewAllLabel));
        OnPropertyChanged(nameof(AuditFindingsTitle));
        OnPropertyChanged(nameof(AuditFindingContext));
    }
}
