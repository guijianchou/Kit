// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Kit.Settings.UI.Services
{
    public static class NavigationService
    {
        // Pages are cached once per type. Bound the separate navigation journal too,
        // so repeated plugin switches cannot retain an unlimited set of parameters.
        internal const int MaximumHistoryEntries = 16;

        public static event NavigatedEventHandler Navigated;

        public static event NavigationFailedEventHandler NavigationFailed;

        private static Frame frame;
        private static object lastParamUsed;
        private static Action cancelNavigationTiming;
        private static long navigationSequence;

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
                var sourcePage = Frame.Content?.GetType().Name ?? "Startup";
                var stopwatch = Stopwatch.StartNew();
                var navigationResult = Frame.Navigate(pageType, parameter, infoOverride ?? new SuppressNavigationTransitionInfo());
                if (navigationResult)
                {
                    lastParamUsed = parameter;
                    LogNavigationTiming(pageType, sourcePage, stopwatch);
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
        private static void LogNavigationTiming(Type pageType, string sourcePage, Stopwatch stopwatch)
        {
            if (Frame.Content is not FrameworkElement page)
            {
                return;
            }

            long constructedMs = stopwatch.ElapsedMilliseconds;
            long sequence = navigationSequence;

            void Cancel()
            {
                page.Loaded -= OnLoaded;
                page.Unloaded -= OnUnloaded;
                CompositionTarget.Rendering -= OnRendering;
                cancelNavigationTiming = null;
            }

            void OnUnloaded(object sender, RoutedEventArgs e) => Cancel();

            void OnRendering(object sender, object e)
            {
                Cancel();
                long renderingMs = stopwatch.ElapsedMilliseconds;

                // Rendering is the start of a render pass, not proof that the frame
                // has reached the display. Keep process sampling off the load path.
                page.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (sequence != navigationSequence || !page.IsLoaded)
                    {
                        return;
                    }

                    try
                    {
                        using var process = Process.GetCurrentProcess();
                        ManagedCommon.Logger.LogInfo($"NAV_FRAME: {sourcePage} -> {pageType.Name}, render callback at {renderingMs}ms, workingSetMiB={process.WorkingSet64 / (1024 * 1024)}, privateMiB={process.PrivateMemorySize64 / (1024 * 1024)}, managedMiB={GC.GetTotalMemory(false) / (1024 * 1024)}");
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Diagnostics must not interfere with navigation or shutdown.
                    }
                });
            }

            void OnLoaded(object sender, RoutedEventArgs e)
            {
                page.Loaded -= OnLoaded;
                ManagedCommon.Logger.LogInfo($"NAV_TIMING: {pageType.Name} loaded in {stopwatch.ElapsedMilliseconds}ms (navigate {constructedMs}ms)");
                CompositionTarget.Rendering += OnRendering;
            }

            cancelNavigationTiming = Cancel;
            page.Unloaded += OnUnloaded;
            if (page.IsLoaded)
            {
                OnLoaded(page, null);
            }
            else
            {
                page.Loaded += OnLoaded;
            }
        }

        public static bool Navigate<T>(object parameter = null, NavigationTransitionInfo infoOverride = null)
            where T : Page
            => Navigate(typeof(T), parameter, infoOverride);

        private static void RegisterFrameEvents()
        {
            if (frame != null)
            {
                frame.Navigated += Frame_Navigated;
                frame.Navigating += Frame_Navigating;
                frame.NavigationFailed += Frame_NavigationFailed;
            }
        }

        private static void UnregisterFrameEvents()
        {
            cancelNavigationTiming?.Invoke();
            navigationSequence++;
            if (frame != null)
            {
                frame.Navigated -= Frame_Navigated;
                frame.Navigating -= Frame_Navigating;
                frame.NavigationFailed -= Frame_NavigationFailed;
            }
        }

        private static void Frame_NavigationFailed(object sender, NavigationFailedEventArgs e) => NavigationFailed?.Invoke(sender, e);

        private static void Frame_Navigating(object sender, NavigatingCancelEventArgs e)
        {
            cancelNavigationTiming?.Invoke();
            navigationSequence++;
        }

        private static void Frame_Navigated(object sender, NavigationEventArgs e)
        {
            while (frame.BackStack.Count > MaximumHistoryEntries)
            {
                frame.BackStack.RemoveAt(0);
            }

            while (frame.ForwardStack.Count > MaximumHistoryEntries)
            {
                frame.ForwardStack.RemoveAt(0);
            }

            Navigated?.Invoke(sender, e);
        }

        internal static void EnsurePageIsSelected(Type pageType)
        {
            if (Frame.Content == null)
            {
                Frame.Navigate(pageType);
            }
        }
    }
}
