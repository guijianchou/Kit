// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.Library.Interfaces;
using ManagedCommon;

namespace Kit.Settings.UI.Library
{
    public class WorkspacesSettings : BasePTModuleSettings, ISettingsConfig
    {
        public const string ModuleName = "Workspaces";
        public const string ModuleVersion = "0.0.1";
        private static readonly JsonSerializerOptions _serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        public WorkspacesSettings()
        {
            Name = ModuleName;
            Version = ModuleVersion;
            Properties = new WorkspacesProperties();
        }

        [JsonPropertyName("properties")]
        public WorkspacesProperties Properties { get; set; }

        public string GetModuleName()
        {
            return Name;
        }

        public bool UpgradeSettingsConfiguration()
        {
            return false;
        }

        public ModuleType GetModuleType() => ModuleType.Workspaces;

        public virtual void Save(SettingsUtils settingsUtils)
        {
            // Save settings to file
            var options = _serializerOptions;

            ArgumentNullException.ThrowIfNull(settingsUtils);

            settingsUtils.SaveSettings(JsonSerializer.Serialize(this, options), ModuleName);
        }
    }
}
