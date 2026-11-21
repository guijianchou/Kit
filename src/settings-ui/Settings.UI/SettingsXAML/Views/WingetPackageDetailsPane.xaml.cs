// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Kit.Settings.UI.Views;

public sealed partial class WingetPackageDetailsPane : UserControl
{
    private WingetPackageRow _boundRow;
    private string _appliedVersion;
    private string _manualCommand;

    public event EventHandler CloseRequested;

    public event EventHandler CancelRequested;

    public event EventHandler RetryRequested;

    public event EventHandler<string> ApplyVersionRequested;

    public WingetPackageDetailsPane()
    {
        InitializeComponent();
        PaneEyebrowText.Text = WingetViewModel.L("DetailsPaneTitle");
        LoadingLabelText.Text = WingetViewModel.L("Phase_LoadingDetails");
        DetailsCancelButton.Content = WingetViewModel.L("Cancel/Content");
        DetailsRetryButton.Content = WingetViewModel.L("RetryDetails");
        VersionChoiceBox.Header = WingetViewModel.L("ChooseVersion");
        ApplyVersionButton.Content = WingetViewModel.L("ApplyVersion");
        VersionExplanationText.Text = WingetViewModel.L("VersionExplanation");
        HomepageLink.Content = WingetViewModel.L("PackageHomepage");
        LicenseLink.Content = WingetViewModel.L("LicenseLink");
        ReleaseNotesHeading.Text = WingetViewModel.L("ReleaseNotesExpander/Header");
        ReleaseNotesLink.Content = WingetViewModel.L("ReleaseLink/Content");
        CopyCommandLabel.Text = WingetViewModel.L("CopyManualCommandAction");

        string closeLabel = WingetViewModel.L("CloseDetails");
        AutomationProperties.SetName(CloseDetailsButton, closeLabel);
        ToolTipService.SetToolTip(CloseDetailsButton, closeLabel);

        string copyIdLabel = WingetViewModel.L("CopyIdAction");
        AutomationProperties.SetName(CopyIdButton, copyIdLabel);
        ToolTipService.SetToolTip(CopyIdButton, copyIdLabel);
    }

    public void UpdateFromViewModel(WingetViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        var selected = vm.ActiveSelection;
        if (selected == null)
        {
            return;
        }

        bool changedRow = !ReferenceEquals(_boundRow, selected);
        _boundRow = selected;
        if (changedRow)
        {
            _appliedVersion = null;
            FeedbackText.Visibility = Visibility.Collapsed;
            DetailsScroll.ChangeView(null, 0, null, disableAnimation: true);
        }

        PackageTitleText.Text = selected.Name;
        IdentityText.Text = vm.SelectionIdentity;

        DetailsLoadingPanel.Visibility = vm.IsLoadingDetails ? Visibility.Visible : Visibility.Collapsed;
        DetailsProgressRing.IsActive = vm.IsLoadingDetails;
        DetailsCancelButton.IsEnabled = vm.CanCancelOperation;

        bool hasError = vm.HasDetailsError;
        DetailsErrorContainer.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        DetailsErrorPanel.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        DetailsErrorText.Text = vm.DetailsErrorText;
        DetailsRetryButton.IsEnabled = vm.CanRetryDetails;

        DetailsContentPanel.Visibility = vm.HasDetails ? Visibility.Visible : Visibility.Collapsed;
        VersionsText.Text = vm.SelectionVersions;
        PublisherText.Text = vm.SelectionPublisher;
        PublisherText.Visibility = string.IsNullOrWhiteSpace(vm.SelectionPublisher) ? Visibility.Collapsed : Visibility.Visible;
        DescriptionText.Text = vm.PackageDescription;

        var availableVersions = vm.SelectedDetails?.AvailableVersions;
        bool showVersions = vm.ActiveTabIndex != (int)WingetWorkspace.Installed && availableVersions is { Count: > 0 };
        VersionOptionsPanel.Visibility = showVersions ? Visibility.Visible : Visibility.Collapsed;
        if (showVersions)
        {
            string currentVersion = vm.SelectedDetails?.Package.AvailableVersion;
            if (!VersionChoiceBox.Items.Cast<string>().SequenceEqual(availableVersions, StringComparer.Ordinal)
                || _appliedVersion != currentVersion)
            {
                VersionChoiceBox.ItemsSource = availableVersions;
                VersionChoiceBox.SelectedItem = currentVersion;
                _appliedVersion = currentVersion;
            }
        }

        ApplyVersionButton.IsEnabled = vm.CanActOnPackage;
        InstallExplanationText.Text = vm.InstallExplanation;

        HomepageLink.NavigateUri = WingetViewModel.SafeWebUri(vm.SelectedDetails?.PackageUrl);
        HomepageLink.Visibility = HomepageLink.NavigateUri != null ? Visibility.Visible : Visibility.Collapsed;

        string license = string.IsNullOrWhiteSpace(vm.SelectedDetails?.License)
            ? string.Empty
            : WingetViewModel.L("LicenseLabel") + ": " + vm.SelectedDetails.License;
        LicenseText.Text = license;
        LicenseText.Visibility = string.IsNullOrEmpty(license) ? Visibility.Collapsed : Visibility.Visible;

        LicenseLink.NavigateUri = WingetViewModel.SafeWebUri(vm.SelectedDetails?.LicenseUrl);
        LicenseLink.Visibility = LicenseLink.NavigateUri != null ? Visibility.Visible : Visibility.Collapsed;

        MetadataLinksPanel.Visibility = HomepageLink.Visibility == Visibility.Visible
            || LicenseText.Visibility == Visibility.Visible
            || LicenseLink.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;

        string notes = vm.ReleaseNotes;
        if (!string.Equals(ReleaseNotesMarkdown.Text, notes, StringComparison.Ordinal))
        {
            ReleaseNotesMarkdown.Text = notes;
        }

        ReleaseNotesLink.NavigateUri = WingetViewModel.SafeWebUri(vm.SelectedDetails?.Package.ReleaseNotesUrl);
        ReleaseNotesLink.Visibility = ReleaseNotesLink.NavigateUri != null ? Visibility.Visible : Visibility.Collapsed;

        _manualCommand = vm.ManualCommand;
        CopyCommandButton.Visibility = !string.IsNullOrWhiteSpace(_manualCommand) ? Visibility.Visible : Visibility.Collapsed;
    }

    public void FocusCloseButton() => CloseDetailsButton.Focus(FocusState.Programmatic);

    public void ClearBoundRow()
    {
        _boundRow = null;
        _appliedVersion = null;
        DetailsProgressRing.IsActive = false;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private void OnRetryClick(object sender, RoutedEventArgs e) => RetryRequested?.Invoke(this, EventArgs.Empty);

    private void OnApplyVersionClick(object sender, RoutedEventArgs e)
    {
        if (VersionChoiceBox.SelectedItem is string version)
        {
            ApplyVersionRequested?.Invoke(this, version);
        }
    }

    private void OnCopyIdClick(object sender, RoutedEventArgs e)
    {
        if (_boundRow == null)
        {
            return;
        }

        CopyToClipboard(_boundRow.Id);
    }

    private void OnCopyCommandClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_manualCommand))
        {
            return;
        }

        CopyToClipboard(_manualCommand);
    }

    private void CopyToClipboard(string text)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(text);
            Clipboard.SetContent(data);
            FeedbackText.Text = WingetViewModel.L("CopiedToClipboard");
            FeedbackText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            FeedbackText.Text = ex.Message;
            FeedbackText.Visibility = Visibility.Visible;
        }
    }
}
