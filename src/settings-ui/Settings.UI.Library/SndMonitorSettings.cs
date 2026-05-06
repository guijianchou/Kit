// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.PowerToys.Settings.UI.Library
{
    public class SndMonitorSettings
    {
        public SndMonitorSettings()
        {
        }

        public SndMonitorSettings(MonitorSettings settings)
        {
            Settings = settings;
        }

        [JsonPropertyName("Monitor")]
        public MonitorSettings Settings { get; set; }

        public string ToJsonString()
        {
            return JsonSerializer.Serialize(this, SettingsSerializationContext.Default.SndMonitorSettings);
        }
    }
}
