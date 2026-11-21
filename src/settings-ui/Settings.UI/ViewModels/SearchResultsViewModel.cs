// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Services;

namespace Kit.Settings.UI.ViewModels
{
    public class SearchResultsViewModel : INotifyPropertyChanged
    {
        private ObservableCollection<SettingEntry> _moduleResults = new();
        private ObservableCollection<SettingsGroup> _groupedSettingsResults = new();
        private bool _hasNoResults;
        private int _totalResultsCount;
        private int _settingsResultsCount;
        private int _selectedFilterIndex;

        public ObservableCollection<SettingEntry> ModuleResults
        {
            get => _moduleResults;
            set
            {
                _moduleResults = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<SettingsGroup> GroupedSettingsResults
        {
            get => _groupedSettingsResults;
            set
            {
                _groupedSettingsResults = value;
                OnPropertyChanged();
            }
        }

        public bool HasNoResults
        {
            get => _hasNoResults;
            set
            {
                _hasNoResults = value;
                OnPropertyChanged();
            }
        }

        public int TotalResultsCount
        {
            get => _totalResultsCount;
            private set
            {
                _totalResultsCount = value;
                OnPropertyChanged();
            }
        }

        public int SettingsResultsCount
        {
            get => _settingsResultsCount;
            private set
            {
                _settingsResultsCount = value;
                OnPropertyChanged();
            }
        }

        public int SelectedFilterIndex
        {
            get => _selectedFilterIndex;
            set
            {
                if (_selectedFilterIndex != value)
                {
                    _selectedFilterIndex = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowModulesSection));
                    OnPropertyChanged(nameof(ShowSettingsSection));
                }
            }
        }

        public bool ShowFilterBar => !HasNoResults && ModuleResults.Count > 0 && GroupedSettingsResults.Count > 0;

        public bool ShowModulesSection => ModuleResults.Count > 0 && SelectedFilterIndex != 2;

        public bool ShowSettingsSection => GroupedSettingsResults.Count > 0 && SelectedFilterIndex != 1;

        public void SetSearchResults(string query, List<SettingEntry> results)
        {
            _selectedFilterIndex = 0;
            if (results == null || results.Count == 0)
            {
                HasNoResults = true;
                TotalResultsCount = 0;
                SettingsResultsCount = 0;
                ModuleResults.Clear();
                GroupedSettingsResults.Clear();
                NotifyFilterProperties();
                return;
            }

            HasNoResults = false;

            // Separate modules and settings
            var modules = results.Where(r => r.Type == EntryType.SettingsPage).ToList();
            var settings = results.Where(r => r.Type == EntryType.SettingsCard).ToList();

            // Update module results
            ModuleResults.Clear();
            foreach (var module in modules)
            {
                ModuleResults.Add(module);
            }

            // Group settings by their page/module
            var groupedSettings = settings
                .GroupBy(s => SearchIndexService.GetLocalizedPageName(s.PageTypeName))
                .Select(g => new SettingsGroup
                {
                    GroupName = g.Key,
                    Settings = new ObservableCollection<SettingEntry>(g),
                })
                .ToList();

            GroupedSettingsResults.Clear();
            foreach (var group in groupedSettings)
            {
                GroupedSettingsResults.Add(group);
            }

            SettingsResultsCount = settings.Count;
            TotalResultsCount = modules.Count + settings.Count;
            NotifyFilterProperties();
        }

        private void NotifyFilterProperties()
        {
            OnPropertyChanged(nameof(SelectedFilterIndex));
            OnPropertyChanged(nameof(ShowFilterBar));
            OnPropertyChanged(nameof(ShowModulesSection));
            OnPropertyChanged(nameof(ShowSettingsSection));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

#pragma warning disable SA1402 // File may only contain a single type
    public class SettingsGroup : INotifyPropertyChanged
#pragma warning restore SA1402 // File may only contain a single type
    {
        private string _groupName;
        private ObservableCollection<SettingEntry> _settings;

        public string GroupName
        {
            get => _groupName;
            set
            {
                _groupName = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<SettingEntry> Settings
        {
            get => _settings;
            set
            {
                _settings = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
