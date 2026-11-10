// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Text.Json;
using Kit.Settings.UI.Library;
using ManagedCommon;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.Helpers
{
    internal sealed class NavigationPaneState
    {
        private readonly SettingsUtils _settingsUtils;
        private readonly SettingsUiPreferences _preferences;

        public NavigationPaneState(SettingsUtils settingsUtils)
        {
            _settingsUtils = settingsUtils;
            try
            {
                _preferences = settingsUtils.GetSettings<SettingsUiPreferences>(fileName: SettingsUiPreferences.FileName);
            }
            catch (FileNotFoundException)
            {
                // Existing installations keep the expanded default until the user toggles it.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NullReferenceException)
            {
                Logger.LogWarning($"Could not load the Settings navigation preference; using the default ({ex.GetType().Name}).");
            }

            _preferences ??= new SettingsUiPreferences();
        }

        public bool IsPaneOpen => _preferences.NavigationPaneOpen;

        public bool ForDisplayMode(NavigationViewDisplayMode mode) => mode == NavigationViewDisplayMode.Expanded && IsPaneOpen;

        public void SetUserPreference(bool isPaneOpen)
        {
            _preferences.NavigationPaneOpen = isPaneOpen;
            try
            {
                _settingsUtils.SaveSettings(_preferences.ToJsonString(), fileName: SettingsUiPreferences.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning($"Could not save the Settings navigation preference ({ex.GetType().Name}).");
            }
        }
    }
}
