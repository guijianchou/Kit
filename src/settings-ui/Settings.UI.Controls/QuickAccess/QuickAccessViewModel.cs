// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Library.Helpers;
using Microsoft.PowerToys.Settings.UI.Library.Interfaces;
using Microsoft.PowerToys.Settings.UI.Library.ViewModels.Commands;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Microsoft.PowerToys.Settings.UI.Controls
{
    public partial class QuickAccessViewModel : Observable
    {
        private readonly ISettingsRepository<GeneralSettings> _settingsRepository;

        // Pulling in KBMSettingsRepository separately as we need to listen to changes in the
        // UseNewEditor property to determine the visibility of the KeyboardManager quick access item.
        private readonly SettingsRepository<KeyboardManagerSettings> _kbmSettingsRepository;
        private readonly IQuickAccessLauncher _launcher;
        private readonly Func<ModuleType, bool> _isModuleGpoDisabled;
        private readonly Func<ModuleType, bool> _isModuleGpoEnabled;
        private readonly ResourceLoader _resourceLoader;
        private readonly IEnumerable<ModuleType> _moduleTypes;
        private readonly Func<ModuleType, bool>? _fallbackLauncher;
        private readonly DispatcherQueue _dispatcherQueue;
        private GeneralSettings _generalSettings;

        public ObservableCollection<QuickAccessItem> Items { get; } = new();

        private int _visibleItemCount;

        public int VisibleItemCount
        {
            get => _visibleItemCount;
            private set => Set(ref _visibleItemCount, value);
        }

        public QuickAccessViewModel(
            ISettingsRepository<GeneralSettings> settingsRepository,
            IQuickAccessLauncher launcher,
            Func<ModuleType, bool> isModuleGpoDisabled,
            Func<ModuleType, bool> isModuleGpoEnabled,
            ResourceLoader resourceLoader,
            IEnumerable<ModuleType>? moduleTypes = null,
            Func<ModuleType, bool>? fallbackLauncher = null)
        {
            _settingsRepository = settingsRepository;
            _launcher = launcher;
            _isModuleGpoDisabled = isModuleGpoDisabled;
            _isModuleGpoEnabled = isModuleGpoEnabled;
            _resourceLoader = resourceLoader;
            _moduleTypes = moduleTypes ?? KitModuleCatalog.QuickAccessModules;
            _fallbackLauncher = fallbackLauncher;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            _generalSettings = _settingsRepository.SettingsConfig;
            _generalSettings.AddEnabledModuleChangeNotification(ModuleEnabledChanged);
            _settingsRepository.SettingsChanged += OnSettingsChanged;

            _kbmSettingsRepository = SettingsRepository<KeyboardManagerSettings>.GetInstance(SettingsUtils.Default);
            _kbmSettingsRepository.SettingsChanged += OnKbmSettingsChanged;

            InitializeItems();
        }

        private void OnSettingsChanged(GeneralSettings newSettings)
        {
            if (_dispatcherQueue != null)
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    _generalSettings = newSettings;
                    _generalSettings.AddEnabledModuleChangeNotification(ModuleEnabledChanged);
                    RefreshItemsVisibility();
                });
            }
        }

        private void InitializeItems()
        {
            foreach (ModuleType moduleType in _moduleTypes)
            {
                AddFlyoutMenuItem(moduleType);
            }

            RefreshVisibleItemCount();
        }

        private void AddFlyoutMenuItem(ModuleType moduleType)
        {
            if (_isModuleGpoDisabled(moduleType))
            {
                return;
            }

            Items.Add(new QuickAccessItem
            {
                Title = _resourceLoader.GetString(Microsoft.PowerToys.Settings.UI.Library.Helpers.ModuleHelper.GetModuleLabelResourceName(moduleType)),
                Tag = moduleType,
                Visible = GetItemVisibility(moduleType),
                Description = GetModuleToolTip(moduleType),
                Icon = Microsoft.PowerToys.Settings.UI.Library.Helpers.ModuleHelper.GetModuleTypeFluentIconName(moduleType),
                Command = new RelayCommand(() =>
                {
                    if (!_launcher.Launch(moduleType))
                    {
                        _fallbackLauncher?.Invoke(moduleType);
                    }
                }),
            });
        }

        private void ModuleEnabledChanged()
        {
            if (_dispatcherQueue != null)
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    _generalSettings = _settingsRepository.SettingsConfig;
                    _generalSettings.AddEnabledModuleChangeNotification(ModuleEnabledChanged);
                    RefreshItemsVisibility();
                });
            }
        }

        private void RefreshItemsVisibility()
        {
            foreach (var item in Items)
            {
                if (item.Tag is ModuleType moduleType)
                {
                    bool visible = GetItemVisibility(moduleType);

                    item.Visible = visible;
                }
            }

            RefreshVisibleItemCount();
        }

        private void RefreshVisibleItemCount()
        {
            VisibleItemCount = Items.Count(item => item.Visible);
        }

        private void OnKbmSettingsChanged(KeyboardManagerSettings newSettings)
        {
            if (_dispatcherQueue != null)
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    RefreshItemsVisibility();
                });
            }
        }

        private bool GetItemVisibility(ModuleType moduleType)
        {
            // Generally, if gpo is enabled or if module enabled, then quick access item is visible.
            bool visible = _isModuleGpoEnabled(moduleType) || Microsoft.PowerToys.Settings.UI.Library.Helpers.ModuleHelper.GetIsModuleEnabled(_generalSettings, moduleType);

            // For KeyboardManager Quick Access item is only shown when using the new editor
            if (moduleType == ModuleType.KeyboardManager)
            {
                visible = visible && _kbmSettingsRepository.SettingsConfig.Properties.UseNewEditor;
            }

            return visible;
        }

        private string GetModuleToolTip(ModuleType moduleType)
        {
            return moduleType switch
            {
                ModuleType.Awake => GetAwakeModeToolTip(),
                ModuleType.ColorPicker => SettingsRepository<ColorPickerSettings>.GetInstance(SettingsUtils.Default).SettingsConfig.Properties.ActivationShortcut.ToString(),
                ModuleType.FancyZones => SettingsRepository<FancyZonesSettings>.GetInstance(SettingsUtils.Default).SettingsConfig.Properties.FancyzonesEditorHotkey.Value.ToString(),
                ModuleType.KeyboardManager => SettingsRepository<KeyboardManagerSettings>.GetInstance(SettingsUtils.Default).SettingsConfig.Properties.DefaultEditorShortcut.ToString(),
                ModuleType.LightSwitch => SettingsRepository<LightSwitchSettings>.GetInstance(SettingsUtils.Default).SettingsConfig.Properties.ToggleThemeHotkey.Value.ToString(),
                ModuleType.Monitor => SettingsRepository<MonitorSettings>.GetInstance(SettingsUtils.Default).SettingsConfig.Properties.DownloadsPath.Value,
                ModuleType.PowerDisplay => SettingsRepository<PowerDisplaySettings>.GetInstance(SettingsUtils.Default).SettingsConfig.Properties.ActivationShortcut.ToString(),
                ModuleType.PowerLauncher => SettingsRepository<PowerLauncherSettings>.GetInstance(SettingsUtils.Default).SettingsConfig.Properties.OpenPowerLauncher.ToString(),
                ModuleType.PowerOCR => SettingsRepository<PowerOcrSettings>.GetInstance(SettingsUtils.Default).SettingsConfig.Properties.ActivationShortcut.ToString(),
                ModuleType.Workspaces => SettingsRepository<WorkspacesSettings>.GetInstance(SettingsUtils.Default).SettingsConfig.Properties.Hotkey.Value.ToString(),
                ModuleType.MeasureTool => SettingsRepository<MeasureToolSettings>.GetInstance(SettingsUtils.Default).SettingsConfig.Properties.ActivationShortcut.ToString(),
                ModuleType.ShortcutGuide => GetShortcutGuideToolTip(),
                _ => string.Empty,
            };
        }

        private string GetAwakeModeToolTip()
        {
            AwakeMode mode = SettingsRepository<AwakeSettings>.GetInstance(SettingsUtils.Default).SettingsConfig.Properties.Mode;

            return mode switch
            {
                AwakeMode.INDEFINITE => _resourceLoader.GetString("Awake_IndefiniteKeepAwakeSelector/Content"),
                AwakeMode.TIMED => _resourceLoader.GetString("Awake_TemporaryKeepAwakeSelector/Content"),
                AwakeMode.EXPIRABLE => _resourceLoader.GetString("Awake_ExpirableKeepAwakeSelector/Content"),
                _ => _resourceLoader.GetString("Awake_NoKeepAwakeSelector/Content"),
            };
        }

        private string GetShortcutGuideToolTip()
        {
            var shortcutGuideSettings = SettingsRepository<ShortcutGuideSettings>.GetInstance(SettingsUtils.Default).SettingsConfig;
            return shortcutGuideSettings.Properties.UseLegacyPressWinKeyBehavior.Value
                ? "Win"
                : shortcutGuideSettings.Properties.OpenShortcutGuide.ToString();
        }
    }
}
