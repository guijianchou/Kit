// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using global::Kit.GPOWrapper;
using ManagedCommon;
using Kit.QuickAccess.Helpers;
using Kit.QuickAccess.Services;
using Kit.Settings.UI.Controls;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.Library.Interfaces;
using Kit.Settings.UI.Library.ViewModels.Commands;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Kit.QuickAccess.ViewModels;

public sealed class AllAppsViewModel : Observable
{
    private readonly object _sortLock = new object();
    private readonly IQuickAccessCoordinator _coordinator;
    private readonly ISettingsRepository<GeneralSettings> _settingsRepository;
    private readonly SettingsUtils _settingsUtils;
    private readonly ResourceLoader _resourceLoader;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly List<FlyoutMenuItem> _allFlyoutMenuItems = new();
    private readonly Dictionary<ModuleType, (bool TargetState, long Timestamp)> _pendingTransitions = new();
    private static readonly long TransitionLockTicks = System.Diagnostics.Stopwatch.Frequency * 2; // 2.0s cold-start grace window
    private GeneralSettings _generalSettings;

    // Flag to prevent toggle operations during sorting to avoid race conditions.
    private bool _isSorting;

    public ObservableCollection<FlyoutMenuItem> FlyoutMenuItems { get; }

    public DashboardSortOrder DashboardSortOrder
    {
        get => _generalSettings.DashboardSortOrder;
        set
        {
            if (_generalSettings.DashboardSortOrder != value)
            {
                _generalSettings.DashboardSortOrder = value;
                _coordinator.SendSortOrderUpdate(_generalSettings);
                OnPropertyChanged();
                SortFlyoutMenuItems();
            }
        }
    }

    public AllAppsViewModel(IQuickAccessCoordinator coordinator)
    {
        _coordinator = coordinator;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _settingsUtils = SettingsUtils.Default;
        _settingsRepository = SettingsRepository<GeneralSettings>.GetInstance(_settingsUtils);
        _generalSettings = _settingsRepository.SettingsConfig;
        _settingsRepository.SettingsChanged += OnSettingsChanged;

        _resourceLoader = Helpers.ResourceLoaderInstance.ResourceLoader;
        FlyoutMenuItems = new ObservableCollection<FlyoutMenuItem>();

        BuildFlyoutMenuItems();
        RefreshFlyoutMenuItems();
    }

    private void BuildFlyoutMenuItems()
    {
        _allFlyoutMenuItems.Clear();
        foreach (ModuleType moduleType in KitModuleCatalog.ActiveModules)
        {
            var currentModule = moduleType;
            _allFlyoutMenuItems.Add(new FlyoutMenuItem
            {
                Tag = currentModule,
                EnabledChangedCallback = EnabledChangedOnUI,
                ClickCommand = new ButtonClickCommand(() => _coordinator.OpenModuleSettings(currentModule)),
            });
        }
    }

    private void OnSettingsChanged(GeneralSettings newSettings)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _generalSettings = newSettings;

            // Clear pending transition lock if incoming settings match target state
            lock (_pendingTransitions)
            {
                if (_pendingTransitions.Count > 0)
                {
                    var resolved = new List<ModuleType>();
                    foreach (var (mod, (target, _)) in _pendingTransitions)
                    {
                        if (Kit.Settings.UI.Library.Helpers.ModuleHelper.GetIsModuleEnabled(newSettings, mod) == target)
                        {
                            resolved.Add(mod);
                        }
                    }

                    foreach (var mod in resolved)
                    {
                        _pendingTransitions.Remove(mod);
                    }
                }
            }

            OnPropertyChanged(nameof(DashboardSortOrder));
            RefreshFlyoutMenuItems();
        });
    }

    public void RefreshSettings()
    {
        if (_settingsRepository.ReloadSettings())
        {
            OnSettingsChanged(_settingsRepository.SettingsConfig);
        }
    }

    private void RefreshFlyoutMenuItems()
    {
        foreach (var item in _allFlyoutMenuItems)
        {
            var moduleType = item.Tag;
            var gpo = Helpers.ModuleGpoHelper.GetModuleGpoConfiguration(moduleType);
            var isLocked = gpo is GpoRuleConfigured.Enabled or GpoRuleConfigured.Disabled;

            bool isPending = false;
            bool pendingTargetState = false;
            lock (_pendingTransitions)
            {
                if (_pendingTransitions.TryGetValue(moduleType, out var transition))
                {
                    long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - transition.Timestamp;
                    if (elapsed < TransitionLockTicks)
                    {
                        isPending = true;
                        pendingTargetState = transition.TargetState;
                    }
                    else
                    {
                        _pendingTransitions.Remove(moduleType);
                    }
                }
            }

            var isEnabled = isPending
                ? pendingTargetState
                : (gpo == GpoRuleConfigured.Enabled || (!isLocked && Kit.Settings.UI.Library.Helpers.ModuleHelper.GetIsModuleEnabled(_generalSettings, moduleType)));

            item.Label = _resourceLoader.GetString(Kit.Settings.UI.Library.Helpers.ModuleHelper.GetModuleLabelResourceName(moduleType));
            item.IsLocked = isLocked;
            item.Icon = Kit.Settings.UI.Library.Helpers.ModuleHelper.GetModuleTypeFluentIconName(moduleType);

            if (item.IsEnabled != isEnabled)
            {
                item.UpdateStatus(isEnabled);
            }
        }

        SortFlyoutMenuItems();
    }

    private void SortFlyoutMenuItems()
    {
        if (_isSorting)
        {
            return;
        }

        lock (_sortLock)
        {
            _isSorting = true;
            try
            {
                var sortedItems = DashboardSortOrder switch
                {
                    DashboardSortOrder.ByStatus => _allFlyoutMenuItems.OrderByDescending(x => x.IsEnabled).ThenBy(x => x.Label).ToList(),
                    _ => _allFlyoutMenuItems.OrderBy(x => x.Label).ToList(),
                };

                if (FlyoutMenuItems.Count == 0)
                {
                    foreach (var item in sortedItems)
                    {
                        FlyoutMenuItems.Add(item);
                    }

                    return;
                }

                for (int i = 0; i < sortedItems.Count; i++)
                {
                    var item = sortedItems[i];
                    var oldIndex = FlyoutMenuItems.IndexOf(item);

                    if (oldIndex != -1 && oldIndex != i)
                    {
                        FlyoutMenuItems.Move(oldIndex, i);
                    }
                }
            }
            finally
            {
                // Use dispatcher to reset flag after UI updates complete
                _dispatcherQueue.TryEnqueue(() =>
                {
                    _isSorting = false;
                });
            }
        }
    }

    private void EnabledChangedOnUI(ModuleListItem item)
    {
        var flyoutItem = (FlyoutMenuItem)item;
        var isEnabled = flyoutItem.IsEnabled;

        // Ignore toggle operations during sorting to prevent race conditions.
        // Revert the toggle state since UI already changed due to TwoWay binding.
        if (_isSorting)
        {
            flyoutItem.UpdateStatus(!isEnabled);
            return;
        }

        // Scheme A: Set transition lock to protect target state during cold start
        lock (_pendingTransitions)
        {
            _pendingTransitions[flyoutItem.Tag] = (isEnabled, System.Diagnostics.Stopwatch.GetTimestamp());
        }

        if (!_coordinator.UpdateModuleEnabled(flyoutItem.Tag, flyoutItem.IsEnabled))
        {
            lock (_pendingTransitions)
            {
                _pendingTransitions.Remove(flyoutItem.Tag);
            }

            flyoutItem.UpdateStatus(!isEnabled);
            return;
        }

        // Scheme B: Optimistically commit in-memory settings to align with DashboardViewModel
        Kit.Settings.UI.Library.Helpers.ModuleHelper.SetIsModuleEnabled(_generalSettings, flyoutItem.Tag, isEnabled);

        SortFlyoutMenuItems();
    }
}
