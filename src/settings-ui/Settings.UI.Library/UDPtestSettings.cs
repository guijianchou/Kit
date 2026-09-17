// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Reflection;
using System.Text.Json.Serialization;
using Kit.Settings.UI.Library.Interfaces;

namespace Kit.Settings.UI.Library
{
    public class UDPtestSettings : BasePTModuleSettings, ISettingsConfig, ICloneable
    {
        public const string ModuleName = "UDPtest";

        public UDPtestSettings()
        {
            Name = ModuleName;
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0";
            Properties = new UDPtestProperties();
        }

        [JsonPropertyName("properties")]
        public UDPtestProperties Properties { get; set; }

        public object Clone()
        {
            return new UDPtestSettings()
            {
                Name = Name,
                Version = Version,
                Properties = new UDPtestProperties()
                {
                    ProbeIntervalMilliseconds = new IntProperty(Properties.ProbeIntervalMilliseconds.Value),
                },
            };
        }

        public string GetModuleName() => Name;

        public bool UpgradeSettingsConfiguration() => false;
    }
}
