// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using Kit.Settings.UI.Helpers;
using LocalServerHub.Core.Models;
using Microsoft.UI.Xaml.Media;

namespace Kit.Settings.UI.ViewModels
{
    /// <summary>
    /// Shared micro-bar sample for the recent-activity strips on the Localserver and
    /// UDPtest pages (rendered via <c>controls:StatusBarsPanel</c>). Single home for the
    /// severity to brush / height ladders that were previously duplicated as
    /// <c>RecentBarViewModel</c> (Localserver) and <c>RecentProbeBarViewModel</c> (UDPtest).
    /// </summary>
    public sealed class StatusBarViewModel
    {
        private StatusBarViewModel(Brush brush, double barHeight, string description)
        {
            Brush = brush;
            BarHeight = barHeight;
            Description = description;
        }

        public Brush Brush { get; }

        public double BarHeight { get; }

        public string Description { get; }

        /// <summary>Localserver ladder: state severity to 16 / 16 / 8 / 6.</summary>
        public static StatusBarViewModel FromServiceState(ServiceState state, bool? healthProbePassed, string description)
        {
            ServiceSeverity severity = state.ToSeverity(healthProbePassed);
            Brush brush = severity switch
            {
                ServiceSeverity.Success => ThemeBrushHelper.SuccessBrush,
                ServiceSeverity.Caution => ThemeBrushHelper.CautionBrush,
                ServiceSeverity.Critical => ThemeBrushHelper.CriticalBrush,
                _ => ThemeBrushHelper.StrokeDefaultBrush,
            };
            double barHeight = severity switch
            {
                ServiceSeverity.Success => 16d,
                ServiceSeverity.Caution => 16d,
                ServiceSeverity.Critical => 8d,
                _ => 6d,
            };
            return new StatusBarViewModel(brush, barHeight, description);
        }

        /// <summary>UDPtest ladder: probe outcome to success 16 / slow-warn 11 / fail 6.</summary>
        public static StatusBarViewModel FromProbeResult(bool isSuccess, double durationMilliseconds, string description, bool isWarning = false)
        {
            if (!isSuccess)
            {
                return new StatusBarViewModel(ThemeBrushHelper.CriticalBrush, 6.0, description);
            }

            return isWarning || durationMilliseconds > 250
                ? new StatusBarViewModel(ThemeBrushHelper.CautionBrush, 11.0, description)
                : new StatusBarViewModel(ThemeBrushHelper.SuccessBrush, 16.0, description);
        }
    }
}
