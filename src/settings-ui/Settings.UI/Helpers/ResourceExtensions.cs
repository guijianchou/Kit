// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Kit.Settings.UI.Helpers
{
    internal static class ResourceExtensions
    {
        private static readonly ResourceLoader ResLoader = ResourceLoaderInstance.ResourceLoader;

        public static string GetLocalized(this string resourceKey)
        {
            return ResLoader.GetString(resourceKey);
        }

        public static string GetLocalizedOrFallback(this string resourceKey, string fallback)
        {
            string value = ResLoader.GetString(resourceKey);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        public static string FormatLocalized(this string resourceKey, string fallback, params object[] args)
        {
            string format = ResLoader.GetString(resourceKey);
            if (string.IsNullOrWhiteSpace(format))
            {
                format = fallback;
            }

            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
    }
}
