using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Kit.Settings.UI.Controls;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Kit.Settings.UI.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;
using UDPtestLib.Core;

internal static class UDPtestRenderingChecks
{
    internal static async Task Run(string report)
    {
        NavigationService.Navigate(typeof(UDPtestPage));
        await Task.Delay(150);
        var page = (UDPtestPage)NavigationService.Frame.Content;
        var viewModel = page.ViewModel;
        for (int attempt = 0; attempt < 30 && viewModel.Lines.Count == 0; attempt++)
        {
            await Task.Delay(50);
        }

        Require(viewModel.Lines.Count > 0, "UDPtest lines did not initialize.");
        Require(!viewModel.IsEnabled, "The synthetic rendering check requires UDPtest to remain disabled.");
        // Disabled settings are still materialized; only IsEnabled and opacity change.
        // Wait for layout rather than enabling the module (which would start identity polling).
        var canvas = (Canvas)page.FindName("HealthChartCanvas");
        Require(canvas != null, "The health chart canvas was not materialized.");
        for (int attempt = 0; attempt < 30 && (!page.IsLoaded || canvas.ActualWidth <= 0); attempt++)
        {
            await Task.Delay(50);
        }

        Require(page.IsLoaded && canvas.ActualWidth > 0 && canvas.ActualHeight > 0, "The disabled health chart did not complete layout.");
        await CheckCachedRows(page, report);
        await CheckHistoryDirection(canvas);
        var line = viewModel.Lines[0];
        viewModel.SelectedLine = line;
        viewModel.SetPageActive(false);
        int initialBars = line.RecentBars.Count;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int index = 0; index < 300; index++)
        {
            Commit(viewModel, Snapshot(line.Id, now.AddMilliseconds(index - 300)));
        }

        Require(line.RecentBars.Count == initialBars, "Hidden snapshots mutated the UI collection.");
        var pendingBars = (IDictionary)Field(viewModel, "_pendingResultBars");
        Require((int)pendingBars[line.Id].GetType().GetProperty("Count").GetValue(pendingBars[line.Id]) == 240,
            "Hidden result history did not retain only the latest 240 samples.");

        int changes = 0;
        NotifyCollectionChangedEventHandler changed = (_, _) => changes++;
        line.RecentBars.CollectionChanged += changed;
        viewModel.SetPageActive(true);
        Require(line.RecentBars.Count == initialBars && changes == 0, "Activation synchronously replayed the history.");
        await DrainLowPriority();
        line.RecentBars.CollectionChanged -= changed;
        Require(line.RecentBars.Count == 240 && changes == 1, "Activation did not apply the bounded history in one batch.");
        Require(viewModel.GetCurrentChartPoints().Count == 300, "Hidden sampling lost chart history.");

        viewModel.SetPageActive(false);
        int cap = (int)typeof(UDPtestViewModel).GetField("MaximumChartSamples", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
        var commit = typeof(UDPtestViewModel).GetMethod("OnCoordinatorSnapshotCommitted", BindingFlags.NonPublic | BindingFlags.Instance);
        var fresh = Snapshot(line.Id, DateTimeOffset.UtcNow);
        for (int index = 0; index <= cap; index++)
        {
            commit.Invoke(viewModel, new object[] { fresh });
        }

        Require(((ICollection)Field(viewModel, "_chartSamples")).Count == cap, "Chart history exceeded its hard cap.");
        Require(viewModel.GetCurrentChartPoints().Count == cap, "Chart cap did not retain fresh samples.");

        // Work only with in-memory samples; do not clear persisted history or change target settings.
        Field(viewModel, "_chartSamples").GetType().GetMethod("Clear").Invoke(Field(viewModel, "_chartSamples"), null);
        ((IDictionary)Field(viewModel, "_pendingSnapshots")).Clear();
        pendingBars.Clear();
        DateTimeOffset reference = DateTimeOffset.UtcNow;
        Commit(viewModel, Snapshot(line.Id, reference.AddSeconds(-90)));
        Commit(viewModel, Snapshot(line.Id, reference.AddSeconds(-30)));
        Require(viewModel.GetCurrentChartPoints(reference).Count == 1, "The selected period included expired samples.");
        Require(viewModel.GetCurrentChartPoints(reference.AddSeconds(31)).Count == 0, "A stopped history did not expire with the time window.");

        viewModel.SetPageActive(true);
        await DrainLowPriority();
        await DrainLowPriority();
        var redraw = typeof(UDPtestPage).GetMethod("RedrawHealthChart", BindingFlags.NonPublic | BindingFlags.Instance);
        var curve = (ShapePath)page.FindName("HealthCurvePath");
        Require(curve != null, "Health chart was not materialized.");
        bool checkedCache = false;
        for (int attempt = 0; attempt < 3 && !checkedCache; attempt++)
        {
            long second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            redraw.Invoke(page, null);
            var geometry = curve.Data;
            Require(geometry != null, "A successful sample did not draw a health curve.");
            redraw.Invoke(page, null);
            if (second == DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                Require(ReferenceEquals(geometry, curve.Data), "An unchanged chart rebuilt its geometry.");
                checkedCache = true;
            }
        }

        Require(checkedCache, "Could not check chart reuse within a stable time window.");
        var previousGeometry = curve.Data;
        Commit(viewModel, Snapshot(line.Id, DateTimeOffset.UtcNow, 90));
        redraw.Invoke(page, null);
        Require(!ReferenceEquals(previousGeometry, curve.Data), "New data did not invalidate the chart cache.");
        await DrainLowPriority();
        File.AppendAllText(report, "UDPtest rendering: hidden sampling, deferred activation, one batch, bounded history, window expiry and geometry reuse PASS\n");
        File.AppendAllText(report, "UDPtest status: left-to-right growth, chronological overflow and narrow-width drawing PASS\n");
    }

    private static async Task CheckCachedRows(UDPtestPage page, string report)
    {
        var viewModel = page.ViewModel;
        var tcpRows = (ItemsControl)page.FindName("TcpRows");
        var udpRows = (ItemsControl)page.FindName("UdpRows");
        var scroll = Descendants(page).OfType<ScrollViewer>()
            .Single(item => AutomationProperties.GetAutomationId(item) == "PageScrollViewer");
        var activeIds = (HashSet<string>)Field(viewModel, "_activeLineIds");
        var additions = new List<UDPtestLineRowViewModel>();
        var originalSelection = viewModel.SelectedLine;
        double originalOffset = scroll.VerticalOffset;
        int collectionChanges = 0;
        int rowNotifications = 0;
        int enabledNotifications = 0;
        NotifyCollectionChangedEventHandler collectionChanged = (_, _) => collectionChanges++;
        PropertyChangedEventHandler rowChanged = (_, _) => rowNotifications++;
        PropertyChangedEventHandler enabledChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(UDPtestViewModel.IsEnabled)) enabledNotifications++;
        };
        try
        {
            // Keep the module disabled: these rows and snapshots are in memory only,
            // with no save/toggle callbacks, probes or identity requests.
            for (int index = 0; index < 10; index++)
            {
                bool isTcp = index < 2;
                var row = new UDPtestLineRowViewModel(new ProbeLineDefinition(
                    "render-only-" + Guid.NewGuid().ToString("N"), "Rendering fixture " + index,
                    isTcp ? ProbeProtocol.Tcp : ProbeProtocol.Udp,
                    isTcp ? ProbeKind.Https204 : ProbeKind.StunBinding,
                    "example.invalid", isTcp ? 443 : 3478, TargetLabel: "Rendering fixture " + index));
                row.StatusVisibility = Visibility.Visible;
                additions.Add(row);
                activeIds.Add(row.Id);
                viewModel.Lines.Add(row);
                (isTcp ? viewModel.TcpLines : viewModel.UdpLines).Add(row);
            }

            var tcpLine = additions[0];
            var udpLine = additions[2];
            udpLine.IsConfigExpanded = true;
            udpLine.EditName = "Unsaved rendering draft";
            udpLine.EditEndpoint = "draft.example.invalid:3478";
            viewModel.SelectedLine = udpLine;
            Commit(viewModel, Snapshot(tcpLine.Id, DateTimeOffset.UtcNow, 20));
            Commit(viewModel, Snapshot(udpLine.Id, DateTimeOffset.UtcNow, 30));
            await DrainLowPriority();
            await NextFrame(page);
            scroll.ChangeView(null, Math.Min(250, scroll.ScrollableHeight), null, true);
            await NextFrame(page);
            double savedOffset = scroll.VerticalOffset;
            Require(savedOffset > 0, "UDPtest fixture did not create scrollable content.");

            var all = viewModel.Lines.ToArray();
            var tcp = viewModel.TcpLines.ToArray();
            var udp = viewModel.UdpLines.ToArray();
            var tcpSource = tcpRows.ItemsSource;
            var udpSource = udpRows.ItemsSource;
            var tcpContainers = tcp.Select(row => tcpRows.ContainerFromItem(row)).ToArray();
            var udpContainers = udp.Select(row => udpRows.ContainerFromItem(row)).ToArray();
            Require(tcpContainers.Concat(udpContainers).All(item => item is FrameworkElement { ActualHeight: > 0 }),
                "UDPtest TCP/UDP row containers were not materialized.");
            Require((tcpRows.ItemsPanelRoot.ChildrenTransitions?.Count ?? 0) == 0 &&
                (udpRows.ItemsPanelRoot.ChildrenTransitions?.Count ?? 0) == 0,
                "Cached TCP/UDP rows still replay entrance transitions.");

            viewModel.Lines.CollectionChanged += collectionChanged;
            viewModel.TcpLines.CollectionChanged += collectionChanged;
            viewModel.UdpLines.CollectionChanged += collectionChanged;
            viewModel.PropertyChanged += enabledChanged;
            foreach (var row in all) row.PropertyChanged += rowChanged;
            viewModel.RefreshEnabledState();
            viewModel.SetPageActive(true);
            Require(enabledNotifications == 0 && rowNotifications == 0 && (int)Field(viewModel, "_snapshotRefreshQueued") == 0,
                "Unchanged enabled state or duplicate activation refreshed cached UDPtest rows.");

            // A return without new measurements must retain every existing object and
            // draft. This distinguishes animation replay from a real catalog reload.
            Require(NavigationService.Navigate(typeof(DashboardPage)), "Could not leave UDPtest.");
            await WaitForLoaded((FrameworkElement)NavigationService.Frame.Content);
            Require(NavigationService.Navigate(typeof(UDPtestPage)), "Could not return to UDPtest.");
            await WaitForLoaded(page);
            await DrainLowPriority();
            await NextFrame(page);
            CheckRetainedRows();
            Require(rowNotifications == 0 && enabledNotifications == 0 && tcpLine.RecentBars.Count == 1 && udpLine.RecentBars.Count == 1,
                "An unchanged cached return republished rows or reset measurement history.");

            Require(NavigationService.Navigate(typeof(DashboardPage)), "Could not leave UDPtest for background snapshots.");
            await WaitForLoaded((FrameworkElement)NavigationService.Frame.Content);
            Commit(viewModel, Snapshot(tcpLine.Id, DateTimeOffset.UtcNow, 80));
            Commit(viewModel, Snapshot(udpLine.Id, DateTimeOffset.UtcNow, 40));
            Commit(viewModel, Snapshot(udpLine.Id, DateTimeOffset.UtcNow, 60));
            Require(rowNotifications == 0 && tcpLine.AverageRtt == "20 ms" && udpLine.AverageRtt == "30 ms",
                "Background snapshots changed hidden row controls before activation.");

            Require(NavigationService.Navigate(typeof(UDPtestPage)), "Could not return to pending UDPtest snapshots.");
            await WaitForLoaded(page);
            await DrainLowPriority();
            await NextFrame(page);
            CheckRetainedRows();
            Require(rowNotifications > 0 && tcpLine.AverageRtt == "80 ms" && udpLine.AverageRtt == "60 ms" &&
                tcpLine.RecentBars.Count == 2 && udpLine.RecentBars.Count == 3,
                "Returning to UDPtest failed to apply the latest background measurements and retained history.");
            foreach (var entry in new[] { (Rows: tcpRows, Line: tcpLine), (Rows: udpRows, Line: udpLine) })
            {
                var container = (FrameworkElement)entry.Rows.ContainerFromItem(entry.Line);
                Require(Descendants(container).OfType<TextBlock>().Any(text => text.Text == entry.Line.AverageRtt && text.ActualHeight > 0),
                    "The returned TCP/UDP row did not render its latest measurement.");
            }
            Require(Descendants((FrameworkElement)udpRows.ContainerFromItem(udpLine)).OfType<TextBox>()
                .Any(editor => editor.Text == "Unsaved rendering draft" && editor.ActualHeight > 0),
                "The returned UDP editor lost its unsaved draft.");
            File.AppendAllText(report, "UDPtest cached return: same page/VM/collections/TCP+UDP rows/containers, selection/draft/scroll retained, no entrance transition or unchanged row refresh, pending background measurements and history applied without probes PASS\n");

            void CheckRetainedRows()
            {
                Require(ReferenceEquals(NavigationService.Frame.Content, page) && ReferenceEquals(page.ViewModel, viewModel),
                    "Navigation replaced the cached UDPtest page or ViewModel.");
                Require(ReferenceEquals(tcpRows.ItemsSource, tcpSource) && ReferenceEquals(udpRows.ItemsSource, udpSource) &&
                    viewModel.Lines.SequenceEqual(all) && viewModel.TcpLines.SequenceEqual(tcp) && viewModel.UdpLines.SequenceEqual(udp) && collectionChanges == 0,
                    "Navigation rebuilt or reset the TCP/UDP collections.");
                Require(ReferenceEquals(viewModel.SelectedLine, udpLine) && udpLine.IsConfigExpanded &&
                    udpLine.EditName == "Unsaved rendering draft" && udpLine.EditEndpoint == "draft.example.invalid:3478" &&
                    Math.Abs(scroll.VerticalOffset - savedOffset) < 1,
                    "Returning to UDPtest lost selection, an expanded draft or scroll position.");
                for (int index = 0; index < tcp.Length; index++)
                    Require(ReferenceEquals(tcpRows.ContainerFromItem(tcp[index]), tcpContainers[index]), "Returning recreated a TCP row container.");
                for (int index = 0; index < udp.Length; index++)
                    Require(ReferenceEquals(udpRows.ContainerFromItem(udp[index]), udpContainers[index]), "Returning recreated a UDP row container.");
            }
        }
        finally
        {
            viewModel.Lines.CollectionChanged -= collectionChanged;
            viewModel.TcpLines.CollectionChanged -= collectionChanged;
            viewModel.UdpLines.CollectionChanged -= collectionChanged;
            viewModel.PropertyChanged -= enabledChanged;
            foreach (var row in viewModel.Lines) row.PropertyChanged -= rowChanged;
            foreach (var row in additions)
            {
                activeIds.Remove(row.Id);
                viewModel.Lines.Remove(row);
                viewModel.TcpLines.Remove(row);
                viewModel.UdpLines.Remove(row);
            }
            viewModel.SelectedLine = originalSelection;
            scroll.ChangeView(null, originalOffset, null, true);
        }
    }

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

    private static async Task CheckHistoryDirection(Canvas host)
    {
        var samples = new ObservableCollection<StatusBarViewModel>();
        var strip = new ProbeHistoryStrip { Width = 68, Height = 20, ItemsSource = samples };
        host.Children.Add(strip);
        try
        {
            for (int attempt = 0; attempt < 30 && (!strip.IsLoaded || strip.ActualWidth != 68); attempt++)
            {
                await Task.Delay(50);
            }

            Require(strip.IsLoaded && strip.ActualWidth == 68, "The status strip fixture did not complete layout.");
            var paths = ((Canvas)strip.Content).Children.OfType<ShapePath>().ToArray();
            Require(paths.Length == 3, "Status history must retain its fixed three-path renderer.");
            samples.Add(StatusBarViewModel.FromProbeResult(false, 0, "oldest"));
            var first = ((PathGeometry)paths[2].Data).Figures.Single();
            Require(Math.Abs(first.StartPoint.X - 2.5) < 0.01, "A new status history must begin at the left edge.");

            samples.Add(StatusBarViewModel.FromProbeResult(true, 20, "second"));
            var previous = ((PathGeometry)paths[2].Data).Figures.Single();
            var next = ((PathGeometry)paths[0].Data).Figures.Single();
            Require(Math.Abs(previous.StartPoint.X - first.StartPoint.X) < 0.01 && next.StartPoint.X > previous.StartPoint.X,
                "Appending a sample must grow the status history rightward without moving the first sample.");

            for (int index = 2; index < 11; index++)
            {
                samples.Add(StatusBarViewModel.FromProbeResult(true, 20, "sample " + index));
            }

            samples.Add(StatusBarViewModel.FromProbeResult(true, 300, "newest"));
            Require(((PathGeometry)paths[2].Data).Figures.Count == 0, "Overflow did not discard the oldest visible failure.");
            Require(paths.Sum(path => ((PathGeometry)path.Data).Figures.Count) == 10, "Overflow must draw only the ten visible slots.");
            Require(Math.Abs(((PathGeometry)paths[1].Data).Figures.Single().StartPoint.X + 2.5 - strip.ActualWidth) < 0.01,
                "A full status history must keep its newest sample at the right edge.");

            strip.Width = 5;
            host.UpdateLayout();
            await DrainLowPriority();
            Require(strip.ActualWidth == 5, "The status strip did not resize to one slot.");
            Require(paths.Sum(path => ((PathGeometry)path.Data).Figures.Count) == 1,
                "A one-slot status history must show only its newest sample.");
            Require(Math.Abs(((PathGeometry)paths[1].Data).Figures.Single().StartPoint.X - 2.5) < 0.01,
                "The newest sample must remain inside a narrow status history.");
        }
        finally
        {
            host.Children.Remove(strip);
        }
    }

    private static LineMetricSnapshot Snapshot(string lineId, DateTimeOffset timestamp, double rtt = 20) => new(
        lineId, "OK", 1, 0, 1, 240, 0, 100, (int)rtt, 0, null, null, null,
        true, true, ProbeOutcome.Success, timestamp, rtt);

    private static void Commit(UDPtestViewModel viewModel, LineMetricSnapshot snapshot) =>
        typeof(UDPtestViewModel).GetMethod("OnCoordinatorSnapshotCommitted", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(viewModel, new object[] { snapshot });

    private static object Field(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(owner);

    private static Task DrainLowPriority()
    {
        var completion = new TaskCompletionSource<bool>();
        if (!DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, () => completion.SetResult(true)))
        {
            throw new InvalidOperationException("Could not queue the rendering assertion.");
        }

        return completion.Task;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
