// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text;
using Kit.AIHubLib.Models;
using Kit.Settings.UI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Kit.Settings.UI.Views;

public sealed partial class FindingDetailsDialog : ContentDialog
{
    private AuditIssueEnhanced _currentIssue;

    public FindingDetailsDialog()
    {
        InitializeComponent();
    }

    public void SetFinding(AuditIssueEnhanced issue)
    {
        _currentIssue = issue;
        if (issue == null)
        {
            return;
        }

        FindingTitleTextBlock.Text = issue.DisplayTitle;
        SeverityTextBlock.Text = issue.SeverityText;

        SeverityBadge.Style = (Style)Resources[issue.IsHigh ? "HighSeverityBadgeStyle" : issue.IsMedium ? "MediumSeverityBadgeStyle" : "LowSeverityBadgeStyle"];

        CategoryTextBlock.Text = issue.CategoryLabel;
        EventIdTextBlock.Text = "FindingDetails_EventIdFormat".FormatLocalized("Event {0}", issue.EventId);

        if (issue.HasOccurrences)
        {
            OccurrencesBadge.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            OccurrencesTextBlock.Text = issue.OccurrencesLabel;
        }
        else
        {
            OccurrencesBadge.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        DescriptionTextBlock.Text = issue.EvidenceSummaryText;
        RootCauseTextBlock.Text = issue.PriorityReasonText;
        RecommendationTextBlock.Text = issue.PriorityActionText;

        TimestampTextBlock.Text = issue.EventTimestampText;
        AffectedTextBlock.Text = string.IsNullOrWhiteSpace(issue.Affected) ? "FindingDetails_AffectedNotAvailable".GetLocalizedOrFallback("N/A") : issue.Affected;
        KeyTextBlock.Text = issue.Key;
    }

    private void OnCopySummaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_currentIssue == null)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("FindingDetails_CopyHeader".FormatLocalized("[Security Finding] {0}", _currentIssue.DisplayTitle));
        sb.AppendLine("FindingDetails_CopySeverity".FormatLocalized("Severity: {0} | Category: {1} | Event ID: {2}", _currentIssue.SeverityText, _currentIssue.CategoryLabel, _currentIssue.EventId));
        sb.AppendLine("FindingDetails_CopyTimestamp".FormatLocalized("Timestamp: {0}", _currentIssue.EventTimestampText));
        sb.AppendLine("FindingDetails_CopyDetection".FormatLocalized("Detection: {0}", _currentIssue.EvidenceSummaryText));
        sb.AppendLine("FindingDetails_CopyRootCause".FormatLocalized("Root Cause: {0}", _currentIssue.PriorityReasonText));
        sb.AppendLine("FindingDetails_CopyAction".FormatLocalized("Recommended Action: {0}", _currentIssue.PriorityActionText));

        var dataPackage = new DataPackage();
        dataPackage.SetText(sb.ToString());
        Clipboard.SetContent(dataPackage);
    }
}
