// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Interfaces;
using Kit.Settings.UI.Services;
using ManagedCommon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Kit.QuickAccess.Services;

internal sealed class QuickAccessTypography : IDisposable
{
    private readonly FrameworkElement _root;
    private readonly ISettingsRepository<GeneralSettings> _settingsRepository;
    private int _refreshQueued;
    private int _disposed;

    internal QuickAccessTypography(FrameworkElement root)
    {
        _root = root;
        _settingsRepository = SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default);
        _settingsRepository.SettingsChanged += OnSettingsChanged;
        _root.Loaded += OnRootLoaded;
    }

    internal void RequestRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _refreshQueued, 1) != 0)
        {
            return;
        }

        // Wait for the new page's templates to load. Navigation and settings
        // notifications share one pass; ordinary layout does not rescan the tree.
        if (!_root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Refresh))
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _settingsRepository.SettingsChanged -= OnSettingsChanged;
        _root.Loaded -= OnRootLoaded;
        GC.SuppressFinalize(this);
    }

    private void OnSettingsChanged(GeneralSettings settings)
    {
        if (settings.UiFontFamily != TypographyResources.AppliedFontFamily || settings.UiFontSize != TypographyResources.AppliedFontSize)
        {
            RequestRefresh();
        }
    }

    private void OnRootLoaded(object sender, RoutedEventArgs args) => RequestRefresh();

    private void Refresh()
    {
        Interlocked.Exchange(ref _refreshQueued, 0);
        if (Volatile.Read(ref _disposed) != 0 || !_root.IsLoaded)
        {
            return;
        }

        try
        {
            var elements = GetLoadedElements();
            foreach (var element in elements)
            {
                // Capture every old baseline before changing the global scale.
                TypographyResources.TrackElement(element);
            }

            var settings = _settingsRepository.SettingsConfig;
            if (settings.UiFontFamily != TypographyResources.AppliedFontFamily || settings.UiFontSize != TypographyResources.AppliedFontSize)
            {
                TypographyResources.Apply(settings.UiFontFamily, settings.UiFontSize);
            }

            foreach (var element in elements)
            {
                TypographyResources.ApplyElement(element);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Could not refresh Quick Access typography.", ex);
        }
    }

    private List<DependencyObject> GetLoadedElements()
    {
        var elements = new List<DependencyObject>();
        var pending = new Queue<DependencyObject>();
        pending.Enqueue(_root);
        while (pending.TryDequeue(out var element))
        {
            if (element is FrameworkElement { IsLoaded: false })
            {
                continue;
            }

            elements.Add(element);
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            {
                pending.Enqueue(VisualTreeHelper.GetChild(element, index));
            }
        }

        return elements;
    }
}
