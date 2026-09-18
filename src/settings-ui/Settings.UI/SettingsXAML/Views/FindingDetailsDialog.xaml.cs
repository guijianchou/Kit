// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;
using System.Text;
using Kit.AIHubLib.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Kit.Settings.UI.Views;

public sealed partial class FindingDetailsDialog : ContentDialog
{
    private AuditIssueEnhanced _currentIssue;

    private static bool IsChinese => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public FindingDetailsDialog()
    {
        InitializeComponent();
        ApplyLocalization();
    }

    private void ApplyLocalization()
    {
        Title = IsChinese ? "发现详情" : "Finding Details";
        PrimaryButtonText = IsChinese ? "关闭" : "Close";
        SecondaryButtonText = IsChinese ? "复制摘要" : "Copy Summary";

        FactsHeaderTextBlock.Text = IsChinese ? "检测事实" : "Detection Facts";
        RootCauseHeaderTextBlock.Text = IsChinese ? "触发原因" : "Root Cause";
        RecommendationHeaderTextBlock.Text = IsChinese ? "建议操作" : "Recommended Action";
        EvidenceHeaderTextBlock.Text = IsChinese ? "事件证据" : "Event Evidence";
        TimestampHeaderTextBlock.Text = IsChinese ? "发生时间:" : "Timestamp:";
        AffectedHeaderTextBlock.Text = IsChinese ? "影响范围:" : "Affected:";
        KeyHeaderTextBlock.Text = IsChinese ? "问题标识:" : "Issue Key:";
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

        if (issue.IsHigh)
        {
            SeverityBadge.Background = new SolidColorBrush(Colors.IndianRed);
        }
        else if (issue.IsMedium)
        {
            SeverityBadge.Background = new SolidColorBrush(Colors.DarkOrange);
        }
        else
        {
            SeverityBadge.Background = new SolidColorBrush(Colors.DodgerBlue);
        }

        CategoryTextBlock.Text = issue.CategoryLabel;
        EventIdTextBlock.Text = IsChinese ? $"事件 {issue.EventId}" : $"Event {issue.EventId}";

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
        AffectedTextBlock.Text = string.IsNullOrWhiteSpace(issue.Affected) ? (IsChinese ? "无" : "N/A") : issue.Affected;
        KeyTextBlock.Text = issue.Key;
    }

    private void OnCopySummaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_currentIssue == null)
        {
            return;
        }

        bool isZh = IsChinese;
        var sb = new StringBuilder();
        sb.AppendLine(isZh ? $"[安全发现] {_currentIssue.DisplayTitle}" : $"[Security Finding] {_currentIssue.DisplayTitle}");
        sb.AppendLine(isZh
            ? $"严重度: {_currentIssue.SeverityText} | 分类: {_currentIssue.CategoryLabel} | 事件 ID: {_currentIssue.EventId}"
            : $"Severity: {_currentIssue.SeverityText} | Category: {_currentIssue.CategoryLabel} | Event ID: {_currentIssue.EventId}");
        sb.AppendLine(isZh ? $"时间戳: {_currentIssue.EventTimestampText}" : $"Timestamp: {_currentIssue.EventTimestampText}");
        sb.AppendLine(isZh ? $"检测内容: {_currentIssue.EvidenceSummaryText}" : $"Detection: {_currentIssue.EvidenceSummaryText}");
        sb.AppendLine(isZh ? $"触发原因: {_currentIssue.PriorityReasonText}" : $"Root Cause: {_currentIssue.PriorityReasonText}");
        sb.AppendLine(isZh ? $"建议操作: {_currentIssue.PriorityActionText}" : $"Recommended Action: {_currentIssue.PriorityActionText}");

        var dataPackage = new DataPackage();
        dataPackage.SetText(sb.ToString());
        Clipboard.SetContent(dataPackage);
    }
}
