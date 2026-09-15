// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Interfaces;
using Kit.Settings.UI.UnitTests;

namespace Kit.Settings.UnitTest
{
    public class BasePTSettingsTest : BasePTModuleSettings, ISettingsConfig
    {
        public BasePTSettingsTest()
        {
            Name = string.Empty;
            Version = string.Empty;
        }

        public string GetModuleName()
        {
            return Name;
        }

        public bool UpgradeSettingsConfiguration()
        {
            return false;
        }

        // Override ToJsonString to use test-specific serialization context
        public override string ToJsonString()
        {
            return JsonSerializer.Serialize(this, TestSettingsSerializationContext.Default.BasePTSettingsTest);
        }
    }
}
