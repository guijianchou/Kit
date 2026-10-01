// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text.Json.Serialization;
using Kit.Settings.UI.Library.Interfaces;

namespace Kit.Settings.UI.Library
{
    public sealed class NetMapSettings : BasePTModuleSettings, ISettingsConfig, ICloneable
    {
        public const string ModuleName = "NetMap";

        public NetMapSettings()
        {
            Name = ModuleName;
            Version = "1.0";
        }

        [JsonPropertyName("properties")]
        public NetMapProperties Properties { get; set; } = new();

        public object Clone() => new NetMapSettings
        {
            Properties = new NetMapProperties
            {
                ProxyMode = new IntProperty(Properties.ProxyMode.Value),
                ProxyAddress = new StringProperty(Properties.ProxyAddress.Value),
                AsnDatabase = new StringProperty(Properties.AsnDatabase.Value),
                CityDatabase = new StringProperty(Properties.CityDatabase.Value),
            },
        };

        public string GetModuleName() => ModuleName;

        public bool UpgradeSettingsConfiguration() => false;
    }
}
