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
            sb.AppendLine("AIHub_CopyReportTitle".FormatLocalized("# {0} - AI Diagnostic Report", ViewModel.SecurityAuditTitle));
            sb.AppendLine("AIHub_CopyReportGeneratedAt".FormatLocalized("Generated: {0}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)));
            if (!string.IsNullOrWhiteSpace(ViewModel.AiReportMetaText))
            {
                sb.AppendLine("AIHub_CopyReportEnvironment".FormatLocalized("Environment: {0}", ViewModel.AiReportMetaText));
            }

            if (!string.IsNullOrWhiteSpace(ViewModel.AiReportSummaryText))
            {
                sb.AppendLine();
                sb.AppendLine("AIHub_CopyReportSummaryHeading".GetLocalizedOrFallback("## Summary"));
                sb.AppendLine(ViewModel.AiReportSummaryText);
            }

            if (ViewModel.AiReportFindings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("AIHub_CopyReportFindingsHeading".FormatLocalized("## Diagnostic Findings and Recommendations ({0} items)", ViewModel.AiReportFindings.Count));
                int index = 1;
                foreach (var finding in ViewModel.AiReportFindings)
                {
                    sb.AppendLine();
                    sb.AppendLine(CultureInfo.InvariantCulture, $"### {index}. {finding.Title} [{finding.SeverityLabel}]");
                    if (!string.IsNullOrWhiteSpace(finding.EventId))
                    {
                        sb.AppendLine("AIHub_CopyReportEventId".FormatLocalized("- Event ID: {0}", finding.EventId));
                    }

                    if (!string.IsNullOrWhiteSpace(finding.Description))
                    {
                        sb.AppendLine("AIHub_CopyReportDetails".FormatLocalized("- Details: {0}", finding.Description));
                    }

                    if (!string.IsNullOrWhiteSpace(finding.Recommendation))
                    {
                        sb.AppendLine("AIHub_CopyReportRecommendation".FormatLocalized("- Recommendation: {0}", finding.Recommendation));
                    }

                    index++;
                }
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(sb.ToString());
            Clipboard.SetContent(dataPackage);

            ViewModel.PostStatusNotification("AIHub_CopyReportSuccess".GetLocalizedOrFallback("AI diagnostic report copied to clipboard."), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ViewModel.PostStatusNotification("AIHub_CopyReportFailed".FormatLocalized("Copy failed: {0}", ex.Message), InfoBarSeverity.Error);
        }
    }

    private void OnCancelCleanFlyoutClick(object sender, RoutedEventArgs e)
    {
        DependencyObject current = sender as DependencyObject;
        while (current != null)
        {
            if (current is FlyoutPresenter && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current) is Microsoft.UI.Xaml.Controls.Primitives.Popup popup)
            {
                popup.IsOpen = false;
                return;
            }

            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
    }

    public string FormatFilterCount(string label, int count) => $"{label} ({count})";
}
