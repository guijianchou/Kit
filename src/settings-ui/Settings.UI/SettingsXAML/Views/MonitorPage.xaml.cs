// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.IO.Abstractions;
using System.Threading.Tasks;

using ManagedCommon;
using Microsoft.Data.Sqlite;
using Microsoft.PowerToys.Settings.UI.Helpers;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Library.Interfaces;
using Microsoft.PowerToys.Settings.UI.Library.Utilities;
using Microsoft.PowerToys.Settings.UI.Services;
using Microsoft.PowerToys.Settings.UI.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

using MonitorCore = Microsoft.PowerToys.Monitor;

namespace Microsoft.PowerToys.Settings.UI.Views
{
    public sealed partial class MonitorPage : NavigablePage, IRefreshablePage
    {
        private readonly string _appName = MonitorSettings.ModuleName;
        private readonly SettingsUtils _settingsUtils;
        private readonly SettingsRepository<GeneralSettings> _generalSettingsRepository;
        private readonly SettingsRepository<MonitorSettings> _moduleSettingsRepository;
        private readonly IFileSystem _fileSystem;
        private readonly IFileSystemWatcher _fileSystemWatcher;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly DispatcherQueueTimer _manualScanProgressTimer;
        private readonly Func<string, int> _sendConfigMsg;
        private readonly MonitorStatusQueryService _statusQueryService;
        private readonly MonitorManualScanCoordinator _manualScanCoordinator;
        private bool _suppressViewModelUpdates;

        private MonitorViewModel ViewModel { get; set; }

        public MonitorPage()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _fileSystem = new FileSystem();
            _settingsUtils = SettingsUtils.Default;
            _sendConfigMsg = ShellPage.SendDefaultIPCMessage;
            _statusQueryService = new MonitorStatusQueryService();
            _manualScanCoordinator = new MonitorManualScanCoordinator(
                new MonitorProgressSnapshotReader(),
                _statusQueryService,
                MonitorSettingsStoragePaths.ResolveProgressPath(),
                MonitorSettingsStoragePaths.ResolveStatusDatabasePath());
            _manualScanProgressTimer = _dispatcherQueue.CreateTimer();
            _manualScanProgressTimer.Interval = TimeSpan.FromMilliseconds(500);
            _manualScanProgressTimer.Tick += ManualScanProgressTimer_Tick;

            ViewModel = new MonitorViewModel();
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;

            _generalSettingsRepository = SettingsRepository<GeneralSettings>.GetInstance(_settingsUtils);
            _moduleSettingsRepository = SettingsRepository<MonitorSettings>.GetInstance(_settingsUtils);

            LoadSettings(_generalSettingsRepository, _moduleSettingsRepository);
            DataContext = ViewModel;

            var settingsPath = _settingsUtils.GetSettingsFilePath(_appName);

            _fileSystemWatcher = _fileSystem.FileSystemWatcher.New();
            _fileSystemWatcher.Path = _fileSystem.Path.GetDirectoryName(settingsPath);
            _fileSystemWatcher.Filter = _fileSystem.Path.GetFileName(settingsPath);
            _fileSystemWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime;
            _fileSystemWatcher.Changed += Settings_Changed;
            _fileSystemWatcher.EnableRaisingEvents = true;

            InitializeComponent();
            RefreshStatusSummary();
            RestoreManualScanProgressIfRunning();
            UpdateStatusRangeButtonStyles();

            Unloaded += MonitorPage_Unloaded;
        }

        private void MonitorPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // The page is recreated on each navigation (NavigationCacheMode is not set), so the
            // watcher and timer must be released here or they leak and keep this instance alive.
            Unloaded -= MonitorPage_Unloaded;

            _manualScanProgressTimer.Stop();
            _manualScanProgressTimer.Tick -= ManualScanProgressTimer_Tick;

            _fileSystemWatcher.Changed -= Settings_Changed;
            _fileSystemWatcher.EnableRaisingEvents = false;
            _fileSystemWatcher.Dispose();

            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        public void RefreshEnabledState()
        {
            ViewModel.IsEnabled = _generalSettingsRepository.SettingsConfig.Enabled.Monitor;
            ViewModel.RefreshEnabledState();
        }

        private void ScanNow_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.CanStartManualScan)
            {
                return;
            }

            _manualScanProgressTimer.Stop();
            MonitorManualScanProgressUpdate progressUpdate = _manualScanCoordinator.StartManualScanProgress();
            ApplyManualScanProgressUpdate(progressUpdate);
            _manualScanProgressTimer.Start();
            string manualScanId = progressUpdate.ScanId;
            int sendResult = _sendConfigMsg(Helper.GetSerializedCustomAction(MonitorSettings.ModuleName, "scanNow", manualScanId));
            if (sendResult != 0)
            {
                FailManualScanStart();
            }
        }

        private void FailManualScanStart()
        {
            _manualScanProgressTimer.Stop();
            ApplyManualScanProgressUpdate(_manualScanCoordinator.FailManualScanStart());
            RefreshStatusSummary();
        }

        private void ManualScanProgressTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            MonitorManualScanProgressUpdate progressUpdate = _manualScanCoordinator.UpdateManualScanProgress();
            ApplyManualScanProgressUpdate(progressUpdate);
            if (progressUpdate.ShouldStopTimer)
            {
                sender.Stop();
            }

            if (progressUpdate.ShouldRefreshStatus)
            {
                RefreshStatusSummary();
            }
        }

        private void StatusRangeAll_Click(object sender, RoutedEventArgs e)
        {
            SetStatusRange(MonitorCore.MonitorStatusRange.All);
        }

        private void StatusRange30Days_Click(object sender, RoutedEventArgs e)
        {
            SetStatusRange(MonitorCore.MonitorStatusRange.ThirtyDays);
        }

        private void StatusRange7Days_Click(object sender, RoutedEventArgs e)
        {
            SetStatusRange(MonitorCore.MonitorStatusRange.SevenDays);
        }

        private void SetStatusRange(MonitorCore.MonitorStatusRange range)
        {
            ViewModel.SelectedStatusRange = range;
            RefreshStatusSummary();
            UpdateStatusRangeButtonStyles();
        }

        private async void RefreshStatusSummary()
        {
            MonitorCore.MonitorStatusRange selectedRange = ViewModel.SelectedStatusRange;
            try
            {
                MonitorCore.MonitorStatusSummary summary = await Task.Run(() =>
                {
                    string statusDatabasePath = MonitorSettingsStoragePaths.ResolveStatusDatabasePath();
                    string progressPath = MonitorSettingsStoragePaths.ResolveProgressPath();
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    return _statusQueryService.GetSummaryWithStaleRefresh(statusDatabasePath, progressPath, selectedRange, now);
                });
                if (ViewModel.SelectedStatusRange == selectedRange)
                {
                    ViewModel.ApplyStatusSummary(summary);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SqliteException)
            {
                if (ViewModel.SelectedStatusRange == selectedRange)
                {
                    ViewModel.ApplyStatusUnavailable();
                }
            }
        }

        private void UpdateStatusRangeButtonStyles()
        {
            Style accentButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
            MonitorStatusAllButton.Style = ViewModel.SelectedStatusRange == MonitorCore.MonitorStatusRange.All ? accentButtonStyle : null;
            MonitorStatus30DaysButton.Style = ViewModel.SelectedStatusRange == MonitorCore.MonitorStatusRange.ThirtyDays ? accentButtonStyle : null;
            MonitorStatus7DaysButton.Style = ViewModel.SelectedStatusRange == MonitorCore.MonitorStatusRange.SevenDays ? accentButtonStyle : null;
        }

        private void BrowseDownloadsFolder_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.GetSettingsWindow());
            var selectedFolder = ShellGetFolder.GetFolderDialog(hwnd);
            if (!string.IsNullOrWhiteSpace(selectedFolder))
            {
                ViewModel.DownloadsPath = selectedFolder;
            }
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_suppressViewModelUpdates || _sendConfigMsg == null)
            {
                return;
            }

            if (e.PropertyName == nameof(MonitorViewModel.IsEnabled))
            {
                if (ViewModel.IsEnabled != _generalSettingsRepository.SettingsConfig.Enabled.Monitor)
                {
                    _generalSettingsRepository.SettingsConfig.Enabled.Monitor = ViewModel.IsEnabled;
                    var generalSettingsMessage = new OutGoingGeneralSettings(_generalSettingsRepository.SettingsConfig).ToString();

                    Logger.LogInfo("Saved general settings from Monitor page.");
                    _sendConfigMsg(generalSettingsMessage);
                }
            }
            else if (ViewModel.ModuleSettings != null)
            {
                SndMonitorSettings currentSettings = new(_moduleSettingsRepository.SettingsConfig);
                SndModuleSettings<SndMonitorSettings> currentMessage = new(currentSettings);

                SndMonitorSettings outgoingSettings = new(ViewModel.ModuleSettings);
                SndModuleSettings<SndMonitorSettings> outgoingMessage = new(outgoingSettings);

                string currentJson = currentMessage.ToJsonString();
                string outgoingJson = outgoingMessage.ToJsonString();

                if (!currentJson.Equals(outgoingJson, StringComparison.Ordinal))
                {
                    Logger.LogInfo("Saved Monitor settings from Monitor page.");
                    _sendConfigMsg(outgoingJson);
                }
            }
        }

        private void LoadSettings(ISettingsRepository<GeneralSettings> generalSettingsRepository, ISettingsRepository<MonitorSettings> moduleSettingsRepository)
        {
            ArgumentNullException.ThrowIfNull(generalSettingsRepository);
            ArgumentNullException.ThrowIfNull(moduleSettingsRepository);

            UpdateViewModelSettings(moduleSettingsRepository.SettingsConfig, generalSettingsRepository.SettingsConfig);
        }

        private void UpdateViewModelSettings(MonitorSettings monitorSettings, GeneralSettings generalSettings)
        {
            ArgumentNullException.ThrowIfNull(monitorSettings);
            ArgumentNullException.ThrowIfNull(generalSettings);

            ViewModel.IsEnabled = generalSettings.Enabled.Monitor;
            ViewModel.ModuleSettings = (MonitorSettings)monitorSettings.Clone();
            ViewModel.RefreshEnabledState();
        }

        private void Settings_Changed(object sender, FileSystemEventArgs e)
        {
            _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                _suppressViewModelUpdates = true;
                try
                {
                    _moduleSettingsRepository.ReloadSettings();
                    LoadSettings(_generalSettingsRepository, _moduleSettingsRepository);
                }
                finally
                {
                    _suppressViewModelUpdates = false;
                }
            });
        }

        private void RestoreManualScanProgressIfRunning()
        {
            MonitorManualScanProgressUpdate progressUpdate = _manualScanCoordinator.RestoreManualScanProgressIfRunning();
            if (progressUpdate == null)
            {
                return;
            }

            ApplyManualScanProgressUpdate(progressUpdate);
            _manualScanProgressTimer.Start();
        }

        private void ApplyManualScanProgressUpdate(MonitorManualScanProgressUpdate update)
        {
            ViewModel.ManualScanProgressValue = update.ProgressValue;
            ViewModel.IsManualScanProgressIndeterminate = update.IsIndeterminate;
            ViewModel.ManualScanProgressDetail = update.Detail;
            ViewModel.IsManualScanProgressVisible = update.IsVisible;
            if (update.IsRunning)
            {
                ViewModel.IsManualScanRunning = true;
            }
            else
            {
                ViewModel.IsManualScanRunning = false;
            }
        }
    }
}
