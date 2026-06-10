// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.IO.Abstractions;
using System.Text.Json;
using System.Threading;

using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Helpers;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Library.Interfaces;
using Microsoft.PowerToys.Settings.UI.Library.Utilities;
using Microsoft.PowerToys.Settings.UI.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Microsoft.PowerToys.Settings.UI.Views
{
    public sealed partial class MonitorPage : NavigablePage, IRefreshablePage
    {
        private const string MonitorScanCompletedEvent = @"Local\KitMonitorScanCompletedEvent-b7fb014b-c1fd-46c4-9d33-b517ef54824c";
        private const string MonitorProgressFileName = "scan-progress.json";

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
        }

        public void RefreshEnabledState()
        {
            ViewModel.IsEnabled = _generalSettingsRepository.SettingsConfig.Enabled.Monitor;
            ViewModel.RefreshEnabledState();
        }

        private void ScanNow_Click(object sender, RoutedEventArgs e)
        {
            StartManualScanProgress();
            _sendConfigMsg(Helper.GetSerializedCustomAction(MonitorSettings.ModuleName, "scanNow", string.Empty));
        }

        private void StartManualScanProgress()
        {
            _manualScanProgressTimer.Stop();
            using EventWaitHandle scanCompletedEvent = new(false, EventResetMode.AutoReset, MonitorScanCompletedEvent);
            scanCompletedEvent.Reset();
            DeleteStaleManualScanProgress();

            ViewModel.ManualScanProgressValue = 1;
            ViewModel.IsManualScanProgressIndeterminate = true;
            ViewModel.ManualScanProgressDetail = "Starting scan";
            ViewModel.IsManualScanProgressVisible = true;
            _manualScanProgressTimer.Start();
        }

        private void ManualScanProgressTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            bool completionSignaled = IsScanCompletedSignaled();
            WorkerProgressSnapshot progressSnapshot = ReadWorkerProgressSnapshot();
            if (progressSnapshot != null)
            {
                ApplyWorkerProgressSnapshot(progressSnapshot);
            }
            else
            {
                ViewModel.IsManualScanProgressIndeterminate = true;
                ViewModel.ManualScanProgressDetail = "Waiting for worker progress";
            }

            if (completionSignaled || string.Equals(progressSnapshot?.Phase, "completed", StringComparison.OrdinalIgnoreCase))
            {
                ViewModel.IsManualScanProgressIndeterminate = false;
                ViewModel.ManualScanProgressValue = 100;
                sender.Stop();
            }
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

                _moduleSettingsRepository.ReloadSettings();
                LoadSettings(_generalSettingsRepository, _moduleSettingsRepository);

                _suppressViewModelUpdates = false;
            });
        }

        private static bool IsScanCompletedSignaled()
        {
            try
            {
                using EventWaitHandle scanCompletedEvent = EventWaitHandle.OpenExisting(MonitorScanCompletedEvent);
                return scanCompletedEvent.WaitOne(0);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private WorkerProgressSnapshot ReadWorkerProgressSnapshot()
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

        private static void DeleteStaleManualScanProgress()
        {
            string progressPath = ResolveProgressPath();
            try
            {
                if (File.Exists(progressPath))
                {
                    File.Delete(progressPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
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

        private void ApplyWorkerProgressSnapshot(WorkerProgressSnapshot snapshot)
        {
            bool hasTotal = snapshot.FilesTotal > 0;
            ViewModel.IsManualScanProgressIndeterminate = !hasTotal && !string.Equals(snapshot.Phase, "completed", StringComparison.OrdinalIgnoreCase);
            if (hasTotal)
            {
                ViewModel.ManualScanProgressValue = Math.Clamp((double)snapshot.FilesProcessed / snapshot.FilesTotal * 100, 1, 100);
            }

            if (string.Equals(snapshot.Phase, "completed", StringComparison.OrdinalIgnoreCase))
            {
                ViewModel.ManualScanProgressValue = 100;
            }

            ViewModel.ManualScanProgressDetail = FormatProgressDetail(snapshot);
        }

        private static string FormatProgressDetail(WorkerProgressSnapshot snapshot)
        {
            string phase = snapshot.Phase switch
            {
                "hashing" => "Hashing",
                "categorizing" => "Categorizing",
                "writing" => "Writing",
                "completed" => "Complete",
                _ => "Scanning",
            };

            if (string.Equals(snapshot.Phase, "completed", StringComparison.OrdinalIgnoreCase) && snapshot.RecordCount.HasValue)
            {
                return $"{phase}: {snapshot.RecordCount.Value} files";
            }

            return snapshot.FilesTotal > 0
                ? $"{phase}: {snapshot.FilesProcessed}/{snapshot.FilesTotal}"
                : phase;
        }

        private sealed class WorkerProgressSnapshot
        {
            public static readonly JsonSerializerOptions JsonOptions = new()
            {
                PropertyNameCaseInsensitive = true,
            };

            public string Phase { get; set; }

            public int FilesProcessed { get; set; }

            public int FilesTotal { get; set; }

            public string CurrentDirectory { get; set; }

            public DateTimeOffset StartedAt { get; set; }

            public DateTimeOffset? CompletedAt { get; set; }

            public int? RecordCount { get; set; }
        }
    }
}
