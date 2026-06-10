// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Library.Interfaces;

namespace Microsoft.PowerToys.Settings.UI.Services
{
    /// <summary>
    /// Factory service for getting Kit hotkey settings used by the shortcut conflict window.
    /// </summary>
    public class SettingsFactory
    {
        private const string GeneralSettingsModuleKey = "GeneralSettings";

        private readonly SettingsUtils _settingsUtils;
        private readonly IReadOnlyDictionary<string, Func<IHotkeyConfig>> _settingsLoaders;

        public SettingsFactory(SettingsUtils settingsUtils)
        {
            _settingsUtils = settingsUtils ?? throw new ArgumentNullException(nameof(settingsUtils));
            _settingsLoaders = new Dictionary<string, Func<IHotkeyConfig>>(StringComparer.OrdinalIgnoreCase)
            {
                [GeneralSettingsModuleKey] = () => SettingsRepository<GeneralSettings>.GetInstance(_settingsUtils).SettingsConfig,
                [LightSwitchSettings.ModuleName] = () => SettingsRepository<LightSwitchSettings>.GetInstance(_settingsUtils).SettingsConfig,
                [PowerDisplaySettings.ModuleName] = () => SettingsRepository<PowerDisplaySettings>.GetInstance(_settingsUtils).SettingsConfig,
            };
        }

        /// <summary>
        /// Gets a settings instance for the specified module using SettingsRepository
        /// </summary>
        /// <param name="moduleKey">The module key/name</param>
        /// <returns>The settings instance implementing IHotkeyConfig, or null if not found</returns>
        public IHotkeyConfig GetSettings(string moduleKey)
        {
            moduleKey = string.IsNullOrEmpty(moduleKey) ? GeneralSettingsModuleKey : moduleKey;
            if (!_settingsLoaders.TryGetValue(moduleKey, out var loadSettings))
            {
                return null;
            }

            try
            {
                return loadSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting Settings for {moduleKey}: {ex.Message}");
            }

            return null;
        }
    }
}
