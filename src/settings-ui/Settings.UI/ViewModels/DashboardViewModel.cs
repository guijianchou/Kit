// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

using global::PowerToys.GPOWrapper;
using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Controls;
using Microsoft.PowerToys.Settings.UI.Helpers;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Library.Helpers;
using Microsoft.PowerToys.Settings.UI.Library.Interfaces;
using Microsoft.PowerToys.Settings.UI.Library.Utilities;
using Microsoft.PowerToys.Settings.UI.Services;
using Microsoft.PowerToys.Settings.UI.Views;
using Microsoft.UI.Dispatching;
using Settings.UI.Library;

namespace Microsoft.PowerToys.Settings.UI.ViewModels
{
    public partial class DashboardViewModel : PageViewModelBase
    {
        private readonly object _sortLock = new object();

        protected override string ModuleName => "Dashboard";

        private readonly DispatcherQueue dispatcher;
        private readonly ISettingsRepository<GeneralSettings> settingsRepository;
        private readonly QuickAccessViewModel quickAccessViewModel;
        private readonly List<DashboardListItem> moduleItems = new List<DashboardListItem>();
        private readonly Windows.ApplicationModel.Resources.ResourceLoader resourceLoader = ResourceLoaderInstance.ResourceLoader;

        private GeneralSettings generalSettingsConfig;
        private DashboardSortOrder dashboardSortOrder;
        private bool isUpdatingFromUI;
        private bool isSorting;
        private bool isDisposed;

        public Func<string, int> SendConfigMSG { get; }

        public ObservableCollection<DashboardListItem> AllModules { get; } = new ObservableCollection<DashboardListItem>();

        public ObservableCollection<DashboardListItem> ShortcutModules { get; } = new ObservableCollection<DashboardListItem>();

        public ObservableCollection<DashboardListItem> ActionModules { get; } = new ObservableCollection<DashboardListItem>();

        public ObservableCollection<QuickAccessItem> QuickAccessItems => quickAccessViewModel.Items;

        public int VisibleQuickAccessItemsCount => quickAccessViewModel.VisibleItemCount;

        public string PowerToysVersion => Helper.GetProductVersion();

        public DashboardSortOrder DashboardSortOrder
        {
            get => generalSettingsConfig.DashboardSortOrder;
            set
            {
                if (dashboardSortOrder != value)
                {
                    dashboardSortOrder = value;
                    generalSettingsConfig.DashboardSortOrder = value;
                    SendConfigMSG(new OutGoingGeneralSettings(generalSettingsConfig).ToString());
                    OnPropertyChanged(nameof(DashboardSortOrder));
                    SortModuleList();
                }
            }
        }

        public DashboardViewModel(ISettingsRepository<GeneralSettings> settingsRepository, Func<string, int> ipcMSGCallBackFunc)
        {
            dispatcher = DispatcherQueue.GetForCurrentThread();
            this.settingsRepository = settingsRepository;
            generalSettingsConfig = settingsRepository.SettingsConfig;
            dashboardSortOrder = generalSettingsConfig.DashboardSortOrder;

            this.settingsRepository.SettingsChanged += OnSettingsChanged;
            SendConfigMSG = ipcMSGCallBackFunc;

            quickAccessViewModel = new QuickAccessViewModel(
                this.settingsRepository,
                new QuickAccessLauncher(App.IsElevated),
                moduleType => ModuleGpoHelper.GetModuleGpoConfiguration(moduleType) == GpoRuleConfigured.Disabled,
                moduleType => ModuleGpoHelper.GetModuleGpoConfiguration(moduleType) == GpoRuleConfigured.Enabled,
                resourceLoader,
                moduleTypes: KitModuleCatalog.DashboardModules,
                fallbackLauncher: OpenModuleSettingsFromQuickAccess);
            quickAccessViewModel.PropertyChanged += OnQuickAccessPropertyChanged;

            BuildModuleList();
            SortModuleList();
            RefreshShortcutModules();
        }

        private void OnQuickAccessPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(QuickAccessViewModel.VisibleItemCount))
            {
                OnPropertyChanged(nameof(VisibleQuickAccessItemsCount));
            }
        }

        private bool OpenModuleSettingsFromQuickAccess(ModuleType moduleType)
        {
            NavigationService.Navigate(ModuleGpoHelper.GetModulePageType(moduleType));
            return true;
        }

        private void OnSettingsChanged(GeneralSettings newSettings)
        {
            if (isDisposed)
            {
                return;
            }

            RunOnUiThread(() =>
            {
                if (isDisposed)
                {
                    return;
                }

                generalSettingsConfig = newSettings;

                if (dashboardSortOrder != generalSettingsConfig.DashboardSortOrder)
                {
                    dashboardSortOrder = generalSettingsConfig.DashboardSortOrder;
                    OnPropertyChanged(nameof(DashboardSortOrder));
                }

                ModuleEnabledChangedOnSettingsPage();
            });
        }

        private void BuildModuleList()
        {
            moduleItems.Clear();

            foreach (ModuleType moduleType in KitModuleCatalog.DashboardModules)
            {
                GpoRuleConfigured gpo = ModuleGpoHelper.GetModuleGpoConfiguration(moduleType);
                var newItem = new DashboardListItem()
                {
                    Tag = moduleType,
                    Label = resourceLoader.GetString(ModuleHelper.GetModuleLabelResourceName(moduleType)),
                    IsEnabled = gpo == GpoRuleConfigured.Enabled || (gpo != GpoRuleConfigured.Disabled && ModuleHelper.GetIsModuleEnabled(generalSettingsConfig, moduleType)),
                    IsLocked = gpo == GpoRuleConfigured.Enabled || gpo == GpoRuleConfigured.Disabled,
                    Icon = ModuleHelper.GetModuleTypeFluentIconName(moduleType),
                    IsNew = false,
                    DashboardModuleItems = GetModuleItems(moduleType),
                    ClickCommand = new RelayCommand<object>(DashboardListItemClick),
                };
                newItem.EnabledChangedCallback = EnabledChangedOnUI;
                moduleItems.Add(newItem);
            }
        }

        private void SortModuleList()
        {
            if (isSorting)
            {
                return;
            }

            lock (_sortLock)
            {
                isSorting = true;
                try
                {
                    var sortedItems = (DashboardSortOrder switch
                    {
                        DashboardSortOrder.ByStatus => moduleItems.OrderByDescending(x => x.IsEnabled).ThenBy(x => x.Label),
                        _ => moduleItems.OrderBy(x => x.Label),
                    }).ToList();

                    if (AllModules.Count == 0)
                    {
                        foreach (var item in sortedItems)
                        {
                            AllModules.Add(item);
                        }

                        return;
                    }

                    for (int i = 0; i < sortedItems.Count; i++)
                    {
                        var currentItem = sortedItems[i];
                        var currentIndex = AllModules.IndexOf(currentItem);

                        if (currentIndex != -1 && currentIndex != i)
                        {
                            AllModules.Move(currentIndex, i);
                        }
                    }
                }
                finally
                {
                    RunOnUiThread(() => isSorting = false);
                }
            }
        }

        private void RefreshModuleList()
        {
            foreach (var item in moduleItems)
            {
                GpoRuleConfigured gpo = ModuleGpoHelper.GetModuleGpoConfiguration(item.Tag);
                bool newEnabledState = gpo == GpoRuleConfigured.Enabled || (gpo != GpoRuleConfigured.Disabled && ModuleHelper.GetIsModuleEnabled(generalSettingsConfig, item.Tag));
                bool newLockedState = gpo == GpoRuleConfigured.Enabled || gpo == GpoRuleConfigured.Disabled;

                if (item.IsEnabled != newEnabledState)
                {
                    item.UpdateStatus(newEnabledState);
                }

                if (item.IsLocked != newLockedState)
                {
                    item.IsLocked = newLockedState;
                }
            }

            SortModuleList();
        }

        private void EnabledChangedOnUI(ModuleListItem item)
        {
            var dashboardListItem = (DashboardListItem)item;
            var isEnabled = dashboardListItem.IsEnabled;

            if (isSorting)
            {
                dashboardListItem.UpdateStatus(!isEnabled);
                return;
            }

            isUpdatingFromUI = true;
            try
            {
                string moduleKey = ModuleHelper.GetModuleKey(dashboardListItem.Tag);
                string moduleStatusJson = $"{{\"module_status\": {{\"{moduleKey}\": {isEnabled.ToString().ToLowerInvariant()}}}}}";
                SendConfigMSG(moduleStatusJson);

                ModuleHelper.SetIsModuleEnabled(generalSettingsConfig, dashboardListItem.Tag, isEnabled);

                if (DashboardSortOrder == DashboardSortOrder.ByStatus)
                {
                    SortModuleList();
                }

                RefreshShortcutModules();
            }
            finally
            {
                isUpdatingFromUI = false;
            }
        }

        public void ModuleEnabledChangedOnSettingsPage()
        {
            if (isDisposed || isUpdatingFromUI)
            {
                return;
            }

            try
            {
                RefreshModuleList();
                RefreshShortcutModules();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Updating active/disabled modules list failed: {ex.Message}");
            }
        }

        private void RefreshShortcutModules()
        {
            if (isDisposed)
            {
                return;
            }

            if (dispatcher != null && !dispatcher.HasThreadAccess)
            {
                _ = dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, RefreshShortcutModules);
                return;
            }

            ShortcutModules.Clear();
            ActionModules.Clear();

            foreach (var module in AllModules.Where(x => x.IsEnabled))
            {
                var shortcutItems = GetShortcutItemsForDashboardModule(module);

                if (shortcutItems.Count != 0)
                {
                    ShortcutModules.Add(CreateModuleProjection(module, shortcutItems));
                }

                var actionItems = module.DashboardModuleItems
                    .Where(m => m is DashboardModuleButtonItem)
                    .ToList();

                if (actionItems.Count != 0)
                {
                    ActionModules.Add(CreateModuleProjection(module, actionItems));
                }
            }
        }

        private static List<DashboardModuleItem> GetShortcutItemsForDashboardModule(DashboardListItem module)
        {
            return module.DashboardModuleItems
                .Where(m => m is DashboardModuleShortcutItem || (module.Tag != ModuleType.Monitor && m is DashboardModuleActivationItem))
                .ToList();
        }

        private static DashboardListItem CreateModuleProjection(DashboardListItem source, List<DashboardModuleItem> items)
        {
            return new DashboardListItem
            {
                Icon = source.Icon,
                IsLocked = source.IsLocked,
                Label = source.Label,
                Tag = source.Tag,
                IsEnabled = source.IsEnabled,
                EnabledChangedCallback = source.EnabledChangedCallback,
                DashboardModuleItems = new ObservableCollection<DashboardModuleItem>(items),
            };
        }

        private ObservableCollection<DashboardModuleItem> GetModuleItems(ModuleType moduleType)
        {
            return moduleType switch
            {
                ModuleType.Awake => GetModuleItemsAwake(),
                ModuleType.LightSwitch => GetModuleItemsLightSwitch(),
                ModuleType.Monitor => GetModuleItemsMonitor(),
                ModuleType.PowerDisplay => GetModuleItemsPowerDisplay(),
                _ => new ObservableCollection<DashboardModuleItem>(),
            };
        }

        private ObservableCollection<DashboardModuleItem> GetModuleItemsAwake()
        {
            ISettingsRepository<AwakeSettings> moduleSettingsRepository = SettingsRepository<AwakeSettings>.GetInstance(SettingsUtils.Default);
            var settings = moduleSettingsRepository.SettingsConfig;
            var list = new List<DashboardModuleItem>
            {
                new DashboardModuleActivationItem() { Label = resourceLoader.GetString("Awake_ModeSettingsCard/Header"), Activation = GetAwakeModeDisplayText(settings.Properties.Mode) },
            };

            return new ObservableCollection<DashboardModuleItem>(list);
        }

        private string GetAwakeModeDisplayText(AwakeMode mode)
        {
            return mode switch
            {
                AwakeMode.INDEFINITE => resourceLoader.GetString("Awake_IndefiniteKeepAwakeSelector/Content"),
                AwakeMode.TIMED => resourceLoader.GetString("Awake_TemporaryKeepAwakeSelector/Content"),
                AwakeMode.EXPIRABLE => resourceLoader.GetString("Awake_ExpirableKeepAwakeSelector/Content"),
                _ => resourceLoader.GetString("Awake_NoKeepAwakeSelector/Content"),
            };
        }

        private ObservableCollection<DashboardModuleItem> GetModuleItemsLightSwitch()
        {
            ISettingsRepository<LightSwitchSettings> moduleSettingsRepository = SettingsRepository<LightSwitchSettings>.GetInstance(SettingsUtils.Default);
            var settings = moduleSettingsRepository.SettingsConfig;
            var list = new List<DashboardModuleItem>
            {
                new DashboardModuleShortcutItem() { Label = resourceLoader.GetString("LightSwitch_ForceDarkMode"), Shortcut = settings.Properties.ToggleThemeHotkey.Value.GetKeysList() },
            };

            return new ObservableCollection<DashboardModuleItem>(list);
        }

        private ObservableCollection<DashboardModuleItem> GetModuleItemsMonitor()
        {
            ISettingsRepository<MonitorSettings> moduleSettingsRepository = SettingsRepository<MonitorSettings>.GetInstance(SettingsUtils.Default);
            var settings = moduleSettingsRepository.SettingsConfig;
            var list = new List<DashboardModuleItem>
            {
                new DashboardModuleActivationItem() { Label = resourceLoader.GetString("Monitor_DownloadsPathSettingsCard/Header"), Activation = settings.Properties.DownloadsPath.Value },
                new DashboardModuleActivationItem() { Label = resourceLoader.GetString("Monitor_RunInBackgroundSettingsCard/Header"), Activation = settings.Properties.RunInBackground.Value ? resourceLoader.GetString("Monitor_RunInBackgroundOn") : resourceLoader.GetString("Monitor_RunInBackgroundOff") },
                new DashboardModuleActivationItem() { Label = resourceLoader.GetString("Monitor_ScanIntervalSeconds/Header"), Activation = $"{settings.Properties.ScanIntervalSeconds.Value}s" },
            };

            return new ObservableCollection<DashboardModuleItem>(list);
        }

        private ObservableCollection<DashboardModuleItem> GetModuleItemsPowerDisplay()
        {
            ISettingsRepository<PowerDisplaySettings> moduleSettingsRepository = SettingsRepository<PowerDisplaySettings>.GetInstance(SettingsUtils.Default);
            var settings = moduleSettingsRepository.SettingsConfig;
            var list = new List<DashboardModuleItem>
            {
                new DashboardModuleShortcutItem() { Label = resourceLoader.GetString("PowerDisplay_ToggleWindow"), Shortcut = settings.Properties.ActivationShortcut.GetKeysList() },
                new DashboardModuleButtonItem() { ButtonTitle = resourceLoader.GetString("PowerDisplay_LaunchButtonControl/Header"), IsButtonDescriptionVisible = true, ButtonDescription = resourceLoader.GetString("PowerDisplay_LaunchButtonControl/Description"), ButtonGlyph = "ms-appx:///Assets/Settings/Icons/PowerDisplay.png", ButtonClickHandler = PowerDisplayLaunchClicked },
            };

            return new ObservableCollection<DashboardModuleItem>(list);
        }

        private void PowerDisplayLaunchClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            SendConfigMSG("{\"action\":{\"PowerDisplay\":{\"action_name\":\"Launch\", \"value\":\"\"}}}");
        }

        internal void DashboardListItemClick(object sender)
        {
            if (sender is ModuleType moduleType)
            {
                NavigationService.Navigate(ModuleGpoHelper.GetModulePageType(moduleType));
            }
        }

        private void RunOnUiThread(Action action)
        {
            if (dispatcher != null && !dispatcher.HasThreadAccess)
            {
                dispatcher.TryEnqueue(() => action());
                return;
            }

            action();
        }

        public override void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            base.Dispose();
            quickAccessViewModel.PropertyChanged -= OnQuickAccessPropertyChanged;
            settingsRepository.SettingsChanged -= OnSettingsChanged;
            GC.SuppressFinalize(this);
        }
    }
}
