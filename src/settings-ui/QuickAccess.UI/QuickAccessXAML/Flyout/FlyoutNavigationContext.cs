// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Kit.QuickAccess.Services;
using Kit.QuickAccess.ViewModels;

namespace Kit.QuickAccess.Flyout;

internal sealed record FlyoutNavigationContext(
    LauncherViewModel LauncherViewModel,
    AllAppsViewModel AllAppsViewModel,
    IQuickAccessCoordinator Coordinator);
