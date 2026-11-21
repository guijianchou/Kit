// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.Views;

public sealed partial class WingetActivityDialog : ContentDialog
{
    public WingetActivityDialog(WingetViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        InitializeComponent();

        Title = WingetViewModel.L("Activity/Header");
        CloseButtonText = WingetViewModel.L("CancelText");

        QueueHeadingText.Text = WingetViewModel.L("QueueTitle");
        HistoryHeadingText.Text = WingetViewModel.L("HistoryTitle");
        HistoryRetentionComboBox.Header = WingetViewModel.L("HistoryRetention");
        HistoryRetentionComboBox.ItemsSource = new[]
        {
            WingetViewModel.L("HistoryOlder7"),
            WingetViewModel.L("HistoryOlder30"),
            WingetViewModel.L("HistoryAllCompleted"),
        };
        HistoryCleanupHintText.Text = WingetViewModel.L("HistoryCleanupHint");

        string clearLabel = WingetViewModel.L("ClearHistory");
        ClearHistoryButton.Content = clearLabel;
        AutomationProperties.SetName(ClearHistoryButton, clearLabel);

        string searchPlaceholder = WingetViewModel.L("HistorySearch");
        HistorySearchBox.PlaceholderText = searchPlaceholder;
        AutomationProperties.SetName(HistorySearchBox, searchPlaceholder);

        HistoryAttentionCheckBox.Content = WingetViewModel.L("HistoryAttentionOnly");
    }

    public WingetViewModel ViewModel { get; }

    private async void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ClearHistoryAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ReportActionError(ex);
        }
    }
}
