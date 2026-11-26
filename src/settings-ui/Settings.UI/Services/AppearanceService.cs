// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Kit.Settings.UI.Library;
using ManagedCommon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Kit.Settings.UI.Services;

/// <summary>Updates the existing visual trees, keeping page models and running work alive.</summary>
public static class AppearanceService
{
    private const int DiscoveryBatchWorkLimit = 64;
    private static readonly TimeSpan DiscoveryBatchBudget = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan LayoutDiscoveryInterval = TimeSpan.FromMilliseconds(120);
    private static readonly List<WeakReference<FrameworkElement>> Roots = new();
    private static readonly ConditionalWeakTable<DependencyObject, ElementVersion> Elements = new();
    private static MainWindow window;
    private static DispatcherQueue dispatcher;
    private static bool pendingTypography;
    private static string pendingLanguage;
    private static bool applying;
    private static DiscoveryScan discoveryScan;
    private static DispatcherQueueTimer layoutDiscoveryTimer;
    private static bool scanFollowupRequested;
    private static long discoveryGeneration;
    private static TaskCompletionSource completion;

    public static long CurrentVersion { get; private set; }

    public static Task WhenIdleAsync() => completion?.Task ?? Task.CompletedTask;

    internal static void Attach(MainWindow owner)
    {
        window = owner;
        dispatcher = owner.DispatcherQueue;
        if (owner.Content is FrameworkElement root)
        {
            RegisterRoot(root);
            root.LayoutUpdated += OnLayoutUpdated;
        }

        SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default).SettingsChanged += OnSettingsChanged;
    }

    internal static void Detach(MainWindow owner)
    {
        if (!ReferenceEquals(window, owner))
        {
            return;
        }

        if (owner.Content is FrameworkElement root)
        {
            root.LayoutUpdated -= OnLayoutUpdated;
        }

        SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default).SettingsChanged -= OnSettingsChanged;
        CancelDiscoveryScan();
        if (layoutDiscoveryTimer != null)
        {
            layoutDiscoveryTimer.Tick -= OnLayoutDiscoveryTick;
            layoutDiscoveryTimer = null;
        }
        foreach (var reference in Roots)
        {
            if (reference.TryGetTarget(out var registeredRoot))
            {
                registeredRoot.Loaded -= OnRootLoaded;
            }
        }

        window = null;
        dispatcher = null;
        Roots.Clear();
    }

    public static void RegisterRoot(FrameworkElement root)
    {
        if (Roots.Any(reference => reference.TryGetTarget(out var existing) && ReferenceEquals(root, existing)))
        {
            return;
        }

        Roots.Add(new WeakReference<FrameworkElement>(root));
        root.Loaded += OnRootLoaded;
        QueueScan();
    }

    public static void RequestApply()
    {
        if (dispatcher == null)
        {
            return;
        }

        if (!dispatcher.HasThreadAccess)
        {
            dispatcher.TryEnqueue(RequestApply);
            return;
        }

        pendingTypography = true;
        QueueApply();
    }

    public static void RequestLanguage(string language)
    {
        if (dispatcher == null)
        {
            return;
        }

        if (!dispatcher.HasThreadAccess)
        {
            dispatcher.TryEnqueue(() => RequestLanguage(language));
            return;
        }

        pendingLanguage = language ?? string.Empty;
        QueueApply();
    }

    private static void QueueApply()
    {
        if (applying)
        {
            return;
        }

        CancelDiscoveryScan();
        applying = true;
        completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(DispatcherQueuePriority.Low, ApplyPending))
        {
            applying = false;
            completion.TrySetCanceled();
        }
    }

    private static async void ApplyPending()
    {
        try
        {
            while (pendingTypography || pendingLanguage != null)
            {
                bool typography = pendingTypography;
                string language = pendingLanguage;
                pendingTypography = false;
                pendingLanguage = null;
                var elements = GetElements();
                foreach (var element in elements)
                {
                    TypographyResources.TrackElement(element);
                }

                var scrolls = elements.OfType<ScrollViewer>()
                    .Select(scroll => (Scroll: scroll, Horizontal: scroll.HorizontalOffset, Vertical: scroll.VerticalOffset)).ToArray();
                var textEdits = elements.OfType<TextBox>().Where(text => !text.IsReadOnly)
                    .Select(text => (TextBox: text, text.Text, text.SelectionStart, text.SelectionLength)).ToArray();
                var passwords = elements.OfType<PasswordBox>().Select(box => (Box: box, box.Password)).ToArray();
                var focused = window?.Content is FrameworkElement { XamlRoot: { } xamlRoot }
                    ? FocusManager.GetFocusedElement(xamlRoot) as Control : null;
                if (typography)
                {
                    var settings = SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default).SettingsConfig;
                    TypographyResources.Apply(settings.UiFontFamily, settings.UiFontSize);
                }

                if (language != null)
                {
                    RuntimeLocalization.ApplyLanguage(language);
                    var refreshed = new HashSet<object>(ReferenceEqualityComparer.Instance);
                    foreach (var element in elements)
                    {
                        Refresh(element, refreshed);
                        if (element is FrameworkElement frameworkElement)
                        {
                            frameworkElement.Language = RuntimeLocalization.EffectiveLanguage;
                            Refresh(frameworkElement.DataContext, refreshed);
                        }
                    }

                    window?.RefreshLocalization();
                    foreach (var edit in textEdits)
                    {
                        if (edit.TextBox.Text != edit.Text)
                        {
                            edit.TextBox.Text = edit.Text;
                        }

                        edit.TextBox.Select(edit.SelectionStart, edit.SelectionLength);
                    }

                    foreach (var password in passwords)
                    {
                        if (password.Box.Password != password.Password)
                        {
                            password.Box.Password = password.Password;
                        }
                    }
                }

                CurrentVersion++;
                Scan();
                if (window?.Content is FrameworkElement content)
                {
                    content.UpdateLayout();
                }

                await NextLayoutAsync();
                Scan();
                foreach (var entry in scrolls)
                {
                    entry.Scroll.ChangeView(Math.Min(entry.Horizontal, entry.Scroll.ScrollableWidth),
                        Math.Min(entry.Vertical, entry.Scroll.ScrollableHeight), null, disableAnimation: true);
                }

                if (focused?.IsLoaded == true && focused.IsEnabled)
                {
                    focused.Focus(FocusState.Programmatic);
                }
            }

            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            Logger.LogError("Could not apply the interface appearance in the existing window.", ex);
            completion.TrySetException(ex);
        }
        finally
        {
            applying = false;
            if (scanFollowupRequested)
            {
                scanFollowupRequested = false;
                QueueScan();
            }
        }
    }

    private static void Refresh(object value, HashSet<object> refreshed)
    {
        if (value is ILiveLocalizable localizable && refreshed.Add(value))
        {
            localizable.RefreshLocalization();
        }
    }

    private static Task NextLayoutAsync()
    {
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => next.TrySetResult()))
        {
            next.TrySetCanceled();
        }

        return next.Task;
    }

    private static void OnSettingsChanged(GeneralSettings settings)
    {
        if (settings.UiFontFamily != TypographyResources.AppliedFontFamily || settings.UiFontSize != TypographyResources.AppliedFontSize)
        {
            RequestApply();
        }
    }

    private static void OnRootLoaded(object sender, RoutedEventArgs args) => QueueScan();

    private static void OnLayoutUpdated(object sender, object args) => ScheduleLayoutDiscovery();

    private static void ScheduleLayoutDiscovery()
    {
        if (dispatcher == null)
        {
            return;
        }

        // Expander animations and live telemetry can invalidate layout every frame.
        // Coalesce them without restarting the timer: continuously changing content
        // still discovers newly realized controls, but never scans once per frame.
        if (layoutDiscoveryTimer == null)
        {
            layoutDiscoveryTimer = dispatcher.CreateTimer();
            layoutDiscoveryTimer.Interval = LayoutDiscoveryInterval;
            layoutDiscoveryTimer.IsRepeating = false;
            layoutDiscoveryTimer.Tick += OnLayoutDiscoveryTick;
        }

        if (!layoutDiscoveryTimer.IsRunning)
        {
            layoutDiscoveryTimer.Start();
        }
    }

    private static void OnLayoutDiscoveryTick(DispatcherQueueTimer sender, object args) => QueueScan();

    private static void QueueScan()
    {
        if (dispatcher == null)
        {
            return;
        }

        layoutDiscoveryTimer?.Stop();

        if (applying)
        {
            scanFollowupRequested = true;
            return;
        }

        if (discoveryScan != null)
        {
            // Until the first batch starts, the current request already covers
            // all roots and templates that have just been added.
            scanFollowupRequested |= discoveryScan.Started;
            return;
        }

        discoveryScan = new DiscoveryScan(discoveryGeneration);
        QueueDiscoveryBatch(discoveryScan);
    }

    private static void CancelDiscoveryScan()
    {
        layoutDiscoveryTimer?.Stop();
        discoveryGeneration++;
        discoveryScan = null;
        scanFollowupRequested = false;
    }

    private static bool IsCurrentDiscoveryScan(DiscoveryScan scan) =>
        dispatcher != null && !applying && scan.Generation == discoveryGeneration && ReferenceEquals(discoveryScan, scan);

    private static void QueueDiscoveryBatch(DiscoveryScan scan)
    {
        if (IsCurrentDiscoveryScan(scan) && !dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => DiscoverBatch(scan)))
        {
            CancelDiscoveryScan();
        }
    }

    private static void DiscoverBatch(DiscoveryScan scan)
    {
        if (!IsCurrentDiscoveryScan(scan))
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        int work = 0;
        try
        {
            if (!scan.Started)
            {
                EnqueueRoots(scan.Pending, includeCached: false);
                scan.Started = true;
            }

            while (IsCurrentDiscoveryScan(scan) && work < DiscoveryBatchWorkLimit && stopwatch.Elapsed < DiscoveryBatchBudget)
            {
                if (scan.Parent != null)
                {
                    // A template can change between batches. Re-read the child count;
                    // LayoutUpdated also requests a fresh pass for insertions behind us.
                    if (scan.Parent is FrameworkElement { IsLoaded: false }
                        || scan.ChildIndex >= VisualTreeHelper.GetChildrenCount(scan.Parent))
                    {
                        scan.Parent = null;
                        continue;
                    }

                    scan.Pending.Enqueue(VisualTreeHelper.GetChild(scan.Parent, scan.ChildIndex++));
                    work++;
                    continue;
                }

                if (!scan.Pending.TryDequeue(out var element))
                {
                    FinishDiscoveryScan(scan);
                    return;
                }

                work++;
                if (!scan.Visited.Add(element))
                {
                    continue;
                }

                if (element is FrameworkElement { IsLoaded: false } unloaded)
                {
                    // Native templates can be discovered while their setters are
                    // still being built. Apply only after Loaded, as marked text does.
                    var version = Elements.GetValue(element, _ => new ElementVersion());
                    if (!version.WaitingForLoaded)
                    {
                        version.WaitingForLoaded = true;
                        unloaded.Loaded += OnDiscoveredElementLoaded;
                    }

                    continue;
                }

                ApplyElement(element);
                scan.Parent = element;
                scan.ChildIndex = 0;
            }

            QueueDiscoveryBatch(scan);
        }
        catch (Exception ex)
        {
            if (IsCurrentDiscoveryScan(scan))
            {
                CancelDiscoveryScan();
                Logger.LogError("Could not discover new interface appearance targets.", ex);
            }
        }
    }

    private static void OnDiscoveredElementLoaded(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        element.Loaded -= OnDiscoveredElementLoaded;
        if (Elements.TryGetValue(element, out var version))
        {
            version.WaitingForLoaded = false;
        }

        if (!applying && window?.Content is FrameworkElement { XamlRoot: { } xamlRoot }
            && ReferenceEquals(element.XamlRoot, xamlRoot))
        {
            // The native template is complete now. Update this one element before
            // its first frame; descendants are still discovered in bounded batches.
            ApplyElement(element);
        }

        QueueScan();
    }

    private static void FinishDiscoveryScan(DiscoveryScan scan)
    {
        if (!IsCurrentDiscoveryScan(scan))
        {
            return;
        }

        discoveryScan = null;
        if (scanFollowupRequested)
        {
            scanFollowupRequested = false;
            ScheduleLayoutDiscovery();
        }
    }

    private static void Scan(bool includeCached = true)
    {
        foreach (var element in GetElements(includeCached))
        {
            ApplyElement(element);
        }
    }

    private static void ApplyElement(DependencyObject element)
    {
        RefreshComboBoxSelection(element);
        var version = Elements.GetValue(element, _ => new ElementVersion());
        if (version.Value == CurrentVersion)
        {
            return;
        }

        TypographyResources.ApplyElement(element);
        RuntimeLocalization.Apply(element);
        if (element is FrameworkElement frameworkElement)
        {
            frameworkElement.Language = RuntimeLocalization.EffectiveLanguage;
        }

        if (element is ComboBox combo && version.Value < 0)
        {
            combo.DropDownOpened += (_, _) => QueueScan();
        }

        version.Value = CurrentVersion;
    }

    private static void RefreshComboBoxSelection(DependencyObject element)
    {
        // DefaultComboBoxStyle uses this presenter for the collapsed selection.
        // Its Content is assigned by native code and can retain the old string
        // after a localized ComboBoxItem.Content changes.
        if (element is not ContentPresenter { Name: "ContentPresenter" } presenter)
        {
            return;
        }

        DependencyObject owner = VisualTreeHelper.GetParent(presenter);
        while (owner != null && owner is not Control)
        {
            owner = VisualTreeHelper.GetParent(owner);
        }

        if (owner is not ComboBox combo || combo.IsEditable || combo.ItemTemplate != null || combo.ItemTemplateSelector != null
            || !string.IsNullOrEmpty(combo.DisplayMemberPath) || combo.SelectedItem is not ComboBoxItem { Content: string } selected
            || string.IsNullOrEmpty(RuntimeLocalization.GetUid(selected))
            || combo.Items.Any(item => item is not ComboBoxItem { Content: string, ContentTemplate: null, ContentTemplateSelector: null }))
        {
            return;
        }

        var existing = presenter.GetBindingExpression(ContentPresenter.ContentProperty)?.ParentBinding;
        if (ReferenceEquals(existing?.Source, combo) && existing.Path?.Path == "SelectedItem.Content")
        {
            return;
        }

        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding
        {
            Source = combo,
            Path = new PropertyPath("SelectedItem.Content"),
            Mode = BindingMode.OneWay,
        });
    }

    private static List<DependencyObject> GetElements(bool includeCached = true)
    {
        var result = new List<DependencyObject>();
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<DependencyObject>();
        EnqueueRoots(queue, includeCached);

        while (queue.Count > 0)
        {
            var element = queue.Dequeue();
            if (!visited.Add(element))
            {
                continue;
            }

            result.Add(element);
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(element, index));
            }
        }

        return result;
    }

    private static void EnqueueRoots(Queue<DependencyObject> queue, bool includeCached)
    {
        for (int index = Roots.Count - 1; index >= 0; index--)
        {
            if (Roots[index].TryGetTarget(out var root))
            {
                if (includeCached || root.IsLoaded)
                {
                    queue.Enqueue(root);
                }
            }
            else
            {
                Roots.RemoveAt(index);
            }
        }

        if (window?.Content is FrameworkElement { XamlRoot: { } xamlRoot })
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
            {
                if (popup.Child != null)
                {
                    queue.Enqueue(popup.Child);
                }
            }
        }
    }

    private sealed class DiscoveryScan(long generation)
    {
        public long Generation { get; } = generation;

        public Queue<DependencyObject> Pending { get; } = new();

        public HashSet<DependencyObject> Visited { get; } = new(ReferenceEqualityComparer.Instance);

        public bool Started { get; set; }

        public DependencyObject Parent { get; set; }

        public int ChildIndex { get; set; }
    }

    private sealed class ElementVersion
    {
        public long Value { get; set; } = -1;

        public bool WaitingForLoaded { get; set; }
    }
}
