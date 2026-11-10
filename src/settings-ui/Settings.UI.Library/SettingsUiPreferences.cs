// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Kit.Settings.UI.Library.Interfaces;

namespace Kit.Settings.UI.Library
{
    // Window presentation belongs to Settings, not to the Runner's general settings.
    public sealed class SettingsUiPreferences : ISettingsConfig
    {
        public const string FileName = "settings-ui.json";

        [JsonPropertyName("navigation_pane_open")]
        public bool NavigationPaneOpen { get; set; } = true;

        public string ToJsonString() => JsonSerializer.Serialize(this, SettingsSerializationContext.Default.SettingsUiPreferences);

        public string GetModuleName() => string.Empty;

        public bool UpgradeSettingsConfiguration() => false;
    }
}
