// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;
using Microsoft.Management.Deployment;

namespace Kit.WingetLib;

internal static class WinGetComFactory
{
    // The official native activation factory supplies registration-free WinRT marshaling
    // for unpackaged callers and delegates to the installed App Installer COM service.
    internal static PackageManager CreateManager() => new();

    internal static FindPackagesOptions CreateFindOptions() => new();

    internal static PackageMatchFilter CreateFilter() => new();

    internal static CreateCompositePackageCatalogOptions CreateCompositeOptions() => new();

    internal static InstallOptions CreateInstallOptions() => new();

    internal static DownloadOptions CreateDownloadOptions() => new();

    internal static T Run<T>(Func<T> action)
    {
        // Each public operation runs on a background thread and keeps its COM apartment
        // alive until the asynchronous WinGet operation (including cancellation) finishes.
        Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 0));
        try
        {
            return action();
        }
        finally
        {
            CoUninitialize();
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrencyModel);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
