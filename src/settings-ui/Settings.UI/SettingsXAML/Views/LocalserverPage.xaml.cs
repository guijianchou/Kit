// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Threading.Tasks;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.ViewModels;
using ManagedCommon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Kit.Settings.UI.Views
{
    public sealed partial class LocalserverPage : NavigablePage, IRefreshablePage
    {
        private readonly MainWindow _settingsWindow;
        private bool _confirmationOpen;

        public LocalserverViewModel ViewModel { get; }

        public LocalserverPage()
        {
            InitializeComponent();

            var settingsUtils = SettingsUtils.Default;
            var generalSettingsRepository = SettingsRepository<GeneralSettings>.GetInstance(settingsUtils);
            var moduleSettingsRepository = SettingsRepository<LocalserverSettings>.GetInstance(settingsUtils);

            ViewModel = new LocalserverViewModel(
                generalSettingsRepository,
                moduleSettingsRepository,
                ShellPage.SendDefaultIPCMessage);

            DataContext = ViewModel;

            ViewModel.LogScrollRequested += OnLogScrollRequested;

            _settingsWindow = App.GetSettingsWindow();
            _settingsWindow.FlushPendingSettingsAsync = ViewModel.FlushPendingSaveAsync;
            _settingsWindow.KeepAliveForBackgroundWork = () => ViewModel.HasActiveServices;
            _settingsWindow.Closed += OnSettingsWindowClosed;
            _settingsWindow.Activated += OnSettingsWindowActivated;
            Loaded += OnLocalserverLoaded;
            Unloaded += OnLocalserverUnloaded;
        }

        public static Visibility GetEmptyVisibility(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void OnLocalserverLoaded(object sender, RoutedEventArgs e)
        {
            ViewModel.RefreshEnabledState();
            ViewModel.SetPageActive(true);
        }

        private void OnLocalserverUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.SetPageActive(false);
            _ = ViewModel.FlushPendingSaveAsync();
        }

        private void OnSettingsWindowClosed(object sender, WindowEventArgs args)
        {
            if (args.Handled)
            {
                if (!_settingsWindow.AppWindow.IsVisible)
                {
                    ViewModel.SetPageActive(false);
                }

                return;
            }

            _settingsWindow.Closed -= OnSettingsWindowClosed;
            _settingsWindow.Activated -= OnSettingsWindowActivated;
            if (_settingsWindow.FlushPendingSettingsAsync == ViewModel.FlushPendingSaveAsync)
            {
                _settingsWindow.FlushPendingSettingsAsync = null;
                _settingsWindow.KeepAliveForBackgroundWork = null;
            }

            ViewModel.LogScrollRequested -= OnLogScrollRequested;
            ViewModel.Dispose();
        }

        private void OnSettingsWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated && IsLoaded)
            {
                ViewModel.SetPageActive(true);
            }
        }

        public void Refresh()
        {
            _ = ViewModel.RefreshTelemetryAsync();
        }

        public void RefreshEnabledState()
        {
            ViewModel.RefreshEnabledState();
        }
        private void OnStatusChartSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                ViewModel?.UpdateChartGeometry(e.NewSize.Width, e.NewSize.Height);
            }
        }

        private void OnLogScrollRequested()
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (LogScroller != null && ViewModel.IsLogFollowEnabled)
                {
                    LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null, disableAnimation: true);
                }
            });
        }

        private void ToggleConfigExpanded_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is LineRowViewModel row)
            {
                row.ToggleConfigExpanded();
            }
        }

        private async void OpenServiceUrl_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is LineRowViewModel row && row.OpenUrl != null)
            {
                await global::Windows.System.Launcher.LaunchUriAsync(row.OpenUrl);
            }
        }

        private void ToggleLogPanel_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ToggleLogPanel();
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ClearLogs();
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.OpenSelectedLineLogFolder();
        }

        private async void BrowseExecutable_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is LineRowViewModel row && row.IsEditable)
            {
                try
                {
                    var picker = new FileOpenPicker();
                    picker.FileTypeFilter.Add("*");
                    picker.FileTypeFilter.Add(".exe");
                    picker.FileTypeFilter.Add(".bat");
                    picker.FileTypeFilter.Add(".cmd");
                    picker.FileTypeFilter.Add(".ps1");

                    var handle = WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);

                    var file = await picker.PickSingleFileAsync();
                    if (file != null && row.IsEditable)
                    {
                        row.Executable = file.Path;
                        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        if (string.IsNullOrWhiteSpace(row.Cwd) || row.Cwd == "." || string.Equals(row.Cwd, userProfile, StringComparison.OrdinalIgnoreCase))
                        {
                            row.Cwd = Path.GetDirectoryName(file.Path) ?? string.Empty;
                        }

                        if (string.Equals(row.BaseArgsText, "/c echo Running", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(row.BaseArgsText, "echo Running", StringComparison.OrdinalIgnoreCase))
                        {
                            row.BaseArgsText = string.Empty;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Localserver file picker failed: {ex.GetType().Name}");
                    ViewModel.ReportError("Localserver_FilePickerFailed");
                }
            }
        }

        private async void BrowseCwd_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is LineRowViewModel row && row.IsEditable)
            {
                try
                {
                    var picker = new FolderPicker();
                    picker.FileTypeFilter.Add("*");

                    var handle = WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);

                    var folder = await picker.PickSingleFolderAsync();
                    if (folder != null && row.IsEditable)
                    {
                        row.Cwd = folder.Path;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Localserver folder picker failed: {ex.GetType().Name}");
                    ViewModel.ReportError("Localserver_FilePickerFailed");
                }
            }
        }

        private void StartAll_Click(object sender, RoutedEventArgs e)
        {
            _ = ViewModel.StartAllLinesAsync();
        }

        private void StopAll_Click(object sender, RoutedEventArgs e)
        {
            _ = ViewModel.StopAllLinesAsync();
        }

        private void RefreshTelemetry_Click(object sender, RoutedEventArgs e)
        {
            _ = ViewModel.RefreshTelemetryAsync();
        }

        private void AddLine_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.AddNewLine();
        }

        private void ReloadCatalog_Click(object sender, RoutedEventArgs e)
        {
            _ = ViewModel.LoadLinesAsync();
        }

        private void SaveCatalog_Click(object sender, RoutedEventArgs e)
        {
            _ = ViewModel.SaveLinesAsync(markDirty: false, showSuccess: true);
        }

        private void RestartLine_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is LineRowViewModel row)
            {
                _ = ViewModel.RestartLineAsync(row);
            }
        }

        private async void ForceKillLine_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is LineRowViewModel row && row.CanForceKill)
            {
                string message = LocalserverViewModel.FormatMessage(
                    "Localserver_ForceKillConfirmMessage",
                    "Force stop {0}? Unsaved data may be lost.",
                    row.Name);
                if (await ConfirmTerminationAsync("Localserver_ForceKillTitle".GetLocalized(), message))
                {
                    await ViewModel.ForceKillLineAsync(row);
                }
            }
        }

        private void MoveLineUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is LineRowViewModel row)
            {
                ViewModel.MoveLineUp(row);
            }
        }

        private void MoveLineDown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is LineRowViewModel row)
            {
                ViewModel.MoveLineDown(row);
            }
        }

        private void DeleteLine_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is LineRowViewModel row)
            {
                _ = ViewModel.DeleteLineAsync(row);
            }
        }

        private void CopyFixCommand_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is EnvironmentCheckChipModel chip && chip.HasCommand)
            {
                var dataPackage = new DataPackage();
                dataPackage.SetText(chip.Fix!.Command);
                Clipboard.SetContent(dataPackage);
            }
        }

        private async void ReleasePort_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is EnvironmentCheckChipModel chip && chip.CanReleasePort)
            {
                var fix = chip.Fix!;
                string message = LocalserverViewModel.FormatMessage(
                    "Localserver_ReleasePortConfirmMessage",
                    "Release port {0} by terminating {1} (PID {2})? Unsaved data may be lost.",
                    fix.Port,
                    fix.ProcessName,
                    fix.ProcessId);
                if (await ConfirmTerminationAsync("Localserver_ReleasePortTitle".GetLocalized(), message))
                {
                    await ViewModel.ReleasePortAsync(fix);
                }
            }
        }

        private async Task<bool> ConfirmTerminationAsync(string title, string message)
        {
            if (_confirmationOpen || XamlRoot == null)
            {
                return false;
            }

            _confirmationOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = ActualTheme,
                    Title = title,
                    Content = message,
                    PrimaryButtonText = "Localserver_TerminateProcessButton".GetLocalized(),
                    CloseButtonText = "Localserver_CancelButton".GetLocalized(),
                    DefaultButton = ContentDialogButton.Close,
                };
                return await dialog.ShowAsync() == ContentDialogResult.Primary;
            }
            finally
            {
                _confirmationOpen = false;
            }
        }
    }
}
