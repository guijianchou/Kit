// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using CommunityToolkit.WinUI.Controls;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Kit.Settings.UI.Views
{
    public partial class SearchResultsPage : NavigablePage
    {
        public SearchResultsViewModel ViewModel { get; set; }

        public SearchResultsPage()
        {
            ViewModel = new SearchResultsViewModel();
            InitializeComponent();
            DataContext = ViewModel;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is SearchResultsNavigationParams searchParams)
            {
                ViewModel.SetSearchResults(searchParams.Query, searchParams.Results);
                var loader = ResourceLoaderInstance.ResourceLoader;
                var resultsFor = loader.GetString("Search_ResultsFor");
                PageControl.ModuleDescription = ViewModel.TotalResultsCount > 0
                    ? $"{resultsFor} '{searchParams.Query}' ({ViewModel.TotalResultsCount})"
                    : $"{resultsFor} '{searchParams.Query}'";

                var allLabel = loader.GetString("AIHub_Filter_All/Content");
                if (string.IsNullOrWhiteSpace(allLabel))
                {
                    allLabel = "All";
                }

                var modulesLabel = loader.GetString("SearchResults_ModulesTitle/Header");
                if (string.IsNullOrWhiteSpace(modulesLabel))
                {
                    modulesLabel = "Modules";
                }

                var settingsLabel = loader.GetString("Shell_Settings/Content");
                if (string.IsNullOrWhiteSpace(settingsLabel))
                {
                    settingsLabel = "Settings";
                }

                FilterAllItem.Text = $"{allLabel} ({ViewModel.TotalResultsCount})";
                FilterModulesItem.Text = $"{modulesLabel} ({ViewModel.ModuleResults.Count})";
                FilterSettingsItem.Text = $"{settingsLabel} ({ViewModel.SettingsResultsCount})";
                ResultsFilterBar.SelectedItem = FilterAllItem;
            }
        }

        private void ResultsFilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (sender.SelectedItem is SelectorBarItem item &&
                int.TryParse(item.Tag?.ToString(), out int filterIndex))
            {
                ViewModel.SelectedFilterIndex = filterIndex;
            }
        }

        private void ModuleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is SettingsCard card && card.DataContext is SettingEntry tagEntry)
            {
                NavigateToModule(tagEntry);
            }
        }

        private void SettingButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is SettingsCard card && card.DataContext is SettingEntry tagEntry)
            {
                NavigateToSetting(tagEntry);
            }
        }

        private void NavigateToModule(SettingEntry settingEntry)
        {
            // Get the page type from the setting entry
            var pageType = GetPageTypeFromName(settingEntry.PageTypeName);
            if (pageType != null)
            {
                NavigationService.Navigate(pageType);
            }
        }

        private void NavigateToSetting(SettingEntry settingEntry)
        {
            // Get the page type from the setting entry
            var pageType = GetPageTypeFromName(settingEntry.PageTypeName);
            if (pageType != null)
            {
                // Create navigation parameters to highlight the specific setting
                var navigationParams = new NavigationParams(settingEntry.ElementName, settingEntry.ParentElementName);
                NavigationService.Navigate(pageType, navigationParams);
            }
        }

        private Type GetPageTypeFromName(string pageTypeName)
        {
            if (string.IsNullOrEmpty(pageTypeName))
            {
                return null;
            }

            var assembly = typeof(GeneralPage).Assembly;
            return assembly.GetType($"Kit.Settings.UI.Views.{pageTypeName}");
        }
    }

#pragma warning disable SA1402 // File may only contain a single type
    public class SearchResultsNavigationParams
#pragma warning restore SA1402 // File may only contain a single type
    {
        public string Query { get; set; }

        public List<SettingEntry> Results { get; set; }

        public SearchResultsNavigationParams(string query, List<SettingEntry> results)
        {
            Query = query;
            Results = results;
        }
    }
}
