using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI;
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
using LocalServerHub.Windows;
using Windows.Graphics.Imaging;
using Windows.Storage;

internal static class DashboardPageSmoke
{
    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();
    private static readonly string Output = Environment.GetEnvironmentVariable("KIT_DASHBOARD_TEST_OUTPUT");
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main()
    {
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        WindowsAppRuntime_EnsureIsLoaded();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        File.WriteAllText(Path.Combine(Output, "page-smoke.log"), "Real WinUI; live read-only initial snapshot; synthetic display captures.\n");
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

    private static async Task Run()
    {
        var page = (DashboardPage)NavigationService.Frame.Content;
        var vm = page.ViewModel;
        var window = App.GetSettingsWindow();
        Require(window.SystemBackdrop is MicaBackdrop, "The shell uses the native Mica backdrop.");
        bool chinese = System.Globalization.CultureInfo.CurrentUICulture.Name == "zh-CN";
        Require(page.FindName("RefreshOverviewButton") == null, "Network egress refreshes automatically without a manual refresh button.");
        Require(page.FindName("SystemStatusText") == null && page.FindName("OverviewStatusText") == null, "Collection timestamps and refresh frequency are not displayed.");
        Require(page.FindName("DirectConnectionText") == null && page.FindName("ProxyConnectionText") == null, "Network cards omit connection details.");
        Require(page.FindName("RefreshSystemButton") == null, "System overview refreshes automatically without a manual refresh button.");
        await WaitUntil(() => vm.CanRefreshOverview && vm.CanRefreshSystem);
        Require(vm.SystemDetails.Count == 7 && vm.GraphicsCards.Count > 0 && vm.StorageDetails.Count > 0, "System, storage, and graphics information load independently.");
        Require(vm.SystemDetails.Any(item => item.HasUsage) && vm.StorageDetails.Any(item => item.HasUsage), "Live system and storage reads supply numeric usage values.");
        Require(vm.DirectEgress.Title == (chinese ? "直连" : "Direct") && vm.ProxyEgress.Title == (chinese ? "代理" : "Proxy"), "Egress follows Kit language.");
        Require(vm.DirectEgress.Status != NetMapViewModel.Text("Waiting") && vm.ProxyEgress.Status != NetMapViewModel.Text("Waiting"), "Both probes finish or report an explicit error.");

        var originalSystem = vm.SystemDetails;
        var originalProxy = vm.ProxyEgress;
        var originalStatus = vm.SystemStatus;
        await vm.RefreshSystemAsync();
        await WaitUntil(() => vm.CanRefreshSystem);
        Require(vm.SystemStatus == originalStatus && ReferenceEquals(vm.ProxyEgress, originalProxy), "Recent system samples are reused without querying network egress.");

        var context = SynchronizationContext.Current;
        Task refresh;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            refresh = vm.RefreshSystemAsync(force: true);
        }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
        Require(!vm.CanRefreshSystem && vm.CanRefreshOverview, "System refresh blocks duplicate work but leaves network refresh independent.");
        await vm.RefreshSystemAsync(force: true);
        vm.SetOverviewActive(false);
        await refresh;
        await Task.Delay(100);
        Require(!vm.CanRefreshSystem, "Cancelled completion cannot reactivate an inactive page.");
        NavigationService.Navigate(typeof(GeneralPage));
        await Task.Delay(250);
        Require(!Timer(vm).IsRunning, "Leaving Home stops its network timer.");
        NavigationService.Navigate(typeof(DashboardPage));
        await Task.Delay(500);
        page = (DashboardPage)NavigationService.Frame.Content;
        vm = page.ViewModel;
        await WaitUntil(() => vm.CanRefreshOverview && vm.CanRefreshSystem);
        Require(vm.SystemDetails.Count == 7, "Returning Home refreshes after the previous page unloaded.");
        if (Environment.GetEnvironmentVariable("KIT_DASHBOARD_LAYOUT_ONLY") != "1")
        {
            await VerifyPolling(vm, window);
            await VerifyCache();
        }
        else
        {
            File.AppendAllText(Path.Combine(Output, "page-smoke.log"), "Layout-only run: polling and cache checks skipped.\n");
        }
        VerifyGraphicsMatching();

        // Keep synthetic screenshots isolated from real probes, including resize events.
        typeof(DashboardPage).GetMethod("UnsubscribeWindow", PrivateInstance).Invoke(page, null);
        vm.Dispose();
        var now = DateTimeOffset.Now;
        var direct = new IdentityResult(new("192.0.2.16", "CN", "", Region: "江苏省", City: "南京市"), ProbeError.None, now, "Direct");
        var proxy = new IdentityResult(new("2001:db8:1234:5678:abcd:ef01:2345:6789", "JP", ""), ProbeError.None, now, "SystemBypass");
        Set(vm, "DirectEgress", new NetMapCard("Direct", new(direct, direct), true));
        Set(vm, "ProxyEgress", new NetMapCard("Proxy", new(proxy, proxy), true));
        var fixtureProxy = vm.ProxyEgress;
        Require(fixtureProxy.Route == NetMapViewModel.Text("Route_SystemBypass"), "Synthetic proxy bypass starts with the expected route label.");
        Set(vm, "OverviewStatus", chinese ? "采样于 09:41:00 · 每 10 秒自动刷新" : "Sampled at 09:41:00 · Auto-refresh every 10 seconds");
        Set(vm, "SystemStatus", chinese ? "用量 09:41:00 · 磁盘 09:40:30 · 显卡 09:41:00" : "Usage 09:41:00 · Storage 09:40:30 · Graphics 09:41:00");
        Set(vm, "SystemDetails", new DashboardDetail[]
        {
            new(chinese ? "设备" : "Device", "KIT-DESKTOP · X64"),
            new("Windows", "Windows 11 Enterprise LTSC 2024 24H2\n26100.0000"),
            new(chinese ? "处理器" : "Processor", "AMD Ryzen 9 9950X 16-Core Processor"),
            new(chinese ? "逻辑处理器" : "Logical CPUs", "32"),
            new(chinese ? "CPU 使用率" : "CPU load", "12%", 12),
            new(chinese ? "内存" : "Memory", "23.4 / 64.0 GB", 23.4 / 64 * 100),
            new(chinese ? "运行时间" : "Uptime", "2d 04:32:10"),
        });
        Set(vm, "StorageDetails", new DashboardDetail[]
        {
            new("C:\\", "320 / 953 GB", 320d / 953 * 100),
            new("D:\\", "640 / 1863 GB", 640d / 1863 * 100),
        });
        Set(vm, "GraphicsCards", new DashboardGpuCard[]
        {
            new("GPU 1 · AMD Radeon Graphics", new DashboardDetail[]
            {
                new(chinese ? "驱动" : "Driver", "32.0.0000.0"),
                new(chinese ? "显存" : "VRAM", "—"),
                new(chinese ? "使用率" : "Usage", "—"),
                new(chinese ? "温度" : "Temperature", "—"),
            }, chinese ? "此显卡暂未提供用量与温度监测。" : "Live usage and temperature monitoring is not available for this GPU."),
            new("GPU 2 · NVIDIA GeForce RTX 5080", new DashboardDetail[]
            {
                new(chinese ? "驱动" : "Driver", "32.0.0000.0"),
                new(chinese ? "显存" : "VRAM", "3.2 / 16.0 GB", 20),
                new(chinese ? "使用率" : "Usage", "12%", 12),
                new(chinese ? "温度" : "Temperature", "48 °C"),
            }, string.Empty),
        });
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            ((FrameworkElement)window.Content).RequestedTheme = theme;
            foreach (int width in new[] { 1280, 1008, 760, 480 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1100));
                await Task.Delay(250);
                page.UpdateLayout();
                Require(ReferenceEquals(vm.ProxyEgress, fixtureProxy) && !Timer(vm).IsRunning, "Display captures retain synthetic network data with polling stopped.");
                var systemBars = Descendants((DependencyObject)page.FindName("SystemDetailsList")).OfType<ProgressBar>().Where(bar => bar.Visibility == Visibility.Visible).ToArray();
                Require(systemBars.Length == 2 && systemBars[0].Value == 12 && Math.Abs(systemBars[1].Value - 23.4 / 64 * 100) < 0.01, "CPU and memory bars show numeric usage.");
                RequireAligned(systemBars, page, "CPU and memory", width);
                var storageBars = Descendants((DependencyObject)page.FindName("StorageDetailsList")).OfType<ProgressBar>().Where(bar => bar.Visibility == Visibility.Visible).ToArray();
                Require(storageBars.Length == 2 && Math.Abs(storageBars[0].Value - 320d / 953 * 100) < 0.01, "Each disk has its own capacity bar.");
                var gpuBars = Descendants((DependencyObject)page.FindName("GraphicsCardsList")).OfType<ProgressBar>().Where(bar => bar.Visibility == Visibility.Visible).ToArray();
                Require(gpuBars.Length == 2 && gpuBars[0].Value == 20 && gpuBars[1].Value == 12, "Only supported GPU metrics display usage bars; unavailable metrics stay empty.");
                RequireAligned(gpuBars, page, "VRAM and GPU usage", width);
                RequireAligned(systemBars.Concat(storageBars).Concat(gpuBars).ToArray(), page, "All system, disk, and GPU", width);
                var labels = Descendants((DependencyObject)page.FindName("SystemOverviewCard")).OfType<TextBlock>().Where(text => text.Name == "DetailLabel").ToArray();
                var labelX = labels[0].TransformToVisual(page).TransformPoint(new()).X;
                Require(labels.All(label => Math.Abs(label.TransformToVisual(page).TransformPoint(new()).X - labelX) < 1), "All system, disk, and GPU labels share a left edge at " + width);
                Require(Descendants((DependencyObject)page.FindName("SystemOverviewCard")).OfType<TextBlock>().Where(text => text.IsTextSelectionEnabled).All(text => text.FontSize == 13 && text.FontWeight.Weight == 600), "System overview values share one font size and weight.");
                var temperatureLabels = Descendants((DependencyObject)page.FindName("GraphicsCardsList")).OfType<TextBlock>().Where(text => text.Name == "DetailLabel" && text.Text == (chinese ? "温度" : "Temperature")).ToArray();
                Require(temperatureLabels.Length == 2 && temperatureLabels.All(text => text.ActualHeight <= 24), "Temperature labels stay on one line in each GPU group.");
                Require(Descendants((DependencyObject)page.FindName("SystemOverviewCard")).OfType<Grid>().Where(grid => grid.Name == "UsageRow" && grid.Visibility == Visibility.Visible).All(grid => grid.ActualHeight >= 16), "Usage rows leave room for readable values at " + width);
                var scroll = (ScrollViewer)page.FindName("MainScrollViewer");
                Require(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "No horizontal overflow at " + width);
                var ip = (TextBlock)page.FindName("ProxyIpText");
                Require(ip.ActualWidth > 80 && ip.TextWrapping == TextWrapping.Wrap, "IPv6 can wrap.");
                var directIp = (TextBlock)page.FindName("DirectIpText");
                var directLocation = (TextBlock)page.FindName("DirectLocationText");
                var proxyLocation = (TextBlock)page.FindName("ProxyLocationText");
                Require(width == 480
                    ? Grid.GetRow(ip) > Grid.GetRow(directIp) && Math.Abs(ip.TransformToVisual(page).TransformPoint(new()).X - directIp.TransformToVisual(page).TransformPoint(new()).X) < 1
                    : Math.Abs(directLocation.TransformToVisual(page).TransformPoint(new()).Y - proxyLocation.TransformToVisual(page).TransformPoint(new()).Y) < 1,
                    "Network comparison aligns rows or stacks at narrow width: " + width);
                var overview = (Border)page.FindName("SystemOverviewCard");
                Require(overview.Background is SolidColorBrush surface && surface.Color.A is > 0 and < 255, "Overview surfaces preserve Mica through translucent WinUI colors.");
                var surfaceColor = ((SolidColorBrush)overview.Background).Color;
                Require(theme == ElementTheme.Dark ? surfaceColor.A < 32 : surfaceColor.A > 64, "Overview surfaces resolve the current WinUI theme palette: " + theme);
                var cardBrushes = new[] { "QuickAccessCard", "UtilitiesCard" }
                    .Select(name => (SolidColorBrush)Descendants((DependencyObject)page.FindName(name)).OfType<Grid>().First(grid => grid.BorderThickness.Left == 1 && grid.Background is SolidColorBrush).Background)
                    .Append((SolidColorBrush)((Border)page.FindName("NetworkOverviewCard")).Background);
                Require(cardBrushes.All(brush => brush.Color == surfaceColor && brush.Opacity == overview.Background.Opacity), "All four Home cards share the same native surface color and opacity in " + theme);
                // Check the rendered theme-aware brush in both light and dark modes.
                var color = ((SolidColorBrush)ip.Foreground).Color;
                Require(color.G > color.R && color.G > color.B && ((SolidColorBrush)directIp.Foreground).Color == color, "Successful IPs are green in " + theme);
                var secondary = (StackPanel)page.FindName("SecondaryColumn");
                Require(ReferenceEquals(secondary.Children[0], page.FindName("UtilitiesCard")) && ReferenceEquals(secondary.Children[1], page.FindName("NetworkOverviewCard")), "Utilities precedes Network egress in the right column.");
                var systemCard = (FrameworkElement)page.FindName("SystemOverviewCard");
                var quickCard = (FrameworkElement)page.FindName("QuickAccessCard");
                var systemPosition = systemCard.TransformToVisual(page).TransformPoint(new());
                var quickPosition = quickCard.TransformToVisual(page).TransformPoint(new());
                Require(Math.Abs(systemPosition.X - quickPosition.X) < 1 && Math.Abs(systemCard.ActualWidth - quickCard.ActualWidth) < 1 && systemPosition.Y >= quickPosition.Y + quickCard.ActualHeight + 15, "Quick Access sits directly above System overview with matching edges at " + width);
                if (width == 1280)
                {
                    var systemList = (ItemsControl)page.FindName("SystemDetailsList");
                    Require(systemList.ActualWidth >= 400, "System details retain readable column width.");
                    var utilitiesPosition = ((FrameworkElement)page.FindName("UtilitiesCard")).TransformToVisual(page).TransformPoint(new());
                    Require(quickPosition.X < utilitiesPosition.X && Math.Abs(quickPosition.Y - utilitiesPosition.Y) < 1, "Quick Access starts on the left, aligned with Utilities on the right.");
                }
                await Capture(page, $"dashboard-{theme}-{width}.png");
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
                await Task.Delay(100);
                await Capture(page, $"dashboard-{theme}-{width}-details.png");
                scroll.ChangeView(null, 0, null, true);
            }
        }
        Require(vm.ProxyEgress.Route == NetMapViewModel.Text("Route_SystemBypass"), "Proxy bypass is explicitly labeled: " + vm.ProxyEgress.Route + " / " + NetMapViewModel.Text("Route_SystemBypass"));
        var failure = new IdentityResult(null, ProbeError.Timeout, now, "Explicit");
        Set(vm, "ProxyEgress", new NetMapCard("Proxy", new(failure), true));
        var failedIp = (TextBlock)page.FindName("ProxyIpText");
        Require(failedIp.Text == "N/A" && vm.ProxyEgress.Status == NetMapViewModel.Text("Error_Timeout"), "Failure immediately displays N/A instead of stale success.");
        Require(DashboardViewModel.EgressStatus(vm.ProxyEgress) == NetMapViewModel.Text("Error_Timeout"), "Network errors remain visible without a collection-status footer.");
        page.UpdateLayout();
        Require(((TextBlock)page.FindName("ProxyEgressStatusText")).Visibility == Visibility.Visible, "The failed route displays its error message.");
        Require(((SolidColorBrush)failedIp.Foreground).Color != ((SolidColorBrush)((TextBlock)page.FindName("DirectIpText")).Foreground).Color, "Failed IP is no longer green.");
        Set(vm, "ProxyEgress", new NetMapCard("Proxy", new(proxy, proxy), true));
        Require(failedIp.Text == proxy.Identity.Ip && ((SolidColorBrush)failedIp.Foreground).Color == ((SolidColorBrush)((TextBlock)page.FindName("DirectIpText")).Foreground).Color, "Recovery restores the IP and its green success color.");
        Require(DashboardViewModel.EgressStatus(vm.ProxyEgress) == string.Empty, "Successful network lookups do not display collection chatter.");
        page.UpdateLayout();
        Require(((TextBlock)page.FindName("ProxyEgressStatusText")).Visibility == Visibility.Collapsed, "Recovery removes the empty status row.");
        vm.Dispose();
        Require(new DashboardDetail("test", "0%", 0).HasUsage && new DashboardDetail("test", "100%", 150).UsageValue == 100 && !new DashboardDetail("test", "—", double.NaN).HasUsage, "Zero usage is available, out-of-range values are bounded, and invalid readings stay unavailable.");

        NavigationService.Navigate(typeof(GeneralPage));
        await Task.Delay(300);
        var general = (GeneralPage)NavigationService.Frame.Content;
        var root = window.Content as ShellPage ?? Descendants(window.Content).OfType<ShellPage>().Single();
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            root.RequestedTheme = theme;
            foreach (int width in new[] { 1280, 480 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1100));
                await Task.Delay(250);
                general.UpdateLayout();
                var scroll = Descendants(general).OfType<ScrollViewer>().First(item => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(item) == "PageScrollViewer");
                Require(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "Settings has no horizontal overflow at " + width);
                var navigation = Descendants(root).OfType<NavigationView>().Single();
                Require(width == 1280 ? navigation.DisplayMode == NavigationViewDisplayMode.Expanded : navigation.DisplayMode == NavigationViewDisplayMode.Minimal, "Navigation adapts to available width: " + width);
                var buttons = Descendants(general).OfType<Button>().Where(button => button.Tag is string name && name.EndsWith("Group", StringComparison.Ordinal)).ToArray();
                await Capture(root, $"settings-{theme}-{width}.png");
                var firstPosition = buttons[0].TransformToVisual(general).TransformPoint(new());
                Require(buttons.Length == 6 && buttons.All(button => button.ActualHeight >= 36 && button.ActualHeight <= 40 && button.ActualWidth < 180), "Settings section tags stay compact at " + width);
                Require(width == 1280
                    ? buttons.All(button => Math.Abs(button.TransformToVisual(general).TransformPoint(new()).Y - firstPosition.Y) < 1)
                    : buttons.Last().TransformToVisual(general).TransformPoint(new()).Y > firstPosition.Y,
                    "Settings section tags share one wide row and wrap in a narrow window.");
                var fills = buttons.Select(button => ((SolidColorBrush)button.Background).Color).ToArray();
                Require(fills.Distinct().Count() == 6 && fills.All(color => color.A > 0 && color.A < 100), "Settings section tags use six translucent colors in " + theme);
                Require(fills[0].R == (theme == ElementTheme.Dark ? 0xB7 : 0x76), "Section colors follow the current theme.");
                Require(buttons.All(button => Descendants(button).OfType<FontIcon>().Count() == 1 && button.IsTabStop), "Each settings section offers an icon and keyboard focus.");
                foreach (var button in buttons)
                {
                    var normal = ((SolidColorBrush)button.Background).Color;
                    var presenter = Descendants(button).OfType<ContentPresenter>().First(item => item.Name == "ContentPresenter");
                    VisualStateManager.GoToState(button, "PointerOver", false);
                    var hover = ((SolidColorBrush)presenter.Background).Color;
                    VisualStateManager.GoToState(button, "Pressed", false);
                    var pressed = ((SolidColorBrush)presenter.Background).Color;
                    Require(hover.R == normal.R && hover.G == normal.G && hover.B == normal.B && hover.A > normal.A && pressed.A > hover.A, "Section hover and pressed states retain their color.");
                    VisualStateManager.GoToState(button, "Normal", false);
                }
            }
        }

        var sectionLinks = Descendants(general).OfType<Button>().Where(link => link.Tag is string name && name.EndsWith("Group", StringComparison.Ordinal)).ToArray();
        string[] expectedLabels = chinese ? ["外观主题", "启动权限", "AI 服务", "诊断日志", "备份还原", "版本更新"] : ["Appearance", "Startup", "AI service", "Diagnostics", "Backup", "Updates"];
        Require(sectionLinks.Length == 6 && sectionLinks.Select(link => ((StackPanel)link.Content).Children.OfType<TextBlock>().Single().Text).SequenceEqual(expectedLabels), "Settings section tags use short localized labels.");
        Require(sectionLinks.All(link => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(link) == (string)general.FindName((string)link.Tag).GetType().GetProperty("Header").GetValue(general.FindName((string)link.Tag))), "Settings section tags retain full group names for accessibility.");
        var pageScroll = Descendants(general).OfType<ScrollViewer>().First(item => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(item) == "PageScrollViewer");
        foreach (var name in new[] { "AiServicesGroup", "VersionAndUpdateGroup", "AppearanceAndBehaviorGroup" })
        {
            var link = sectionLinks.Single(item => (string)item.Tag == name);
            typeof(GeneralPage).GetMethod("SettingsSection_Click", PrivateInstance).Invoke(general, new object[] { link, new RoutedEventArgs() });
            var section = (FrameworkElement)general.FindName(name);
            double top = 0;
            double previousOffset = double.NaN;
            int stableSamples = 0;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(100);
                top = section.TransformToVisual(pageScroll).TransformPoint(new()).Y;
                stableSamples = Math.Abs(pageScroll.VerticalOffset - previousOffset) < 0.1 ? stableSamples + 1 : 0;
                previousOffset = pageScroll.VerticalOffset;
                if (stableSamples >= 2) break;
            }
            Require(top >= -1 && top < pageScroll.ViewportHeight, $"Settings shortcut reveals its section heading: {name}; top={top}, offset={pageScroll.VerticalOffset}");
        }
    }

    private static void RequireAligned(ProgressBar[] bars, FrameworkElement page, string label, int width)
    {
        var first = bars[0].TransformToVisual(page).TransformPoint(new());
        Require(bars.All(bar => Math.Abs(first.X - bar.TransformToVisual(page).TransformPoint(new()).X) < 1 && Math.Abs(bars[0].ActualWidth - bar.ActualWidth) < 1 && bar.ActualWidth >= 24), label + " bars share both edges at " + width);
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static DispatcherQueueTimer Timer(DashboardViewModel vm) => (DispatcherQueueTimer)typeof(DashboardViewModel).GetField("overviewTimer", PrivateInstance).GetValue(vm);

    private static async Task VerifyPolling(DashboardViewModel vm, MainWindow window)
    {
        Require(Timer(vm).IsRunning && Timer(vm).Interval == TimeSpan.FromSeconds(10), "Home starts a 10-second network timer.");
        var uptimeTimer = (DispatcherQueueTimer)typeof(DashboardViewModel).GetField("uptimeTimer", PrivateInstance).GetValue(vm);
        var telemetryTimer = (DispatcherQueueTimer)typeof(DashboardViewModel).GetField("telemetryTimer", PrivateInstance).GetValue(vm);
        var storageTimer = (DispatcherQueueTimer)typeof(DashboardViewModel).GetField("storageTimer", PrivateInstance).GetValue(vm);
        Require(uptimeTimer.IsRunning && uptimeTimer.Interval == TimeSpan.FromSeconds(1) && telemetryTimer.IsRunning && telemetryTimer.Interval == TimeSpan.FromSeconds(1), "Uptime, CPU, memory, and GPU telemetry run on one-second timers.");
        Require(storageTimer.IsRunning && storageTimer.Interval == TimeSpan.FromMinutes(5), "Storage retains its five-minute interval.");
        telemetryTimer.Stop();
        await WaitUntil(() => typeof(DashboardViewModel).GetField("telemetryCancellation", PrivateInstance).GetValue(vm) == null);
        var fastRefresh = typeof(DashboardViewModel).GetMethod("RefreshFastTelemetryAsync", PrivateInstance);
        await (Task)fastRefresh.Invoke(vm, null);
        await WaitUntil(() => typeof(DashboardViewModel).GetField("telemetryCancellation", PrivateInstance).GetValue(vm) == null);
        var info = typeof(DashboardViewModel).Assembly.GetType("Kit.Settings.UI.Helpers.DashboardSystemInfo");
        var fastCaches = new[] { "CpuMemory", "GpuTelemetryFast" }.Select(name => info.GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).ToArray();
        var samples = fastCaches.Select(cache => cache.GetType().GetField("pending", PrivateInstance).GetValue(cache)).ToArray();
        await (Task)fastRefresh.Invoke(vm, null);
        await WaitUntil(() => typeof(DashboardViewModel).GetField("telemetryCancellation", PrivateInstance).GetValue(vm) == null);
        Require(fastCaches.Select((cache, index) => !ReferenceEquals(samples[index], cache.GetType().GetField("pending", PrivateInstance).GetValue(cache))).All(changed => changed), "Each telemetry refresh collects a new CPU/memory and GPU sample without waiting for the cache TTL.");
        telemetryTimer.Start();
        int starts = 0;
        bool busy = false;
        var times = new System.Collections.Generic.List<long>();
        void Changed(object sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName != "CanRefreshOverview") return;
            if (!vm.CanRefreshOverview && !busy)
            {
                starts++;
                times.Add(Environment.TickCount64);
            }
            busy = !vm.CanRefreshOverview;
        }
        vm.PropertyChanged += Changed;
        var original = vm.DirectEgress;
        var refresh = vm.RefreshOverviewAsync();
        Require(!vm.CanRefreshOverview && ReferenceEquals(original, vm.DirectEgress), "Refresh keeps the current result visible while probing.");
        await vm.RefreshOverviewAsync();
        Require(starts == 1, "Duplicate refresh does not start overlapping probes.");
        await refresh;
        await WaitUntil(() => vm.CanRefreshOverview);
        var system = vm.SystemDetails;
        await WaitUntil(() => starts >= 2);
        await WaitUntil(() => starts >= 3);
        Require(vm.SystemDetails.Count == 7, "Hardware telemetry remains active independently.");
        vm.PropertyChanged -= Changed;

        var presenter = (OverlappedPresenter)window.AppWindow.Presenter;
        presenter.Minimize();
        await WaitUntil(() => !Timer(vm).IsRunning);
        Require(!uptimeTimer.IsRunning && !telemetryTimer.IsRunning && !storageTimer.IsRunning, "Minimizing also pauses all system telemetry timers.");
        Require(!vm.CanRefreshOverview, "Minimizing pauses and cancels observations.");
        presenter.Restore();
        await WaitUntil(() => Timer(vm).IsRunning && vm.CanRefreshOverview && vm.CanRefreshSystem);
        window.AppWindow.Hide();
        await WaitUntil(() => !Timer(vm).IsRunning);
        original = vm.DirectEgress;
        await Task.Delay(10500);
        Require(ReferenceEquals(original, vm.DirectEgress) && !Timer(vm).IsRunning, "Hidden window stays idle past a polling interval.");
        window.AppWindow.Show();
        await WaitUntil(() => Timer(vm).IsRunning && vm.CanRefreshOverview && vm.CanRefreshSystem);
        Require(!ReferenceEquals(original, vm.DirectEgress), "Showing Home resumes observations.");
    }

    private static void Set(DashboardViewModel vm, string property, object value)
    {
        typeof(DashboardViewModel).GetProperty(property).SetValue(vm, value);
        typeof(DashboardViewModel).GetMethod("OnPropertyChanged", PrivateInstance, null, new[] { typeof(string) }, null).Invoke(vm, new object[] { property });
    }

    private static async Task VerifyCache()
    {
        var type = typeof(DashboardViewModel).Assembly.GetType("Kit.Settings.UI.Helpers.DashboardProbeCache`1").MakeGenericType(typeof(int));
        var cache = Activator.CreateInstance(type, new object[] { TimeSpan.FromMinutes(10) });
        var method = type.GetMethod("ReadAsync", PrivateInstance);
        int count = 0;
        var held = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task<int>> probe = () => { Interlocked.Increment(ref count); return held.Task; };
        Task Read(Func<Task<int>> read, bool force = false, CancellationToken token = default) => (Task)method.Invoke(cache, new object[] { read, force, token });
        using var cancellation = new CancellationTokenSource();
        var cancelled = Read(probe, token: cancellation.Token);
        var shared = Read(probe, force: true);
        cancellation.Cancel();
        try { await cancelled; throw new InvalidOperationException("Cancelled waiter completed."); }
        catch (OperationCanceledException) { }
        held.SetResult(42);
        await shared;
        await Read(probe);
        Require(count == 1, "Concurrent, cancelled, and recent reads share one probe even across forced callers.");
        await Read(() => { Interlocked.Increment(ref count); return Task.FromResult(43); }, force: true);
        Require(count == 2, "Manual refresh replaces a completed cached sample.");

        cache = Activator.CreateInstance(type, new object[] { TimeSpan.FromMilliseconds(1) });
        count = 0;
        await Read(() => Task.FromResult(++count));
        await Task.Delay(20);
        await Read(() => Task.FromResult(++count));
        Require(count == 2, "Expired samples are refreshed.");

        held = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        cache = Activator.CreateInstance(type, new object[] { TimeSpan.FromMinutes(1) });
        count = 0;
        try { await Read(probe); throw new InvalidOperationException("Hung native read completed."); }
        catch (TimeoutException) { }
        var retry = Read(probe, force: true);
        held.SetResult(5);
        await retry;
        Require(count == 1, "A timed-out native read cannot accumulate another background probe on retry.");
    }

    private static void VerifyGraphicsMatching()
    {
        const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.NonPublic;
        var assembly = typeof(DashboardViewModel).Assembly;
        var info = assembly.GetType("Kit.Settings.UI.Helpers.DashboardSystemInfo");
        var vendor = info.GetMethod("VendorForDevice", staticFlags);
        foreach (var pair in new[] { ("10DE", "NVIDIA"), ("1002", "AMD"), ("8086", "Intel") })
        {
            Require((string)vendor.Invoke(null, new object[] { "PCI\\VEN_" + pair.Item1 + "&DEV_0001" }) == pair.Item2, "Detect vendor " + pair.Item2);
        }
        Require(vendor.Invoke(null, new object[] { "ROOT\\VEN_10DE&DEV_0001" }) == null && vendor.Invoke(null, new object[] { "PCI\\VEN_1234&DEV_0001" }) == null, "Virtual and other vendors are excluded.");
        var adapterType = assembly.GetType("Kit.Settings.UI.Helpers.DashboardAdapter");
        var adapters = Array.CreateInstance(adapterType, 3);
        adapters.SetValue(Activator.CreateInstance(adapterType, "one", "NVIDIA Test", "nv-driver", "NVIDIA"), 0);
        adapters.SetValue(Activator.CreateInstance(adapterType, "two", "AMD Radeon Graphics", "amd-driver", "AMD"), 1);
        adapters.SetValue(Activator.CreateInstance(adapterType, "three", "Intel UHD Graphics", "intel-driver", "Intel"), 2);
        var matching = info.GetMethod("MatchGraphics", staticFlags);
        var telemetry = new GpuSnapshot(new[] { new GpuTelemetry("NVIDIA Test", "nv-driver", 17, 128, 8192, 45) }, null);
        var readings = ((System.Collections.IEnumerable)matching.Invoke(null, new object[] { adapters, telemetry })).Cast<object>().ToArray();
        object Sample(object value) => value.GetType().GetProperty("Telemetry").GetValue(value);
        Require(Sample(readings[0]) != null && Sample(readings[1]) == null && Sample(readings[2]) == null, "NVIDIA telemetry does not leak onto AMD or Intel adapters.");
        adapters.SetValue(Activator.CreateInstance(adapterType, "four", "NVIDIA Test", "nv-driver", "NVIDIA"), 1);
        readings = ((System.Collections.IEnumerable)matching.Invoke(null, new object[] { adapters, telemetry })).Cast<object>().ToArray();
        Require(Sample(readings[0]) == null && Sample(readings[1]) == null, "Duplicate NVIDIA model names do not get an arbitrary adapter's telemetry.");
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (int i = 0; i < 160 && !done(); i++) await Task.Delay(100);
        Require(done(), "Bounded refresh completes.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        File.AppendAllText(Path.Combine(Output, "page-smoke.log"), message + "\n");
    }
    private static async Task Capture(FrameworkElement element, string name)
    {
        // RenderTargetBitmap does not capture the window's Mica backdrop.
        var surface = (Control)element;
        var panel = (element as ShellPage)?.FindName("RootGrid") as Grid;
        var background = panel?.Background ?? surface.Background;
        var fallback = new SolidColorBrush(element.ActualTheme == ElementTheme.Dark ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32) : Microsoft.UI.ColorHelper.FromArgb(255, 243, 243, 243));
        if (panel != null) panel.Background = fallback;
        else surface.Background = fallback;
        var bitmap = new RenderTargetBitmap();
        try { await bitmap.RenderAsync(element); }
        finally
        {
            if (panel != null) panel.Background = background;
            else surface.Background = background;
        }
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
