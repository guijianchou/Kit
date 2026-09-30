// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Kit.Settings.UI.Services
{
    public static class NavigationService
    {
        public static event NavigatedEventHandler Navigated;

        public static event NavigationFailedEventHandler NavigationFailed;

        private static Frame frame;
        private static object lastParamUsed;

        public static Frame Frame
        {
            get
            {
                return frame;
            }

            set
            {
                UnregisterFrameEvents();
                frame = value;
                RegisterFrameEvents();
            }
        }

        public static bool CanGoBack => Frame.CanGoBack;

        public static bool CanGoForward => Frame.CanGoForward;

        public static bool GoBack()
        {
            if (CanGoBack)
            {
                Frame.GoBack();
                return true;
            }

            return false;
        }

        public static void GoForward() => Frame.GoForward();

        public static bool Navigate(Type pageType, object parameter = null, NavigationTransitionInfo infoOverride = null)
        {
            // Don't open the same page multiple times
            if (Frame.Content?.GetType() != pageType || (parameter != null && !parameter.Equals(lastParamUsed)))
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var navigationResult = Frame.Navigate(pageType, parameter, infoOverride);
                if (navigationResult)
                {
                    lastParamUsed = parameter;
                    LogNavigationTiming(pageType, stopwatch);
                }

                return navigationResult;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Logs how long a page took from the navigation request until it was loaded, so slow
        /// pages show up in the logs (tools\diagnostics\Get-KitDiagnostics.ps1 lists them).
        /// </summary>
        private static void LogNavigationTiming(Type pageType, System.Diagnostics.Stopwatch stopwatch)
        {
            if (Frame.Content is not FrameworkElement page)
            {
                return;
            }

            long constructedMs = stopwatch.ElapsedMilliseconds;
            if (page.IsLoaded)
            {
                ManagedCommon.Logger.LogInfo($"NAV_TIMING: {pageType.Name} cached, shown in {constructedMs}ms");
                return;
            }

            void OnLoaded(object sender, RoutedEventArgs e)
            {
                page.Loaded -= OnLoaded;
                ManagedCommon.Logger.LogInfo($"NAV_TIMING: {pageType.Name} loaded in {stopwatch.ElapsedMilliseconds}ms (navigate {constructedMs}ms)");
            }

            page.Loaded += OnLoaded;
        }

        public static bool Navigate<T>(object parameter = null, NavigationTransitionInfo infoOverride = null)
            where T : Page
            => Navigate(typeof(T), parameter, infoOverride);

        private static void RegisterFrameEvents()
        {
            if (frame != null)
            {
                frame.Navigated += Frame_Navigated;
                frame.NavigationFailed += Frame_NavigationFailed;
            }
        }

        private static void UnregisterFrameEvents()
        {
            if (frame != null)
            {
                frame.Navigated -= Frame_Navigated;
                frame.NavigationFailed -= Frame_NavigationFailed;
            }
        }

        private static void Frame_NavigationFailed(object sender, NavigationFailedEventArgs e) => NavigationFailed?.Invoke(sender, e);

        private static void Frame_Navigated(object sender, NavigationEventArgs e) => Navigated?.Invoke(sender, e);

        internal static void EnsurePageIsSelected(Type pageType)
        {
            if (Frame.Content == null)
            {
                Frame.Navigate(pageType);
            }
        }
    }
}
