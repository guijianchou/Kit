// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace Kit.Settings.UI.Library
{
    public class LocalserverProperties
    {
        public LocalserverProperties()
        {
            StopTimeoutSec = new IntProperty(5);
            MaxLogLines = new IntProperty(1000);
        }

        [JsonPropertyName("stopTimeoutSec")]
        public IntProperty StopTimeoutSec { get; set; }

        [JsonPropertyName("maxLogLines")]
        public IntProperty MaxLogLines { get; set; }
    }
}
