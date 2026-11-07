using System;
using System.Collections;
using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Kit.Settings.UI.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
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
