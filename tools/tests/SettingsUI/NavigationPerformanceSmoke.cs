using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Kit.Settings.UI.Views;
using ManagedCommon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;
using UDPtestLib.Core;

internal static class NavigationPerformanceSmoke
{
    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly string Report = Path.Combine(Environment.GetEnvironmentVariable("KIT_NAV_TEST_OUTPUT"), "navigation-performance.log");
    private static readonly bool Baseline = Environment.GetEnvironmentVariable("KIT_NAV_BASELINE") == "1";
    private static readonly Dictionary<Type, HashSet<int>> Instances = new();
    private static readonly Dictionary<Type, HashSet<int>> ViewModels = new();
    private static readonly List<(string Page, double Loaded, double Rendered)> Timings = new();
    private static readonly ConditionalWeakTable<object, Identity> Identities = new();
    private static int nextIdentity;
    private sealed class Identity { public int Id = ++nextIdentity; public bool Prepared; }
    private static Identity Identify(object value) => Identities.GetValue(value, _ => new Identity());

    [STAThread]
    private static void Main()
    {
        File.WriteAllText(Report, "Real WinUI; synthetic UDP history; no AI requests or UDP/network probes. Baseline=" + Baseline + "\n");
        Log("Dashboard sampling is disposed before Loaded to isolate visual navigation; memory checkpoints describe this synthetic harness, not whole-application peak RAM.");
        try
        {
            // The wrapper temporarily disables AI and restores its exact configuration bytes.
            Log("START loading disabled AI service configuration");
            Require(!new Kit.AiHub.Storage.AiHubSettingsStore().Load().IsEnabled, "AI service must be disabled by the wrapper before navigation.");
            Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
            Log("START WindowsAppRuntime_EnsureIsLoaded");
            WindowsAppRuntime_EnsureIsLoaded();
            Log("START InitializeComWrappers");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Log("START Application.Start");
            Application.Start(_ =>
            {
                Log("START Application callback");
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                Log("START App constructor with GeneralPage");
                var app = new App { StartupPage = typeof(GeneralPage) };
                Log("START App constructed");
                DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
                {
                    try
                    {
                        Log("START queued benchmark");
                        await Task.Delay(350);
                        await Run();
                        Log("PASS");
                        Environment.Exit(0);
                    }
                    catch (Exception ex)
                    {
                        Log(ex.ToString());
                        Environment.Exit(1);
                    }
                });
            });
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            Environment.Exit(2);
        }
    }

    private static async Task Run()
    {
        var repository = SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default);
        repository.StopWatching();
        repository.SettingsConfig = new GeneralSettings();
        repository.SettingsConfig.Enabled.UDPtest = false;
        repository.SettingsConfig.Enabled.AiHub = true;
        ShellPage.SetDefaultSndMessageCallback(_ => { });
        NavigationService.Frame.Navigated += (_, args) => PreparePage((Page)args.Content);
        App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1000));

        var pages = new[] { typeof(DashboardPage), typeof(AIHubPage), typeof(UDPtestPage) };
        LogMemory("before-first-visits");
        foreach (var type in pages) await Navigate(type, warmup: true);
        var udp = (UDPtestPage)NavigationService.Frame.Content;
        await Task.Delay(200);
        SeedUdp(udp.ViewModel);
        udp.UpdateLayout();
        await NextRendering();
        VerifyHistoryVisuals(udp);
        LogMemory("after-warmup");

        // Twenty-one visits exercise the sixteen-entry navigation journal bound.
        for (int visit = 0; visit < 21; visit++)
        {
            await Navigate(pages[visit % pages.Length], warmup: false);
            await Task.Delay(75);
        }

        VerifyHistoryVisuals((UDPtestPage)NavigationService.Frame.Content);
        foreach (var type in pages)
        {
            Log($"INSTANCES {type.Name}: pages={Instances[type].Count}, viewModels={ViewModels[type].Count}");
            if (!Baseline) Require(Instances[type].Count == 1 && ViewModels[type].Count == 1, type.Name + " must reuse one page and view model.");
            var times = Timings.Where(item => item.Page == type.Name).ToArray();
            Log($"SUMMARY {type.Name}: n={times.Length}, loadedMedianMs={Median(times.Select(item => item.Loaded)):F2}, loadedMaxMs={times.Max(item => item.Loaded):F2}, renderMedianMs={Median(times.Select(item => item.Rendered)):F2}, renderMaxMs={times.Max(item => item.Rendered):F2}");
        }
        Log($"JOURNAL back={NavigationService.Frame.BackStack.Count}, forward={NavigationService.Frame.ForwardStack.Count}");
        if (!Baseline) Require(NavigationService.Frame.BackStack.Count <= 16, "Navigation journal must be bounded to sixteen entries.");
        LogMemory("after-revisits");
        if (!Baseline) await VerifyHistoryStripBehavior((UDPtestPage)NavigationService.Frame.Content);
    }

    private static void PreparePage(Page page)
    {
        var type = page.GetType();
        if (!Instances.TryGetValue(type, out var instances)) Instances[type] = instances = new();
        if (!ViewModels.TryGetValue(type, out var models)) ViewModels[type] = models = new();
        var identity = Identify(page);
        instances.Add(identity.Id);
        models.Add(Identify(page.DataContext).Id);
        if (identity.Prepared) return;
        identity.Prepared = true;

        if (page is DashboardPage dashboard)
        {
            // Navigated occurs before Loaded. Disable real system/network work before visibility activates it.
            dashboard.ViewModel.Dispose();
            Set(dashboard.ViewModel, "SystemDetails", new DashboardDetail[]
            {
                new("Device", "Navigation fixture"), new("Windows", "Windows 11"), new("Processor", "Synthetic CPU"),
                new("Logical processors", "16"), new("CPU load", "12%", 12), new("Memory", "8 / 32 GB", 25), new("Uptime", "1d 02:03:04"),
            });
            Set(dashboard.ViewModel, "StorageDetails", new[] { new DashboardDetail("C:", "120 / 500 GB", 24) });
            Set(dashboard.ViewModel, "GraphicsCards", new[] { new DashboardGpuCard("GPU 1 · Synthetic", new[] { new DashboardDetail("Load", "10%", 10), new DashboardDetail("Memory", "2 / 8 GB", 25), new DashboardDetail("Temperature", "40 C"), new DashboardDetail("Driver", "Fixture") }, "") });
        }
    }

    private static void SeedUdp(UDPtestViewModel vm)
    {
        // Cancel initialization/identity polling; never start the coordinator or write target settings.
        ((CancellationTokenSource)typeof(UDPtestViewModel).GetField("_lifecycleCancellation", PrivateInstance).GetValue(vm)).Cancel();
        vm.Lines.Clear();
        vm.TcpLines.Clear();
        vm.UdpLines.Clear();
        for (int index = 0; index < 6; index++)
        {
            bool tcp = index < 2;
            var definition = new ProbeLineDefinition("fixture-" + index, "Fixture " + index, tcp ? ProbeProtocol.Tcp : ProbeProtocol.Udp,
                tcp ? ProbeKind.Https204 : ProbeKind.StunBinding, "192.0.2.1", tcp ? 443 : 3478, IsEnabled: true);
            var row = new UDPtestLineRowViewModel(definition) { StatusVisibility = Visibility.Visible };
            for (int sample = 0; sample < 240; sample++) row.AddResultBar(sample % 17 == 0 ? ProbeOutcome.Failure : ProbeOutcome.Success, 40 + sample % 300);
            vm.Lines.Add(row);
            if (tcp) vm.TcpLines.Add(row); else vm.UdpLines.Add(row);
        }
        vm.SelectedLine = vm.Lines[0];
        Log("FIXTURE six lines with 240 samples each; coordinator remains stopped.");
    }

    private static async Task Navigate(Type type, bool warmup)
    {
        var watch = Stopwatch.StartNew();
        Require(NavigationService.Navigate(type), "Navigation failed for " + type.Name);
        var page = (Page)NavigationService.Frame.Content;
        if (!page.IsLoaded)
        {
            var loaded = new TaskCompletionSource<bool>();
            RoutedEventHandler handler = null;
            handler = (_, _) => { page.Loaded -= handler; loaded.TrySetResult(true); };
            page.Loaded += handler;
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        double loadedMs = watch.Elapsed.TotalMilliseconds;
        await NextRendering();
        double renderMs = watch.Elapsed.TotalMilliseconds;
        Log($"NAV {(warmup ? "cold" : "warm")} {type.Name}: loadedMs={loadedMs:F2}, nextRenderingMs={renderMs:F2}, instance={Identify(page).Id}, back={NavigationService.Frame.BackStack.Count}");
        if (!warmup) Timings.Add((type.Name, loadedMs, renderMs));
    }

    private static async Task NextRendering()
    {
        var rendered = new TaskCompletionSource<bool>();
        EventHandler<object> handler = null;
        handler = (_, _) => { CompositionTarget.Rendering -= handler; rendered.TrySetResult(true); };
        CompositionTarget.Rendering += handler;
        try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= handler; }
    }

    private static void VerifyHistoryVisuals(UDPtestPage page)
    {
        var all = Descendants(page).ToArray();
        var strips = all.Where(item => item.GetType().FullName == "Kit.Settings.UI.Controls.ProbeHistoryStrip").ToArray();
        int oldBars = all.OfType<FrameworkElement>().Count(item => item.DataContext is StatusBarViewModel);
        Log($"VISUALS UDP: total={all.Length}, strips={strips.Length}, legacySampleElements={oldBars}, samples={page.ViewModel.Lines.Sum(line => line.RecentBars.Count)}");
        Require(page.ViewModel.Lines.Count == 6 && page.ViewModel.Lines.All(line => line.RecentBars.Count == 240), "Synthetic history must survive navigation intact.");
        if (!Baseline)
        {
            Require(strips.Length == 6, "Each line must use one lightweight history strip.");
            Require(strips.All(strip => Descendants(strip).Count() <= 8), "History visual count must remain fixed rather than create one element per sample.");
            Require(oldBars == 0, "History strips must not retain legacy sample controls.");
        }
    }

    private static async Task VerifyHistoryStripBehavior(UDPtestPage page)
    {
        var strips = Descendants(page).OfType<FrameworkElement>()
            .Where(item => item.GetType().FullName == "Kit.Settings.UI.Controls.ProbeHistoryStrip").ToArray();
        Require(strips.Length == 6, "Probe history checks require six loaded strips.");
        foreach (var strip in strips)
        {
            VerifyStripGeometry(strip);
        }

        var target = strips[0];
        Require(target.DispatcherQueue.HasThreadAccess, "Probe history checks must stay on the XAML thread.");
        Require(target.IsLoaded && target.Visibility == Visibility.Visible, "Probe history theme target must be loaded and visible.");
        target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
        await NextRendering();
        await Task.Delay(50);
        var targetPosition = target.TransformToVisual((UIElement)App.GetSettingsWindow().Content).TransformPoint(new Windows.Foundation.Point());
        Log($"PROBE_TARGET loaded={target.IsLoaded}, visibility={target.Visibility}, size={target.ActualWidth:F1}x{target.ActualHeight:F1}, rootY={targetPosition.Y:F1}, uiThread={target.DispatcherQueue.HasThreadAccess}");
        Require(targetPosition.Y + target.ActualHeight > 0 && targetPosition.Y < ((FrameworkElement)App.GetSettingsWindow().Content).ActualHeight,
            "Probe history theme target must be inside the window viewport.");
        var samples = (IList<StatusBarViewModel>)target.GetType().GetProperty("ItemsSource").GetValue(target);
        var select = target.GetType().GetMethod("SelectSample", PrivateInstance);
        Require(select != null, "Probe history sample selection must exist.");
        var tooltip = ToolTipService.GetToolTip(target) as ToolTip;
        Require(tooltip != null, "Probe history must share one tooltip.");
        foreach (int index in new[] { samples.Count - 1, samples.Count - 2 })
        {
            select.Invoke(target, new object[] { index });
            Require(Equals(tooltip.Content, samples[index].Description), "Probe history tooltip selected the wrong sample.");
            Require(AutomationProperties.GetHelpText(target) == $"{index + 1} / {samples.Count}: {samples[index].Description}",
                "Probe history accessibility text must describe the selected sample.");
        }

        var oldGeometry = HistoryPaths(target).Select(path => path.Data).ToArray();
        var themeRoot = (FrameworkElement)App.GetSettingsWindow().Content;
        ElementTheme originalRequestedTheme = themeRoot.RequestedTheme;
        ElementTheme changedTheme = target.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        await ChangeStripTheme(target, themeRoot, changedTheme);
        Require(target.ActualTheme == changedTheme, "Probe history did not adopt the requested theme.");
        var newGeometry = HistoryPaths(target).Select(path => path.Data).ToArray();
        Log($"PROBE_THEME applied={target.ActualTheme}, rendered={target.GetType().GetField("_renderedTheme", PrivateInstance).GetValue(target)}, retainedGeometry={oldGeometry.Where((geometry, index) => ReferenceEquals(geometry, newGeometry[index])).Count()}");
        Require(oldGeometry.Where((geometry, index) => ReferenceEquals(geometry, newGeometry[index])).Count() == 0,
            "Theme changes must invalidate every probe history geometry.");
        VerifyStripGeometry(target);
        await ChangeStripTheme(target, themeRoot, originalRequestedTheme);
        VerifyStripGeometry(target);

        var retainedGeometry = strips.Select(strip => HistoryPaths(strip).Select(path => path.Data).ToArray()).ToArray();
        var retainedSizes = strips.Select(strip => (strip.ActualWidth, strip.ActualHeight)).ToArray();
        Require(NavigationService.Navigate(typeof(AIHubPage)), "Could not leave UDPtest to check geometry retention.");
        await NextRendering();
        Require(strips.All(strip => !strip.IsLoaded), "Probe history strips must unload while their page is away.");
        Require(NavigationService.Navigate(typeof(UDPtestPage)), "Could not return to UDPtest to check geometry retention.");
        await NextRendering();
        Require(ReferenceEquals(page, NavigationService.Frame.Content), "Returning to UDPtest replaced its cached page.");
        for (int stripIndex = 0; stripIndex < strips.Length; stripIndex++)
        {
            var strip = strips[stripIndex];
            Require(strip.IsLoaded, "Cached probe history did not load on return.");
            Require(retainedSizes[stripIndex] == (strip.ActualWidth, strip.ActualHeight), "Geometry reuse requires unchanged strip dimensions.");
            var paths = HistoryPaths(strip);
            for (int index = 0; index < paths.Length; index++)
            {
                Require(ReferenceEquals(retainedGeometry[stripIndex][index], paths[index].Data),
                    "Unchanged probe history rebuilt geometry on page return.");
            }
        }

        Log("PROBE_STRIP fixed paths, bounded figures, rightmost latest sample, heights, theme brushes, tooltip/a11y selection, theme invalidation and geometry retention PASS");
    }

    private static async Task ChangeStripTheme(FrameworkElement strip, FrameworkElement themeRoot, ElementTheme requestedTheme)
    {
        var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canvas = (FrameworkElement)((UserControl)strip).Content;
        Windows.Foundation.TypedEventHandler<FrameworkElement, object> rootHandler = (_, _) => Log($"PROBE_THEME root event actual={themeRoot.ActualTheme}");
        Windows.Foundation.TypedEventHandler<FrameworkElement, object> handler = (_, _) =>
        {
            Log($"PROBE_THEME content event actual={strip.ActualTheme}, rendered={strip.GetType().GetField("_renderedTheme", PrivateInstance).GetValue(strip)}, loaded={strip.IsLoaded}");
            changed.TrySetResult(true);
        };
        canvas.ActualThemeChanged += handler;
        themeRoot.ActualThemeChanged += rootHandler;
        try
        {
            Log($"PROBE_THEME request actual={strip.ActualTheme}, rootActual={themeRoot.ActualTheme}, rootRequested={requestedTheme}, localRequested={strip.RequestedTheme}");
            // Match ThemeService -> WindowHelper.SetTheme: the app changes the root
            // theme, while cached content inherits it. Keep the target in the viewport.
            themeRoot.RequestedTheme = requestedTheme;
            // Code-created UserControl receives the theme through its native content.
            // Wait for that actual notification, then assert the resulting geometry.
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await NextRendering();
        }
        catch (TimeoutException)
        {
            Log($"PROBE_THEME timeout rootActual={themeRoot.ActualTheme}, rootRequested={themeRoot.RequestedTheme}, stripActual={strip.ActualTheme}, localRequested={strip.RequestedTheme}, canvasActual={canvas.ActualTheme}, rendered={strip.GetType().GetField("_renderedTheme", PrivateInstance).GetValue(strip)}, loaded={strip.IsLoaded}, uiThread={strip.DispatcherQueue.HasThreadAccess}");
            throw;
        }
        finally
        {
            canvas.ActualThemeChanged -= handler;
            themeRoot.ActualThemeChanged -= rootHandler;
        }
    }

    private static ShapePath[] HistoryPaths(DependencyObject strip) => Descendants(strip).OfType<ShapePath>().ToArray();

    private static void VerifyStripGeometry(FrameworkElement strip)
    {
        var paths = HistoryPaths(strip);
        Require(paths.Length == 3, "Probe history must contain exactly three paths.");
        var samples = (IList<StatusBarViewModel>)strip.GetType().GetProperty("ItemsSource").GetValue(strip);
        int slots = Math.Max(1, (int)Math.Floor((strip.ActualWidth + 2) / 7));
        int visible = Math.Min(samples.Count, slots);
        var geometries = paths.Select(path => path.Data as PathGeometry).ToArray();
        Require(geometries.All(geometry => geometry != null), "Loaded probe history must draw every severity path.");
        Require(geometries.Sum(geometry => geometry.Figures.Count) == visible, "Probe history must only draw visible samples.");
        string[] brushKeys = { "SystemFillColorSuccessBrush", "SystemFillColorCautionBrush", "SystemFillColorCriticalBrush" };
        double[] heights = { 16, 11, 6 };
        double rightmost = double.MinValue;
        int rightmostSeverity = -1;
        for (int severity = 0; severity < paths.Length; severity++)
        {
            var expectedBrush = Kit.Settings.UI.Helpers.ThemeBrushHelper.GetBrush(brushKeys[severity], null, strip.ActualTheme);
            Require(expectedBrush != null, "The fixture did not expose the required theme brush.");
            Require(paths[severity].Stroke is SolidColorBrush actual && expectedBrush is SolidColorBrush expected && actual.Color == expected.Color,
                "Probe history severity color differs from the current theme.");
            Require(Math.Abs(paths[severity].StrokeThickness - 5) < 0.01, "Probe history bar width changed.");
            foreach (var figure in geometries[severity].Figures)
            {
                Require(figure.Segments.Count == 1 && figure.Segments[0] is LineSegment, "Probe history bars must contain one line segment.");
                var end = ((LineSegment)figure.Segments[0]).Point;
                Require(Math.Abs((end.Y - figure.StartPoint.Y + paths[severity].StrokeThickness) - Math.Min(heights[severity], strip.ActualHeight)) < 0.01,
                    "Probe history severity height changed.");
                Require(Math.Abs(end.Y + 2.5 - strip.ActualHeight) < 0.01, "Probe history bars must align to the bottom edge.");
                if (end.X > rightmost)
                {
                    rightmost = end.X;
                    rightmostSeverity = severity;
                }
            }
        }

        Require(Math.Abs(rightmost + 2.5 - strip.ActualWidth) < 0.01, "The newest probe sample must reach the right edge.");
        var latest = samples[samples.Count - 1];
        int expectedSeverity = latest.BarHeight > 11 ? 0 : latest.BarHeight > 6 ? 1 : 2;
        Require(rightmostSeverity == expectedSeverity, "The newest probe sample was drawn with the wrong severity.");
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

    private static void Set(DashboardViewModel vm, string property, object value)
    {
        typeof(DashboardViewModel).GetProperty(property).SetValue(vm, value);
        typeof(DashboardViewModel).GetMethod("OnPropertyChanged", PrivateInstance, null, new[] { typeof(string) }, null).Invoke(vm, new object[] { property });
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        return sorted[sorted.Length / 2];
    }

    private static void LogMemory(string phase)
    {
        using var process = Process.GetCurrentProcess();
        Log($"MEMORY {phase}: workingSetBytes={process.WorkingSet64}, privateBytes={process.PrivateMemorySize64}, managedBytes={GC.GetTotalMemory(false)}, gen0={GC.CollectionCount(0)}, gen1={GC.CollectionCount(1)}, gen2={GC.CollectionCount(2)}");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Log(string text) => File.AppendAllText(Report, text + "\n");
}
