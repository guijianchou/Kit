// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Reflection;
using System.Text.Json.Serialization;

using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library.Interfaces;

namespace Microsoft.PowerToys.Settings.UI.Library
{
    public class MonitorSettings : BasePTModuleSettings, ISettingsConfig, ICloneable
    {
        public const string ModuleName = "Monitor";

        public MonitorSettings()
        {
            Name = ModuleName;
            Version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            Properties = new MonitorProperties();
        }

        [JsonPropertyName("properties")]
        public MonitorProperties Properties { get; set; }

        public ModuleType GetModuleType() => ModuleType.Monitor;

        public object Clone()
        {
            return new MonitorSettings()
            {
                Name = Name,
                Version = Version,
                Properties = new MonitorProperties()
                {
                    DownloadsPath = new StringProperty(Properties.DownloadsPath.Value),
                    CsvFileName = new StringProperty(Properties.CsvFileName.Value),
                    ScanIntervalSeconds = new IntProperty(Properties.ScanIntervalSeconds.Value),
                    MaxFileSizeMegabytes = new IntProperty(Properties.MaxFileSizeMegabytes.Value),
                    HashAlgorithm = new StringProperty(Properties.HashAlgorithm.Value),
                    UseIncrementalHashing = new BoolProperty(Properties.UseIncrementalHashing.Value),
                    RunInBackground = new BoolProperty(Properties.RunInBackground.Value),
                    OrganizeDownloads = new BoolProperty(Properties.OrganizeDownloads.Value),
                    CleanInstallers = new BoolProperty(Properties.CleanInstallers.Value),
                },
            };
        }

        public string GetModuleName()
        {
            return Name;
        }

        public bool UpgradeSettingsConfiguration()
        {
            return false;
        }
    }
}
