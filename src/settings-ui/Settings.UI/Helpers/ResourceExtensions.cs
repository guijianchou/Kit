// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Kit.Settings.UI.Helpers
{
    internal static class ResourceExtensions
    {
        private static readonly ConcurrentDictionary<string, LocalizedText> RecentText = new();
        private static readonly ConcurrentQueue<string> RecentOrder = new();

        private static ResourceLoader ResLoader => ResourceLoaderInstance.ResourceLoader;

        public static string GetLocalized(this string resourceKey)
        {
            string value = ResLoader.GetString(resourceKey);
            return Remember(value, resourceKey, string.Empty, null);
        }

        public static string GetLocalizedOrFallback(this string resourceKey, string fallback)
        {
            string value = ResLoader.GetString(resourceKey);
            return Remember(string.IsNullOrWhiteSpace(value) ? fallback : value, resourceKey, fallback, null);
        }

        public static string FormatLocalized(this string resourceKey, string fallback, params object[] args)
        {
            string format = ResLoader.GetString(resourceKey);
            if (string.IsNullOrWhiteSpace(format))
            {
                format = fallback;
            }

            return Remember(string.Format(CultureInfo.CurrentCulture, format, args), resourceKey, fallback, args);
        }

        /// <summary>Reformats a cached presentation string without recreating its underlying state.</summary>
        public static string Relocalize(this string text)
        {
            if (string.IsNullOrEmpty(text) || !RecentText.TryGetValue(text, out var value))
            {
                return text;
            }

            return value.Arguments == null
                ? value.Key.GetLocalizedOrFallback(value.Fallback)
                : value.Key.FormatLocalized(value.Fallback, value.Arguments);
        }

        private static string Remember(string text, string key, string fallback, object[] arguments)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            if (RecentText.TryAdd(text, new LocalizedText(key, fallback, arguments)))
            {
                RecentOrder.Enqueue(text);
                while (RecentText.Count > 4096 && RecentOrder.TryDequeue(out string expired))
                {
                    RecentText.TryRemove(expired, out _);
                }
            }

            return text;
        }

        private sealed record LocalizedText(string Key, string Fallback, object[] Arguments);
    }
}
