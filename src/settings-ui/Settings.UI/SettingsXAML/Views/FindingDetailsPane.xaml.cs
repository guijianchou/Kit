// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Kit.AIHubLib.Models;
using Kit.Settings.UI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Kit.Settings.UI.Views;

public sealed partial class FindingDetailsPane : UserControl
{
    private AuditIssueEnhanced _finding;

    public event EventHandler CloseRequested;

    public event EventHandler PreviousRequested;

    public event EventHandler NextRequested;

    public event EventHandler AnalyzeRequested;

    public FindingDetailsPane()
    {
        InitializeComponent();
        PaneTitleText.Text = "AIHub_Fluent_DetailsTitle".GetLocalizedOrFallback("Finding details");
        EventIdLabel.Text = "AIHub_Fluent_EventId".GetLocalizedOrFallback("Event ID");
        SourceLabel.Text = "AIHub_Fluent_EventSource".GetLocalizedOrFallback("Source");
        RangeLabel.Text = "AIHub_Fluent_ResultRange".GetLocalizedOrFallback("Audit range");
        ReasonLabel.Text = "AIHub_Fluent_Assessment".GetLocalizedOrFallback("Assessment");
        EvidenceExpander.Header = "AIHub_Fluent_EventEvidence".GetLocalizedOrFallback("Event evidence and references");
        EvidenceDescriptionText.Text = "AIHub_Fluent_EventEvidenceDescription".GetLocalizedOrFallback("Evidence recorded with this finding. Event references identify the underlying records.");
        CopyLabel.Text = "AIHub_Fluent_CopyFinding".GetLocalizedOrFallback("Copy summary");
        AnalyzeLabel.Text = "AIHub_Fluent_AnalyzeRange".GetLocalizedOrFallback("Analyze current range");
        SetButtonLabel(CloseDetailsButton, "AIHub_Fluent_CloseDetails".GetLocalizedOrFallback("Close details"));
        SetButtonLabel(PreviousButton, "AIHub_Fluent_PreviousFinding".GetLocalizedOrFallback("Previous finding"));
        SetButtonLabel(NextButton, "AIHub_Fluent_NextFinding".GetLocalizedOrFallback("Next finding"));
    }

    public void SetFinding(AuditIssueEnhanced finding, string range, int index, int count, bool canAnalyze, string analysisHint)
    {
        ArgumentNullException.ThrowIfNull(finding);
        bool changedFinding = !ReferenceEquals(_finding, finding);
        _finding = finding;

        FindingTitleText.Text = finding.DisplayTitle;
        DescriptionText.Text = finding.EvidenceSummaryText;
        SeverityText.Text = finding.SeverityText;
        SeverityBadge.Style = (Style)Resources[finding.IsHigh ? "HighSeverityBadgeStyle" : finding.IsMedium ? "MediumSeverityBadgeStyle" : "LowSeverityBadgeStyle"];
        CategoryText.Text = finding.CategoryLabel;
        OccurrencesBadge.Visibility = finding.HasOccurrences ? Visibility.Visible : Visibility.Collapsed;
        OccurrencesText.Text = finding.OccurrencesLabel;
        EventIdText.Text = Available(finding.EventId);
        SourceText.Text = Available(finding.Source);
        TimestampText.Text = Available(finding.EventTimestampText);
        RangeText.Text = Available(range);
        ReasonText.Text = finding.PriorityReasonText;
        ReasonSection.Visibility = string.IsNullOrWhiteSpace(finding.PriorityReasonText)
            || string.Equals(finding.PriorityReasonText, finding.EvidenceSummaryText, StringComparison.Ordinal)
            ? Visibility.Collapsed : Visibility.Visible;
        RecommendationText.Text = finding.PriorityActionText;
        EvidenceText.Text = BuildEvidence(finding);
        PositionText.Text = index >= 0
            ? "AIHub_Fluent_FindingPosition".FormatLocalized("{0:N0} of {1:N0} matching findings", index + 1, count)
            : "AIHub_Fluent_RelatedFinding".GetLocalizedOrFallback("Related finding");
        PreviousButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index >= 0 && index < count - 1;
        SetActionState(canAnalyze, analysisHint);

        if (changedFinding)
        {
            EvidenceExpander.IsExpanded = false;
            DetailsScroll.ChangeView(null, 0, null, disableAnimation: true);
            CopyFeedbackText.Visibility = Visibility.Collapsed;
        }
    }

    public void SetActionState(bool canAnalyze, string hint)
    {
        AnalyzeButton.IsEnabled = canAnalyze;
        ActionHintText.Text = hint;
        ActionHintText.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(AnalyzeButton, hint);
    }

    public void FocusCloseButton() => CloseDetailsButton.Focus(FocusState.Programmatic);

    private static string Available(string value) => string.IsNullOrWhiteSpace(value)
        ? "FindingDetails_AffectedNotAvailable".GetLocalizedOrFallback("N/A") : value;

    private static void SetButtonLabel(Button button, string label)
    {
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
    }

    private static string BuildEvidence(AuditIssueEnhanced finding)
    {
        var evidence = new StringBuilder();
        AppendEvidence(evidence, "AIHub_Fluent_EventId".GetLocalizedOrFallback("Event ID"), finding.EventId);
        AppendEvidence(evidence, "AIHub_Fluent_EventSource".GetLocalizedOrFallback("Source"), finding.Source);
        AppendEvidence(evidence, "AIHub_Fluent_EventChannel".GetLocalizedOrFallback("Channel"), finding.LogName);
        AppendEvidence(evidence, "AIHub_Fluent_EventTime".GetLocalizedOrFallback("Timestamp"), finding.EventTimestampText);
        AppendEvidence(evidence, "AIHub_Fluent_Affected".GetLocalizedOrFallback("Affected"), finding.Affected);
        AppendEvidence(evidence, "AIHub_Fluent_IssueKey".GetLocalizedOrFallback("Issue key"), finding.Key);

        var references = (finding.RelatedEventRefs ?? Enumerable.Empty<string>())
            .Prepend(finding.EventRef)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (references.Length > 0)
        {
            evidence.AppendLine();
            evidence.AppendLine("AIHub_Fluent_EventReferences".GetLocalizedOrFallback("Event references"));
            foreach (var reference in references)
            {
                evidence.AppendLine(reference);
            }
        }

        return evidence.Length > 0 ? evidence.ToString().TrimEnd() : Available(null);
    }

    private static void AppendEvidence(StringBuilder builder, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"{label}: {value}");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnPreviousClick(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke(this, EventArgs.Empty);

    private void OnNextClick(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);

    private void OnAnalyzeClick(object sender, RoutedEventArgs e) => AnalyzeRequested?.Invoke(this, EventArgs.Empty);

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_finding == null)
        {
            return;
        }

        try
        {
            var summary = new StringBuilder();
            summary.AppendLine("FindingDetails_CopyHeader".FormatLocalized("[Security Finding] {0}", _finding.DisplayTitle));
            summary.AppendLine("FindingDetails_CopySeverity".FormatLocalized("Severity: {0} | Category: {1} | Event ID: {2}", _finding.SeverityText, _finding.CategoryLabel, _finding.EventId));
            summary.AppendLine("FindingDetails_CopyTimestamp".FormatLocalized("Timestamp: {0}", _finding.EventTimestampText));
            summary.AppendLine("FindingDetails_CopyDetection".FormatLocalized("Detection: {0}", _finding.EvidenceSummaryText));
            summary.AppendLine("FindingDetails_CopyRootCause".FormatLocalized("Root Cause: {0}", _finding.PriorityReasonText));
            summary.AppendLine("FindingDetails_CopyAction".FormatLocalized("Recommended Action: {0}", _finding.PriorityActionText));
            summary.AppendLine();
            summary.AppendLine(BuildEvidence(_finding));
            var data = new DataPackage();
            data.SetText(summary.ToString());
            Clipboard.SetContent(data);
            CopyFeedbackText.Text = "AIHub_ReportCopied".GetLocalizedOrFallback("Audit report copied to clipboard.");
        }
        catch (Exception ex)
        {
            CopyFeedbackText.Text = "AIHub_CopyReportFailed".FormatLocalized("Copy failed: {0}", ex.Message);
        }

        CopyFeedbackText.Visibility = Visibility.Visible;
    }
}
