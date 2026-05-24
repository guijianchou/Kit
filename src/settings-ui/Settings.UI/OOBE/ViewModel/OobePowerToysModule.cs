// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Settings.UI.OOBE.ViewModel
{
    public class OobePowerToysModule
    {
        public string ModuleName { get; set; }

        public string Tag { get; set; }

        public bool IsNew { get; set; }

        public OobePowerToysModule()
        {
        }

        public OobePowerToysModule(OobePowerToysModule other)
        {
            if (other == null)
            {
                return;
            }

            ModuleName = other.ModuleName;
            Tag = other.Tag;
            IsNew = other.IsNew;
        }

        public void LogOpeningSettingsEvent()
        {
        }

        public void LogRunningModuleEvent()
        {
        }

        public void LogOpeningModuleEvent()
        {
        }

        public void LogClosingModuleEvent()
        {
        }
    }
}
