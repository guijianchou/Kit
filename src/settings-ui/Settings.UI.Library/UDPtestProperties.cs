// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace Kit.Settings.UI.Library
{
    public class UDPtestProperties
    {
        public UDPtestProperties()
        {
            ProbeIntervalMilliseconds = new IntProperty(1000);
        }

        [JsonPropertyName("probeIntervalMilliseconds")]
        public IntProperty ProbeIntervalMilliseconds { get; set; }
    }
}
