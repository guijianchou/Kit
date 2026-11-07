// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace Kit.Settings.UI.Library
{
    public sealed class SndWingetSettings
    {
        [JsonPropertyName("Winget")]
        public WingetSettings Settings { get; set; }

        public SndWingetSettings()
        {
        }

        public SndWingetSettings(WingetSettings settings)
        {
            Settings = settings;
        }
    }
}
