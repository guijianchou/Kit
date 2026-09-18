// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace Kit.Settings.UI.Library
{
    public class AIHubProperties
    {
        public AIHubProperties()
        {
            RetentionDays = new IntProperty(30);
            ActiveTabIndex = new IntProperty(0);
        }

        [JsonPropertyName("retentionDays")]
        public IntProperty RetentionDays { get; set; }

        [JsonPropertyName("activeTabIndex")]
        public IntProperty ActiveTabIndex { get; set; }
    }
}
