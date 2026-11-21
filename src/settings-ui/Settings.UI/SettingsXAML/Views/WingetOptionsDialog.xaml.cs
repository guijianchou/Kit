// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Kit.Settings.UI.ViewModels;
using Kit.WingetLib;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.Views;

public sealed partial class WingetOptionsDialog : ContentDialog
{
    private readonly bool _isInstalledTab;

    public WingetOptionsDialog(WingetViewModel viewModel, bool isInstalledTab)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _isInstalledTab = isInstalledTab;
        InitializeComponent();

        Title = WingetViewModel.L("OptionsTitle");
        PrimaryButtonText = WingetViewModel.L("ApplyOptions");
        CloseButtonText = WingetViewModel.L("CancelText");

        SilentCheckBox.Content = WingetViewModel.L("SilentOption");
        SilentCheckBox.IsChecked = viewModel.SilentInstall;

        ScopeComboBox.Header = WingetViewModel.L("ScopeOption");
        ScopeComboBox.ItemsSource = new[]
        {
            WingetViewModel.L("OptionDefault"),
            WingetViewModel.L("ScopeUser"),
            WingetViewModel.L("ScopeMachine"),
        };
        ScopeComboBox.SelectedIndex = viewModel.InstallScopeIndex;

        ArchitectureComboBox.Header = WingetViewModel.L("ArchitectureOption");
        ArchitectureComboBox.ItemsSource = new[]
        {
            WingetViewModel.L("OptionDefault"),
            "x64",
            "x86",
            "ARM64",
        };
        ArchitectureComboBox.SelectedIndex = viewModel.InstallArchitectureIndex;

        InstallLocationBox.Header = WingetViewModel.L("InstallLocation");
        InstallLocationBox.PlaceholderText = WingetViewModel.L("OptionDefault");
        InstallLocationBox.Text = viewModel.InstallLocation ?? string.Empty;
        InvalidLocationText.Text = WingetViewModel.L("InvalidInstallLocation");
        InstallLocationHintText.Text = WingetViewModel.L("InstallLocationHint");
        OptionsExplanationText.Text = WingetViewModel.L("OptionsExplanation");

        InstallOnlyOptionsPanel.Visibility = isInstalledTab ? Visibility.Collapsed : Visibility.Visible;
        ValidateLocation();
    }

    public bool SilentInstall => SilentCheckBox.IsChecked == true;

    public int ScopeIndex => ScopeComboBox.SelectedIndex;

    public int ArchitectureIndex => ArchitectureComboBox.SelectedIndex;

    public string InstallLocation => InstallLocationBox.Text.Trim();

    private void OnInstallLocationTextChanged(object sender, TextChangedEventArgs e) => ValidateLocation();

    private void ValidateLocation()
    {
        bool valid = _isInstalledTab
            || WingetPackageOptions.IsValidInstallLocation(string.IsNullOrWhiteSpace(InstallLocationBox.Text) ? null : InstallLocationBox.Text.Trim());
        IsPrimaryButtonEnabled = valid;
        InvalidLocationText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
    }
}
