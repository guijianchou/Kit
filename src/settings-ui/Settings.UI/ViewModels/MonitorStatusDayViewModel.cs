// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.UI.Xaml.Media;

namespace Microsoft.PowerToys.Settings.UI.ViewModels
{
    public sealed class MonitorStatusDayViewModel
    {
        public MonitorStatusDayViewModel(Brush brush, string toolTip)
        {
            Brush = brush;
            ToolTip = toolTip;
        }

        public Brush Brush { get; }

        public string ToolTip { get; }
    }
}
