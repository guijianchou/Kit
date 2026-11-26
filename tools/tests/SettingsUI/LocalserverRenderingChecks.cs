using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Interfaces;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Kit.Settings.UI.Views;
using LocalServerHub.Core.Models;
using LocalServerHub.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

internal static class LocalserverRenderingChecks
{
    internal static async Task Run(string report)
    {
        NavigationService.Navigate(typeof(LocalserverPage));
        var page = (LocalserverPage)NavigationService.Frame.Content;
        var viewModel = page.ViewModel;
        await ((Task)Field(viewModel, "_initializationTask")).WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForLoaded(page);
        Require(!viewModel.IsEnabled && !viewModel.HasActiveServices,
            "Localserver rendering fixture requires the module and services to remain stopped.");

        var originalSelection = viewModel.SelectedLine;
        var originalEnvironmentLine = Field(viewModel, "_environmentCheckLine");
        var additions = new List<LineRowViewModel>();
        var rows = (ItemsControl)page.FindName("ServiceRows");
        var scroll = Descendants(page).OfType<ScrollViewer>()
            .Single(item => AutomationProperties.GetAutomationId(item) == "PageScrollViewer");
        double originalOffset = scroll.VerticalOffset;
        int collectionChanges = 0;
        int rowNotifications = 0;
        var changedRowProperties = new HashSet<string>(StringComparer.Ordinal);
        NotifyCollectionChangedEventHandler collectionChanged = (_, _) => collectionChanges++;
        PropertyChangedEventHandler rowChanged = (_, args) =>
        {
            rowNotifications++;
            changedRowProperties.Add(args.PropertyName ?? "<all>");
        };
        try
        {
            // Add only in-memory stopped rows. Do not start a process, save a catalog,
            // or replace the user's existing collection/runner objects.
            for (int index = 0; index < 12; index++)
            {
                var definition = new ServiceDefinition
                {
                    Id = "render-only-" + Guid.NewGuid().ToString("N"),
                    Name = "Rendering fixture " + index,
                    Cwd = Path.GetTempPath(),
                    Executable = "unused-rendering-fixture.exe",
                };
                var row = new LineRowViewModel(new ServiceRunner(definition), viewModel);
                additions.Add(row);
                viewModel.Lines.Add(row);
            }

            var selected = additions[4];
            selected.IsConfigExpanded = true;
            viewModel.SelectedLine = selected;
            await NextFrame(page);
            scroll.ChangeView(null, Math.Min(200, scroll.ScrollableHeight), null, true);
            await NextFrame(page);
            double savedOffset = scroll.VerticalOffset;
            Require(savedOffset > 0, "Localserver fixture did not create scrollable content.");

            var expectedRows = viewModel.Lines.ToArray();
            var expectedCollection = rows.ItemsSource;
            var containers = expectedRows.Select(row => rows.ContainerFromItem(row)).ToArray();
            Require(containers.All(item => item is FrameworkElement { ActualHeight: > 0 }),
                "Localserver service rows were not materialized.");
            Require(rows.ItemsPanelRoot.ChildrenTransitions == null || rows.ItemsPanelRoot.ChildrenTransitions.Count == 0,
                "Cached Localserver rows still replay an entrance transition.");
            viewModel.Lines.CollectionChanged += collectionChanged;
            selected.PropertyChanged += rowChanged;

            viewModel.RefreshEnabledState();
            viewModel.SetPageActive(true);
            Require(rowNotifications == 0 && collectionChanges == 0,
                "Unchanged enabled state or duplicate activation refreshed cached service rows.");

            // This represents a completed check. Navigation should cancel only work
            // still in flight, retaining a valid result until its normal expiry.
            SetField(viewModel, "_environmentCheckLine", selected);
            Require(NavigationService.Navigate(typeof(DashboardPage)), "Could not leave Localserver.");
            await WaitForLoaded((FrameworkElement)NavigationService.Frame.Content);
            Require(ReferenceEquals(Field(viewModel, "_environmentCheckLine"), selected),
                "Leaving Localserver discarded a completed environment check.");

            // Exercise the real runner notification path while the page is hidden,
            // without launching a process or waiting for a telemetry sample.
            await TransitionAndWaitForRowAsync(selected, ServiceState.Failed);
            await NextFrame((FrameworkElement)NavigationService.Frame.Content);
            Require(selected.State == ServiceState.Failed && rowNotifications > 0,
                $"A hidden runner state change did not update the existing row: state={selected.State}, notifications={rowNotifications}.");
            rowNotifications = 0;
            changedRowProperties.Clear();

            Require(NavigationService.Navigate(typeof(LocalserverPage)), "Could not return to Localserver.");
            Require(ReferenceEquals(NavigationService.Frame.Content, page), "Navigation replaced the cached Localserver page.");
            Require(ReferenceEquals(page.ViewModel, viewModel) && ReferenceEquals(rows.ItemsSource, expectedCollection),
                "Navigation replaced Localserver's ViewModel or ItemsSource.");
            await WaitForLoaded(page);
            await NextFrame(page);

            Require(viewModel.Lines.SequenceEqual(expectedRows) && collectionChanges == 0,
                "Returning to Localserver rebuilt or reset the service collection.");
            Require(ReferenceEquals(viewModel.SelectedLine, selected) && selected.IsConfigExpanded,
                "Returning to Localserver lost the selected service or expanded editor.");
            Require(Math.Abs(scroll.VerticalOffset - savedOffset) < 1,
                "Returning to Localserver lost its scroll offset.");
            Require(rowNotifications == 0,
                $"Returning to an unchanged module republished row properties: notifications={rowNotifications}, properties=[{string.Join(",", changedRowProperties)}].");
            for (int index = 0; index < expectedRows.Length; index++)
            {
                Require(ReferenceEquals(rows.ContainerFromItem(expectedRows[index]), containers[index]),
                    "Returning to Localserver recreated an unchanged row container.");
            }

            var selectedContainer = (FrameworkElement)rows.ContainerFromItem(selected);
            Require(selectedContainer.ActualHeight > 0 && Descendants(selectedContainer).OfType<TextBlock>()
                    .Any(text => text.Text == selected.StateLabel),
                "The first returned frame did not display the latest runner state.");

            using (var pendingCheck = new CancellationTokenSource())
            {
                SetField(viewModel, "_environmentCheckCancellation", pendingCheck);
                viewModel.SetPageActive(false);
                bool cancelled = pendingCheck.IsCancellationRequested && Field(viewModel, "_environmentCheckLine") == null;
                SetField(viewModel, "_environmentCheckCancellation", null);
                viewModel.SetPageActive(true);
                Require(cancelled, "Leaving Localserver did not cancel and invalidate an incomplete environment check.");
            }

            await CheckThemes(page, rows, scroll, selected, additions[0], expectedRows, containers, report);
            await CheckEnabledReturn(page, rows, scroll, selected, expectedRows, containers);
            File.AppendAllText(report,
                "Localserver cached return: disabled and enabled, same page/VM/collection/rows/containers, selected editor and scroll retained, no activation reset, completed check retained, pending check cancelled, hidden runtime update visible on first frame, enabled telemetry resumes without launching services PASS\n");
        }
        finally
        {
            viewModel.Lines.CollectionChanged -= collectionChanged;
            foreach (var row in additions)
            {
                row.PropertyChanged -= rowChanged;
                viewModel.Lines.Remove(row);
                row.Dispose();
            }

            viewModel.SelectedLine = originalSelection;
            SetField(viewModel, "_environmentCheckLine", originalEnvironmentLine);
            scroll.ChangeView(null, originalOffset, null, true);
        }
    }

    private static async Task CheckThemes(LocalserverPage page, ItemsControl rows, ScrollViewer scroll,
        LineRowViewModel selected, LineRowViewModel stopped, LineRowViewModel[] expectedRows,
        DependencyObject[] containers, string report)
    {
        var viewModel = page.ViewModel;
        var root = (FrameworkElement)App.GetSettingsWindow().Content;
        var originalTheme = root.RequestedTheme;
        var initialActualTheme = page.ActualTheme;
        var alternateTheme = initialActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        var expectedCollection = rows.ItemsSource;
        selected.RecentBars.Add(StatusBarViewModel.FromServiceState(ServiceState.Failed, false, selected.StateLabel));
        await NextFrame(page);
        var history = selected.RecentBars.ToArray();
        double savedOffset = scroll.VerticalOffset;
        int rowNotifications = 0;
        int collectionChanges = 0;
        var changedRows = new HashSet<LineRowViewModel>();
        var changedProperties = new HashSet<string>(StringComparer.Ordinal);
        var brushProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(LineRowViewModel.StateBrush), nameof(LineRowViewModel.UptimeBrush),
            nameof(LineRowViewModel.TagBrush), nameof(LineRowViewModel.TagBackgroundBrush),
        };
        PropertyChangedEventHandler rowChanged = (sender, args) =>
        {
            rowNotifications++;
            changedProperties.Add(args.PropertyName ?? "<all>");
            if (args.PropertyName == nameof(LineRowViewModel.StateBrush) && sender is LineRowViewModel row)
                changedRows.Add(row);
        };
        NotifyCollectionChangedEventHandler collectionChanged = (_, _) => collectionChanges++;
        Require(!viewModel.IsEnabled && selected.State == ServiceState.Failed && stopped.State == ServiceState.Stopped,
            "Theme checks require disabled Localserver with synthetic failed and stopped rows.");
        Require(page.RequestedTheme == ElementTheme.Default,
            "The Localserver theme fixture must inherit the settings window theme.");
        foreach (var row in expectedRows) row.PropertyChanged += rowChanged;
        viewModel.Lines.CollectionChanged += collectionChanged;
        try
        {
            // Localserver row brushes follow the settings window palette. Change it
            // only in memory so the test exercises the same theme path as the UI.
            LogTheme("visible-before-request");
            root.RequestedTheme = alternateTheme;
            await NextFrame(page);
            await WaitForAppliedTheme(alternateTheme, "visible-theme");
            await NextFrame(page);
            CheckRetainedRowsAndPalette();

            Require(NavigationService.Navigate(typeof(DashboardPage)), "Could not leave Localserver for a theme change.");
            await WaitForLoaded((FrameworkElement)NavigationService.Frame.Content);
            await DrainLowPriority();
            rowNotifications = 0;
            changedRows.Clear();
            changedProperties.Clear();
            LogTheme("hidden-before-request");
            root.RequestedTheme = initialActualTheme;
            await NextFrame((FrameworkElement)NavigationService.Frame.Content);
            await DrainLowPriority();
            LogTheme("hidden-after-request");
            Require(!page.IsLoaded && rowNotifications == 0,
                "Changing the hidden Localserver theme eagerly refreshed row bindings: " + ThemeState());

            Require(NavigationService.Navigate(typeof(LocalserverPage)), "Could not return after a hidden Localserver theme change.");
            await WaitForLoaded(page);
            await NextFrame(page);
            await WaitForAppliedTheme(initialActualTheme, "returned-theme");
            await NextFrame(page);
            CheckRetainedRowsAndPalette();
            File.AppendAllText(report,
                "Localserver cached themes: visible and deferred hidden changes refresh all retained row palettes without rebuilding rows, history, selection, expansion or scroll PASS\n");
        }
        finally
        {
            foreach (var row in expectedRows) row.PropertyChanged -= rowChanged;
            viewModel.Lines.CollectionChanged -= collectionChanged;
            root.RequestedTheme = originalTheme;
            await NextFrame(root);
        }

        async Task WaitForAppliedTheme(ElementTheme expected, string stage)
        {
            var elapsed = Stopwatch.StartNew();
            bool Applied() => page.IsLoaded && root.ActualTheme == expected && page.ActualTheme == expected &&
                Equals(Field(page, "_lastAppliedTheme"), expected) && changedRows.Count == expectedRows.Length;
            // ActualThemeChanged may arrive after the first rendering callback.
            while (!Applied() && elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            LogTheme(stage + "-settled-" + elapsed.ElapsedMilliseconds + "ms");
            Require(Applied(), stage + ": the inherited theme or retained row palettes did not update: " + ThemeState());
        }

        string ThemeState() =>
            $"requested={root.RequestedTheme}, root={root.ActualTheme}, actual={page.ActualTheme}, applied={Field(page, "_lastAppliedTheme")}, loaded={page.IsLoaded}, rowNotifications={rowNotifications}, brushRows={changedRows.Count}/{expectedRows.Length}, properties=[{string.Join(",", changedProperties)}], failedColor={((SolidColorBrush)selected.StateBrush).Color}, stoppedColor={((SolidColorBrush)stopped.StateBrush).Color}";

        void LogTheme(string stage) => File.AppendAllText(report, "Localserver theme " + stage + ": " + ThemeState() + "\n");

        void CheckRetainedRowsAndPalette()
        {
            Require(changedProperties.IsSubsetOf(brushProperties),
                "A theme change republished non-palette row properties: " + ThemeState());
            var critical = (SolidColorBrush)ThemeBrushHelper.GetBrush("SystemFillColorCriticalBrush", ThemeBrushHelper.CriticalBrush, page.ActualTheme);
            var stoppedBrush = (SolidColorBrush)ThemeBrushHelper.GetBrush("ControlStrongStrokeColorDefaultBrush", ThemeBrushHelper.StrokeDefaultBrush, page.ActualTheme);
            Require(((SolidColorBrush)selected.StateBrush).Color.Equals(critical.Color) &&
                ((SolidColorBrush)stopped.StateBrush).Color.Equals(stoppedBrush.Color),
                "Retained Localserver rows resolved the wrong theme palette: " + ThemeState());
            foreach (var (row, expectedBrush) in new[] { (selected, critical), (stopped, stoppedBrush) })
            {
                var indicator = Descendants((FrameworkElement)rows.ContainerFromItem(row))
                    .OfType<Microsoft.UI.Xaml.Shapes.Ellipse>().Single(shape => shape.Width == 7 && shape.Height == 7);
                Require(indicator.Fill is SolidColorBrush visibleBrush && visibleBrush.Color.Equals(expectedBrush.Color),
                    "The visible Localserver state indicator retained an outdated palette: " + ThemeState());
            }
            Require(ReferenceEquals(NavigationService.Frame.Content, page) && ReferenceEquals(page.ViewModel, viewModel) &&
                ReferenceEquals(rows.ItemsSource, expectedCollection) && viewModel.Lines.SequenceEqual(expectedRows) && collectionChanges == 0,
                "A theme change replaced the cached Localserver page, ViewModel, collection or rows.");
            Require(ReferenceEquals(viewModel.SelectedLine, selected) && selected.IsConfigExpanded &&
                selected.State == ServiceState.Failed && selected.RecentBars.SequenceEqual(history) &&
                Math.Abs(scroll.VerticalOffset - savedOffset) < 1,
                "A theme change lost Localserver state, history, selection, expansion or scroll.");
            for (int index = 0; index < expectedRows.Length; index++)
                Require(ReferenceEquals(rows.ContainerFromItem(expectedRows[index]), containers[index]),
                    "A theme change recreated a retained Localserver row container.");
        }
    }

    private static async Task CheckEnabledReturn(LocalserverPage page, ItemsControl rows, ScrollViewer scroll,
        LineRowViewModel selected, LineRowViewModel[] expectedRows, DependencyObject[] containers)
    {
        var viewModel = page.ViewModel;
        var repository = (ISettingsRepository<GeneralSettings>)Field(viewModel, "_generalSettingsRepository");
        bool previousEnabled = repository.SettingsConfig.Enabled.Localserver;
        int collectionChanges = 0;
        int rowNotifications = 0;
        NotifyCollectionChangedEventHandler collectionChanged = (_, _) => collectionChanges++;
        PropertyChangedEventHandler rowChanged = (_, _) => rowNotifications++;
        var telemetryTimer = (DispatcherTimer)Field(viewModel, "_telemetryTimer");
        var transition = typeof(ServiceRunner).GetMethod("TransitionTo", BindingFlags.Instance | BindingFlags.NonPublic);
        try
        {
            // Simulate a Runner settings reply in memory. RefreshEnabledState never
            // sends IPC or persists settings; enabling does not start catalog services.
            repository.SettingsConfig.Enabled.Localserver = true;
            viewModel.RefreshEnabledState();
            Require(viewModel.IsEnabled, "The enabled Localserver fixture did not enable its UI.");
            await TransitionAndWaitForRowAsync(selected, ServiceState.Running);
            await NextFrame(page);
            Require(selected.Runner.ProcessId == null && selected.State == ServiceState.Running,
                "The synthetic running row unexpectedly owns a process.");

            // Seed a just-completed result for this synthetic runner. All refresh work
            // continues normally; there is no need to inspect user service executables.
            for (int attempt = 0; attempt < 100 && (int)Field(viewModel, "_environmentCheckInFlight") != 0; attempt++)
                await Task.Delay(10);
            Require((int)Field(viewModel, "_environmentCheckInFlight") == 0, "The fixture environment check did not settle.");
            SetField(viewModel, "_environmentCheckLine", selected);
            SetField(viewModel, "_environmentCheckGeneration", selected.Runner.ProcessGeneration);
            SetField(viewModel, "_environmentCheckState", selected.State);
            SetField(viewModel, "_environmentCheckHealthProbePassed", selected.Runner.HealthProbePassed);
            SetField(viewModel, "_environmentCheckAtUtc", DateTimeOffset.UtcNow);
            await DrainTelemetry(viewModel);
            scroll.ChangeView(null, Math.Min(200, scroll.ScrollableHeight), null, true);
            await NextFrame(page);
            double savedOffset = scroll.VerticalOffset;

            viewModel.Lines.CollectionChanged += collectionChanged;
            selected.PropertyChanged += rowChanged;
            viewModel.RefreshEnabledState();
            viewModel.SetPageActive(true);
            Require(collectionChanges == 0 && rowNotifications == 0,
                "Unchanged enabled refresh or duplicate activation synchronously republished service rows.");

            Require(NavigationService.Navigate(typeof(DashboardPage)), "Could not leave enabled Localserver.");
            await WaitForLoaded((FrameworkElement)NavigationService.Frame.Content);
            Require(!telemetryTimer.IsEnabled && ReferenceEquals(Field(viewModel, "_environmentCheckLine"), selected),
                "Hidden enabled Localserver did not stop sampling or discarded its completed environment result.");
            Require(NavigationService.Navigate(typeof(LocalserverPage)), "Could not return to enabled Localserver.");
            Require(ReferenceEquals(NavigationService.Frame.Content, page), "Enabled return replaced the cached page.");
            await WaitForLoaded(page);
            await NextFrame(page);

            Require(telemetryTimer.IsEnabled && viewModel.Lines.SequenceEqual(expectedRows) && collectionChanges == 0,
                "Enabled return failed to resume telemetry or rebuilt the service collection.");
            Require(ReferenceEquals(viewModel.SelectedLine, selected) && selected.IsConfigExpanded
                    && Math.Abs(scroll.VerticalOffset - savedOffset) < 1,
                "Enabled return lost selection, expansion or scroll.");
            for (int index = 0; index < expectedRows.Length; index++)
                Require(ReferenceEquals(rows.ContainerFromItem(expectedRows[index]), containers[index]),
                    "Enabled return recreated an unchanged service row container.");
            var container = (FrameworkElement)rows.ContainerFromItem(selected);
            Require(selected.State == ServiceState.Running && container.ActualHeight > 0
                    && Descendants(container).OfType<TextBlock>().Any(text => text.Text == selected.StateLabel),
                "The enabled first returned frame did not show its existing running service.");

            rowNotifications = 0;
            await DrainTelemetry(viewModel);
            await viewModel.RefreshTelemetryAsync();
            await DrainTelemetry(viewModel);
            Require(rowNotifications > 0 && collectionChanges == 0,
                "Enabled telemetry no longer refreshes runtime properties, or it rebuilds service rows.");
            Require(expectedRows.All(row => row.Runner.ProcessId == null),
                "Enabling the rendering fixture unexpectedly started a catalog service.");
        }
        finally
        {
            viewModel.Lines.CollectionChanged -= collectionChanged;
            selected.PropertyChanged -= rowChanged;
            transition.Invoke(selected.Runner, new object[] { ServiceState.Stopped, "render-only-restore" });
            repository.SettingsConfig.Enabled.Localserver = previousEnabled;
            viewModel.RefreshEnabledState();
            await DrainTelemetry(viewModel);
        }
    }

    private static async Task DrainTelemetry(LocalserverViewModel viewModel)
    {
        for (int attempt = 0; attempt < 200 && (int)Field(viewModel, "_telemetryRefreshInFlight") != 0; attempt++)
            await Task.Delay(10);
        Require((int)Field(viewModel, "_telemetryRefreshInFlight") == 0, "Localserver telemetry did not settle.");
    }

    private static async Task DrainLowPriority()
    {
        var drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Require(DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, () => drained.TrySetResult(true)),
            "Could not enqueue the Localserver theme notification barrier.");
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task TransitionAndWaitForRowAsync(LineRowViewModel row, ServiceState next)
    {
        // StateChanged queues row notifications on DispatcherQueue. A Rendering event
        // is not a queue-drain barrier and can precede that callback on a cached page.
        var notified = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int notifications = 0;
        PropertyChangedEventHandler handler = (_, args) =>
        {
            notifications++;
            if (args.PropertyName == nameof(LineRowViewModel.StateLabel) && row.State == next)
                notified.TrySetResult(true);
        };
        row.PropertyChanged += handler;
        try
        {
            typeof(ServiceRunner).GetMethod("TransitionTo", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(row.Runner, new object[] { next, "render-only-state-change" });
            Require(row.State == next, $"Runner transition did not publish its state: expected={next}, actual={row.State}.");
            try { await notified.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                throw new InvalidOperationException($"Runner state did not reach the row notification: expected={next}, actual={row.State}, notifications={notifications}.");
            }

            var drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Require(DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, () => drained.TrySetResult(true)),
                "Could not enqueue the Localserver row notification barrier.");
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            row.PropertyChanged -= handler;
        }
    }

    private static object Field(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);

    private static void SetField(object owner, string name, object value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(owner, value);

    private static async Task WaitForLoaded(FrameworkElement element)
    {
        if (element.IsLoaded) return;
        var loaded = new TaskCompletionSource<bool>();
        RoutedEventHandler handler = (_, _) => loaded.TrySetResult(true);
        element.Loaded += handler;
        try { await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { element.Loaded -= handler; }
    }

    private static async Task NextFrame(FrameworkElement element)
    {
        element.UpdateLayout();
        var rendered = new TaskCompletionSource<bool>();
        EventHandler<object> handler = (_, _) => rendered.TrySetResult(true);
        CompositionTarget.Rendering += handler;
        try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= handler; }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
