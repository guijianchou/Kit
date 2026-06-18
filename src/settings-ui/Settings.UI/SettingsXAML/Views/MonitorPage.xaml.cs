// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
using System.Text.Json;
using System.Threading.Tasks;

using ManagedCommon;
using Microsoft.Data.Sqlite;
using Microsoft.PowerToys.Settings.UI.Helpers;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Library.Interfaces;
using Microsoft.PowerToys.Settings.UI.Library.Utilities;
using Microsoft.PowerToys.Settings.UI.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

using MonitorCore = Microsoft.PowerToys.Monitor;

namespace Microsoft.PowerToys.Settings.UI.Views
{
    public sealed partial class MonitorPage : NavigablePage, IRefreshablePage
    {
        private const string MonitorProgressFileName = "scan-progress.json";
        private const string MonitorStatusDatabaseFileName = "monitor-status.db";
        private static readonly TimeSpan ManualScanUiTimeout = TimeSpan.FromMinutes(5);

        private readonly string _appName = MonitorSettings.ModuleName;
        private readonly SettingsUtils _settingsUtils;
        private readonly SettingsRepository<GeneralSettings> _generalSettingsRepository;
        private readonly SettingsRepository<MonitorSettings> _moduleSettingsRepository;
        private readonly IFileSystem _fileSystem;
        private readonly IFileSystemWatcher _fileSystemWatcher;
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly DispatcherQueueTimer _manualScanProgressTimer;
        private readonly Func<string, int> _sendConfigMsg;
        private bool _suppressViewModelUpdates;
        private string _manualScanId = string.Empty;
        private DateTimeOffset _manualScanStartedAt = DateTimeOffset.MinValue;

        private MonitorViewModel ViewModel { get; set; }

        public MonitorPage()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _fileSystem = new FileSystem();
            _settingsUtils = SettingsUtils.Default;
            _sendConfigMsg = ShellPage.SendDefaultIPCMessage;
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

            string manualScanId = StartManualScanProgress();
            _sendConfigMsg(Helper.GetSerializedCustomAction(MonitorSettings.ModuleName, "scanNow", manualScanId));
        }

        private string StartManualScanProgress()
        {
            _manualScanProgressTimer.Stop();
            _manualScanId = CreateManualScanId();
            _manualScanStartedAt = DateTimeOffset.UtcNow;

            ViewModel.ManualScanProgressValue = 1;
            ViewModel.IsManualScanProgressIndeterminate = true;
            ViewModel.ManualScanProgressDetail = GetResourceString("Monitor_ManualScanStarting", "Starting scan");
            ViewModel.IsManualScanProgressVisible = true;
            ViewModel.IsManualScanRunning = true;
            _manualScanProgressTimer.Start();
            return _manualScanId;
        }

        private void ManualScanProgressTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            WorkerProgressSnapshot progressSnapshot = ReadWorkerProgressSnapshot(_manualScanId);
            bool manualScanTimedOut = DateTimeOffset.UtcNow - _manualScanStartedAt >= ManualScanUiTimeout;
            if (progressSnapshot != null)
            {
                ApplyWorkerProgressSnapshot(progressSnapshot);
            }
            else
            {
                ViewModel.IsManualScanProgressIndeterminate = true;
                ViewModel.ManualScanProgressDetail = GetResourceString("Monitor_ManualScanWaitingForProgress", "Waiting for worker progress");
            }

            bool manualScanCompleted = string.Equals(progressSnapshot?.Phase, "completed", StringComparison.OrdinalIgnoreCase);
            bool manualScanFailed = string.Equals(progressSnapshot?.Phase, "failed", StringComparison.OrdinalIgnoreCase);
            if (manualScanFailed)
            {
                FailManualScanProgress(sender, FormatProgressDetail(progressSnapshot));
                return;
            }

            if (manualScanTimedOut && !manualScanCompleted)
            {
                FailManualScanProgress(sender, GetResourceString("Monitor_ManualScanTimedOut", "Scan timed out"));
                return;
            }

            if (manualScanCompleted)
            {
                CompleteManualScanProgress(sender);
            }
        }

        private void CompleteManualScanProgress(DispatcherQueueTimer sender)
        {
            ViewModel.IsManualScanProgressIndeterminate = false;
            ViewModel.ManualScanProgressValue = 100;
            FinishManualScanProgress(sender);
        }

        private void FailManualScanProgress(DispatcherQueueTimer sender, string detail)
        {
            ViewModel.IsManualScanProgressIndeterminate = false;
            ViewModel.ManualScanProgressValue = 100;
            ViewModel.ManualScanProgressDetail = string.IsNullOrWhiteSpace(detail) ? GetResourceString("Monitor_ManualScanFailed", "Scan failed") : detail;
            FinishManualScanProgress(sender);
        }

        private void FinishManualScanProgress(DispatcherQueueTimer sender)
        {
            ViewModel.IsManualScanRunning = false;
            sender.Stop();
            RefreshStatusSummary();
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
                    string statusDatabasePath = ResolveStatusDatabasePath();
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    if (!MonitorCore.MonitorStatusStore.TryGetSummary(statusDatabasePath, selectedRange, now, out MonitorCore.MonitorStatusSummary summary))
                    {
                        return summary;
                    }

                    MonitorCore.MonitorStatusStore.RefreshStaleRunningScans(statusDatabasePath, now);
                    return MonitorCore.MonitorStatusStore.GetSummary(statusDatabasePath, selectedRange, now);
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
            WorkerProgressSnapshot snapshot = ReadLatestWorkerProgressSnapshot();
            if (snapshot == null || IsTerminalScanPhase(snapshot.Phase) || !IsLatestManualScanRunning(snapshot.ScanId))
            {
                return;
            }

            _manualScanId = snapshot.ScanId;
            _manualScanStartedAt = snapshot.StartedAt == default ? DateTimeOffset.UtcNow : snapshot.StartedAt;
            ViewModel.IsManualScanProgressVisible = true;
            ViewModel.IsManualScanRunning = true;
            ApplyWorkerProgressSnapshot(snapshot);
            _manualScanProgressTimer.Start();
        }

        private WorkerProgressSnapshot ReadWorkerProgressSnapshot(string manualScanId)
        {
            WorkerProgressSnapshot snapshot = ReadLatestWorkerProgressSnapshot();
            if (snapshot == null || !string.Equals(snapshot.ScanId, manualScanId, StringComparison.Ordinal))
            {
                return null;
            }

            return snapshot;
        }

        private static bool IsLatestManualScanRunning(string scanId)
        {
            if (string.IsNullOrWhiteSpace(scanId))
            {
                return false;
            }

            try
            {
                if (!MonitorCore.MonitorStatusStore.TryGetLatestRun(ResolveStatusDatabasePath(), out MonitorCore.MonitorStatusRun latestRun) || latestRun == null)
                {
                    return false;
                }

                return latestRun.Trigger == MonitorCore.MonitorScanTrigger.Manual &&
                       latestRun.Status == MonitorCore.MonitorScanStatus.Running &&
                       string.Equals(latestRun.ScanId, scanId, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SqliteException)
            {
                return false;
            }
        }

        private static bool IsTerminalScanPhase(string phase)
        {
            return string.Equals(phase, "completed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(phase, "failed", StringComparison.OrdinalIgnoreCase);
        }

        private static WorkerProgressSnapshot ReadLatestWorkerProgressSnapshot()
        {
            string progressPath = ResolveProgressPath();

            if (!File.Exists(progressPath))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<WorkerProgressSnapshot>(File.ReadAllText(progressPath), WorkerProgressSnapshot.JsonOptions);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string ResolveProgressPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kit",
                MonitorSettings.ModuleName,
                MonitorProgressFileName);
        }

        private static string ResolveStatusDatabasePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kit",
                MonitorSettings.ModuleName,
                MonitorStatusDatabaseFileName);
        }

        private static string CreateManualScanId()
        {
            return Guid.NewGuid().ToString("N");
        }

        private void ApplyWorkerProgressSnapshot(WorkerProgressSnapshot snapshot)
        {
            bool hasTotal = snapshot.FilesTotal > 0;
            bool isCompleted = string.Equals(snapshot.Phase, "completed", StringComparison.OrdinalIgnoreCase);
            bool isFailed = string.Equals(snapshot.Phase, "failed", StringComparison.OrdinalIgnoreCase);
            ViewModel.IsManualScanProgressIndeterminate = !hasTotal && !isCompleted && !isFailed;
            if (hasTotal)
            {
                ViewModel.ManualScanProgressValue = Math.Clamp((double)snapshot.FilesProcessed / snapshot.FilesTotal * 100, 1, 100);
            }

            if (isCompleted || isFailed)
            {
                ViewModel.ManualScanProgressValue = 100;
            }

            ViewModel.ManualScanProgressDetail = FormatProgressDetail(snapshot);
        }

        private static string FormatProgressDetail(WorkerProgressSnapshot snapshot)
        {
            string phase = snapshot.Phase switch
            {
                "hashing" => GetResourceString("Monitor_ManualScanPhaseHashing", "Hashing"),
                "categorizing" => GetResourceString("Monitor_ManualScanPhaseCategorizing", "Categorizing"),
                "writing" => GetResourceString("Monitor_ManualScanPhaseWriting", "Writing"),
                "completed" => GetResourceString("Monitor_ManualScanPhaseComplete", "Complete"),
                "failed" => GetResourceString("Monitor_ManualScanFailed", "Scan failed"),
                _ => GetResourceString("Monitor_ManualScanPhaseScanning", "Scanning"),
            };

            if (string.Equals(snapshot.Phase, "failed", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(snapshot.Message))
            {
                return snapshot.Message;
            }

            if (string.Equals(snapshot.Phase, "completed", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(snapshot.Message))
            {
                return snapshot.Message;
            }

            if (string.Equals(snapshot.Phase, "completed", StringComparison.OrdinalIgnoreCase) && snapshot.RecordCount.HasValue)
            {
                return FormatResourceString("Monitor_ManualScanCompletedFiles", "{0}: {1} files", phase, snapshot.RecordCount.Value);
            }

            return snapshot.FilesTotal > 0
                ? FormatResourceString("Monitor_ManualScanProgressFiles", "{0}: {1}/{2}", phase, snapshot.FilesProcessed, snapshot.FilesTotal)
                : phase;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1863:Cache a CompositeFormat for repeated use", Justification = "The format string is loaded from localizable resources.")]
        private static string FormatResourceString(string resourceKey, string fallback, params object[] args)
        {
            return string.Format(CultureInfo.CurrentCulture, GetResourceString(resourceKey, fallback), args);
        }

        private static string GetResourceString(string resourceKey, string fallback)
        {
            string value = ResourceLoaderInstance.ResourceLoader.GetString(resourceKey);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private sealed class WorkerProgressSnapshot
        {
            public static readonly JsonSerializerOptions JsonOptions = new()
            {
                PropertyNameCaseInsensitive = true,
            };

            public string ScanId { get; set; } = string.Empty;

            public string Phase { get; set; }

            public int FilesProcessed { get; set; }

            public int FilesTotal { get; set; }

            public string CurrentDirectory { get; set; }

            public DateTimeOffset StartedAt { get; set; }

            public DateTimeOffset? CompletedAt { get; set; }

            public int? RecordCount { get; set; }

            public string Message { get; set; }
        }
    }
}
