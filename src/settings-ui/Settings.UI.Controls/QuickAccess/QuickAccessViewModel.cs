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

        private bool GetItemVisibility(ModuleType moduleType)
        {
            // Generally, if gpo is enabled or if module enabled, then quick access item is visible.
            return _isModuleGpoEnabled(moduleType) || Microsoft.PowerToys.Settings.UI.Library.Helpers.ModuleHelper.GetIsModuleEnabled(_generalSettings, moduleType);
        }

        private string GetModuleToolTip(ModuleType moduleType)
        {
            return moduleType switch
            {
                ModuleType.Awake => GetAwakeModeToolTip(),
                ModuleType.LightSwitch => SettingsRepository<LightSwitchSettings>.GetInstance(SettingsUtils.Default).SettingsConfig.Properties.ToggleThemeHotkey.Value.ToString(),
                ModuleType.Monitor => SettingsRepository<MonitorSettings>.GetInstance(SettingsUtils.Default).SettingsConfig.Properties.DownloadsPath.Value,
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
    }
}
