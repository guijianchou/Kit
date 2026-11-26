// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
using Microsoft.Windows.ApplicationModel.Resources;

namespace Kit.Settings.UI.Helpers
{
    internal static class ResourceLoaderInstance
    {
        internal static ResourceLoader ResourceLoader { get; private set; }

        static ResourceLoaderInstance()
        {
            Refresh();
        }

        internal static void Refresh() => ResourceLoader = new ResourceLoader("Kit.Settings.pri");
    }
}
