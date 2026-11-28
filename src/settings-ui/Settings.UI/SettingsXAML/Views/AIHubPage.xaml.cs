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

public sealed partial class AIHubPage : NavigablePage, IRefreshablePage, ILiveLocalizable
{
    private readonly MainWindow _settingsWindow;
    private readonly double[] _tabOffsets = new double[3];
    private int _visibleTab;
    private int _scrollRestoreVersion;
    private bool _disposed;
    private bool _currentPage;
    private bool _updatingTabSelection;
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

    public Visibility TaskRunningVisibility(bool scanning, bool analyzing) => scanning || analyzing ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AuditResultStatusVisibility(bool visible, bool scanning, bool analyzing) => visible && !scanning && !analyzing ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowAiSummaryFooter(bool hasReport, bool hasMetadata) => hasReport || hasMetadata ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowOptimizationNoMatches(bool hasResults, bool hasMatches) => hasResults && !hasMatches ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HasSelectedRun(OptimizationRunViewModel run) => run != null ? Visibility.Visible : Visibility.Collapsed;

    public static string ExecutionItemSummary(string action, string state) => OptimizationExecutionItemViewModel.DescribeState(action, state);

    public Style AiReadinessStyle(AiReadinessLevel level) => (Style)Resources[level switch
    {
        AiReadinessLevel.Ready => "ReadinessReadyStyle",
        AiReadinessLevel.Degraded => "ReadinessDegradedStyle",
        AiReadinessLevel.Unverified => "ReadinessUnverifiedStyle",
        AiReadinessLevel.NotConfigured => "ReadinessNotConfiguredStyle",
        _ => "ReadinessDisabledStyle",
    }];

    public Style HealthScoreRingStyle(int score) => (Style)Resources[score >= 80 ? "HealthRingGoodStyle" : score >= 60 ? "HealthRingWarningStyle" : "HealthRingCriticalStyle"];

    public double HealthScoreToDouble(int score) => Math.Clamp(score, 0, 100);

    public void RefreshLocalization() => Bindings.Update();

    public void RefreshEnabledState()
    {
        ViewModel.RefreshEnabledState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshEnabledState();
        UpdateActiveTabContent();
        RestoreTabScroll();
    }

    private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AIHubPageViewModel.ActiveTabIndex) || _disposed)
        {
            return;
        }

        _tabOffsets[_visibleTab] = AiHubContentScroll.VerticalOffset;
        _visibleTab = Math.Clamp(ViewModel.ActiveTabIndex, 0, 2);
        UpdateActiveTabContent();
        RestoreTabScroll();
    }

    private void OnAiHubSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Segmented can have no selected container while its template is being built.
        // Loaded publishes the persisted model choice after that initialization.
        if (_disposed || _updatingTabSelection || !IsLoaded)
        {
            return;
        }

        int selectedTab = AiHubSections.SelectedIndex;
        if (selectedTab is >= 0 and <= 2)
        {
            ViewModel.ActiveTabIndex = selectedTab;
        }

        UpdateActiveTabContent();
    }

    private void UpdateActiveTabContent()
    {
        int activeTab = ViewModel.ActiveTabIndex;
        _updatingTabSelection = true;
        try
        {
            AiHubSections.SelectedIndex = activeTab;
            AuditPanel.Visibility = activeTab == 0 ? Visibility.Visible : Visibility.Collapsed;
            OptimizationPanel.Visibility = activeTab == 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _updatingTabSelection = false;
        }

        if (activeTab == 2)
        {
            // General edits the global draft directly and never changes this selection.
            // On the first visit, select a task before connecting the deferred bindings.
            if (ViewModel.AiHub.ActivePolicyIndex == 0)
            {
                ViewModel.AiHub.TaskPolicyIndex = 0;
            }

            // Materialize the deferred editor before making it visible, using the
            // same model choice that controls the selector and the other panels.
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

    private void OnShowOptimizationHistoryClick(object sender, RoutedEventArgs e)
    {
        OptimizationHistoryExpander.IsExpanded = true;
        AiHubContentScroll.UpdateLayout();
        OptimizationHistoryExpander.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Tag: string tag } pill)
        {
            ViewModel.SetSeverityFilter(tag);

            // Severity pills act as a radio group; re-clicking the active pill must not visually uncheck it.
            pill.IsChecked = tag switch
            {
                "High" => ViewModel.IsFilterHigh,
                "Medium" => ViewModel.IsFilterMedium,
                "Low" => ViewModel.IsFilterLow,
                _ => ViewModel.IsFilterAll,
            };
        }
    }

    private void OnSourceFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Tag: string tag } pill)
        {
            ViewModel.SetSourceFilter(tag);

            // Source pills act as a radio group; re-clicking the active pill must not visually uncheck it.
            pill.IsChecked = tag switch
            {
                "Application" => ViewModel.IsSourceApplication,
                "Security" => ViewModel.IsSourceSecurity,
                "System" => ViewModel.IsSourceSystem,
                "Setup" => ViewModel.IsSourceSetup,
                "ForwardedEvents" => ViewModel.IsSourceForwarded,
                _ => ViewModel.IsSourceAll,
            };
        }
    }

    private void OnAuditDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (args.NewDate.HasValue)
        {
            ViewModel.SelectedAuditDate = args.NewDate.Value;
        }
    }

    private void OnTaskPolicyEditorModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AiTaskPolicyContentBox == null || AiTaskPolicyPreviewHost == null || sender is not CommunityToolkit.WinUI.Controls.Segmented segmented)
        {
            return;
        }

        bool isPreview = segmented.SelectedIndex == 1;
        AiTaskPolicyContentBox.Visibility = isPreview ? Visibility.Collapsed : Visibility.Visible;
        AiTaskPolicyPreviewHost.Visibility = isPreview ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Forces a live readiness probe. The page shows the cached verdict by default, so this
    /// is how an operator confirms the route after changing an endpoint or credential.
    /// </summary>
    private async void OnRecheckAiReadinessClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RefreshAiReadinessAsync();
        }
        catch (System.Exception ex)
        {
            ManagedCommon.Logger.LogError("Settings action OnRecheckAiReadinessClick failed.", ex);
        }
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
            Content = new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = TypographyResources.FontSize(12) },
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
}
