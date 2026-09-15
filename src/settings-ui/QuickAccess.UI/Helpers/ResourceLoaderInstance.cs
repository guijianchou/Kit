// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Windows.ApplicationModel.Resources;

namespace Kit.QuickAccess.Helpers;

internal static class ResourceLoaderInstance
{
    internal static ResourceLoader ResourceLoader { get; } = new("Kit.QuickAccess.pri");
}
