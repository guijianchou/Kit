// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;

using Kit.Settings.UI.Library.Attributes;

namespace Kit.Settings.UI.Library
{
    public class PowerOcrProperties
    {
        public PowerOcrProperties()
        {
            PreferredLanguage = string.Empty;
        }

        public string PreferredLanguage { get; set; }

        public override string ToString()
            => JsonSerializer.Serialize(this, SettingsSerializationContext.Default.PowerOcrProperties);
    }
}
