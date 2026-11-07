// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.Library.Interfaces;
using ManagedCommon;

namespace Kit.Settings.UI.Library
{
    public class MouseWithoutBordersSettings : BasePTModuleSettings, ISettingsConfig
    {
        public const string ModuleName = "MouseWithoutBorders";

        private static readonly JsonSerializerOptions _serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            MaxDepth = 0,
            IncludeFields = true,
        };

        [JsonPropertyName("properties")]
        public MouseWithoutBordersProperties Properties { get; set; }

        public MouseWithoutBordersSettings()
        {
            Name = ModuleName;
            Properties = new MouseWithoutBordersProperties();
            Version = "1.1";
        }

        public string GetModuleName()
        {
            return Name;
        }

        public ModuleType GetModuleType() => ModuleType.MouseWithoutBorders;

        public bool UpgradeSettingsConfiguration()
        {
            if (Version == "1.0")
            {
                Version = "1.1";
                return true;
            }

            return false;
        }

        public virtual void Save(SettingsUtils settingsUtils)
        {
            // Save settings to file
            var options = _serializerOptions;

            ArgumentNullException.ThrowIfNull(settingsUtils);

            settingsUtils.SaveSettings(JsonSerializer.Serialize(this, options), ModuleName);
        }
    }
}
