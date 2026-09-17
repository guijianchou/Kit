// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Kit.Settings.UI.ViewModels
{
    public sealed class RecentProbeBarViewModel
    {
        public Brush Brush { get; }
        public double BarHeight { get; }
        public string Description { get; }

        public RecentProbeBarViewModel(bool isSuccess, double durationMilliseconds, string description, bool isWarning = false)
        {
            if (isSuccess)
            {
                if (isWarning || durationMilliseconds > 250)
                {
                    Brush = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
                    BarHeight = 12.0;
                }
                else
                {
                    Brush = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                    BarHeight = 18.0;
                }
            }
            else
            {
                Brush = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                BarHeight = 9.0;
            }
            Description = description;
        }
    }
}
