// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;
using System.Text;
using Kit.AiHub.Contract;
using Kit.AIHubLib.Models;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Kit.Settings.UI.Views;

public sealed partial class AIHubPage : NavigablePage, IRefreshablePage
{
    public AIHubPageViewModel ViewModel { get; }

    public AIHubPage()
    {
        var settingsUtils = SettingsUtils.Default;
        var generalSettingsRepository = SettingsRepository<GeneralSettings>.GetInstance(settingsUtils);
        var moduleSettingsRepository = SettingsRepository<AIHubSettings>.GetInstance(settingsUtils);

        ViewModel = new AIHubPageViewModel(
            generalSettingsRepository,
            moduleSettingsRepository,
            ShellPage.SendDefaultIPCMessage);

        ViewModel.RequestJumpToAiReport += OnRequestJumpToAiReport;

        DataContext = ViewModel;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public Visibility IsAuditTab(int index) => index == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IsOptTab(int index) => index == 1 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IsTaskPoliciesTab(int index) => index == 2 ? Visibility.Visible : Visibility.Collapsed;

    public Style AiReadinessStyle(AiReadinessLevel level) => (Style)Resources[level switch
    {
        AiReadinessLevel.Ready => "ReadinessReadyStyle",
        AiReadinessLevel.Degraded => "ReadinessDegradedStyle",
        AiReadinessLevel.Unverified => "ReadinessUnverifiedStyle",
        AiReadinessLevel.NotConfigured => "ReadinessNotConfiguredStyle",
        _ => "ReadinessDisabledStyle",
    }];

    public Style HealthScoreStyle(int score) => (Style)Resources[score >= 80 ? "HealthGoodStyle" : score >= 60 ? "HealthWarningStyle" : "HealthCriticalStyle"];

    public Style HealthScoreRingStyle(int score) => (Style)Resources[score >= 80 ? "HealthRingGoodStyle" : score >= 60 ? "HealthRingWarningStyle" : "HealthRingCriticalStyle"];

    public double HealthScoreToDouble(int score) => Math.Clamp(score, 0, 100);

    public void RefreshEnabledState()
    {
        ViewModel.RefreshEnabledState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshEnabledState();
    }

    private void OnReadinessSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 640;
        Grid.SetRow(AiReadinessActions, narrow ? 1 : 0);
        Grid.SetColumn(AiReadinessActions, narrow ? 1 : 2);
        Grid.SetColumnSpan(AiReadinessActions, narrow ? 2 : 1);
    }

    private void OnAuditOverviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 760;
        Grid.SetColumnSpan(AuditScoreCard, narrow ? 2 : 1);
        Grid.SetRow(AuditFindingsSummary, narrow ? 1 : 0);
        Grid.SetColumn(AuditFindingsSummary, narrow ? 0 : 1);
        Grid.SetColumnSpan(AuditFindingsSummary, narrow ? 2 : 1);
    }

    private void OnCommandHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var grid = (Grid)sender;
        var title = (FrameworkElement)grid.Children[0];
        var actions = (FrameworkElement)grid.Children[1];
        bool narrow = e.NewSize.Width < 960;
        Grid.SetColumnSpan(title, narrow ? 2 : 1);
        Grid.SetRow(actions, narrow ? 1 : 0);
        Grid.SetColumn(actions, narrow ? 0 : 1);
        Grid.SetColumnSpan(actions, narrow ? 2 : 1);
        actions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    private void OnAiServiceSettingsClick(object sender, RoutedEventArgs e)
    {
        NavigationService.Navigate(typeof(GeneralPage), new NavigationParams("GeneralPageAiHubMasterCard"));
    }

    private async void OnFindingClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AuditIssueEnhanced issue })
        {
            var dialog = new FindingDetailsDialog
            {
                XamlRoot = this.XamlRoot,
                RequestedTheme = ActualTheme,
            };
            dialog.SetFinding(issue);
            await dialog.ShowAsync();
        }
    }

    private async void OnPriorityFindingClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AuditIssueEnhanced issue })
        {
            var dialog = new FindingDetailsDialog
            {
                XamlRoot = this.XamlRoot,
                RequestedTheme = ActualTheme,
            };
            dialog.SetFinding(issue);
            await dialog.ShowAsync();
        }
        else if (ViewModel.AuditPriorityFinding != null)
        {
            var dialog = new FindingDetailsDialog
            {
                XamlRoot = this.XamlRoot,
                RequestedTheme = ActualTheme,
            };
            dialog.SetFinding(ViewModel.AuditPriorityFinding);
            await dialog.ShowAsync();
        }
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            ViewModel.SetSeverityFilter(tag);
        }
    }

    private void OnSourceFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            ViewModel.SetSourceFilter(tag);
        }
    }

    private void OnFindingsViewClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            ViewModel.SetViewMode(tag);
        }
    }

    private void OnAuditDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (args.NewDate.HasValue)
        {
            ViewModel.SelectedAuditDate = args.NewDate.Value;
        }
    }

    /// <summary>
    /// Persists the audit mode selection. Full mode additionally reads the Security and
    /// Firewall channels and requires elevation, so it is rejected when not elevated.
    /// </summary>
    private void OnFullAuditModeToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem item)
        {
            ViewModel.AuditModeIndex = item.IsChecked ? 1 : 0;
        }
    }

    /// <summary>
    /// Forces a live readiness probe. The page shows the cached verdict by default, so this
    /// is how an operator confirms the route after changing an endpoint or credential.
    /// </summary>
    private async void OnRecheckAiReadinessClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshAiReadinessAsync();
    }

    private void OnRequestJumpToAiReport(object sender, EventArgs e) => JumpToAiReport();

    private void OnJumpToAiReportClick(object sender, RoutedEventArgs e) => JumpToAiReport();

    private void JumpToAiReport()
    {
        if (ViewModel.ActiveTabIndex != 0)
        {
            ViewModel.ActiveTabIndex = 0;
        }

        AuditAiReport?.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = true,
            VerticalAlignmentRatio = 0,
        });
    }

    private void OnCopyAiReportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"# {ViewModel.SecurityAuditTitle} - AI 诊断报告");
            sb.AppendLine(CultureInfo.InvariantCulture, $"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            if (!string.IsNullOrWhiteSpace(ViewModel.AiReportMetaText))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"执行环境: {ViewModel.AiReportMetaText}");
            }

            if (!string.IsNullOrWhiteSpace(ViewModel.AiReportSummaryText))
            {
                sb.AppendLine();
                sb.AppendLine("## 摘要");
                sb.AppendLine(ViewModel.AiReportSummaryText);
            }

            if (ViewModel.AiReportFindings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"## 详细诊断发现与处置建议 ({ViewModel.AiReportFindings.Count} 项)");
                int index = 1;
                foreach (var finding in ViewModel.AiReportFindings)
                {
                    sb.AppendLine();
                    sb.AppendLine(CultureInfo.InvariantCulture, $"### {index}. {finding.Title} [{finding.SeverityLabel}]");
                    if (!string.IsNullOrWhiteSpace(finding.EventId))
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"- 标识: {finding.EventId}");
                    }

                    if (!string.IsNullOrWhiteSpace(finding.Description))
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"- 详情: {finding.Description}");
                    }

                    if (!string.IsNullOrWhiteSpace(finding.Recommendation))
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"- 处置建议: {finding.Recommendation}");
                    }

                    index++;
                }
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(sb.ToString());
            Clipboard.SetContent(dataPackage);

            ViewModel.PostStatusNotification(AIHubPageViewModel.IsChinese ? "AI 诊断报告已成功复制到剪贴板。" : "AI diagnostic report copied to clipboard.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ViewModel.PostStatusNotification((AIHubPageViewModel.IsChinese ? "复制失败: " : "Copy failed: ") + ex.Message, InfoBarSeverity.Error);
        }
    }
}
