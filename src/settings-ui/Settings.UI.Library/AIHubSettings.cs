// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Reflection;
using System.Text.Json.Serialization;
using Kit.Settings.UI.Library.Interfaces;

namespace Kit.Settings.UI.Library
{
    public class AIHubSettings : BasePTModuleSettings, ISettingsConfig, ICloneable
    {
        public const string ModuleName = "AIHub";

        public AIHubSettings()
        {
            Name = ModuleName;
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0";
            Properties = new AIHubProperties();
        }

        [JsonPropertyName("properties")]
        public AIHubProperties Properties { get; set; }

        public object Clone()
        {
            return new AIHubSettings()
            {
                Name = Name,
                Version = Version,
                Properties = new AIHubProperties()
                {
                    RetentionDays = new IntProperty(Properties.RetentionDays.Value),
                    ActiveTabIndex = new IntProperty(Properties.ActiveTabIndex.Value),
                    AuditMode = new IntProperty(Properties.AuditMode.Value),
                    ScanIntervalHours = new IntProperty(Properties.ScanIntervalHours.Value),
                },
            };
        }

        public string GetModuleName() => Name;

        public bool UpgradeSettingsConfiguration() => false;
    }
}
