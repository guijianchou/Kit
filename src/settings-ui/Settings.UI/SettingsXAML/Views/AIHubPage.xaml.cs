// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AIHubLib.Models;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Kit.Settings.UI.Views;

public sealed partial class AIHubPage : NavigablePage, IRefreshablePage
{
    private readonly MainWindow _settingsWindow;
    private readonly double[] _tabOffsets = new double[3];
    private int _visibleTab;
    private int _scrollRestoreVersion;
    private bool _disposed;
    private bool _currentPage;
    private ContentDialog _activeDialog;

    public AIHubPageViewModel ViewModel { get; }

    public string ModuleDescription => "AIHub/ModuleDescription".GetLocalized();

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
        InitializeFluentDetails();
        _visibleTab = Math.Clamp(ViewModel.ActiveTabIndex, 0, 2);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.RequestOptimizationConfirmation += ConfirmOptimizationAsync;
        _settingsWindow = App.GetSettingsWindow();
        _settingsWindow.Closed += OnWindowClosed;
        Loaded += OnLoaded;
    }

    public Visibility IsAuditTab(int index) => index == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IsOptTab(int index) => index == 1 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TaskRunningVisibility(bool scanning, bool analyzing) => scanning || analyzing ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowOptimizationNoMatches(bool hasResults, bool hasMatches) => hasResults && !hasMatches ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HasSelectedRun(OptimizationRunViewModel run) => run != null ? Visibility.Visible : Visibility.Collapsed;

    public static string ExecutionItemSummary(string action, string state) => state switch
    {
        "deleted" => "AIHub_ExecutionItem_Deleted".GetLocalizedOrFallback("Deleted"),
        "moved" => "AIHub_ExecutionItem_Moved".GetLocalizedOrFallback("Moved"),
        "succeeded" or "completed" => action == "move" ? "AIHub_ExecutionItem_Moved".GetLocalizedOrFallback("Moved") : "AIHub_ExecutionItem_Deleted".GetLocalizedOrFallback("Deleted"),
        "failed" => "AIHub_ExecutionItem_Failed".GetLocalizedOrFallback("Failed"),
        _ => "AIHub_ExecutionItem_Pending".GetLocalizedOrFallback("Not completed"),
    };

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
        UpdateHeaderLayout();
        ViewModel.RefreshEnabledState();
        UpdateTaskPolicyPanel();
        RestoreTabScroll();
    }

    private void OnHeaderLayoutChanged(object sender, SizeChangedEventArgs e) => UpdateHeaderLayout();

    private void UpdateHeaderLayout()
    {
        if (AiHubIntroScroll == null || AiHubHeaderTail == null || AIHubRoot.ActualHeight <= 0)
        {
            return;
        }

        // Keep readiness, tab navigation and at least 80 DIPs of tab content
        // available. The introduction and module gate remain scrollable and
        // keyboard reachable when the window cannot show the whole header.
        const double minimumContentHeight = 80;
        const double roundingAllowance = 2;
        AiHubIntroScroll.MaxHeight = Math.Max(64, Math.Floor(
            AIHubRoot.ActualHeight - AiHubPageTitle.ActualHeight - AiHubHeaderTail.ActualHeight
            - 36 - minimumContentHeight - roundingAllowance));
    }

    private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AIHubPageViewModel.ActiveTabIndex) || _disposed)
        {
            return;
        }

        _tabOffsets[_visibleTab] = AiHubContentScroll.VerticalOffset;
        _visibleTab = Math.Clamp(ViewModel.ActiveTabIndex, 0, 2);
        UpdateTaskPolicyPanel();
        RestoreTabScroll();
    }

    private void UpdateTaskPolicyPanel()
    {
        if (ViewModel.ActiveTabIndex == 2)
        {
            // Materialize the deferred editor before making it visible. Keep these two
            // steps together instead of relying on a function binding's cached value
            // while ActiveTabIndex notifications are still being dispatched.
            var panel = (FrameworkElement)FindName(nameof(TaskPolicyPanel));
            panel.Visibility = Visibility.Visible;
            ViewModel.AiHub.EnsurePoliciesLoaded();
        }
        else if (TaskPolicyPanel != null)
        {
            TaskPolicyPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnTaskPolicyContentLoaded(object sender, RoutedEventArgs e)
    {
        // Deferred x:Bind assigns its cached Text during Connect, before the XAML
        // AcceptsReturn value is applied. Reapply the current draft after the editor
        // is initialized so a policy loaded before the first visit keeps every line.
        // PolicyDraft treats the TextBox's normalized line endings as the same content.
        if (sender is TextBox editor)
        {
            editor.Text = ViewModel.AiHub.CurrentPolicyContent;
        }
    }

    private void RestoreTabScroll()
    {
        int version = ++_scrollRestoreVersion;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_disposed || !IsLoaded || version != _scrollRestoreVersion)
            {
                return;
            }

            AiHubContentScroll.UpdateLayout();
            AiHubContentScroll.ChangeView(null, _tabOffsets[_visibleTab], null, disableAnimation: true);
        });
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _currentPage = true;
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        _currentPage = false;
        _tabOffsets[_visibleTab] = AiHubContentScroll.VerticalOffset;
        _activeDialog?.Hide();
        CloseFluentDetails(restoreFocus: false);
        base.OnNavigatedFrom(e);
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        if (e.Handled || _disposed)
        {
            return;
        }

        _disposed = true;
        _currentPage = false;
        _activeDialog?.Hide();
        DisposeFluentDetails();
        _settingsWindow.Closed -= OnWindowClosed;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.RequestJumpToAiReport -= OnRequestJumpToAiReport;
        ViewModel.RequestOptimizationConfirmation -= ConfirmOptimizationAsync;
        ViewModel.Dispose();
    }

    private void OnAiServiceSettingsClick(object sender, RoutedEventArgs e)
    {
        NavigationService.Navigate(typeof(GeneralPage), new NavigationParams("GeneralPageAiHubMasterCard"));
    }

    private void OnFindingClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AuditIssueEnhanced issue } trigger)
        {
            OpenFinding(issue, trigger);
        }
    }

    private void OnPriorityFindingClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AuditIssueEnhanced issue } trigger)
        {
            OpenFinding(issue, trigger);
        }
        else if (ViewModel.AuditPriorityFinding != null)
        {
            OpenFinding(ViewModel.AuditPriorityFinding, sender as FrameworkElement);
        }
    }

    private void OnScanButtonIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        // WinUI can leave the outer SplitButton in its previous state while the inner
        // buttons follow IsEnabled. Keep both surfaces aligned with the audit gate.
        if (sender is SplitButton button)
        {
            VisualStateManager.GoToState(button, button.IsEnabled ? "Normal" : "Disabled", useTransitions: false);
        }
    }

    private void OnSelectedScanClick(SplitButton sender, SplitButtonClickEventArgs args)
    {
        sender.Flyout?.Hide();
        if (ViewModel.StartSelectedScanCommand.CanExecute(null))
        {
            ViewModel.StartSelectedScanCommand.Execute(null);
        }
    }

    private void OnFullAuditCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox check)
        {
            ViewModel.AuditModeIndex = check.IsChecked == true ? 1 : 0;
            check.IsChecked = ViewModel.IsFullAuditMode;
        }
    }

    private void OnFindingSearchToggleClick(object sender, RoutedEventArgs e)
    {
        bool show = FindingSearchToggle.IsChecked == true;
        FindingSearchPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            FindingSearchBox.Focus(FocusState.Programmatic);
        }
        else
        {
            ViewModel.FindingSearchText = string.Empty;
            ViewModel.SetSourceFilter("All");
        }
    }

    private void OnShowOptimizationHistoryClick(object sender, RoutedEventArgs e)
    {
        OptimizationHistoryExpander.IsExpanded = true;
        AiHubContentScroll.UpdateLayout();
        OptimizationHistoryExpander.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
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

        AuditReportExpander.IsExpanded = true;
        AiHubContentScroll.UpdateLayout();
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

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_disposed || !_currentPage || !IsLoaded || _activeDialog != null)
        {
            return ContentDialogResult.None;
        }

        _activeDialog = dialog;
        try
        {
            dialog.XamlRoot = XamlRoot;
            dialog.RequestedTheme = ActualTheme;
            var result = await dialog.ShowAsync();
            return _disposed || !_currentPage || !IsLoaded ? ContentDialogResult.None : result;
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                ViewModel.PostStatusNotification("AIHub_ActionFailedFormat".FormatLocalized("Action failed: {0}", ex.Message), InfoBarSeverity.Error, optimization: ViewModel.ActiveTabIndex == 1);
            }

            return ContentDialogResult.None;
        }
        finally
        {
            _activeDialog = null;
        }
    }

    private async Task<bool> ConfirmOptimizationAsync(IReadOnlyList<TempFileInfo> items)
    {
        if (items.Count == 0 || _disposed || !_currentPage || !IsLoaded || _activeDialog != null)
        {
            return false;
        }

        const int previewLimit = 100;
        string deleteSize = new TempFileInfo { SizeInBytes = items.Where(item => item.Action == "delete").Sum(item => item.SizeInBytes) }.SizeFormatted;
        string moveSize = new TempFileInfo { SizeInBytes = items.Where(item => item.Action == "move").Sum(item => item.SizeInBytes) }.SizeFormatted;
        var content = new StackPanel { Spacing = 12, MaxWidth = 560 };
        content.Children.Add(new TextBlock
        {
            Text = "AIHub_Fluent_ConfirmSummary".FormatLocalized(
                "{0:N0} selected · {1:N0} delete ({2} to free) · {3:N0} move ({4} to organize)",
                items.Count, items.Count(item => item.Action == "delete"), deleteSize, items.Count(item => item.Action == "move"), moveSize),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = "AIHub_OptimizationConfirmDescription".GetLocalizedOrFallback("Review these file operations before continuing. Deleted cache files cannot be restored from this report."),
            TextWrapping = TextWrapping.Wrap,
        });
        if (items.Count > previewLimit)
        {
            content.Children.Add(new TextBlock
            {
                Text = "AIHub_OptimizationConfirmPreviewLimitFormat".FormatLocalized("Showing the first {0:N0} of {1:N0} items", previewLimit, items.Count),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        string preview = string.Join(Environment.NewLine + Environment.NewLine, items.Take(previewLimit).Select(item =>
        {
            string action = item.Action == "move" ? "AIHub_Action_Organize".GetLocalizedOrFallback("Organize") : "AIHub_Action_Clean".GetLocalizedOrFallback("Clean");
            string destination = item.Action == "move" && !string.IsNullOrWhiteSpace(item.TargetRelativePath)
                ? Environment.NewLine + "→ " + Path.Combine(Path.GetDirectoryName(item.FilePath) ?? string.Empty, item.TargetRelativePath)
                : string.Empty;
            return $"{action} · {item.SizeFormatted}{Environment.NewLine}{item.FilePath}{destination}";
        }));
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 300,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 },
        });

        var dialog = new ContentDialog
        {
            Title = "AIHub_OptimizationConfirmTitle".GetLocalizedOrFallback("Review selected file operations"),
            Content = content,
            PrimaryButtonText = "AIHub_OptimizationConfirmContinue".GetLocalizedOrFallback("Continue"),
            CloseButtonText = "AIHub_OptimizationConfirmCancel".GetLocalizedOrFallback("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private void OnCopyOptimizationReportClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedOptimizationRun is not { } run)
        {
            return;
        }

        try
        {
            var data = new DataPackage();
            data.SetText(run.ExportJson);
            Clipboard.SetContent(data);
            ViewModel.PostStatusNotification("AIHub_OptimizationReportCopied".GetLocalizedOrFallback("Optimization report copied to clipboard."), InfoBarSeverity.Success, optimization: true);
        }
        catch (Exception ex)
        {
            ViewModel.PostStatusNotification("AIHub_ActionFailedFormat".FormatLocalized("Action failed: {0}", ex.Message), InfoBarSeverity.Error, optimization: true);
        }
    }

    public string FormatFilterCount(string label, int count) => $"{label} ({count})";

    private void OnOptFilterPillClick(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Tag: string tag } pill
            && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            ViewModel.OptimizationFilterIndex = index;

            // These pills select one filter; a second click must not visually
            // deselect the active filter while its unchanged value remains applied.
            pill.IsChecked = ViewModel.OptimizationFilterIndex == index;
        }
    }

    private void OnTaskPolicyColumnsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TaskPolicyEditorCard == null || TaskPolicyScopeCard == null)
        {
            return;
        }

        bool narrow = e.NewSize.Width < 720;
        Grid.SetColumnSpan(TaskPolicyEditorCard, narrow ? 2 : 1);
        Grid.SetRow(TaskPolicyScopeCard, narrow ? 1 : 0);
        Grid.SetColumn(TaskPolicyScopeCard, narrow ? 0 : 1);
        Grid.SetColumnSpan(TaskPolicyScopeCard, narrow ? 2 : 1);
    }
}
