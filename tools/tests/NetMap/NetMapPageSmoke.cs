using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Kit.Settings.UI.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NetMapLib;
using Windows.Graphics.Imaging;
using Windows.Storage;

internal static class NetMapPageSmoke
{
    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();

    private static readonly string Output = Environment.GetEnvironmentVariable("KIT_NETMAP_TEST_OUTPUT");
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main()
    {
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        WindowsAppRuntime_EnsureIsLoaded();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        File.WriteAllText(Path.Combine(Output, "page-smoke.log"), "NetMap real WinUI; synthetic egress; no external network probes.\n");
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            var app = new App();
            DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
            {
                try
                {
                    await Task.Delay(1200);
                    await Run();
                    File.AppendAllText(Path.Combine(Output, "page-smoke.log"), "PASS\n");
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    File.AppendAllText(Path.Combine(Output, "page-smoke.log"), ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
                    Environment.Exit(1);
                }
            });
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        File.AppendAllText(Path.Combine(Output, "page-smoke.log"), message + "\n");
    }

    private static async Task Run()
    {
        var repository = SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default);
        repository.StopWatching();
        repository.SettingsConfig = new GeneralSettings();
        ShellPage.SetDefaultSndMessageCallback(_ => { });
        NavigationService.Navigate(typeof(NetMapPage));
        await Task.Delay(300);
        var page = (NetMapPage)NavigationService.Frame.Content;
        var vm = page.ViewModel;
        var window = App.GetSettingsWindow();
        bool chinese = System.Globalization.CultureInfo.CurrentUICulture.Name == "zh-CN";
        var start = (Button)page.FindName("StartButton");
        var stop = (Button)page.FindName("StopButton");
        Require((string)start.Content == (chinese ? "开始" : "Start") && (string)stop.Content == (chinese ? "停止" : "Stop"), "Start/Stop follow Kit language: " + System.Globalization.CultureInfo.CurrentUICulture.Name);
        Require(vm.Direct.Title == (chinese ? "直连" : "Direct") && vm.Proxy.Title == (chinese ? "代理" : "Proxy"), "Dynamic egress labels follow Kit language.");
        var enabledSwitch = (ToggleSwitch)page.FindName("EnableSwitch");
        Require((string)enabledSwitch.OnContent == (chinese ? "启用" : "On"), "Module switch text follows Kit language rather than Windows language.");
        var updateAsn = (Button)page.FindName("UpdateAsnButton");
        Require((string)updateAsn.Content == (chinese ? "下载 / 更新 ASN 库" : "Download / update ASN"), "ASN download action follows Kit language.");
        Require(page.FindName("UpdateMapButton") == null, "Map remains embedded without a download action.");
        Require(!vm.IsEnabled && !vm.IsDetectionOn, "New module and detection default off.");
        var world = (Microsoft.UI.Xaml.Shapes.Path)page.FindName("WorldOutline");
        Require(world.Data is PathGeometry geometry && geometry.Figures.Count > 170, "Offline map loads while module is disabled.");

        // Replace only the test instance's transport before enabling detection.
        var session = (NetMapSession)typeof(NetMapViewModel).GetField("session", PrivateInstance).GetValue(vm);
        typeof(NetMapSession).GetField("observe", PrivateInstance).SetValue(session,
            new Func<bool, NetMapOptions, CancellationToken, Task<IdentityResult>>(async (_, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("Cancelled synthetic transport should not return.");
            }));
        vm.IsEnabled = true;
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(start)).Invoke();
        await Task.Delay(100);
        Require(vm.IsDetectionOn && !start.IsEnabled && stop.IsEnabled, "Start begins detection and updates button states.");
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(stop)).Invoke();
        await Task.Delay(100);
        Require(!vm.IsDetectionOn && start.IsEnabled && !stop.IsEnabled, "Stop cancels detection and restores Start.");
        vm.IsDetectionOn = true;

        ((OverlappedPresenter)window.AppWindow.Presenter).Minimize();
        await Task.Delay(250);
        Require(!vm.IsDetectionOn, "Minimizing stops detection.");
        ((OverlappedPresenter)window.AppWindow.Presenter).Restore();
        await Task.Delay(200);
        Require(!vm.IsDetectionOn, "Restoring does not restart detection.");
        vm.IsDetectionOn = true;
        window.AppWindow.Hide();
        await Task.Delay(250);
        Require(!vm.IsDetectionOn, "Hiding stops detection.");
        window.AppWindow.Show();
        await Task.Delay(200);

        vm.IsDetectionOn = true;
        repository.SettingsConfig.Enabled.NetMap = false;
        repository.NotifySettingsChanged();
        await Task.Delay(200);
        Require(!vm.IsDetectionOn, "Runner enabled-state reply stops detection.");
        vm.IsEnabled = true;
        vm.IsDetectionOn = true;
        NavigationService.Navigate(typeof(DashboardPage));
        await Task.Delay(200);
        Require(!vm.IsDetectionOn, "Navigating away stops detection.");
        NavigationService.Navigate(typeof(NetMapPage));
        await Task.Delay(200);
        for (int attempt = 0; attempt < 40 && !(bool)typeof(NetMapViewModel).GetField("pageActive", PrivateInstance).GetValue(vm); attempt++) await Task.Delay(50);
        Require(ReferenceEquals(page, NavigationService.Frame.Content) && (bool)typeof(NetMapViewModel).GetField("pageActive", PrivateInstance).GetValue(vm), "Cached page becomes active after the navigation transition.");
        Require(!vm.IsDetectionOn, "Cached page does not restart detection.");

        // Hold session teardown so the real update command can be cancelled before any HTTP request.
        var blockedCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(NetMapSession).GetField("completion", PrivateInstance).SetValue(session, blockedCompletion.Task);
        var cancelledUpdate = vm.UpdateAsnAsync();
        await Task.Delay(50);
        Require(vm.IsUpdating && !vm.CanStart && !updateAsn.IsEnabled, "ASN update blocks detection and duplicate downloads.");
        vm.CancelDataUpdate();
        await cancelledUpdate;
        Require(!vm.IsUpdating && vm.CanStart && vm.UpdateStatus == NetMapViewModel.Text("UpdateCancelled"), "Cancelling ASN update restores controls.");
        var hiddenUpdate = vm.UpdateAsnAsync();
        vm.SetPageActive(false);
        await hiddenUpdate;
        Require(!vm.IsUpdating && vm.UpdateStatus == NetMapViewModel.Text("UpdateCancelled"), "Page deactivation cancels ASN update.");
        blockedCompletion.SetResult();
        vm.SetPageActive(true);

        // Render realistic, explicitly synthetic state without invoking a probe.
        var now = DateTimeOffset.Now;
        var direct = new IdentityResult(new("192.0.2.16", "CN", "中国大陆", Region: "江苏省", City: "南京市"), ProbeError.None, now, "Direct");
        var proxy = new IdentityResult(new("198.51.100.27", "JP", "日本 · JP"), ProbeError.None, now, "SystemProxy");
        var directGeo = new GeoInfo(64496, "Example network", chinese ? "南京" : "Nanjing", "CN", 32.06, 118.78, LookupStatus: "Ready", Source: "MMDB", Region: chinese ? "江苏省" : "Jiangsu");
        var proxyGeo = new GeoInfo(64497, "Example proxy", chinese ? "东京" : "Tokyo", "JP", 35.7, 139.7, LookupStatus: "Ready", Source: "ipwho.is");
        var hops = new[]
        {
            new HopResult(1, "10.0.0.1", 20, 20, 1, 1, false, new GeoInfo(LookupStatus: "Private")),
            new HopResult(2, "192.0.2.20", 20, 19, null, 8, false, directGeo, ConsecutiveFailures: 1, LastKnownMs: 8),
            new HopResult(3, "192.0.2.30", 20, 18, 90.1, 29, false, new GeoInfo(64496, "Example transit", "", "", 38, 125, LookupStatus: "Ready", Source: "ipwho.is")),
            new HopResult(4, "198.51.100.27", 20, 19, 45, 44, true, proxyGeo),
        };
        var fixture = new NetMapSnapshot(true, 1, new(direct, direct, directGeo), new(proxy, proxy, proxyGeo),
            new[] { new ServiceResult("Claude", true, ProbeError.None, 200, now, "TraceResponded", 45, Checkpoint: new("198.51.100.66", "SG", "SG")), new ServiceResult("ChatGPT", true, ProbeError.None, 200, now, "TraceResponded", 180, Checkpoint: new("198.51.100.88", "JP", "JP")), new ServiceResult("Gemini", true, ProbeError.None, 403, now, "BrowserCheck", 30), new ServiceResult("Google", true, ProbeError.Timeout, null, now, ConsecutiveFailures: 3) }, hops);
        typeof(NetMapSession).GetField("current", PrivateInstance).SetValue(session, fixture);
        typeof(NetMapViewModel).GetMethod("ApplySnapshot", PrivateInstance).Invoke(vm, new object[] { fixture });
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 900));
        ((FrameworkElement)window.Content).RequestedTheme = ElementTheme.Light;
        await Task.Delay(250);
        Require(vm.Direct.Ip == "192.0.2.16" && vm.Proxy.Ip == "198.51.100.27", "Synthetic dual-egress results remain visible.");
        Require(vm.Direct.Asn.Contains("AS64496") && vm.Proxy.Asn.Contains("AS64497"), "Local and online ASN results are displayed.");
        Require(vm.Direct.Location.StartsWith(chinese ? "中国大陆" : "Mainland China") && vm.Proxy.Location.StartsWith(chinese ? "日本" : "Japan"), "Country names follow Kit language independently of Windows language.");
        Require(vm.Direct.Location.Contains(chinese ? "江苏省" : "Jiangsu") && vm.Direct.Location.Contains(chinese ? "南京市" : "Nanjing"), "Egress card preserves province and city.");
        Require(vm.Hops[1].Location.Contains(chinese ? "江苏省" : "Jiangsu") && vm.Hops[1].Location.Contains(chinese ? "南京" : "Nanjing"), "MTR location shows the region and city associated with each IP.");
        Require(vm.Hops[0].Location == NetMapViewModel.Text("LocPrivate"), "Private routers are not assigned a public city.");
        Require(!NetMapViewModel.LocationText(direct.Identity, new GeoInfo(City: "Seattle", CountryCode: "US")).Contains("Seattle"), "Conflicting country data cannot relabel a mainland egress abroad.");
        Require(vm.Hops.Count == 4 && vm.Hops[2].Loss == "10.0%" && vm.Hops[2].Last == "90.1" && vm.Hops[2].Rtt == "29.0", "MTR exposes per-hop loss, recent RTT and average RTT.");
        var mapLines = ((Canvas)page.FindName("MapLines")).Children.OfType<Microsoft.UI.Xaml.Shapes.Polyline>().ToArray();
        Require(mapLines.Count(line => (string)line.Tag == "Observed") == 2 && mapLines.Any(line => (string)line.Tag == "Approximate"), "Route includes observed links and a dashed gap from the local egress.");
        Require(vm.TargetLatency.Contains("45.0") && vm.TargetLatency.Contains("44.0"), "Target latency remains visible without hover.");
        Require(vm.Services[1].Proxy.Contains(NetMapViewModel.Text("TraceResponded")) && vm.Services[1].Proxy.Contains("198.51.100.88"), "ChatGPT displays the validated checkpoint and its own egress IP.");
        Require(vm.Services[0].Proxy.Contains(NetMapViewModel.Text("TraceResponded")) && vm.Services[0].Proxy.Contains("198.51.100.66") && vm.Services[0].Endpoint == "https://api.anthropic.com/cdn-cgi/trace", "Claude uses its own trace checkpoint and egress identity.");
        Require(typeof(NetMapServiceRow).GetProperty("Direct") == null && vm.Services[2].Endpoint == "https://gemini.google.com/", "Service rows contain only Proxy results and retain the Gemini website.");
        Require(vm.Services.Count == 4 && vm.Services[3].Endpoint == "https://www.google.com/", "Google is included as the fourth Proxy website check.");
        Require(vm.Services[0].Health == ProbeHealth.Good && vm.Services[1].Health == ProbeHealth.Warning && vm.Services[2].Health == ProbeHealth.Warning && vm.Services[3].Health == ProbeHealth.Error && vm.Services[3].Latency == "N/A", "Service lights distinguish fast, slow, challenge and failed results.");
        Require(vm.Direct.Health == ProbeHealth.Good && vm.Proxy.Health == ProbeHealth.Good && vm.TargetHealth == ProbeHealth.Good, "Successful egress IPs and low target RTT use green.");
        var statusStyles = (ResourceDictionary)page.Resources["NetMapHealthStyles"];
        Require(ReferenceEquals(((Microsoft.UI.Xaml.Shapes.Ellipse)page.FindName("TargetStatusLight")).Style, statusStyles["GoodDot"]), "The target indicator is bound to the measured status.");
        foreach (double milliseconds in new[] { 75.0, 75.1 })
        {
            var boundary = fixture with { Hops = hops.Select(hop => hop.Reached ? hop with { LastMs = milliseconds } : hop).ToArray() };
            typeof(NetMapSession).GetField("current", PrivateInstance).SetValue(session, boundary);
            typeof(NetMapViewModel).GetMethod("ApplySnapshot", PrivateInstance).Invoke(vm, new object[] { boundary });
            Require(vm.TargetHealth == (milliseconds == 75 ? ProbeHealth.Good : ProbeHealth.Warning), "Target RTT applies the inclusive 75 ms boundary: " + milliseconds);
        }

        var timeout = new IdentityResult(null, ProbeError.Timeout, now, "Explicit");
        var unavailable = fixture with { Direct = new(timeout, null, null, 3), Proxy = new(timeout, null, null, 3), Services = Array.Empty<ServiceResult>(), Hops = Array.Empty<HopResult>() };
        typeof(NetMapSession).GetField("current", PrivateInstance).SetValue(session, unavailable);
        typeof(NetMapViewModel).GetMethod("ApplySnapshot", PrivateInstance).Invoke(vm, new object[] { unavailable });
        await Task.Delay(100);
        Require(vm.Direct.Ip == "N/A" && vm.Proxy.Ip == "N/A" && vm.Direct.Asn == "N/A" && vm.TargetLatency.Contains("N/A") && vm.Services.All(row => row.Latency == "N/A"), "Three failures replace unavailable identities and live values with N/A.");
        Require(vm.Direct.Health == ProbeHealth.Error && vm.Proxy.Health == ProbeHealth.Error && vm.TargetHealth == ProbeHealth.Error, "Failed identities and target RTT use red.");
        await Capture(page, "netmap-winui-status-failures.png");
        typeof(NetMapSession).GetField("current", PrivateInstance).SetValue(session, fixture);
        typeof(NetMapViewModel).GetMethod("ApplySnapshot", PrivateInstance).Invoke(vm, new object[] { fixture });
        await Task.Delay(100);
        Require(vm.Direct.Health == ProbeHealth.Good && vm.Direct.Ip == "192.0.2.16", "A successful observation restores the IP and green state.");
        var detectionCard = (FrameworkElement)page.FindName("DetectionCard");
        var directCard = (FrameworkElement)page.FindName("DirectCard");
        var mapCard = (FrameworkElement)page.FindName("MapCard");
        Require(detectionCard.ActualHeight <= 64, "Detection toolbar stays within 64 DIPs: " + detectionCard.ActualHeight);
        Require(directCard.ActualHeight <= 200, "Egress card stays within 200 DIPs: " + directCard.ActualHeight);
        var mapView = (FrameworkElement)page.FindName("MapView");
        var routePanel = (FrameworkElement)page.FindName("RoutePanel");
        Require(mapView.ActualWidth >= 450 && mapView.ActualWidth > routePanel.ActualWidth, "The map receives more horizontal space than the compact MTR table: " + mapView.ActualWidth);
        var mapBottom = mapCard.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point(0, mapCard.ActualHeight)).Y;
        Require(mapBottom <= page.ActualHeight, "Full map fits the 1200 x 900 window: bottom=" + mapBottom + ", viewport=" + page.ActualHeight);
        await Capture(page, "netmap-winui-light.png");
        ((FrameworkElement)window.Content).RequestedTheme = ElementTheme.Dark;
        await Task.Delay(200);
        await Capture(page, "netmap-winui-dark.png");
        ((FrameworkElement)page.FindName("ServicesCard")).StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        await Task.Delay(200);
        await Capture(page, "netmap-winui-services.png");
        ((ScrollViewer)page.Content).ChangeView(null, 0, null, true);
        await Task.Delay(100);

        // China -> US west/central -> Singapore must stay connected across the Pacific.
        var singapore = new IdentityResult(new("198.51.100.90", "SG", "Singapore"), ProbeError.None, now, "SystemProxy");
        var singaporeGeo = new GeoInfo(64497, "Example proxy", "Singapore", "SG", 1.35, 103.82, LookupStatus: "Ready", Source: "MMDB");
        var pacificHops = new[]
        {
            new HopResult(1, "192.168.50.1", 12, 12, 1, 2, false, new GeoInfo(LookupStatus: "Private")),
            new HopResult(2, "192.0.2.22", 12, 11, 40, 38, false, directGeo),
            new HopResult(3, "192.0.2.33", 12, 10, 155, 148, false, new GeoInfo(City: "Los Angeles", CountryCode: "US", Latitude: 34.05, Longitude: -118.24)),
            new HopResult(4, "192.0.2.44", 12, 11, 180, 172, false, new GeoInfo(City: "Dallas", CountryCode: "US", Latitude: 32.78, Longitude: -96.80)),
            new HopResult(5, "198.51.100.90", 12, 10, 255.2, 240.6, true, singaporeGeo),
        };
        var pacific = fixture with { Proxy = new(singapore, singapore, singaporeGeo), Hops = pacificHops };
        typeof(NetMapSession).GetField("current", PrivateInstance).SetValue(session, pacific);
        typeof(NetMapViewModel).GetMethod("ApplySnapshot", PrivateInstance).Invoke(vm, new object[] { pacific });
        await Task.Delay(150);
        var pacificLines = ((Canvas)page.FindName("MapLines")).Children.OfType<Microsoft.UI.Xaml.Shapes.Polyline>().ToArray();
        var observedPacificLines = pacificLines.Where(line => (string)line.Tag == "Observed").ToArray();
        Require(observedPacificLines.Length == 3, "China-US-Singapore uses one continuous segment per located hop, without date-line splitting.");
        Require(pacificLines.SelectMany(line => line.Points).All(point => point.X >= 24 && point.X <= 936 && point.Y >= 24 && point.Y <= 456), "All Pacific route endpoints remain inside the visible map with marker padding.");
        Require(observedPacificLines.Zip(observedPacificLines.Skip(1), (first, next) => first.Points[1] == next.Points[0]).All(connected => connected), "Successive Pacific route segments share the exact same endpoint.");
        double chinaX = observedPacificLines[0].Points[0].X;
        double usX = observedPacificLines[0].Points[1].X;
        Require(chinaX < usX && usX - chinaX < 800, "The Pacific-centered map places China west of the US without crossing the canvas edges.");
        await Capture(page, "netmap-winui-pacific-dark.png");
        ((FrameworkElement)window.Content).RequestedTheme = ElementTheme.Light;
        await Task.Delay(150);
        await Capture(page, "netmap-winui-pacific-light.png");

        // Close points on either side of +/-180 must remain close after projection.
        var dateline = pacific with
        {
            Direct = new(),
            Proxy = new(),
            Hops = new[]
            {
                new HopResult(1, "192.0.2.1", 1, 1, 20, 20, false, new GeoInfo(Latitude: 10, Longitude: 179)),
                new HopResult(2, "192.0.2.2", 1, 1, 25, 25, false, new GeoInfo(Latitude: 11, Longitude: -179)),
            },
        };
        typeof(NetMapSession).GetField("current", PrivateInstance).SetValue(session, dateline);
        typeof(NetMapViewModel).GetMethod("ApplySnapshot", PrivateInstance).Invoke(vm, new object[] { dateline });
        var datelineLines = ((Canvas)page.FindName("MapLines")).Children.OfType<Microsoft.UI.Xaml.Shapes.Polyline>().ToArray();
        Require(datelineLines.Length == 1 && Math.Abs(datelineLines[0].Points[1].X - datelineLines[0].Points[0].X) < 30, "Nearby +179/-179 degree hops stay adjacent in one visible segment.");
        typeof(NetMapSession).GetField("current", PrivateInstance).SetValue(session, fixture);
        typeof(NetMapViewModel).GetMethod("ApplySnapshot", PrivateInstance).Invoke(vm, new object[] { fixture });
        ((FrameworkElement)window.Content).RequestedTheme = ElementTheme.Dark;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(720, 1000));
        await Task.Delay(250);
        var proxyCard = (ContentControl)page.FindName("ProxyCard");
        Require(Grid.GetRow(proxyCard) == 1, "Narrow page stacks egress cards.");
        var egressGrid = (Grid)page.FindName("EgressGrid");
        Require(Math.Abs(directCard.ActualWidth - egressGrid.ActualWidth) < 1 && Math.Abs(proxyCard.ActualWidth - egressGrid.ActualWidth) < 1, "Stacked cards use the full available width.");
        Require(Grid.GetRow(routePanel) == 1, "Narrow windows place the MTR table below the full-width map.");
        await Capture(page, "netmap-winui-narrow.png");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 900));
        var hopList = (ListView)page.FindName("HopList");
        var longHops = Enumerable.Range(1, 24).Select(ttl => new HopResult(ttl, "192.0.2." + ttl, 20, 18, ttl * 2, ttl * 1.5, false)).ToArray();
        var longFixture = fixture with { Hops = longHops };
        typeof(NetMapSession).GetField("current", PrivateInstance).SetValue(session, longFixture);
        typeof(NetMapViewModel).GetMethod("ApplySnapshot", PrivateInstance).Invoke(vm, new object[] { longFixture });
        await Task.Delay(200);
        hopList.SelectedIndex = 18;
        hopList.ScrollIntoView(vm.Hops[18]);
        await Task.Delay(200);
        Require(((FrameworkElement)hopList.ContainerFromIndex(18)).ActualHeight <= 36, "Compact MTR rows retain room for two location lines without the old vertical spacing.");
        var innerScroll = FindChild<ScrollViewer>(hopList);
        Require(innerScroll != null && innerScroll.VerticalOffset > 0, "MTR list can scroll beyond the first five rows.");
        double innerOffset = innerScroll.VerticalOffset;
        var stableSource = hopList.ItemsSource;
        var stableRow = vm.Hops[18];
        var stableLine = ((Canvas)page.FindName("MapLines")).Children.FirstOrDefault();
        for (int sample = 0; sample < 12; sample++)
        {
            var update = longFixture with { Hops = longHops.Select(hop => hop with { Sent = 21 + sample, LastMs = hop.Ttl + sample }).ToArray() };
            typeof(NetMapSession).GetField("current", PrivateInstance).SetValue(session, update);
            typeof(NetMapViewModel).GetMethod("ApplySnapshot", PrivateInstance).Invoke(vm, new object[] { update });
            await Task.Delay(15);
        }

        Require(ReferenceEquals(stableSource, hopList.ItemsSource) && ReferenceEquals(stableRow, vm.Hops[18]), "Sampling preserves the ItemsSource and existing row objects.");
        Require(hopList.SelectedIndex == 18 && Math.Abs(innerScroll.VerticalOffset - innerOffset) < 1, "Sampling preserves selection and the lower scroll position.");
        Require(ReferenceEquals(stableLine, ((Canvas)page.FindName("MapLines")).Children.FirstOrDefault()), "RTT-only updates do not rebuild the map.");
        var locatedFixture = vm.Snapshot with { Hops = vm.Snapshot.Hops.Select(hop => hop.Ttl == 19 ? hop with { Geography = directGeo } : hop).ToArray() };
        typeof(NetMapSession).GetField("current", PrivateInstance).SetValue(session, locatedFixture);
        typeof(NetMapViewModel).GetMethod("ApplySnapshot", PrivateInstance).Invoke(vm, new object[] { locatedFixture });
        await Task.Delay(100);
        Require(ReferenceEquals(stableRow, vm.Hops[18]) && vm.Hops[18].Location.Contains(chinese ? "南京" : "Nanjing") && Math.Abs(innerScroll.VerticalOffset - innerOffset) < 1, "Delayed location enrichment updates the existing row without moving the scroll position.");
        await Capture(page, "netmap-winui-scrolled-mtr.png");
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 900));
        ((Expander)page.FindName("SettingsExpander")).IsExpanded = true;
        ((Expander)page.FindName("DataUpdateExpander")).IsExpanded = true;
        await Task.Delay(250);
        ((ScrollViewer)page.Content).ChangeView(null, double.MaxValue, null, true);
        await Task.Delay(250);
        Require(updateAsn.ActualWidth > 0 && updateAsn.ActualWidth < page.ActualWidth, "ASN update action fits the expanded settings panel.");
        await Capture(page, "netmap-winui-asn-update.png");
        Require(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Settings", "Icons", "NetMap.png")), "NetMap navigation icon is deployed.");
        Require(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Settings", "NetMapLogo.png")), "NetMap colored logo is deployed.");
        vm.Dispose();
    }

    private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindChild<T>(child);
            if (nested != null) return nested;
        }

        return null;
    }

    private static async Task Capture(FrameworkElement element, string name)
    {
        // RenderTargetBitmap does not capture the window's Mica backdrop.
        var page = (Page)element;
        page.Background = new SolidColorBrush(element.ActualTheme == ElementTheme.Dark ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32) : Microsoft.UI.ColorHelper.FromArgb(255, 243, 243, 243));
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(Output);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var bytes = new byte[pixels.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
    }
}
