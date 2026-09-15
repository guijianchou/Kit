// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Reflection;
using System.Text.Json.Serialization;
using Kit.Settings.UI.Library.Interfaces;

namespace Kit.Settings.UI.Library
{
    public class LocalserverSettings : BasePTModuleSettings, ISettingsConfig, ICloneable
    {
        public const string ModuleName = "Localserver";

        public LocalserverSettings()
        {
            Name = ModuleName;
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0";
            Properties = new LocalserverProperties();
        }

        [JsonPropertyName("properties")]
        public LocalserverProperties Properties { get; set; }

        public object Clone()
        {
            return new LocalserverSettings()
            {
                Name = Name,
                Version = Version,
                Properties = new LocalserverProperties()
                {
                    StopTimeoutSec = new IntProperty(Properties.StopTimeoutSec.Value),
                    MaxLogLines = new IntProperty(Properties.MaxLogLines.Value),
                },
            };
        }

        public string GetModuleName() => Name;

        public bool UpgradeSettingsConfiguration() => false;
    }
}
