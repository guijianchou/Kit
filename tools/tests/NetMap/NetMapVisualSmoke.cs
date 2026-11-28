// Renders the compiled NetMapPage in a separate WinUI process. Product App startup,
// IPC, probes, AI requests and persisted MTR mutations are deliberately not entered.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Interfaces;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Kit.Settings.UI.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NetMapLib;
using Windows.Graphics.Imaging;

internal static class NetMapVisualSmoke
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    internal static readonly string Output = Path.GetFullPath(Environment.GetEnvironmentVariable("KIT_NETMAP_VISUAL_OUTPUT") ?? throw new InvalidOperationException("Output directory is required."));
    private static readonly string Fixture = Path.Combine(Output, "fixture");
    private static MainWindow window;

    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();

    [STAThread]
    private static void Main()
    {
        Directory.CreateDirectory(Output);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("UNHANDLED " + e.ExceptionObject);
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        WindowsAppRuntime_EnsureIsLoaded();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            var app = new VisualApplication();
            app.UnhandledException += (_, e) => Log("XAML UNHANDLED " + e.Exception);
        });
    }

    internal sealed class VisualApplication : Application, IXamlMetadataProvider
    {
        private readonly IXamlMetadataProvider provider = new Kit.Settings.UI.Kit_Settings_XamlTypeInfo.XamlMetaDataProvider();
        public IXamlType GetXamlType(Type type) => provider.GetXamlType(type);
        public IXamlType GetXamlType(string name) => provider.GetXamlType(name);
        public XmlnsDefinition[] GetXmlnsDefinitions() => provider.GetXmlnsDefinitions();

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                ConfigureIsolation();
                typeof(RuntimeLocalization).GetMethod("Initialize", PrivateStatic).Invoke(null,
                    new object[] { Environment.GetEnvironmentVariable("KIT_NETMAP_VISUAL_LANGUAGE") ?? "en-US" });
                Resources = (ResourceDictionary)XamlReader.Load(File.ReadAllText(Path.Combine(Output, "application-resources.xaml")));
                window = new MainWindow();
                typeof(App).GetField("settingsWindow", PrivateStatic).SetValue(null, window);
                ShellPage.SetDefaultSndMessageCallback(_ => { });
                ShellPage.SetUpdatingGeneralSettingsCallback((_, _) => false);
                Require(NavigationService.Navigate(typeof(NetMapPage)), "Actual compiled NetMapPage navigated.");
                window.Activate();
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1320, 940));
                await Settle();
                var page = (NetMapPage)NavigationService.Frame.Content;
                await Run(page);
                await HoldManualPreview(page);
                Log("PASS");
                Environment.Exit(0);
            }
            catch (Exception exception)
            {
                Log("FAIL " + exception);
                Environment.Exit(1);
            }
        }
    }

    private static void ConfigureIsolation()
    {
        Require(Output.Contains(Path.DirectorySeparatorChar + "TestResults" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Output is inside TestResults.");
        Directory.CreateDirectory(Fixture);
        ManagedCommon.Logger.SetLogging(false);
        // Instance fields can be replaced before any settings consumers are created.
        // The product assemblies/XAML remain unchanged; only this process's I/O seam is redirected.
        var fs = new FileSystem();
        var paths = FixturePathProxy.Wrap<IPath>(fs.Path, Fixture);
        var directories = FixturePathProxy.Wrap<IDirectory>(fs.Directory, Fixture);
        var files = FixturePathProxy.Wrap<IFile>(fs.File, Fixture);
        typeof(SettingsUtils).GetField("_settingsPath", PrivateInstance).SetValue(SettingsUtils.Default, new SettingPath(directories, paths));
        typeof(SettingsUtils).GetField("_file", PrivateInstance).SetValue(SettingsUtils.Default, files);
        foreach (string module in new[] { "", "NetMap" })
            Require(IsFixturePath(SettingsUtils.Default.GetSettingsFilePath(module)), "Settings path redirected: " + module);

        int fontSize = int.TryParse(Environment.GetEnvironmentVariable("KIT_NETMAP_VISUAL_FONT_SIZE"), out int requestedSize) ? requestedSize : 14;
        var general = new GeneralSettings { Theme = "light", UiFontSize = fontSize };
        general.Enabled.NetMap = true;
        SetRepository(general);
        SetRepository(new NetMapSettings
        {
            Properties = new NetMapProperties
            {
                OnlineLookup = new BoolProperty(false),
                AsnDatabase = new StringProperty(Path.Combine(Fixture, "asn.mmdb")),
                CityDatabase = new StringProperty(Path.Combine(Fixture, "city.mmdb")),
            },
        });
        string placement = Path.Combine(Fixture, "settings-placement.json");
        typeof(App).Assembly.GetType("Kit.Settings.UI.Helpers.WindowHelper", true)
            .GetField("_placementPath", PrivateStatic).SetValue(null, placement);
        Require(IsFixturePath(placement), "Window placement redirected before MainWindow construction.");
        Log("No product App constructor, IPC, probe, AI request or generation-changing snapshot is used.");
    }

    private static void SetRepository<T>(T configuration) where T : class, ISettingsConfig, new()
    {
        var type = typeof(SettingsRepository<T>);
        var repository = (SettingsRepository<T>)Activator.CreateInstance(type, nonPublic: true);
        repository.SettingsConfig = configuration;
        type.GetField("_settingsUtils", PrivateStatic).SetValue(null, SettingsUtils.Default);
        type.GetField("settingsRepository", PrivateStatic).SetValue(null, repository);
    }

    private static bool IsFixturePath(string path) => Path.GetFullPath(path).StartsWith(Fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static async Task Run(NetMapPage page)
    {
        var vm = page.ViewModel;
        var session = (NetMapSession)typeof(NetMapViewModel).GetField("session", PrivateInstance).GetValue(vm);
        var map = (FrameworkElement)page.FindName("MapView");
        var list = (ListView)page.FindName("HopList");
        var root = (FrameworkElement)window.Content;
        Require(page.IsLoaded && map.ActualWidth > 0, "Actual page and map are laid out.");
        Require(!vm.IsDetectionOn, "No detection session was started.");
        long generation = vm.Snapshot.Generation;
        await CheckGlobalMap(page, session, generation);
        foreach (var scenario in new[] { "regional", "pacific" })
        {
            HopResult[] hops = scenario == "regional"
                ? RegionalHops()
                : new[]
                {
                    Hop(1, "192.0.2.1", 31.23, 121.47, "Shanghai", "Shanghai", "CN", 64496, "Example access"),
                    Hop(2, "192.0.2.2", 34.05, -118.24, "Los Angeles", "California", "US", 64497, "Example Pacific transit"),
                    Hop(3, "192.0.2.3", 1.35, 103.82, "Singapore", "Singapore", "SG", 64498, "Example Singapore exchange"),
                };
            // Keep the current generation: changing it would enter the product's MTR-file cleanup.
            // Reuse the last observed hop as the synthetic target so the header
            // exercises a full IPv6 address and complete metrics at large font sizes.
            // No egress geography is supplied; only the existing hop positions fit the map.
            // Route is a product localization discriminator, not a fixture label.
            var targetIdentity = new IdentityResult(new(hops[^1].Ip, string.Empty, string.Empty), ProbeError.None, DateTimeOffset.UtcNow, "SystemProxy");
            var snapshot = vm.Snapshot with { Generation = generation, Running = false, Direct = new(), Proxy = new(targetIdentity, targetIdentity), Hops = hops };
            ApplySnapshot(page, session, snapshot);
            list.SelectedIndex = 2;
            foreach (int width in new[] { 1320, 620 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 940));
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    root.RequestedTheme = theme;
                    typeof(NetMapPage).GetMethod("MapZoomReset_Click", PrivateInstance).Invoke(page, new object[] { null, null });
                    map.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
                    await Settle();
                    CheckMap(page, vm.Hops[2], scenario + "/" + width + "/" + theme);
                    string name = scenario + "-" + width + "-" + theme.ToString().ToLowerInvariant();
                    await Capture(page, name + "-page.png");
                    await Capture(page, name + "-map.png", map);
                    if (width == 620)
                    {
                        ((FrameworkElement)page.FindName("MapHeader")).StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
                        await Settle();
                        await Capture(page, name + "-header.png");
                        map.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
                        await Settle();
                    }
                    if (scenario == "regional")
                    {
                        typeof(NetMapPage).GetMethod("MapZoomIn_Click", PrivateInstance).Invoke(page, new object[] { null, null });
                        await Settle();
                        CheckMap(page, vm.Hops[2], name + "/zoom");
                        await Capture(page, name + "-zoom-map.png", map);
                        if (width == 1320 && theme == ElementTheme.Light)
                            await CheckMapNavigation(page);
                    }
                }
            }
        }
        await CheckIncrementalUpdates(page, session, generation);
        var close = Descendants((Canvas)page.FindName("MapPoints")).OfType<Button>().Single(button => (string)button.Tag == "CloseHopDetails");
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(close)).Invoke();
        await Settle();
        Require(list.SelectedIndex == -1 && !((Canvas)page.FindName("MapPoints")).Children.OfType<Border>().Any(border => (string)border.Tag == "HopDetails"),
            "Closing selected-hop details clears selection and removes the card.");
        Require(!vm.IsDetectionOn && session.Completion.IsCompleted, "Render fixtures never started network probes.");
        Require(vm.Snapshot.Generation == generation, "MTR cleanup path was never entered.");
    }

    private static HopResult[] RegionalHops() => new[]
    {
        Hop(1, "192.0.2.1", 32.06, 118.78, "Nanjing", "Jiangsu", "CN", 64496, "Example regional access network"),
        Hop(2, "192.0.2.2", 31.23, 121.47, "Shanghai", "Shanghai", "CN", 64497, "Example transit and international network with a long organization name"),
        Hop(3, "2001:db8:1234:5678:9012:3456:7890:1234", 22.32, 114.17, "Hong Kong", "Hong Kong", "HK", 64498, "Example international backbone and peering organization with long ASN details"),
    };

    private static async Task CheckMapNavigation(NetMapPage page)
    {
        var list = (ListView)page.FindName("HopList");
        int selected = list.SelectedIndex;
        list.SelectedIndex = -1;
        await Settle();
        var outline = (Microsoft.UI.Xaml.Shapes.Path)page.FindName("WorldOutline");
        var geometry = outline.Data;
        var before = ((MatrixTransform)outline.RenderTransform).Matrix;
        typeof(NetMapPage).GetMethod("PanMap", PrivateInstance).Invoke(page, new object[] { 36.0, 12.0 });
        await Settle();
        var moved = ((MatrixTransform)outline.RenderTransform).Matrix;
        Require(Math.Abs(moved.OffsetX - before.OffsetX - 36) < .01 && Math.Abs(moved.OffsetY - before.OffsetY - 12) < .01,
            "Manual map pan translates the displayed geography.");
        Require(ReferenceEquals(geometry, outline.Data), "Short drags reuse cached regional geometry.");
        Require(((Canvas)page.FindName("MapLabels")).Children.Count > 0, "Unobscured regional map shows geographic labels.");
        var map = (FrameworkElement)page.FindName("MapView");
        var anchor = new MapPoint(map.ActualWidth * .4, map.ActualHeight * .45);
        double worldX = (anchor.X - moved.OffsetX) / moved.M11;
        double worldY = (anchor.Y - moved.OffsetY) / moved.M22;
        typeof(NetMapPage).GetMethod("ZoomMap", PrivateInstance).Invoke(page, new object[] { 2.0, anchor });
        await Settle();
        var zoomed = ((MatrixTransform)outline.RenderTransform).Matrix;
        Require(Math.Abs(worldX * zoomed.M11 + zoomed.OffsetX - anchor.X) < .01 && Math.Abs(worldY * zoomed.M22 + zoomed.OffsetY - anchor.Y) < .01,
            "Map zoom preserves the geographic point beneath its pointer anchor.");
        CheckMapStrokes(page, "manual-pan-and-pointer-zoom");
        await Capture(page, "regional-detail-pan-zoom-map.png", map);
        typeof(NetMapPage).GetMethod("MapZoomReset_Click", PrivateInstance).Invoke(page, new object[] { null, null });
        list.SelectedIndex = selected;
        await Settle();
    }

    private static async Task HoldManualPreview(NetMapPage page)
    {
        string requested = Environment.GetEnvironmentVariable("KIT_NETMAP_MANUAL_VISUAL_SECONDS");
        if (string.IsNullOrEmpty(requested) || requested == "0") return;
        Require(int.TryParse(requested, out int seconds) && seconds is >= 0 and <= 300,
            "Manual preview duration is within 0..300 seconds.");
        if (seconds == 0) return;

        // This optional preview starts only after every automated assertion passes.
        // Keep navigation and operations disabled while Details, map controls and scrolling remain usable.
        NavigationService.Frame.Navigating += (_, args) => args.Cancel = true;
        ((Control)page.FindName("EnableSwitch")).IsEnabled = false;
        ((Control)page.FindName("SettingsExpander")).IsEnabled = false;
        foreach (var button in Descendants((FrameworkElement)page.FindName("HeroTelemetryActions")).OfType<Button>())
            button.IsEnabled = false;

        var vm = page.ViewModel;
        var session = (NetMapSession)typeof(NetMapViewModel).GetField("session", PrivateInstance).GetValue(vm);
        long generation = vm.Snapshot.Generation;
        var hops = RegionalHops();
        var now = DateTimeOffset.UtcNow;
        var direct = new IdentityResult(new(hops[0].Ip, "CN", "Nanjing, Jiangsu", "Example regional access network", "Jiangsu", "Nanjing"), ProbeError.None, now, "Direct");
        var proxy = new IdentityResult(new(hops[^1].Ip, "HK", "Hong Kong", "Example international backbone", "Hong Kong", "Hong Kong"), ProbeError.None, now, "SystemProxy");
        ApplySnapshot(page, session, vm.Snapshot with
        {
            Generation = generation,
            Running = false,
            Direct = new(direct, direct, hops[0].Geography),
            Proxy = new(proxy, proxy),
            Hops = hops,
        });
        // The snapshot can refresh x:Bind values on action buttons; disable them after applying it too.
        foreach (var button in Descendants((FrameworkElement)page.FindName("HeroTelemetryActions")).OfType<Button>())
            button.IsEnabled = false;
        ((ListView)page.FindName("HopList")).SelectedIndex = 2;
        ((FrameworkElement)window.Content).RequestedTheme = ElementTheme.Light;
        page.ClearValue(Control.BackgroundProperty);
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1320, 940));
        typeof(NetMapPage).GetMethod("MapZoomReset_Click", PrivateInstance).Invoke(page, new object[] { null, null });
        ((FrameworkElement)page.FindName("MapView")).StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        await Settle();
        window.Activate();
        Log($"MANUAL VISUAL READY: {seconds}s; theme=Light; PID={Environment.ProcessId}; regional node selected; scroll up to open Direct or Proxy Details.");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Require(!vm.IsDetectionOn && session.Completion.IsCompleted && vm.Snapshot.Generation == generation,
            "Manual preview completed without network probes or route-generation changes.");
    }

    private static HopResult Hop(int ttl, string ip, double latitude, double longitude, string city, string region, string country, long asn, string organization) =>
        new(ttl, ip, 20, 19, 42.3, 40.1, false, new GeoInfo(asn, organization, city, country, latitude, longitude, LookupStatus: "Ready", Source: "Synthetic visual fixture", Region: region));

    private static void ApplySnapshot(NetMapPage page, NetMapSession session, NetMapSnapshot snapshot)
    {
        Require(snapshot.Generation == page.ViewModel.Snapshot.Generation, "Fixture keeps the route generation and does not enter MTR-file cleanup.");
        typeof(NetMapSession).GetField("current", PrivateInstance).SetValue(session, snapshot);
        typeof(NetMapViewModel).GetMethod("ApplySnapshot", PrivateInstance).Invoke(page.ViewModel, new object[] { snapshot });
    }

    private static Border SelectedDetails(NetMapPage page) => ((Canvas)page.FindName("MapPoints")).Children.OfType<Border>().SingleOrDefault(item => (string)item.Tag == "HopDetails");

    private static Dictionary<string, TextBlock> DetailFields(Border label) => Descendants(label).OfType<TextBlock>()
        .Where(text => text.Tag is string tag && tag.StartsWith("Map", StringComparison.Ordinal)).ToDictionary(text => (string)text.Tag);

    private static async Task CheckGlobalMap(NetMapPage page, NetMapSession session, long generation)
    {
        var map = (FrameworkElement)page.FindName("MapView");
        ApplySnapshot(page, session, NetMapSnapshot.Empty with { Generation = generation });
        ((ListView)page.FindName("HopList")).SelectedIndex = -1;
        foreach (int width in new[] { 1320, 620 })
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 940));
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                ((FrameworkElement)window.Content).RequestedTheme = theme;
                typeof(NetMapPage).GetMethod("MapZoomReset_Click", PrivateInstance).Invoke(page, new object[] { null, null });
                map.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
                await Settle();
                string name = "global-" + width + "-" + theme.ToString().ToLowerInvariant();
                CheckMapStrokes(page, name);
                Require(SelectedDetails(page) == null && page.ViewModel.Hops.Count == 0, name + ": world map has no selected-hop overlay.");
                await Capture(page, name + "-map.png", map);
            }
        }
    }

    private static async Task CheckIncrementalUpdates(NetMapPage page, NetMapSession session, long generation)
    {
        var vm = page.ViewModel;
        var map = (FrameworkElement)page.FindName("MapView");
        var list = (ListView)page.FindName("HopList");
        var initial = new[]
        {
            Hop(1, "192.0.2.1", 32.06, 118.78, "Nanjing", "Jiangsu", "CN", 64496, "Example access"),
            Hop(2, "192.0.2.2", 31.23, 121.47, "Shanghai", "Shanghai", "CN", 64497, "Example transit"),
            new HopResult(3, "2001:db8:1234:5678:9012:3456:7890:1234", 20, 19, 42.3, 40.1, false, new GeoInfo(LookupStatus: "Pending")),
        };
        var snapshot = vm.Snapshot with { Generation = generation, Hops = initial };
        ApplySnapshot(page, session, snapshot);
        list.SelectedIndex = 2;
        map.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        await Settle();
        var selectedRow = vm.Hops[2];
        Require(SelectedDetails(page) == null && selectedRow.MapAsn == NetMapViewModel.Text("Asn_Pending"), "Pending geography does not invent a marker or ASN.");

        var arrived = initial.ToArray();
        arrived[2] = Hop(3, initial[2].Ip, 22.32, 114.17, "Hong Kong", "Hong Kong", "HK", 64498, "Example international backbone");
        snapshot = snapshot with { Hops = arrived };
        ApplySnapshot(page, session, snapshot);
        await Settle();
        Require(ReferenceEquals(selectedRow, vm.Hops[2]) && list.SelectedIndex == 2, "Late geography preserves the selected row.");
        CheckMap(page, selectedRow, "late-geography");
        await Capture(page, "late-geography-map.png", map);

        var label = SelectedDetails(page);
        var fields = DetailFields(label);
        string previousMetrics = fields["MapMetrics"].Text;
        var outline = (Microsoft.UI.Xaml.Shapes.Path)page.FindName("WorldOutline");
        var previousGeometry = outline.Data;
        var previousTransform = outline.RenderTransform;
        var previousLines = ((Canvas)page.FindName("MapLines")).Children.ToArray();
        var metrics = arrived.ToArray();
        metrics[2] = metrics[2] with { Sent = 30, Received = 28, LastMs = 68.6, AverageMs = 60.2 };
        snapshot = snapshot with { Hops = metrics };
        ApplySnapshot(page, session, snapshot);
        await Settle();
        Require(ReferenceEquals(label, SelectedDetails(page)) && ReferenceEquals(previousGeometry, outline.Data) && ReferenceEquals(previousTransform, outline.RenderTransform) &&
            previousLines.SequenceEqual(((Canvas)page.FindName("MapLines")).Children), "RTT-only updates reuse the details card, world geometry, transform and route shapes.");
        Require(fields["MapMetrics"].Text == selectedRow.MapMetrics && fields["MapMetrics"].Text != previousMetrics, "RTT-only updates refresh metrics on the existing card.");
        await Capture(page, "rtt-only-update-map.png", map);

        var enriched = metrics.ToArray();
        enriched[2] = enriched[2] with
        {
            Geography = enriched[2].Geography with
            {
                Asn = 64500, City = "Victoria Harbour", Region = "Hong Kong", Latitude = 22.28, Longitude = 114.16,
                Organization = string.Join(" ", Enumerable.Repeat("Example updated international transit and peering organization", 8)),
            },
        };
        snapshot = snapshot with { Hops = enriched };
        ApplySnapshot(page, session, snapshot);
        await Settle();
        label = SelectedDetails(page);
        fields = DetailFields(label);
        Require(ReferenceEquals(selectedRow, vm.Hops[2]) && fields["MapAsn"].Text.Contains("AS64500", StringComparison.Ordinal) &&
            fields["MapLocation"].Text.Contains("Victoria Harbour", StringComparison.Ordinal), "Late geography/ASN revision refreshes the selected card.");
        CheckMap(page, selectedRow, "revised-geography");
        var scroll = Descendants(label).OfType<ScrollViewer>().First();
        Require(scroll.ScrollableHeight > 0, "Long ASN detail creates a real scrollable body.");
        scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
        await Settle();
        var last = fields["MapMetrics"];
        var bounds = last.TransformToVisual(scroll).TransformBounds(new Windows.Foundation.Rect(0, 0, last.ActualWidth, last.ActualHeight));
        Require(scroll.VerticalOffset >= scroll.ScrollableHeight - 1 && bounds.Top >= -.5 && bounds.Bottom <= scroll.ViewportHeight + .5,
            "Scrolling details to the end exposes the complete last metrics row.");
        await Capture(page, "revised-geography-scrolled-map.png", map);
    }

    private static void CheckMapStrokes(NetMapPage page, string caseName)
    {
        var header = (Grid)page.FindName("MapHeader");
        var title = (FrameworkElement)page.FindName("MapHeaderTitle");
        var target = (FrameworkElement)page.FindName("RouteTargetPill");
        var metrics = (FrameworkElement)page.FindName("TargetMetricsPill");
        var states = VisualStateManager.GetVisualStateGroups((FrameworkElement)page.Content)
            .Single(group => group.Name == "MapHeaderLayoutStates");
        var trigger = states.States.Single(state => state.Name == "MapHeaderNarrow").StateTriggers[0];
        var triggerTarget = trigger.GetType().GetProperty("TargetElement").GetValue(trigger) as FrameworkElement;
        Log(caseName + $": headerLayout page={page.ActualWidth:0.#}, header={header.ActualWidth:0.#}x{header.ActualHeight:0.#}, state={states.CurrentState?.Name ?? "none"}, triggerTarget={triggerTarget?.Name ?? "null"}/{triggerTarget?.ActualWidth:0.#}, title={Layout(title)}, target={Layout(target)}, metrics={Layout(metrics)}");
        Require(ReferenceEquals(triggerTarget, header), caseName + ": header trigger tracks the available header width.");
        foreach (var name in new[] { "MapHeaderTitleText", "RouteTargetText", "TargetLatencyText" })
        {
            var text = (TextBlock)page.FindName(name);
            var bounds = text.TransformToVisual(header).TransformBounds(new Windows.Foundation.Rect(0, 0, text.ActualWidth, text.ActualHeight));
            Require(text.ActualWidth > 0 && text.ActualHeight > 0 && !text.IsTextTrimmed && text.TextWrapping == TextWrapping.Wrap &&
                bounds.Left >= -1 && bounds.Right <= header.ActualWidth + 1 && bounds.Top >= -1 && bounds.Bottom <= header.ActualHeight + 1,
                caseName + $": complete {name} stays in the header at font size {TypographyResources.AppliedFontSize}.");
        }
        Require(((TextBlock)page.FindName("RouteTargetText")).Text == page.ViewModel.RouteTarget &&
            ((TextBlock)page.FindName("TargetLatencyText")).Text == page.ViewModel.TargetLatency,
            caseName + ": responsive header retains the target address and all statistics.");
        var titleBounds = Bounds(title);
        var targetBounds = Bounds(target);
        var metricsBounds = Bounds(metrics);
        Require(metricsBounds.Top >= Math.Max(titleBounds.Bottom, targetBounds.Bottom) + 7,
            caseName + ": target statistics occupy their own nonoverlapping row.");
        if (header.ActualWidth <= 860)
        {
            Require(states.CurrentState?.Name == "MapHeaderNarrow" && Grid.GetColumnSpan(title) == 2 &&
                Grid.GetRow(target) == 1 && Grid.GetColumn(target) == 0 && Grid.GetColumnSpan(target) == 2 &&
                Grid.GetRow(metrics) == 2 && targetBounds.Top >= titleBounds.Bottom + 7,
                caseName + ": narrow header stacks the full target address below its title.");
        }
        else
        {
            Require(states.CurrentState?.Name == "MapHeaderWide" && Grid.GetColumnSpan(title) == 1 &&
                Grid.GetRow(target) == 0 && Grid.GetColumn(target) == 1 && Grid.GetColumnSpan(target) == 1 && Grid.GetRow(metrics) == 1,
                caseName + ": wide header restores its title and address columns.");
        }
        var outline = (Microsoft.UI.Xaml.Shapes.Path)page.FindName("WorldOutline");
        var graticule = (Microsoft.UI.Xaml.Shapes.Path)page.FindName("WorldGraticule");
        var transform = (MatrixTransform)outline.RenderTransform;
        Require(outline.Data is PathGeometry geometry && geometry.Figures.Count > 0, caseName + ": compiled map geometry is present.");
        Require(Math.Abs(outline.StrokeThickness * transform.Matrix.M11 - 1) < .02 &&
            Math.Abs(graticule.StrokeThickness * transform.Matrix.M11 - .6) < .02, caseName + ": map strokes remain 1/.6 DIPs after scaling.");
        var coastline = (Microsoft.UI.Xaml.Shapes.Path)page.FindName("WorldCoastline");
        if (transform.Matrix.M11 >= 1.4)
            Require(coastline.Data is PathGeometry coastGeometry && coastGeometry.Figures.Count > 0 &&
                Math.Abs(coastline.StrokeThickness * transform.Matrix.M11 - 1.2) < .02, caseName + ": regional map has separate detailed coastline geometry and stable strokes.");
        else
            Require(coastline.Data == null, caseName + ": world overview avoids building detailed coastal shapes.");
        var scale = (TextBlock)page.FindName("MapScaleText");
        var bar = (Border)page.FindName("MapScaleBar");
        Require(scale.Text.StartsWith("≈ ", StringComparison.Ordinal) && bar.Width > 0 && bar.Width <= 81,
            caseName + ": approximate distance scale is bounded and visible.");
        var mapLabels = ((Canvas)page.FindName("MapLabels")).Children.OfType<Border>().ToArray();
        Require(mapLabels.Length <= 42, caseName + ": geographic label budget remains bounded.");
        for (int i = 0; i < mapLabels.Length; i++)
        {
            var first = mapLabels[i].TransformToVisual(page.FindName("MapView") as UIElement).TransformBounds(new Windows.Foundation.Rect(0, 0, mapLabels[i].ActualWidth, mapLabels[i].ActualHeight));
            for (int j = i + 1; j < mapLabels.Length; j++)
            {
                var second = mapLabels[j].TransformToVisual(page.FindName("MapView") as UIElement).TransformBounds(new Windows.Foundation.Rect(0, 0, mapLabels[j].ActualWidth, mapLabels[j].ActualHeight));
                Require(first.Right <= second.Left || first.Left >= second.Right || first.Bottom <= second.Top || first.Top >= second.Bottom,
                    caseName + ": geographic labels do not overlap.");
            }
        }

        Windows.Foundation.Rect Bounds(FrameworkElement element) =>
            element.TransformToVisual(header).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

        string Layout(FrameworkElement element) =>
            $"r{Grid.GetRow(element)}c{Grid.GetColumn(element)}s{Grid.GetColumnSpan(element)}:{Bounds(element)}";
    }

    private static void CheckMap(NetMapPage page, NetMapHopRow row, string caseName)
    {
        var map = (FrameworkElement)page.FindName("MapView");
        var outline = (Microsoft.UI.Xaml.Shapes.Path)page.FindName("WorldOutline");
        var transform = (MatrixTransform)outline.RenderTransform;
        CheckMapStrokes(page, caseName);
        var labels = ((Canvas)page.FindName("MapPoints")).Children.OfType<Border>().Where(item => (string)item.Tag == "HopDetails").ToArray();
        Require(labels.Length == 1, caseName + ": one selected-hop details card exists.");
        var label = labels[0];
        bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        Require(label.Background is AcrylicBrush acrylic && !acrylic.AlwaysUseFallback &&
            acrylic.TintLuminosityOpacity is double luminosity && luminosity > 0 && luminosity < 1 && acrylic.FallbackColor.A == 255 ||
            highContrast && label.Background is SolidColorBrush solid && solid.Color.A == 255,
            caseName + ": selected-hop details use translucent acrylic with a solid accessibility fallback.");
        if (label.Background is AcrylicBrush material)
            Log(caseName + $": details material tint={material.TintColor}, tintOpacity={material.TintOpacity}, luminosity={material.TintLuminosityOpacity}, fallback={material.FallbackColor}; native capture is required to judge composition blur.");
        var scroll = Descendants(label).OfType<ScrollViewer>().First();
        var fields = DetailFields(label);
        foreach (var expected in new[] { ("MapHeading", row.MapHeading), ("MapLocation", row.MapLocation), ("MapAsn", row.MapAsn), ("MapMetrics", row.MapMetrics) })
        {
            var text = fields[expected.Item1];
            Require(text.Text == expected.Item2 && text.TextWrapping == TextWrapping.Wrap && text.TextTrimming == TextTrimming.None,
                caseName + ": full wrapping " + expected.Item1 + " = " + text.Text);
            bool fullyOpaque = true;
            for (DependencyObject current = text; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is UIElement element) fullyOpaque &= Math.Abs(element.Opacity - 1) < .001;
                if (ReferenceEquals(current, label)) break;
            }
            Require(fullyOpaque, caseName + ": " + expected.Item1 + " keeps full foreground opacity independently of its material.");
        }
        Require(row.MapAsn.StartsWith("AS", StringComparison.Ordinal) && row.MapLocation.Length > 0, caseName + ": ASN and location are not omitted.");
        Require(Canvas.GetLeft(label) >= 7 && Canvas.GetLeft(label) + label.ActualWidth <= map.ActualWidth - 7 &&
            Canvas.GetTop(label) >= 47 && Canvas.GetTop(label) + label.ActualHeight <= map.ActualHeight - 39,
            caseName + ": details remain inside the map and clear of zoom controls.");
        Log(caseName + $": viewport={map.ActualWidth:0.#}x{map.ActualHeight:0.#}, scale={transform.Matrix.M11:0.###}, label={label.ActualWidth:0.#}x{label.ActualHeight:0.#}, scroll={scroll.ScrollableHeight:0.#}");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static async Task Settle()
    {
        await Task.Delay(140);
        for (int index = 0; index < 2; index++)
        {
            var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<object> handler = (_, _) => frame.TrySetResult();
            CompositionTarget.Rendering += handler;
            try { await frame.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            finally { CompositionTarget.Rendering -= handler; }
        }
    }

    private static async Task Capture(Page page, string name, FrameworkElement cropElement = null)
    {
        page.Background = new SolidColorBrush(page.ActualTheme == ElementTheme.Dark ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32) : Microsoft.UI.ColorHelper.FromArgb(255, 243, 243, 243));
        var bitmap = new RenderTargetBitmap();
        // Render the bounded page first. Rendering MapView directly includes the three
        // offscreen world copies in its visual bounds and omits its ancestor sea background.
        await bitmap.RenderAsync(page);
        var pixels = await bitmap.GetPixelsAsync();
        using var encoded = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
        var bytes = new byte[pixels.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        if (cropElement != null)
        {
            var rect = cropElement.TransformToVisual(page).TransformBounds(new Windows.Foundation.Rect(0, 0, cropElement.ActualWidth, cropElement.ActualHeight));
            Require(rect.Left >= -.5 && rect.Top >= -.5 && rect.Right <= page.ActualWidth + .5 && rect.Bottom <= page.ActualHeight + .5,
                name + ": full map viewport is visible in the page screenshot.");
            double scale = page.XamlRoot.RasterizationScale;
            Log($"CAPTURE BOUNDS page={page.ActualWidth:0.###}x{page.ActualHeight:0.###}, pixels={bitmap.PixelWidth}x{bitmap.PixelHeight}, rasterScale={scale:0.###}, margin={page.Margin}, map={rect}");
            if (Math.Abs(bitmap.PixelWidth - page.ActualWidth * scale) > 1 || Math.Abs(bitmap.PixelHeight - page.ActualHeight * scale) > 1)
            {
                // RTB may intersect ancestor clipping/visual bounds. A width discrepancy
                // is not a scale factor and does not establish which edge was clipped.
                // Keep the real page image instead of guessing the source pixel origin.
                name = Path.GetFileNameWithoutExtension(name) + "-context-page.png";
                Log("CROP SKIPPED: source render bounds differ from the page layout bounds; preserved full page context in " + name);
            }
            else
            {
                int left = Math.Clamp((int)Math.Floor(rect.Left * scale), 0, bitmap.PixelWidth - 1);
                int top = Math.Clamp((int)Math.Floor(rect.Top * scale), 0, bitmap.PixelHeight - 1);
                int right = Math.Clamp((int)Math.Ceiling(rect.Right * scale), left + 1, bitmap.PixelWidth);
                int bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom * scale), top + 1, bitmap.PixelHeight);
                width = right - left;
                height = bottom - top;
                Require(Math.Abs(width - cropElement.ActualWidth * scale) <= 2 && Math.Abs(height - cropElement.ActualHeight * scale) <= 2,
                    name + ": crop preserves the full viewport pixel dimensions.");
                var cropped = new byte[checked(width * height * 4)];
                for (int row = 0; row < height; row++)
                    Buffer.BlockCopy(bytes, ((top + row) * bitmap.PixelWidth + left) * 4, cropped, row * width * 4, width * 4);
                bytes = cropped;
                Require(bytes.Where((_, index) => index % 4 == 3).All(alpha => alpha == 255), name + ": map crop includes the opaque sea/background layer.");
            }
        }
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, bytes);
        await encoder.FlushAsync();
        var png = new byte[encoded.Size];
        using (var reader = new Windows.Storage.Streams.DataReader(encoded.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)encoded.Size);
            reader.ReadBytes(png);
        }
        File.WriteAllBytes(Path.Combine(Output, name), png);
        Log("SCREENSHOT " + name + $" ({width}x{height})");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Log(message);
    }

    internal static void Log(string message) => File.AppendAllText(Path.Combine(Output, "visual-smoke.log"), message + Environment.NewLine);
}

// Interface proxies are confined to this harness process. They remap the product
// SettingsUtils seam before repository creation, including FileSystemWatcher paths.
public class FixturePathProxy : DispatchProxy
{
    private object target;
    private string fixture;
    private string actualRoot;

    public static T Wrap<T>(T target, string fixture) where T : class
    {
        T result = Create<T, FixturePathProxy>();
        var proxy = (FixturePathProxy)(object)result;
        proxy.target = target;
        proxy.fixture = fixture;
        proxy.actualRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return result;
    }

    protected override object Invoke(MethodInfo method, object[] arguments)
    {
        object Map(object value) => value switch
        {
            string text when string.Equals(text, actualRoot, StringComparison.OrdinalIgnoreCase) => fixture,
            string text when text.StartsWith(actualRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) => fixture + text[actualRoot.Length..],
            string[] paths => paths.Select(path => (string)Map(path)).ToArray(),
            _ => value,
        };
        object[] mapped = arguments?.Select(Map).ToArray();
        if (target is IFile or IDirectory && mapped is { Length: > 0 } && mapped[0] is string path && Path.IsPathRooted(path))
        {
            string full = Path.GetFullPath(path);
            if (!string.Equals(full, fixture, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Settings I/O escaped the fixture: " + method.Name);
        }
        try { return method.Invoke(target, mapped); }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
