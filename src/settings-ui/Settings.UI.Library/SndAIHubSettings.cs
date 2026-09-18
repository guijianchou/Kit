// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kit.Settings.UI.Library
{
    public sealed class SndAIHubSettings
    {
        [JsonPropertyName("AIHub")]
        public AIHubSettings Settings { get; set; }

        public SndAIHubSettings()
        {
        }

        public SndAIHubSettings(AIHubSettings settings)
        {
            Settings = settings;
        }

        public string ToJsonString()
        {
            return JsonSerializer.Serialize(this, SettingsSerializationContext.Default.SndAIHubSettings);
        }
    }
}
