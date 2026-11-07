// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text.Json.Serialization;
using Kit.Settings.UI.Library.Interfaces;

namespace Kit.Settings.UI.Library
{
    public sealed class WingetSettings : BasePTModuleSettings, ISettingsConfig, ICloneable
    {
        public const string ModuleName = "Winget";

        public WingetSettings()
        {
            Name = ModuleName;
            Version = "1.0";
        }

        [JsonPropertyName("properties")]
        public WingetProperties Properties { get; set; } = new();

        public object Clone() => new WingetSettings
        {
            Properties = new WingetProperties
            {
                DailyCheckEnabled = new BoolProperty(Properties.DailyCheckEnabled.Value),
                NotifyUpdates = new BoolProperty(Properties.NotifyUpdates.Value),
            },
        };

        public string GetModuleName() => ModuleName;

        public bool UpgradeSettingsConfiguration() => false;
    }
}
