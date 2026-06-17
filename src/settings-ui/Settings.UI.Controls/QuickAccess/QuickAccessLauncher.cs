// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
using ManagedCommon;
using PowerToys.Interop;

namespace Microsoft.PowerToys.Settings.UI.Controls
{
    public class QuickAccessLauncher : IQuickAccessLauncher
    {
        public virtual bool Launch(ModuleType moduleType)
        {
            switch (moduleType)
            {
                case ModuleType.LightSwitch:
                    using (var eventHandle = new EventWaitHandle(false, EventResetMode.AutoReset, Constants.LightSwitchToggleEvent()))
                    {
                        eventHandle.Set();
                    }

                    return true;
                default:
                    return false;
            }
        }
    }
}
