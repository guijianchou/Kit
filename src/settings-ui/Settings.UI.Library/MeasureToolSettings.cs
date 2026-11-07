// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.Library.Interfaces;
using ManagedCommon;

namespace Kit.Settings.UI.Library
{
    public class MeasureToolSettings : BasePTModuleSettings, ISettingsConfig
    {
        public const string ModuleName = "Measure Tool";

        [JsonPropertyName("properties")]
        public MeasureToolProperties Properties { get; set; }

        public MeasureToolSettings()
        {
            Properties = new MeasureToolProperties();
            Version = "1";
            Name = ModuleName;
        }

        public string GetModuleName()
            => Name;

        public ModuleType GetModuleType() => ModuleType.MeasureTool;

        // This can be utilized in the future if the settings.json file is to be modified/deleted.
        public bool UpgradeSettingsConfiguration()
            => false;
    }
}
