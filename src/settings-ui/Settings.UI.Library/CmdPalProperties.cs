// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Settings.UI.Library
{
    public class CmdPalProperties
    {
        // Default shortcut - Win + Alt + Space
        public static readonly HotkeySettings DefaultHotkeyValue = new HotkeySettings(true, false, true, false, 32);

#pragma warning disable SA1401 // Fields should be private
#pragma warning disable CA1051 // Do not declare visible instance fields
        public HotkeySettings Hotkey;
#pragma warning restore CA1051 // Do not declare visible instance fields
#pragma warning restore SA1401 // Fields should be private

        public CmdPalProperties()
        {
            Hotkey = DefaultHotkeyValue;
        }
    }
}
