// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;

using global::PowerToys.GPOWrapper;
using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Helpers;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Library.Helpers;
using Microsoft.PowerToys.Settings.UI.Library.Interfaces;
using Microsoft.PowerToys.Settings.UI.Library.Utilities;
using Microsoft.PowerToys.Settings.UI.Library.ViewModels.Commands;
using Microsoft.PowerToys.Settings.UI.SerializationContext;

namespace Microsoft.PowerToys.Settings.UI.ViewModels
{
    public partial class GeneralViewModel : PageViewModelBase
    {
        private const bool IsDataDiagnosticsEnabledInKit = false;

        protected override string ModuleName => "GeneralSettings";

        public override Dictionary<string, HotkeySettings[]> GetAllHotkeySettings()
        {
            return new Dictionary<string, HotkeySettings[]>
            {
                { ModuleName, new HotkeySettings[] { QuickAccessShortcut } },
            };
        }

        private GeneralSettings GeneralSettingsConfig { get; set; }

        private UpdatingSettings UpdatingSettingsConfig { get; set; }

        public ButtonClickCommand CheckForUpdatesEventHandler { get; set; }

        public Windows.ApplicationModel.Resources.ResourceLoader ResourceLoader { get; set; }

        private Action HideBackupAndRestoreMessageAreaAction { get; set; }

        private Action<int> DoBackupAndRestoreDryRun { get; set; }

        public ButtonClickCommand BackupConfigsEventHandler { get; set; }

        public ButtonClickCommand RestoreConfigsEventHandler { get; set; }

        public ButtonClickCommand RefreshBackupStatusEventHandler { get; set; }

        public ButtonClickCommand SelectSettingBackupDirEventHandler { get; set; }

        public ButtonClickCommand RestartElevatedButtonEventHandler { get; set; }

        public ButtonClickCommand UpdateNowButtonEventHandler { get; set; }

        public Func<string, int> SendConfigMSG { get; }

        public Func<string, int> SendRestartAsAdminConfigMSG { get; }

        public Func<string, int> SendCheckForUpdatesConfigMSG { get; }

        public string RunningAsUserDefaultText { get; set; }

        public string RunningAsAdminDefaultText { get; set; }

        private string _settingsConfigFileFolder = string.Empty;

        private ISettingsRepository<GeneralSettings> _settingsRepository;
        private Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
        private bool _deferredStartupMaintenanceQueued;

        private Func<Task<string>> PickSingleFolderDialog { get; }

        private SettingsBackupAndRestoreUtils settingsBackupAndRestoreUtils = SettingsBackupAndRestoreUtils.Instance;

        public GeneralViewModel(ISettingsRepository<GeneralSettings> settingsRepository, string runAsAdminText, string runAsUserText, bool isElevated, bool isAdmin, Func<string, int> ipcMSGCallBackFunc, Func<string, int> ipcMSGRestartAsAdminMSGCallBackFunc, Func<string, int> ipcMSGCheckForUpdatesCallBackFunc, string configFileSubfolder = "", Action dispatcherAction = null, Action hideBackupAndRestoreMessageAreaAction = null, Action<int> doBackupAndRestoreDryRun = null, Func<Task<string>> pickSingleFolderDialog = null, Windows.ApplicationModel.Resources.ResourceLoader resourceLoader = null)
        {
            CheckForUpdatesEventHandler = new ButtonClickCommand(CheckForUpdatesClick);
            RestartElevatedButtonEventHandler = new ButtonClickCommand(RestartElevated);
            UpdateNowButtonEventHandler = new ButtonClickCommand(UpdateNowClick);
            BackupConfigsEventHandler = new ButtonClickCommand(BackupConfigsClick);
            SelectSettingBackupDirEventHandler = new ButtonClickCommand(SelectSettingBackupDir);
            RestoreConfigsEventHandler = new ButtonClickCommand(RestoreConfigsClick);
            RefreshBackupStatusEventHandler = new ButtonClickCommand(RefreshBackupStatusEventHandlerClick);
            HideBackupAndRestoreMessageAreaAction = hideBackupAndRestoreMessageAreaAction;
            DoBackupAndRestoreDryRun = doBackupAndRestoreDryRun;
            PickSingleFolderDialog = pickSingleFolderDialog;
            ResourceLoader = resourceLoader;

            // To obtain the general settings configuration of PowerToys if it exists, else to create a new file and return the default configurations.
            ArgumentNullException.ThrowIfNull(settingsRepository);

            _settingsRepository = settingsRepository;
            _settingsRepository.SettingsChanged += OnSettingsChanged;
            _dispatcherQueue = GetDispatcherQueue();

            GeneralSettingsConfig = settingsRepository.SettingsConfig;
            UpdatingSettingsConfig = new UpdatingSettings();

            // set the callback functions value to handle outgoing IPC message.
            SendConfigMSG = ipcMSGCallBackFunc;
            SendCheckForUpdatesConfigMSG = ipcMSGCheckForUpdatesCallBackFunc;
            SendRestartAsAdminConfigMSG = ipcMSGRestartAsAdminMSGCallBackFunc;

            // Update Settings file folder:
            _settingsConfigFileFolder = configFileSubfolder;

            // Using Invariant here as these are internal strings and the analyzer
            // expects strings to be normalized to uppercase. While the theme names
            // are represented in lowercase everywhere else, we'll use uppercase
            // normalization for switch statements
            switch (GeneralSettingsConfig.Theme.ToUpperInvariant())
            {
                case "DARK":
                    _themeIndex = 0;
                    break;
                case "LIGHT":
                    _themeIndex = 1;
                    break;
                case "SYSTEM":
                    _themeIndex = 2;
                    break;
            }

            _isDevBuild = Helper.GetProductVersion() == "v0.0.1";

            _runAtStartupGpoRuleConfiguration = GPOWrapper.GetConfiguredRunAtStartupValue();
            if (_runAtStartupGpoRuleConfiguration == GpoRuleConfigured.Disabled || _runAtStartupGpoRuleConfiguration == GpoRuleConfigured.Enabled)
            {
                // Get the enabled state from GPO.
                _runAtStartupIsGPOConfigured = true;
                _startup = _runAtStartupGpoRuleConfiguration == GpoRuleConfigured.Enabled;
            }
            else
            {
                _startup = GeneralSettingsConfig.Startup;
            }

            _showSysTrayIcon = GeneralSettingsConfig.ShowSysTrayIcon;
            _showThemeAdaptiveSysTrayIcon = GeneralSettingsConfig.ShowThemeAdaptiveTrayIcon;
            GeneralSettingsConfig.ShowNewUpdatesToastNotification = false;
            GeneralSettingsConfig.AutoDownloadUpdates = false;
            GeneralSettingsConfig.ShowWhatsNewAfterUpdates = false;
            _enableExperimentation = GeneralSettingsConfig.EnableExperimentation;

            _isElevated = isElevated;
            _runElevated = GeneralSettingsConfig.RunElevated;
            _enableWarningsElevatedApps = GeneralSettingsConfig.EnableWarningsElevatedApps;
            _enableQuickAccess = GeneralSettingsConfig.EnableQuickAccess;
            _quickAccessShortcut = GeneralSettingsConfig.QuickAccessShortcut;
            if (_quickAccessShortcut != null)
            {
                _quickAccessShortcut.PropertyChanged += QuickAccessShortcut_PropertyChanged;
            }

            RunningAsUserDefaultText = runAsUserText;
            RunningAsAdminDefaultText = runAsAdminText;

            _isAdmin = isAdmin;

            _updatingState = UpdatingSettings.UpdatingState.UpToDate;
            _newAvailableVersion = string.Empty;
            _newAvailableVersionLink = string.Empty;
            _updateCheckedDate = string.Empty;

            _newUpdatesToastIsGpoDisabled = GPOWrapper.GetDisableNewUpdateToastValue() == GpoRuleConfigured.Enabled;
            _autoDownloadUpdatesIsGpoDisabled = GPOWrapper.GetDisableAutomaticUpdateDownloadValue() == GpoRuleConfigured.Enabled;
            _experimentationIsGpoDisallowed = GPOWrapper.GetAllowExperimentationValue() == GpoRuleConfigured.Disabled;
            _showWhatsNewAfterUpdatesIsGpoDisabled = GPOWrapper.GetDisableShowWhatsNewAfterUpdatesValue() == GpoRuleConfigured.Enabled;
            _enableDataDiagnosticsIsGpoDisallowed = GPOWrapper.GetAllowDataDiagnosticsValue() == GpoRuleConfigured.Disabled;

            InitializeLanguages();
        }

        // Supported languages. Taken from Resources.wxs + default + en-US
        private Dictionary<string, string> langTagsAndIds = new Dictionary<string, string>
        {
            { string.Empty, "Default_Language" },
            { "ar-SA", "Arabic_Saudi_Arabia_Language" },
            { "cs-CZ", "Czech_Language" },
            { "de-DE", "German_Language" },
            { "en-US", "English_Language" },
            { "es-ES", "Spanish_Language" },
            { "fa-IR", "Persian_Farsi_Language" },
            { "fr-FR", "French_Language" },
            { "he-IL", "Hebrew_Israel_Language" },
            { "hu-HU", "Hungarian_Language" },
            { "it-IT", "Italian_Language" },
            { "ja-JP", "Japanese_Language" },
            { "ko-KR", "Korean_Language" },
            { "nl-NL", "Dutch_Language" },
            { "pl-PL", "Polish_Language" },
            { "pt-BR", "Portuguese_Brazil_Language" },
            { "pt-PT", "Portuguese_Portugal_Language" },
            { "ru-RU", "Russian_Language" },
            { "sv-SE", "Swedish_Language" },
            { "tr-TR", "Turkish_Language" },
            { "uk-UA", "Ukrainian_Language" },
            { "zh-CN", "Chinese_Simplified_Language" },
            { "zh-TW", "Chinese_Traditional_Language" },
        };

        private static bool _isDevBuild;
        private bool _startup;
        private bool _showSysTrayIcon;
        private bool _showThemeAdaptiveSysTrayIcon;
        private GpoRuleConfigured _runAtStartupGpoRuleConfiguration;
        private bool _runAtStartupIsGPOConfigured;
        private bool _isElevated;
        private bool _runElevated;
        private bool _isAdmin;
        private bool _enableWarningsElevatedApps;
        private bool _enableQuickAccess;
        private HotkeySettings _quickAccessShortcut;
        private int _themeIndex;

        private bool _newUpdatesToastIsGpoDisabled;
        private bool _autoDownloadUpdatesIsGpoDisabled;
        private bool _showWhatsNewAfterUpdatesIsGpoDisabled;
        private bool _enableExperimentation;
        private bool _experimentationIsGpoDisallowed;
        private bool _enableDataDiagnosticsIsGpoDisallowed;
        private bool _viewDiagnosticDataViewerChanged;

        private UpdatingSettings.UpdatingState _updatingState = UpdatingSettings.UpdatingState.UpToDate;
        private string _newAvailableVersion = string.Empty;
        private string _newAvailableVersionLink = string.Empty;
        private string _updateCheckedDate = string.Empty;

        private bool _isNewVersionDownloading;
        private bool _isBugReportRunning;

        private bool _settingsBackupRestoreMessageVisible;
        private string _settingsBackupMessage;
        private string _backupRestoreMessageSeverity;

        private int _languagesIndex;
        private int _initLanguagesIndex;
        private bool _languageChanged;

        // Gets or sets a value indicating whether run powertoys on start-up.
        public bool Startup
        {
            get
            {
                return _startup;
            }

            set
            {
                if (_runAtStartupIsGPOConfigured)
                {
                    // If it's GPO configured, shouldn't be able to change this state.
                    return;
                }

                if (_startup != value)
                {
                    _startup = value;
                    GeneralSettingsConfig.Startup = value;
                    NotifyPropertyChanged();
                }
            }
        }

        // Gets or sets a value indicating whether the PowerToys icon should be shown in the system tray.
        public bool ShowSysTrayIcon
        {
            get
            {
                return _showSysTrayIcon;
            }

            set
            {
                if (_showSysTrayIcon != value)
                {
                    _showSysTrayIcon = value;
                    GeneralSettingsConfig.ShowSysTrayIcon = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public bool ShowThemeAdaptiveTrayIcon
        {
            get
            {
                return _showThemeAdaptiveSysTrayIcon;
            }

            set
            {
                if (_showThemeAdaptiveSysTrayIcon != value)
                {
                    _showThemeAdaptiveSysTrayIcon = value;
                    GeneralSettingsConfig.ShowThemeAdaptiveTrayIcon = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string RunningAsText
        {
            get
            {
                if (!IsElevated)
                {
                    return RunningAsUserDefaultText;
                }
                else
                {
                    return RunningAsAdminDefaultText;
                }
            }

            set
            {
                OnPropertyChanged("RunningAsAdminText");
            }
        }

        // Gets or sets a value indicating whether the powertoy elevated.
        public bool IsElevated
        {
            get
            {
                return _isElevated;
            }

            set
            {
                if (_isElevated != value)
                {
                    _isElevated = value;
                    OnPropertyChanged(nameof(IsElevated));
                    OnPropertyChanged(nameof(IsAdminButtonEnabled));
                    OnPropertyChanged("RunningAsAdminText");
                }
            }
        }

        public bool IsAdminButtonEnabled
        {
            get
            {
                return !IsElevated;
            }

            set
            {
                OnPropertyChanged(nameof(IsAdminButtonEnabled));
            }
        }

        // Gets or sets a value indicating whether powertoys should run elevated.
        public bool RunElevated
        {
            get
            {
                return _runElevated;
            }

            set
            {
                if (_runElevated != value)
                {
                    _runElevated = value;
                    GeneralSettingsConfig.RunElevated = value;
                    NotifyPropertyChanged();
                }
            }
        }

        // Gets a value indicating whether the user is part of administrators group.
        public bool IsAdmin
        {
            get
            {
                return _isAdmin;
            }
        }

        public bool EnableWarningsElevatedApps
        {
            get
            {
                return _enableWarningsElevatedApps;
            }

            set
            {
                if (_enableWarningsElevatedApps != value)
                {
                    _enableWarningsElevatedApps = value;
                    GeneralSettingsConfig.EnableWarningsElevatedApps = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public bool EnableQuickAccess
        {
            get
            {
                return _enableQuickAccess;
            }

            set
            {
                if (_enableQuickAccess != value)
                {
                    _enableQuickAccess = value;
                    GeneralSettingsConfig.EnableQuickAccess = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public HotkeySettings QuickAccessShortcut
        {
            get
            {
                return _quickAccessShortcut;
            }

            set
            {
                if (_quickAccessShortcut != value)
                {
                    if (_quickAccessShortcut != null)
                    {
                        _quickAccessShortcut.PropertyChanged -= QuickAccessShortcut_PropertyChanged;
                    }

                    _quickAccessShortcut = value;
                    if (_quickAccessShortcut != null)
                    {
                        _quickAccessShortcut.PropertyChanged += QuickAccessShortcut_PropertyChanged;
                    }

                    GeneralSettingsConfig.QuickAccessShortcut = value;
                    NotifyPropertyChanged();
                }
            }
        }

        private void QuickAccessShortcut_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            NotifyPropertyChanged(nameof(QuickAccessShortcut));
        }

        public bool SomeUpdateSettingsAreGpoManaged
        {
            get
            {
                return _newUpdatesToastIsGpoDisabled ||
                    (_isAdmin && _autoDownloadUpdatesIsGpoDisabled) ||
                    _showWhatsNewAfterUpdatesIsGpoDisabled;
            }
        }

        public bool ShowNewUpdatesToastNotification
        {
            get
            {
                return false;
            }

            set
            {
                // Update notifications are disabled for Kit.
            }
        }

        public bool IsShowNewUpdatesToastNotificationCardEnabled
        {
            get => false;
        }

        public bool AutoDownloadUpdates
        {
            get
            {
                return false;
            }

            set
            {
                // Automatic update downloads are disabled for Kit.
            }
        }

        public bool IsAutoDownloadUpdatesCardEnabled
        {
            get => false;
        }

        public bool ShowWhatsNewAfterUpdates
        {
            get
            {
                return false;
            }

            set
            {
                // What's New after updates is disabled for Kit.
            }
        }

        public bool IsShowWhatsNewAfterUpdatesCardEnabled
        {
            get => false;
        }

        public bool EnableExperimentation
        {
            get
            {
                return _enableExperimentation && !_experimentationIsGpoDisallowed;
            }

            set
            {
                if (_enableExperimentation != value)
                {
                    _enableExperimentation = value;
                    GeneralSettingsConfig.EnableExperimentation = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public bool EnableDataDiagnostics
        {
            get
            {
                return IsDataDiagnosticsEnabledInKit;
            }

            set
            {
            }
        }

        public bool ViewDiagnosticDataViewerChanged
        {
            get => _viewDiagnosticDataViewerChanged;
        }

        public bool EnableViewDataDiagnostics
        {
            get
            {
                return false;
            }

            set
            {
                _viewDiagnosticDataViewerChanged = false;
            }
        }

        public bool IsExperimentationGpoDisallowed
        {
            get => _experimentationIsGpoDisallowed;
        }

        public bool IsDataDiagnosticsGPOManaged
        {
            get => _enableDataDiagnosticsIsGpoDisallowed;
        }

        public bool IsRunAtStartupGPOManaged
        {
            get => _runAtStartupIsGPOConfigured;
        }

        public string SettingsBackupAndRestoreDir
        {
            get
            {
                return settingsBackupAndRestoreUtils.GetSettingsBackupAndRestoreDir();
            }

            set
            {
                if (settingsBackupAndRestoreUtils.GetSettingsBackupAndRestoreDir() != value)
                {
                    SettingsBackupAndRestoreUtils.SetRegSettingsBackupAndRestoreItem("SettingsBackupAndRestoreDir", value);
                    NotifyPropertyChanged();
                }
            }
        }

        public int ThemeIndex
        {
            get
            {
                return _themeIndex;
            }

            set
            {
                if (_themeIndex != value)
                {
                    switch (value)
                    {
                        case 0: GeneralSettingsConfig.Theme = "dark"; break;
                        case 1: GeneralSettingsConfig.Theme = "light"; break;
                        case 2: GeneralSettingsConfig.Theme = "system"; break;
                    }

                    _themeIndex = value;

                    App.ThemeService.ApplyTheme();

                    NotifyPropertyChanged();
                }
            }
        }

        public string PowerToysVersion
        {
            get
            {
                return Helper.GetProductDisplayVersion();
            }
        }

        public string UpdateCheckedDate
        {
            get
            {
                return string.Empty;
            }

            set
            {
                if (_updateCheckedDate != value)
                {
                    _updateCheckedDate = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string LastSettingsBackupDate
        {
            get
            {
                try
                {
                    var manifest = settingsBackupAndRestoreUtils.GetLatestSettingsBackupManifest();
                    if (manifest != null)
                    {
                        if (manifest["CreateDateTime"] != null)
                        {
                            if (DateTime.TryParse(manifest["CreateDateTime"].ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var theDateTime))
                            {
                                return theDateTime.ToString("G", CultureInfo.CurrentCulture);
                            }
                            else
                            {
                                Logger.LogError("Failed to parse time from backup");
                                return GetResourceString("General_SettingsBackupAndRestore_FailedToParseTime");
                            }
                        }
                        else
                        {
                            return GetResourceString("General_SettingsBackupAndRestore_UnknownBackupTime");
                        }
                    }
                    else
                    {
                        return GetResourceString("General_SettingsBackupAndRestore_NoBackupFound");
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError("Error getting LastSettingsBackupDate", e);
                    return GetResourceString("General_SettingsBackupAndRestore_UnknownBackupTime");
                }
            }
        }

        public string CurrentSettingMatchText
        {
            get
            {
                try
                {
                    var results = settingsBackupAndRestoreUtils.GetLastBackupSettingsResults();

                    var resultText = string.Empty;

                    if (!results.LastRan.HasValue)
                    {
                        // not ran since started.
                        return GetResourceString("General_SettingsBackupAndRestore_CurrentSettingsNoChecked"); // "Current Settings Unknown";
                    }
                    else
                    {
                        if (results.Success)
                        {
                            if (results.LastBackupExists)
                            {
                                // if true, it means a backup would have been made
                                resultText = GetResourceString("General_SettingsBackupAndRestore_CurrentSettingsDiffer"); // "Current Settings Differ";
                            }
                            else
                            {
                                // would have done the backup, but there also was not an existing one there.
                                resultText = GetResourceString("General_SettingsBackupAndRestore_NoBackupFound");
                            }
                        }
                        else
                        {
                            if (results.HadError)
                            {
                                // if false and error we don't really know
                                resultText = GetResourceString("General_SettingsBackupAndRestore_CurrentSettingsUnknown"); // "Current Settings Unknown";
                            }
                            else
                            {
                                // if false, it means a backup would not have been needed/made
                                resultText = GetResourceString("General_SettingsBackupAndRestore_CurrentSettingsMatch"); // "Current Settings Match";
                            }
                        }

                        return $"{resultText} {GetResourceString("General_SettingsBackupAndRestore_CurrentSettingsStatusAt")} {results.LastRan.Value.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)}";
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError("Error getting CurrentSettingMatchText", e);
                    return string.Empty;
                }
            }
        }

        public string LastSettingsBackupSource
        {
            get
            {
                try
                {
                    var manifest = settingsBackupAndRestoreUtils.GetLatestSettingsBackupManifest();
                    if (manifest != null)
                    {
                        if (manifest["BackupSource"] != null)
                        {
                            if (manifest["BackupSource"].ToString().Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                            {
                                return GetResourceString("General_SettingsBackupAndRestore_ThisMachine");
                            }
                            else
                            {
                                return manifest["BackupSource"].ToString();
                            }
                        }
                        else
                        {
                            return GetResourceString("General_SettingsBackupAndRestore_UnknownBackupSource");
                        }
                    }
                    else
                    {
                        return GetResourceString("General_SettingsBackupAndRestore_NoBackupFound");
                    }
                }
                catch (Exception e)
                {
                    Logger.LogError("Error getting LastSettingsBackupSource", e);
                    return GetResourceString("General_SettingsBackupAndRestore_UnknownBackupSource");
                }
            }
        }

        public string LastSettingsBackupFileName
        {
            get
            {
                try
                {
                    var fileName = settingsBackupAndRestoreUtils.GetLatestBackupFileName();
                    return !string.IsNullOrEmpty(fileName) ? fileName : GetResourceString("General_SettingsBackupAndRestore_NoBackupFound");
                }
                catch (Exception e)
                {
                    Logger.LogError("Error getting LastSettingsBackupFileName", e);
                    return string.Empty;
                }
            }
        }

        public UpdatingSettings.UpdatingState PowerToysUpdatingState
        {
            get
            {
                return UpdatingSettings.UpdatingState.UpToDate;
            }

            private set
            {
                if (value != _updatingState)
                {
                    _updatingState = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string PowerToysNewAvailableVersion
        {
            get
            {
                return string.Empty;
            }

            private set
            {
                if (value != _newAvailableVersion)
                {
                    _newAvailableVersion = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string PowerToysNewAvailableVersionLink
        {
            get
            {
                return string.Empty;
            }

            private set
            {
                if (value != _newAvailableVersionLink)
                {
                    _newAvailableVersionLink = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public bool IsNewVersionDownloading
        {
            get
            {
                return false;
            }

            set
            {
                if (value != _isNewVersionDownloading)
                {
                    _isNewVersionDownloading = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public bool IsNewVersionCheckedAndUpToDate
        {
            get
            {
                return false;
            }
        }

        public bool IsNoNetwork
        {
            get
            {
                return false;
            }
        }

        public bool IsBugReportRunning
        {
            get
            {
                return _isBugReportRunning;
            }

            set
            {
                if (value != _isBugReportRunning)
                {
                    _isBugReportRunning = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public bool SettingsBackupRestoreMessageVisible
        {
            get
            {
                return _settingsBackupRestoreMessageVisible;
            }
        }

        public string BackupRestoreMessageSeverity
        {
            get
            {
                return _backupRestoreMessageSeverity;
            }
        }

        public string SettingsBackupMessage
        {
            get
            {
                return _settingsBackupMessage;
            }
        }

        public bool IsDownloadAllowed
        {
            get
            {
                return false;
            }
        }

        public bool IsUpdatePanelVisible
        {
            get
            {
                return false;
            }
        }

        public ObservableCollection<LanguageModel> Languages { get; } = new ObservableCollection<LanguageModel>();

        public int LanguagesIndex
        {
            get
            {
                return _languagesIndex;
            }

            set
            {
                if (_languagesIndex != value)
                {
                    _languagesIndex = value;
                    OnPropertyChanged(nameof(LanguagesIndex));
                    NotifyLanguageChanged();
                    if (_initLanguagesIndex != value)
                    {
                        LanguageChanged = true;
                    }
                    else
                    {
                        LanguageChanged = false;
                    }
                }
            }
        }

        public bool LanguageChanged
        {
            get
            {
                return _languageChanged;
            }

            set
            {
                if (_languageChanged != value)
                {
                    _languageChanged = value;
                    OnPropertyChanged(nameof(LanguageChanged));
                }
            }
        }

        public void NotifyPropertyChanged([CallerMemberName] string propertyName = null, bool reDoBackupDryRun = true)
        {
            // Notify UI of property change
            OnPropertyChanged(propertyName);

            OutGoingGeneralSettings outsettings = new OutGoingGeneralSettings(GeneralSettingsConfig);

            SendConfigMSG(outsettings.ToString());

            if (reDoBackupDryRun && DoBackupAndRestoreDryRun != null)
            {
                DoBackupAndRestoreDryRun(500);
            }
        }

        /// <summary>
        /// Method <c>SelectSettingBackupDir</c> opens folder browser to select a backup and restore location.
        /// </summary>
        private async void SelectSettingBackupDir()
        {
            var currentDir = settingsBackupAndRestoreUtils.GetSettingsBackupAndRestoreDir();

            var newPath = await PickSingleFolderDialog();

            if (!string.IsNullOrEmpty(newPath))
            {
                SettingsBackupAndRestoreDir = newPath;
                NotifyAllBackupAndRestoreProperties();
            }
        }

        private void RefreshBackupStatusEventHandlerClick()
        {
            DoBackupAndRestoreDryRun(0);
        }

        /// <summary>
        /// Method <c>RestoreConfigsClick</c> starts the restore.
        /// </summary>
        private void RestoreConfigsClick()
        {
            string settingsBackupAndRestoreDir = settingsBackupAndRestoreUtils.GetSettingsBackupAndRestoreDir();

            if (string.IsNullOrEmpty(settingsBackupAndRestoreDir))
            {
                SelectSettingBackupDir();
            }

            var results = SettingsUtils.RestoreSettings();
            _backupRestoreMessageSeverity = results.Severity;

            if (!results.Success)
            {
                _settingsBackupRestoreMessageVisible = true;

                _settingsBackupMessage = GetResourceString(results.Message);

                NotifyAllBackupAndRestoreProperties();

                HideBackupAndRestoreMessageAreaAction();
            }
            else
            {
                // make sure not to do NotifyPropertyChanged here, else it will persist the configs from memory and
                // undo the settings restore.
                SettingsBackupAndRestoreUtils.SetRegSettingsBackupAndRestoreItem("LastSettingsRestoreDate", DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture));

                Restart();
            }
        }

        /// <summary>
        /// Method <c>BackupConfigsClick</c> starts the backup.
        /// </summary>
        private void BackupConfigsClick()
        {
            string settingsBackupAndRestoreDir = settingsBackupAndRestoreUtils.GetSettingsBackupAndRestoreDir();

            if (string.IsNullOrEmpty(settingsBackupAndRestoreDir))
            {
                SelectSettingBackupDir();
            }

            var results = SettingsUtils.BackupSettings();

            _settingsBackupRestoreMessageVisible = true;
            _backupRestoreMessageSeverity = results.Severity;
            _settingsBackupMessage = GetResourceString(results.Message) + results.OptionalMessage;

            // now we do a dry run to get the results for "setting match"
            var settingsUtils = SettingsUtils.Default;
            var appBasePath = Path.GetDirectoryName(settingsUtils.GetSettingsFilePath());
            settingsBackupAndRestoreUtils.BackupSettings(appBasePath, settingsBackupAndRestoreDir, true);

            NotifyAllBackupAndRestoreProperties();

            HideBackupAndRestoreMessageAreaAction();
        }

        public void NotifyAllBackupAndRestoreProperties()
        {
            NotifyPropertyChanged(nameof(LastSettingsBackupDate), false);
            NotifyPropertyChanged(nameof(LastSettingsBackupSource), false);
            NotifyPropertyChanged(nameof(LastSettingsBackupFileName), false);
            NotifyPropertyChanged(nameof(CurrentSettingMatchText), false);
            NotifyPropertyChanged(nameof(SettingsBackupMessage), false);
            NotifyPropertyChanged(nameof(BackupRestoreMessageSeverity), false);
            NotifyPropertyChanged(nameof(SettingsBackupRestoreMessageVisible), false);
        }

        private void CheckForUpdatesClick()
        {
            // Update checks are disabled for Kit.
        }

        private void UpdateNowClick()
        {
            // Update installation is disabled for Kit.
        }

        /// <summary>
        /// Class <c>GetResourceString</c> gets a localized text.
        /// </summary>
        /// <remarks>
        /// To do: see if there is a betting way to do this, there should be. It does allow us to return missing localization in a way that makes it obvious they were missed.
        /// </remarks>
        public string GetResourceString(string resource)
        {
            if (ResourceLoader != null)
            {
                var result = ResourceLoader.GetString(resource);
                if (string.IsNullOrEmpty(result))
                {
                    return resource.ToUpperInvariant() + "!!!";
                }
                else
                {
                    return result;
                }
            }
            else
            {
                return resource;
            }
        }

        public void RequestUpdateCheckedDate()
        {
            // Update state refresh IPC is disabled for Kit.
        }

        public void RestartElevated()
        {
            GeneralSettingsConfig.CustomActionName = "restart_elevation";

            OutGoingGeneralSettings outsettings = new OutGoingGeneralSettings(GeneralSettingsConfig);
            GeneralSettingsCustomAction customaction = new GeneralSettingsCustomAction(outsettings);

            SendRestartAsAdminConfigMSG(customaction.ToString());
        }

        /// <summary>
        /// Class <c>Restart</c> begin a restart and signal we want to maintain elevation
        /// </summary>
        /// <remarks>
        /// Other restarts either raised or lowered elevation
        /// </remarks>
        public void Restart()
        {
            GeneralSettingsConfig.CustomActionName = "restart_maintain_elevation";

            OutGoingGeneralSettings outsettings = new OutGoingGeneralSettings(GeneralSettingsConfig);
            GeneralSettingsCustomAction customaction = new GeneralSettingsCustomAction(outsettings);

            var dataToSend = customaction.ToString();
            dataToSend = JsonSerializer.Serialize(ActionMessage.Create("restart_maintain_elevation"), SourceGenerationContextContext.Default.ActionMessage);
            SendRestartAsAdminConfigMSG(dataToSend);
        }

        /// <summary>
        /// Class <c>HideBackupAndRestoreMessageArea</c> hides the backup/restore message area
        /// </summary>
        /// <remarks>
        /// We want to have it go away after a short period.
        /// </remarks>
        public void HideBackupAndRestoreMessageArea()
        {
            _settingsBackupRestoreMessageVisible = false;
            NotifyAllBackupAndRestoreProperties();
        }

        public void RefreshUpdatingState()
        {
            // Update state is fixed to no-update values for Kit.
        }

        public override void OnPageLoaded()
        {
            base.OnPageLoaded();
            RunDeferredStartupMaintenance();
        }

        private void RunDeferredStartupMaintenance()
        {
            if (_deferredStartupMaintenanceQueued)
            {
                return;
            }

            _deferredStartupMaintenanceQueued = true;
            _ = Task.Run(DeleteOldDiagnosticData);
        }

        private void DeleteOldDiagnosticData()
        {
            string etwDirPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit", "etw");
            DeleteDiagnosticDataOlderThan28Days(etwDirPath);

            string localLowEtwDirPath = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE"), "AppData", "LocalLow", "Kit", "etw");
            DeleteDiagnosticDataOlderThan28Days(localLowEtwDirPath);
        }

        private void InitializeLanguages()
        {
            var lang = LanguageModel.LoadSetting();
            var selectedLanguageIndex = 0;

            foreach (var item in langTagsAndIds)
            {
                var language = new LanguageModel { Tag = item.Key, ResourceID = item.Value, Language = GetResourceString(item.Value) };
                var index = GetLanguageIndex(language.Language, item.Key == string.Empty);
                Languages.Insert(index, language);

                if (item.Key.Equals(lang, StringComparison.Ordinal))
                {
                    selectedLanguageIndex = index;
                }
                else if (index <= selectedLanguageIndex)
                {
                    selectedLanguageIndex++;
                }
            }

            _initLanguagesIndex = selectedLanguageIndex;
            LanguagesIndex = selectedLanguageIndex;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1309:Use ordinal string comparison", Justification = "Building a user facing list")]
        private int GetLanguageIndex(string language, bool isDefault)
        {
            if (Languages.Count == 0 || isDefault)
            {
                return 0;
            }

            for (var i = 1; i < Languages.Count; i++)
            {
                if (string.Compare(Languages[i].Language, language, StringComparison.CurrentCultureIgnoreCase) > 0)
                {
                    return i;
                }
            }

            return Languages.Count;
        }

        private void NotifyLanguageChanged()
        {
            OutGoingLanguageSettings outsettings = new OutGoingLanguageSettings(Languages[_languagesIndex].Tag);

            SendConfigMSG(outsettings.ToString());
        }

        internal void RefreshSettingsOnExternalChange()
        {
            NotifyPropertyChanged(nameof(EnableDataDiagnostics));
        }

        // Per retention policy
        private void DeleteDiagnosticDataOlderThan28Days(string etwDirPath)
        {
            if (!Directory.Exists(etwDirPath))
            {
                return;
            }

            var directoryInfo = new DirectoryInfo(etwDirPath);
            var cutoffDate = DateTime.Now.AddDays(-28);

            foreach (var file in directoryInfo.GetFiles())
            {
                if (file.LastWriteTime < cutoffDate)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Failed to delete file: {file.FullName}. Error: {ex.Message}");
                    }
                }
            }
        }

        internal void ViewDiagnosticData()
        {
        }

        private void OnSettingsChanged(GeneralSettings newSettings)
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                GeneralSettingsConfig = newSettings;

                if (_enableQuickAccess != newSettings.EnableQuickAccess)
                {
                    _enableQuickAccess = newSettings.EnableQuickAccess;
                    OnPropertyChanged(nameof(EnableQuickAccess));
                }
            });
        }

        public override void Dispose()
        {
            base.Dispose();
            if (_settingsRepository != null)
            {
                _settingsRepository.SettingsChanged -= OnSettingsChanged;
            }

            GC.SuppressFinalize(this);
        }

        protected virtual Microsoft.UI.Dispatching.DispatcherQueue GetDispatcherQueue()
        {
            return Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        }
    }
}
