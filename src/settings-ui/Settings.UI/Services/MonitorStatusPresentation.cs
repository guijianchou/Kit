// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;

using Microsoft.PowerToys.Settings.UI.ViewModels;

namespace Microsoft.PowerToys.Settings.UI.Services
{
    public sealed record MonitorStatusPresentation(
        string StatusText,
        string StatusDescription,
        string LastStatusMessage,
        string ChartAccessibilityName,
        IReadOnlyList<MonitorStatusMetricViewModel> Metrics,
        IReadOnlyList<MonitorStatusDayViewModel> Days,
        IReadOnlyList<MonitorStatusLegendItemViewModel> LegendItems);
}
