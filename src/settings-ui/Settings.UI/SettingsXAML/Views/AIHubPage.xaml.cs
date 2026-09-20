// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Kit.AIHubLib.Models;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

        DataContext = ViewModel;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public Visibility IsAuditTab(int index) => index == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IsOptTab(int index) => index == 1 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IsAiServicesTab(int index) => index == 2 ? Visibility.Visible : Visibility.Collapsed;

    public void RefreshEnabledState()
    {
        ViewModel.RefreshEnabledState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshEnabledState();
        ApplyStoredApiKeys();
    }

    private async void OnFindingClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AuditIssueEnhanced issue })
        {
            var dialog = new FindingDetailsDialog
            {
                XamlRoot = this.XamlRoot,
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
            };
            dialog.SetFinding(issue);
            await dialog.ShowAsync();
        }
        else if (ViewModel.AuditPriorityFinding != null)
        {
            var dialog = new FindingDetailsDialog
            {
                XamlRoot = this.XamlRoot,
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
    /// Pushes the stored API keys into the password boxes. PasswordBox.Password cannot be
    /// two-way bound safely, so the value is applied when the page loads.
    /// </summary>
    private void ApplyStoredApiKeys()
    {
        if (ViewModel?.AiHub?.MainEndpoint is { } main)
        {
            AiMainApiKeyBox.Password = main.ApiKey ?? string.Empty;
        }

        if (ViewModel?.AiHub?.FallbackEndpoint is { } fallback)
        {
            AiFallbackApiKeyBox.Password = fallback.ApiKey ?? string.Empty;
        }
    }

    private void AiMainApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && ViewModel?.AiHub?.MainEndpoint is { } endpoint)
        {
            endpoint.ApiKey = box.Password;
        }
    }

    private void AiFallbackApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && ViewModel?.AiHub?.FallbackEndpoint is { } endpoint)
        {
            endpoint.ApiKey = box.Password;
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
}
