using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI;
using Kit.Settings.UI.Controls;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Kit.Settings.UI.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NetMapLib;
using LocalServerHub.Windows;
using ManagedCommon;
using Windows.Graphics.Imaging;
using Windows.Storage;

internal static class DashboardPageSmoke
{
    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();
    private static readonly string Output = Environment.GetEnvironmentVariable("KIT_DASHBOARD_TEST_OUTPUT");
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string WrappedCapacity = "6,400,000.0 / 18,630,000.0 GB";

    [STAThread]
    private static void Main()
    {
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        WindowsAppRuntime_EnsureIsLoaded();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        File.WriteAllText(Path.Combine(Output, "page-smoke.log"), "Real WinUI; live read-only initial snapshot; synthetic display captures.\n");
        File.AppendAllText(Path.Combine(Output, "page-smoke.log"), $"Runtime: {AppContext.BaseDirectory}; working directory: {Environment.CurrentDirectory}\n");
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
        Require(page.FindName("DashboardHero") == null, "Home keeps its introduction with the page title instead of a separate decorative card.");
        Require(UpdateSubscribed(vm), "Visible Home observes the existing local update state.");
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
        vm.SetOverviewActive(true);
        await WaitUntil(() => vm.CanRefreshOverview && vm.CanRefreshSystem);
        Require(Timer(vm).IsRunning, "Home is sampling before the navigation pause check.");
        var cachedPage = page;
        var cachedViewModel = vm;
        var cachedSystemDetails = vm.SystemDetails;
        var cachedGraphicsCards = vm.GraphicsCards;
        var cachedStorageDetails = vm.StorageDetails;
        var cachedEgress = vm.DirectEgress;
        NavigationService.Navigate(typeof(GeneralPage));
        await Task.Delay(250);
        Require(!Timer(vm).IsRunning, "Leaving Home stops its network timer.");
        Require(!UpdateSubscribed(vm), "Leaving Home releases its update-state subscription.");
        foreach (string field in new[] { "uptimeTimer", "telemetryTimer", "storageTimer" })
        {
            var timer = (DispatcherQueueTimer)typeof(DashboardViewModel).GetField(field, PrivateInstance).GetValue(vm);
            Require(!timer.IsRunning, "Leaving Home stops " + field + " while retaining the cached page.");
        }
        Require(!vm.CanRefreshSystem && ReferenceEquals(cachedSystemDetails, vm.SystemDetails), "The hidden dashboard retains its last system snapshot with sampling paused.");
        string hiddenUptime = vm.SystemDetails.Last().Value;
        await Task.Delay(1200);
        Require(vm.SystemDetails.Last().Value == hiddenUptime, "The hidden cached dashboard does not publish uptime ticks.");
        NavigationService.Navigate(typeof(DashboardPage));
        Require(ReferenceEquals(cachedPage, NavigationService.Frame.Content), "Returning Home reuses the cached page instance.");
        await Task.Delay(500);
        page = (DashboardPage)NavigationService.Frame.Content;
        vm = page.ViewModel;
        Require(ReferenceEquals(cachedViewModel, vm), "Returning Home reuses its view model and cached telemetry.");
        Require(UpdateSubscribed(vm), "Returning Home subscribes for current update state again.");
        Require(ReferenceEquals(cachedGraphicsCards, vm.GraphicsCards) && ReferenceEquals(cachedStorageDetails, vm.StorageDetails), "Returning Home immediately reuses fresh GPU and storage cards.");
        Require(ReferenceEquals(cachedEgress, vm.DirectEgress), "Returning Home within ten seconds does not restart egress probes.");
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
        VerifyUpdateNotices(page, chinese);
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
            // Deliberately wider than the 144-DIP usage column, even with tabular digits.
            new("D:\\", WrappedCapacity, 6400000d / 18630000 * 100),
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
        await Task.Delay(600);
        var cpuDetail = vm.SystemDetails.First(detail => detail.Percent == 12);
        var cpuBar = Descendants((DependencyObject)page.FindName("SystemDetailsList"))
            .OfType<Kit.Settings.UI.Helpers.UsageProgressBar>().First(bar => bar.Visibility == Visibility.Visible);
        cpuDetail.Update("85%", 85);
        await Task.Delay(100);
        Require(cpuDetail.UsageValue == 85 && Microsoft.UI.Xaml.Automation.AutomationProperties.GetHelpText(cpuBar) == "85%", "CPU text and accessible help retain the actual sample during interpolation.");
        if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            Require(cpuBar.Value > 12 && cpuBar.Value < 85, "CPU usage passes through intermediate values instead of jumping.");
            double intermediate = cpuBar.Value;
            cpuDetail.Update("5%", 5);
            await Task.Delay(100);
            Require(cpuBar.Value > 5 && cpuBar.Value < intermediate, "An unfinished CPU transition retargets smoothly downward.");
        }
        else
        {
            Require(cpuBar.Value == 85, "CPU usage respects disabled system animations.");
        }
        cpuDetail.Update("12%", 12);
        await Task.Delay(600);
        Require(cpuBar.Value == 12, "CPU animation settles on the exact latest sample.");
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            ((FrameworkElement)window.Content).RequestedTheme = theme;
            foreach (int width in new[] { 1280, 1008, 760, 480 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1100));
                await Task.Delay(250);
                page.UpdateLayout();
                VerifyUpdateHeaderLayout(page, width);
                if (width == 1280) await VerifyUsageAnchor(cpuBar, cpuDetail);
                Require(ReferenceEquals(vm.ProxyEgress, fixtureProxy) && !Timer(vm).IsRunning, "Display captures retain synthetic network data with polling stopped.");
                var visibleValues = VerifyDetailAlignment((FrameworkElement)page.FindName("SystemOverviewCard"), page, width);
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
                Require(Descendants((DependencyObject)page.FindName("SystemOverviewCard")).OfType<TextBlock>().Where(text => text.IsTextSelectionEnabled).All(text => text.FontSize == 14 && text.FontWeight.Weight == 400), "System overview values share normal 14px text.");
                var temperatureLabels = Descendants((DependencyObject)page.FindName("GraphicsCardsList")).OfType<TextBlock>().Where(text => text.Name == "DetailLabel" && text.Text == (chinese ? "温度" : "Temperature")).ToArray();
                Require(temperatureLabels.Length == 2 && temperatureLabels.All(text => text.ActualHeight <= 24), "Temperature labels stay on one line in each GPU group.");
                var scroll = (ScrollViewer)page.FindName("MainScrollViewer");
                Require(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "No horizontal overflow at " + width);
                var ip = (TextBlock)page.FindName("ProxyIpText");
                Require(ip.ActualWidth > 80 && ip.TextWrapping == TextWrapping.Wrap, "IPv6 can wrap.");
                var directIp = (TextBlock)page.FindName("DirectIpText");
                var directLocation = (TextBlock)page.FindName("DirectLocationText");
                var proxyLocation = (TextBlock)page.FindName("ProxyLocationText");
                var comparison = (Grid)page.FindName("EgressComparisonGrid");
                Require(comparison.ActualWidth < 400
                    ? Grid.GetRow(ip) > Grid.GetRow(directIp) && Math.Abs(ip.TransformToVisual(page).TransformPoint(new()).X - directIp.TransformToVisual(page).TransformPoint(new()).X) < 1
                    : Math.Abs(directLocation.TransformToVisual(page).TransformPoint(new()).Y - proxyLocation.TransformToVisual(page).TransformPoint(new()).Y) < 1,
                    $"Network comparison responds to its own content width: window={width}, content={comparison.ActualWidth}.");
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
                var primaryColor = ((SolidColorBrush)labels[0].Foreground).Color;
                var secondaryColor = ((SolidColorBrush)visibleValues[0].Foreground).Color;
                Require(primaryColor != secondaryColor && labels.All(label => ((SolidColorBrush)label.Foreground).Color == primaryColor)
                    && visibleValues.All(value => ((SolidColorBrush)value.Foreground).Color == secondaryColor),
                    "System overview consistently separates primary labels from secondary values in " + theme);
                Require(color == primaryColor && ((SolidColorBrush)directIp.Foreground).Color == color, "IP addresses use readable neutral primary text in " + theme);
                Require(new[] { ip, directIp }.All(text => text.FontSize == 14 && text.FontWeight.Weight == 400 && text.IsTextSelectionEnabled), "IP addresses use normal 14px selectable text.");
                var systemCard = (FrameworkElement)page.FindName("SystemOverviewCard");
                var quickCard = (FrameworkElement)page.FindName("QuickAccessCard");
                var utilitiesCard = (FrameworkElement)page.FindName("UtilitiesCard");
                var networkCard = (FrameworkElement)page.FindName("NetworkOverviewCard");
                var dashboardGrid = (Grid)page.FindName("DashboardGrid");
                var systemPosition = systemCard.TransformToVisual(page).TransformPoint(new());
                var quickPosition = quickCard.TransformToVisual(page).TransformPoint(new());
                var utilitiesPosition = utilitiesCard.TransformToVisual(page).TransformPoint(new());
                var networkPosition = networkCard.TransformToVisual(page).TransformPoint(new());
                Require(Math.Abs(systemPosition.X - quickPosition.X) < 1 && Math.Abs(systemCard.ActualWidth - quickCard.ActualWidth) < 1
                    && Math.Abs(systemPosition.Y - quickPosition.Y - quickCard.ActualHeight - 8) < 1,
                    "System overview follows Quick Access with matching edges and an 8px gap at " + width);
                Require(Math.Abs(networkPosition.X - utilitiesPosition.X) < 1 && Math.Abs(networkCard.ActualWidth - utilitiesCard.ActualWidth) < 1
                    && Math.Abs(networkPosition.Y - utilitiesPosition.Y - utilitiesCard.ActualHeight - 8) < 1,
                    "Network egress follows Utilities with matching edges and an 8px gap at " + width);
                if (dashboardGrid.ActualWidth >= 720)
                {
                    Require(Math.Abs(quickPosition.Y - utilitiesPosition.Y) < 1
                        && Math.Abs(utilitiesPosition.X - quickPosition.X - quickCard.ActualWidth - 8) < 1
                        && Math.Abs(quickCard.ActualWidth - utilitiesCard.ActualWidth) <= 1,
                        $"Independent equal-width columns align Quick Access and Utilities at the top: window={width}, content={dashboardGrid.ActualWidth}, quick=({quickPosition.X},{quickPosition.Y},{quickCard.ActualWidth}), utilities=({utilitiesPosition.X},{utilitiesPosition.Y},{utilitiesCard.ActualWidth}).");
                }
                else
                {
                    Require(Math.Abs(quickPosition.X - utilitiesPosition.X) < 1
                        && Math.Abs(quickCard.ActualWidth - dashboardGrid.ActualWidth) < 1
                        && Math.Abs(utilitiesCard.ActualWidth - dashboardGrid.ActualWidth) < 1
                        && utilitiesPosition.Y >= systemPosition.Y + systemCard.ActualHeight + 7,
                        $"The entire right column follows the left column without overlap when narrow: window={width}, content={dashboardGrid.ActualWidth}.");
                }
                if (width == 1280)
                {
                    var systemList = (ItemsControl)page.FindName("SystemDetailsList");
                    Require(systemList.ActualWidth >= 400, "System details retain readable column width.");
                }
                await Capture(page, $"dashboard-{theme}-{width}.png");
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
                await Task.Delay(100);
                await Capture(page, $"dashboard-{theme}-{width}-details.png");
                scroll.ChangeView(null, 0, null, true);
            }
        }
        await VerifyUpdateHeaderLargeFont(page, window);
        await VerifyHomeIcons(page, window);
        Require(vm.ProxyEgress.Route == NetMapViewModel.Text("Route_SystemBypass"), "Proxy bypass is explicitly labeled: " + vm.ProxyEgress.Route + " / " + NetMapViewModel.Text("Route_SystemBypass"));
        var failure = new IdentityResult(null, ProbeError.Timeout, now, "Explicit");
        Set(vm, "ProxyEgress", new NetMapCard("Proxy", new(failure), true));
        var failedIp = (TextBlock)page.FindName("ProxyIpText");
        Require(failedIp.Text == "N/A" && vm.ProxyEgress.Status == NetMapViewModel.Text("Error_Timeout"), "Failure immediately displays N/A instead of stale success.");
        Require(DashboardViewModel.EgressStatus(vm.ProxyEgress) == NetMapViewModel.Text("Error_Timeout"), "Network errors remain visible without a collection-status footer.");
        page.UpdateLayout();
        Require(((TextBlock)page.FindName("ProxyEgressStatusText")).Visibility == Visibility.Visible, "The failed route displays its error message.");
        var errorStatus = (TextBlock)page.FindName("ProxyEgressStatusText");
        Require(((SolidColorBrush)errorStatus.Foreground).Color != ((SolidColorBrush)failedIp.Foreground).Color, "The error message carries the semantic error color separately from the neutral IP value.");
        Require(((SolidColorBrush)failedIp.Foreground).Color == ((SolidColorBrush)((TextBlock)page.FindName("DirectIpText")).Foreground).Color, "Unavailable IP values retain readable neutral text.");
        Set(vm, "ProxyEgress", new NetMapCard("Proxy", new(proxy, proxy), true));
        Require(failedIp.Text == proxy.Identity.Ip && ((SolidColorBrush)failedIp.Foreground).Color == ((SolidColorBrush)((TextBlock)page.FindName("DirectIpText")).Foreground).Color, "Recovery restores the IP without changing its neutral text color.");
        Require(DashboardViewModel.EgressStatus(vm.ProxyEgress) == string.Empty, "Successful network lookups do not display collection chatter.");
        page.UpdateLayout();
        Require(((TextBlock)page.FindName("ProxyEgressStatusText")).Visibility == Visibility.Collapsed, "Recovery removes the empty status row.");
        vm.Dispose();
        Require(new DashboardDetail("test", "0%", 0).HasUsage && new DashboardDetail("test", "100%", 150).UsageValue == 100 && !new DashboardDetail("test", "—", double.NaN).HasUsage, "Zero usage is available, out-of-range values are bounded, and invalid readings stay unavailable.");

        var updateLink = (Button)page.FindName("DashboardUpdateNoticeButton");
        Require(updateLink.Visibility == Visibility.Visible && updateLink.IsEnabled, "The Home update status button is actionable.");
        var updatePeer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(updateLink);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)updatePeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Task.Delay(300);
        var general = (GeneralPage)NavigationService.Frame.Content;
        var root = window.Content as ShellPage ?? Descendants(window.Content).OfType<ShellPage>().Single();
        await WaitUntil(() => general.IsLoaded);
        var updateTarget = (FrameworkElement)general.FindName("VersionAndUpdateGroup");
        await WaitUntil(() =>
        {
            var targetBounds = Bounds(updateTarget, general);
            return targetBounds.Top >= -1 && targetBounds.Top < general.ActualHeight;
        }, "The Home update status button navigates to the visible Version and updates group.");
        var settingsSurface = (Control)general.FindName("GeneralSettingsSurface");
        Require(settingsSurface.IsEnabled && settingsSurface.IsHitTestVisible && settingsSurface.Opacity == 1,
            "Basic settings are available before the AI section finishes loading.");
        var sectionNavigation = (Grid)general.FindName("SettingsSectionNavigation");
        var sectionLinks = sectionNavigation.Children.OfType<Button>().ToArray();
        string[] expectedLabels = chinese ? ["外观主题", "启动权限", "AI 服务", "诊断日志", "备份还原", "版本更新"] : ["Appearance", "Startup", "AI service", "Diagnostics", "Backup", "Updates"];
        Require(sectionLinks.Length == 6 && sectionLinks.Select(link => ((StackPanel)link.Content).Children.OfType<TextBlock>().Single().Text).SequenceEqual(expectedLabels), "Settings section bar retains all six localized destinations.");
        Require(sectionLinks.All(link => Descendants(link).OfType<FontIcon>().Any() && link.IsTabStop), "Each section pill retains a colored icon and keyboard navigation.");
        Require(sectionLinks.All(link => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(link) == (string)general.FindName((string)link.Tag).GetType().GetProperty("Header").GetValue(general.FindName((string)link.Tag))), "Settings destinations retain full group names for accessibility.");
        var search = (AutoSuggestBox)root.FindName("SearchBox");
        Require(search.Focus(FocusState.Keyboard), "Global search accepts keyboard focus.");
        var focusedSearch = FocusManager.GetFocusedElement(root.XamlRoot);
        Require(ReferenceEquals(focusedSearch, search) || Descendants(search).Any(element => ReferenceEquals(element, focusedSearch)), "Keyboard focus enters the search editor.");
        search.Text = string.Empty;
        typeof(ShellPage).GetMethod("SearchBox_QuerySubmitted", PrivateInstance).Invoke(root, new object[] { search, new AutoSuggestBoxQuerySubmittedEventArgs() });
        await Task.Delay(50);
        Require(ReferenceEquals(NavigationService.Frame.Content, general), "Submitting an empty global search keeps the current settings page.");
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            root.RequestedTheme = theme;
            foreach (int searchWidth in new[] { 1280, 920, 900, 760, 480, 1280 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(searchWidth, 1100));
                await Task.Delay(150);
                root.UpdateLayout();
                var titleBar = (FrameworkElement)root.FindName("AppTitleBar");
                var searchHost = Descendants(titleBar).OfType<FrameworkElement>().Single(element => element.Name == "PART_ContentPresenterGrid");
                var searchBounds = search.TransformToVisual(searchHost).TransformBounds(new Windows.Foundation.Rect(0, 0, search.ActualWidth, search.ActualHeight));
                Require(titleBar.ActualWidth >= 900
                    ? Math.Abs(search.ActualWidth - 516) <= 1
                    : search.ActualWidth >= 140 && search.ActualWidth <= 516,
                    $"Search restores the Source PowerToys width and adapts below its breakpoint: titleBar={titleBar.ActualWidth}, search={search.ActualWidth}, theme={theme}.");
                Require(searchBounds.X >= -1 && searchBounds.Right <= searchHost.ActualWidth + 1
                    && searchBounds.Y >= -1 && searchBounds.Bottom <= searchHost.ActualHeight + 1,
                    $"Search stays within the native title-bar content area at {searchWidth} in {theme}: host={searchHost.ActualWidth}x{searchHost.ActualHeight} ({searchHost.Visibility}), search={searchBounds}.");
                var searchEditor = Descendants(search).OfType<TextBox>().Single(element => element.Name == "TextBox");
                var queryButton = Descendants(searchEditor).OfType<Button>().Single(element => element.Name == "QueryButton");
                var editorContent = Descendants(searchEditor).OfType<FrameworkElement>().Single(element => element.Name == "ContentElement");
                var placeholder = Descendants(searchEditor).OfType<FrameworkElement>().Single(element => element.Name == "PlaceholderTextContentPresenter");
                var clearButton = Descendants(searchEditor).OfType<Button>().Single(element => element.Name == "DeleteButton");
                var queryBounds = queryButton.TransformToVisual(search).TransformBounds(new Windows.Foundation.Rect(0, 0, queryButton.ActualWidth, queryButton.ActualHeight));
                var contentBounds = editorContent.TransformToVisual(search).TransformBounds(new Windows.Foundation.Rect(0, 0, editorContent.ActualWidth, editorContent.ActualHeight));
                string searchLayout = $"window={searchWidth}, theme={theme}, search={search.ActualWidth}x{search.ActualHeight}, editor={searchEditor.ActualWidth}x{searchEditor.ActualHeight}, query={queryBounds}, content={contentBounds}, columns={Grid.GetColumn(queryButton)}/{Grid.GetColumn(editorContent)}/{Grid.GetColumn(placeholder)}/{Grid.GetColumn(clearButton)}";
                Require(Math.Abs(searchEditor.ActualWidth - search.ActualWidth) <= 1
                    && queryButton.Visibility == Visibility.Visible && queryButton.IsEnabled && queryButton.IsHitTestVisible
                    && Math.Abs(queryBounds.Left - 2) <= 1 && queryBounds.Width >= 24
                    && queryBounds.Top >= -1 && queryBounds.Bottom <= search.ActualHeight + 1,
                    "The search editor fills its frame and keeps its query action at the left edge: " + searchLayout);
                Require(Grid.GetColumn(queryButton) == 0 && Grid.GetColumn(editorContent) == 1
                    && Grid.GetColumn(placeholder) == 1 && Grid.GetColumnSpan(placeholder) == 1 && Grid.GetColumn(clearButton) == 2
                    && contentBounds.Width > 0 && contentBounds.Left >= queryBounds.Right - 1
                    && contentBounds.Right <= search.ActualWidth + 1,
                    "Search reserves separate leading-query, text, and trailing-clear columns without clipping the editor: " + searchLayout);
                await Capture(root, $"search-{theme}-{searchWidth}.png");
            }

            foreach (int width in new[] { 1280, 480 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1100));
                await Task.Delay(250);
                general.UpdateLayout();
                var scroll = Descendants(general).OfType<ScrollViewer>().First(item => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(item) == "PageScrollViewer");
                Require(scroll.ExtentWidth <= scroll.ViewportWidth + 1, $"Settings has no horizontal overflow at {width}: theme={theme}, extent={scroll.ExtentWidth}, viewport={scroll.ViewportWidth}.");
                var navigation = Descendants(root).OfType<NavigationView>().Single();
                Require(width == 1280 ? navigation.DisplayMode == NavigationViewDisplayMode.Expanded : navigation.DisplayMode == NavigationViewDisplayMode.Minimal, $"Navigation adapts to available width: window={width}, content={navigation.ActualWidth}, mode={navigation.DisplayMode}, thresholds={navigation.CompactModeThresholdWidth}/{navigation.ExpandedModeThresholdWidth}.");
                await Capture(root, $"settings-{theme}-{width}.png");
                Require(sectionLinks.All(link => link.ActualHeight >= 32 && link.IsTabStop), $"Settings section chips remain touch-friendly and keyboard accessible at {width}: theme={theme}.");
                if (width == 1280)
                {
                    double firstRowY = sectionLinks[0].TransformToVisual(sectionNavigation).TransformPoint(new()).Y;
                    Require(sectionLinks.All(link => Math.Abs(link.TransformToVisual(sectionNavigation).TransformPoint(new()).Y - firstRowY) <= 1), $"All six Settings section chips stay on one horizontal row at {width}: theme={theme}.");
                }
                var navigationPosition = sectionNavigation.TransformToVisual(general).TransformPoint(new());
                Require(navigationPosition.X >= 0 && navigationPosition.X + sectionNavigation.ActualWidth <= general.ActualWidth + 1, $"The section bar fits the page header at {width}: position={navigationPosition}, barWidth={sectionNavigation.ActualWidth}, pageWidth={general.ActualWidth}.");
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
                await Task.Delay(100);
                var scrolledPosition = sectionNavigation.TransformToVisual(general).TransformPoint(new());
                Require(Math.Abs(scrolledPosition.Y - navigationPosition.Y) < 1 && scrolledPosition.Y >= 0, $"Section navigation remains pinned and available after scrolling at {width}: before={navigationPosition}, after={scrolledPosition}, scrollOffset={scroll.VerticalOffset}.");
                scroll.ChangeView(null, 0, null, true);
            }
        }

        await general.ViewModel.AiServices.Initialization;
        await WaitUntil(() => ((Control)general.FindName("AiServiceSettingsSurface")).IsHitTestVisible);
        var pageScroll = Descendants(general).OfType<ScrollViewer>().First(item => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(item) == "PageScrollViewer");
        foreach (var name in new[] { "AiServicesGroup", "VersionAndUpdateGroup", "AppearanceAndBehaviorGroup" })
        {
            var link = sectionLinks.Single(item => (string)item.Tag == name);
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(link);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
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
            var focused = FocusManager.GetFocusedElement(general.XamlRoot);
            Require(Descendants(section).Any(element => ReferenceEquals(element, focused)), $"Clicking the section chip transfers keyboard focus into {name}: focused={(focused as FrameworkElement)?.Name}, type={focused?.GetType().Name}.");
        }
    }

    private static async Task VerifyUsageAnchor(ProgressBar bar, DashboardDetail detail)
    {
        var color = ((SolidColorBrush)bar.Foreground).Color;
        async Task<int> LeadingPixel()
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(bar);
            var buffer = await bitmap.GetPixelsAsync();
            var pixels = new byte[buffer.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            for (int x = 0; x < bitmap.PixelWidth; x++)
                for (int y = 0; y < bitmap.PixelHeight; y++)
                {
                    int offset = (y * bitmap.PixelWidth + x) * 4;
                    if (pixels[offset + 3] > 200 && Math.Abs(pixels[offset] - color.B) +
                        Math.Abs(pixels[offset + 1] - color.G) + Math.Abs(pixels[offset + 2] - color.R) < 40)
                        return x;
                }
            throw new InvalidOperationException("Usage indicator has no visible foreground pixels.");
        }

        detail.Update("90%", 90);
        await Task.Delay(700);
        int anchor = await LeadingPixel();
        Require(anchor <= 1, "The stationary CPU indicator starts at the left edge of its track.");
        int maxOffset = 0;
        foreach (double target in new[] { 10d, 85d, 20d })
        {
            detail.Update(target + "%", target);
            for (int frame = 0; frame < 8; frame++)
            {
                await Task.Delay(16);
                maxOffset = Math.Max(maxOffset, await LeadingPixel() - anchor);
            }
        }
        File.AppendAllText(Path.Combine(Output, "page-smoke.log"), $"CPU indicator rendered left-edge drift ({bar.ActualTheme}): {maxOffset}px across 24 animation frames.\n");
        Require(maxOffset <= 1, "The rendered CPU indicator stays anchored to the left during increases, decreases, and retargeting.");
        detail.Update("12%", 12);
        await Task.Delay(700);
    }

    private static TextBlock[] VerifyDetailAlignment(FrameworkElement overview, FrameworkElement page, int width)
    {
        var rows = Descendants(overview).OfType<Grid>().Where(grid => grid.Name == "DetailRow").ToArray();
        Require(rows.Length > 0, "System overview exposes rendered detail rows at " + width);
        var values = rows.Select(row =>
        {
            var ordinaryValue = row.Children.OfType<TextBlock>().Single(text => text.Name == "DetailValue");
            return ordinaryValue.Visibility == Visibility.Visible
                ? ordinaryValue
                : Descendants(row).OfType<TextBlock>().Single(text => text.Name == "DetailUsageValue");
        }).ToArray();
        Windows.Foundation.Rect Bounds(FrameworkElement element, UIElement relativeTo) => element.TransformToVisual(relativeTo)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        string Describe(TextBlock text) => $"name={text.Name}, text={text.Text.Replace("\r", "\\r").Replace("\n", "\\n")}, font={text.FontFamily.Source}, size={text.FontSize:F3}, weight={text.FontWeight.Weight}, line={text.LineHeight:F3}, stacking={text.LineStackingStrategy}, actual={text.ActualWidth:F2}x{text.ActualHeight:F2}, baseline={text.BaselineOffset:F2}, trimmed={text.IsTextTrimmed}, bounds={Bounds(text, page)}, slot={Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(text)}";
        for (int index = 0; index < rows.Length; index++)
        {
            var label = rows[index].Children.OfType<TextBlock>().Single(text => text.Name == "DetailLabel");
            var usage = rows[index].Children.OfType<Grid>().Single(grid => grid.Name == "UsageRow");
            string usageLayout = usage.Visibility == Visibility.Visible
                ? $"; usage columns={string.Join(",", usage.ColumnDefinitions.Select(column => column.ActualWidth.ToString("F2")))}, track={Bounds(Descendants(usage).OfType<ProgressBar>().Single(), page)}"
                : string.Empty;
            File.AppendAllText(Path.Combine(Output, "page-smoke.log"), $"DETAIL width={width}, theme={page.ActualTheme}, row={index}, bounds={Bounds(rows[index], page)}, padding={rows[index].Padding}, minHeight={rows[index].MinHeight:F2}, columns={string.Join(",", rows[index].ColumnDefinitions.Select(column => column.ActualWidth.ToString("F2")))}; label[{Describe(label)}]; value[{Describe(values[index])}]{usageLayout}\n");
        }
        var valueX = values[0].TransformToVisual(page).TransformPoint(new()).X;
        Require(values.All(value => Math.Abs(value.TransformToVisual(page).TransformPoint(new()).X - valueX) <= 1), "Plain values and usage values share one left edge across all detail sections at " + width);
        var mismatchedValues = values.Where(value => value.FontSize != 14 || value.FontWeight.Weight != 400
            || value.LineHeight != 20 || value.LineStackingStrategy != LineStackingStrategy.MaxHeight).ToArray();
        Require(mismatchedValues.Length == 0, "All detail values retain normal 14px text with a 20px minimum line height at " + width
            + (mismatchedValues.Length == 0 ? string.Empty : "; mismatches: " + string.Join(" | ", mismatchedValues.Select(Describe))));
        Require(values.All(value => value.TextWrapping == TextWrapping.Wrap && value.TextTrimming == TextTrimming.None && !value.IsTextTrimmed), "Detail values remain complete and can wrap instead of truncating at " + width);

        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var label = row.Children.OfType<TextBlock>().Single(text => text.Name == "DetailLabel");
            var value = values[i];
            Require(label.FontSize == 14 && label.FontWeight.Weight == 400 && label.LineHeight == 20
                && label.LineStackingStrategy == LineStackingStrategy.MaxHeight && label.FontFamily.Source == value.FontFamily.Source,
                "Detail labels and values share normal 14px type and the same font family: " + label.Text + "; width=" + width
                + $"; label[{Describe(label)}]; value[{Describe(value)}]");
            var labelPosition = label.TransformToVisual(row).TransformPoint(new());
            var valuePosition = value.TransformToVisual(row).TransformPoint(new());
            Require(Math.Abs(labelPosition.Y + label.BaselineOffset - valuePosition.Y - value.BaselineOffset) <= 1,
                $"Detail label and value share a first-line baseline: {label.Text}; width={width}.");
            Require(valuePosition.X >= labelPosition.X + label.ActualWidth + 15,
                $"Detail label and value retain separate columns: {label.Text}; width={width}.");
            Require(row.ColumnDefinitions[1].Width.IsStar
                && Math.Abs(valuePosition.X - row.ColumnDefinitions[0].ActualWidth - row.ColumnSpacing) <= 1
                && valuePosition.X + value.ActualWidth <= row.ActualWidth + 1
                && labelPosition.X + label.ActualWidth <= row.ColumnDefinitions[0].ActualWidth + 1,
                $"Detail text stays inside its allocated columns: {label.Text}; width={width}, row={Bounds(row, page)}, label={Bounds(label, row)}, value={Bounds(value, row)}, columns={string.Join(",", row.ColumnDefinitions.Select(column => column.ActualWidth))}.");
            Require(row.Padding.Top == 5 && row.Padding.Bottom == 5 && row.MinHeight == 30
                && Math.Abs(valuePosition.Y - 5) <= 1
                && Math.Abs(row.ActualHeight - Math.Max(30, Math.Max(value.ActualHeight, label.ActualHeight) + 10)) <= 1,
                $"Detail rows grow with wrapped text and retain equal vertical padding: {label.Text}; width={width}, row={row.ActualHeight}, value={value.ActualHeight}, label={label.ActualHeight}.");
            if (value.ActualHeight <= 21 && label.ActualHeight <= 21)
            {
                Require(Math.Abs(row.ActualHeight - 30) <= 1, "Single-line detail rows keep a 30px rhythm: " + label.Text + "; width=" + width);
            }

            var usage = row.Children.OfType<Grid>().Single(grid => grid.Name == "UsageRow");
            if (usage.Visibility == Visibility.Visible)
            {
                var track = Descendants(usage).OfType<ProgressBar>().Single();
                var trackPosition = track.TransformToVisual(row).TransformPoint(new());
                Require(trackPosition.X >= valuePosition.X + value.ActualWidth + 11,
                    $"Usage value and track have separate columns with a clear gap: {label.Text}; width={width}.");
                Require(Math.Abs(usage.ColumnDefinitions[0].ActualWidth - 144) <= 1
                    && value.ActualWidth <= usage.ColumnDefinitions[0].ActualWidth + 1
                    && Math.Abs(trackPosition.X - valuePosition.X - usage.ColumnDefinitions[0].ActualWidth - usage.ColumnSpacing) <= 1
                    && trackPosition.X + track.ActualWidth <= row.ActualWidth + 1,
                    $"Usage text fits its fixed column and its track stays inside the remaining column: {label.Text}; width={width}, value={Bounds(value, row)}, track={Bounds(track, row)}, usage={Bounds(usage, row)}.");
                Require(Math.Abs(track.ActualHeight - 4) <= 1 && Math.Abs(trackPosition.Y + track.ActualHeight / 2 - valuePosition.Y - 10) <= 1,
                    $"The 4px usage track is centered on the first text line even when values wrap: {label.Text}; width={width}.");
            }
        }

        var longCapacity = values.Single(value => value.Text == WrappedCapacity);
        Require(longCapacity.ActualHeight >= 40 && !longCapacity.IsTextTrimmed, "An over-width capacity value wraps completely without moving its track below the first line at " + width + "; " + Describe(longCapacity));
        var windowsValue = values.Single(value => value.Text.StartsWith("Windows 11 Enterprise", StringComparison.Ordinal));
        var windowsRow = rows[Array.IndexOf(values, windowsValue)];
        Require(windowsValue.ActualHeight >= 40 && !windowsValue.IsTextTrimmed
            && windowsRow.ColumnDefinitions[1].ActualWidth > 144
            && Grid.GetColumn(windowsValue) == 1,
            "Multiline hardware text uses the remaining value column and grows without clipping at " + width + "; " + Describe(windowsValue));
        var orderedRows = rows.OrderBy(row => row.TransformToVisual(page).TransformPoint(new()).Y).ToArray();
        for (int index = 1; index < orderedRows.Length; index++)
        {
            var previous = orderedRows[index - 1];
            var current = orderedRows[index];
            Require(Bounds(current, page).Top >= Bounds(previous, page).Bottom - 1,
                $"Detail rows remain separated after wrapping at {width}: previous={previous.Children.OfType<TextBlock>().Single(text => text.Name == "DetailLabel").Text} {Bounds(previous, page)}, current={current.Children.OfType<TextBlock>().Single(text => text.Name == "DetailLabel").Text} {Bounds(current, page)}.");
        }
        return values;
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

    private static bool UpdateSubscribed(DashboardViewModel vm) =>
        typeof(DashboardViewModel).GetField("updateStateSubscription", PrivateInstance).GetValue(vm) != null;

    private static Windows.Foundation.Rect Bounds(FrameworkElement element, UIElement relativeTo) =>
        element.TransformToVisual(relativeTo).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static void ApplyUpdateState(DashboardViewModel vm, UpdatingSettings settings) =>
        typeof(DashboardViewModel).GetMethod("ApplyUpdateState", PrivateInstance, null, new[] { typeof(UpdatingSettings) }, null).Invoke(vm, new object[] { settings });

    private static void VerifyUpdateNotices(DashboardPage page, bool chinese)
    {
        var vm = page.ViewModel;
        var link = (Button)page.FindName("DashboardUpdateNoticeButton");
        var label = (TextBlock)page.FindName("DashboardUpdateNoticeText");
        Require(!UpdateSubscribed(vm), "Disposing Home releases its update observer before synthetic display checks.");
        Require(((TextBlock)page.FindName("DashboardUpdateTitleText")).Text == "Kit Update"
            && ((TextBlock)page.FindName("DashboardVersionText")).Text == vm.KitVersionShortLabel
            && vm.KitVersionLabel.StartsWith("Kit v", StringComparison.Ordinal), "Home shows Kit Update and the running Kit version.");

        ApplyUpdateState(vm, new UpdatingSettings
        {
            State = UpdatingSettings.UpdatingState.ReadyToInstall,
            ReleasePageLink = "https://github.com/guijianchou/Kit/releases/tag/9.0.0",
        });
        page.UpdateLayout();
        Require(link.Visibility == Visibility.Visible && label.Text == (chinese ? "重启以更新" : "Restart to update"),
            "A prepared newer update displays the localized restart notice.");

        ApplyUpdateState(vm, new UpdatingSettings
        {
            State = UpdatingSettings.UpdatingState.ReadyToInstall,
            ReleasePageLink = "https://github.com/guijianchou/Kit/releases/tag/0.1.0",
        });
        page.UpdateLayout();
        Require(link.Visibility == Visibility.Visible && !vm.HasKitUpdateNotice && label.Text == (chinese ? "上次检查：未检查" : "Last checked: Never"),
            "An older already-installed target cannot advertise a pending update.");
        ApplyUpdateState(vm, null);
        page.UpdateLayout();
        Require(link.Visibility == Visibility.Visible && !vm.HasKitUpdateNotice && label.Text == (chinese ? "上次检查：未检查" : "Last checked: Never"),
            "Unknown update state does not claim that Kit is up to date.");
        ApplyUpdateState(vm, new UpdatingSettings { State = UpdatingSettings.UpdatingState.NetworkError });
        page.UpdateLayout();
        Require(link.Visibility == Visibility.Visible && label.Text == (chinese ? "检查更新失败" : "Update check failed"),
            "An update-check failure has a short localized Settings entry.");
        ApplyUpdateState(vm, new UpdatingSettings
        {
            State = UpdatingSettings.UpdatingState.UpToDate,
            LastCheckedDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        page.UpdateLayout();
        Require(link.Visibility == Visibility.Visible && !vm.HasKitUpdateNotice && label.Text == (chinese ? "上次检查：刚刚" : "Last checked: Just now"),
            "A successful recheck clears the previous failure notice and shows when updates were last checked.");
        ApplyUpdateState(vm, new UpdatingSettings
        {
            State = UpdatingSettings.UpdatingState.ReadyToDownload,
            ReleasePageLink = "https://github.com/guijianchou/Kit/releases/tag/9.0.0",
        });
        page.UpdateLayout();
        Require(label.Text == (chinese ? "有可用更新" : "Update available"), "Available-update screenshots use a synthetic newer release.");
    }

    private static void VerifyUpdateHeaderLayout(DashboardPage page, int width)
    {
        var header = (Grid)page.FindName("DashboardHeader");
        var title = (TextBlock)page.FindName("DashboardHeading");
        var intro = (TextBlock)page.FindName("DashboardIntroduction");
        var panel = (FrameworkElement)page.FindName("DashboardVersionPanel");
        var version = (TextBlock)page.FindName("DashboardVersionText");
        var notice = (TextBlock)page.FindName("DashboardUpdateNoticeText");
        var utilities = (FrameworkElement)page.FindName("UtilitiesCard");
        var titleBounds = Bounds(title, page);
        var introBounds = Bounds(intro, page);
        var panelBounds = Bounds(panel, page);
        Require(Math.Abs(panelBounds.Right - Bounds(utilities, page).Right) <= 1.5,
            $"Home version/update right edge aligns with Utilities at width={width}, font={TypographyResources.AppliedFontSize}: panel={panelBounds}, utilities={Bounds(utilities, page)}.");
        Require(Grid.GetRow(panel) == 0
                ? panelBounds.Left >= titleBounds.Right + 23 && panelBounds.Left >= introBounds.Right + 23
                : panelBounds.Top >= titleBounds.Bottom - 1,
            $"Home title and version/update never overlap at width={width}, font={TypographyResources.AppliedFontSize}.");
        if (Grid.GetRow(panel) == 0)
        {
            var noticeBounds = Bounds(notice, page);
            Require(Math.Abs(noticeBounds.Bottom - introBounds.Bottom) <= 4.0,
                $"Home Last checked line aligns with intro line at width={width}, font={TypographyResources.AppliedFontSize}: notice={noticeBounds}, intro={introBounds}.");
        }
        Require(header.ActualWidth < TypographyResources.FontSize(480)
                ? Grid.GetRow(panel) == 1 : Grid.GetRow(panel) == 0,
            "The Home header changes rows according to available width and text size.");
        var headerBounds = Bounds(header, page);
        Require(panelBounds.Left >= headerBounds.Left - 1 && panelBounds.Right <= headerBounds.Right + 1
            && !version.IsTextTrimmed && !notice.IsTextTrimmed,
            "Home version and update text remain complete inside the header.");
    }

    private static async Task VerifyUpdateHeaderLargeFont(DashboardPage page, Window window)
    {
        string family = TypographyResources.AppliedFontFamily;
        int size = TypographyResources.AppliedFontSize;
        var windowSize = window.AppWindow.Size;
        var track = typeof(TypographyResources).GetMethod("TrackElement", BindingFlags.Static | BindingFlags.NonPublic);
        var apply = typeof(TypographyResources).GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic);
        try
        {
            foreach (var element in Descendants(window.Content)) track.Invoke(null, new object[] { element });
            apply.Invoke(null, new object[] { family, 20 });
            foreach (int width in new[] { 1280, 760, 480 })
            {
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1100));
                await Task.Delay(250);
                page.UpdateLayout();
                Require(((TextBlock)page.FindName("DashboardUpdateTitleText")).FontSize >= 20,
                    "The Home Kit Update title participates in live 20px typography.");
                VerifyUpdateHeaderLayout(page, width);
                await Capture(page, $"dashboard-header-font20-{width}.png");
            }
        }
        finally
        {
            apply.Invoke(null, new object[] { family, size });
            window.AppWindow.Resize(windowSize);
        }
        await Task.Delay(250);
    }

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
        await VerifyCloseAndReopen(vm, window);
    }

    private static async Task VerifyCloseAndReopen(DashboardViewModel vm, MainWindow window)
    {
        var flushProperty = typeof(MainWindow).GetProperty("FlushPendingSettingsAsync", PrivateInstance);
        var keepAliveProperty = typeof(MainWindow).GetProperty("KeepAliveForBackgroundWork", PrivateInstance);
        var previousFlush = flushProperty.GetValue(window);
        var previousKeepAlive = keepAliveProperty.GetValue(window);
        var previousSize = window.AppWindow.Size;
        try
        {
            // Exercise real Closed handlers while keeping this test window alive.
            keepAliveProperty.SetValue(window, (Func<bool>)(() => true));
            flushProperty.SetValue(window, (Func<Task<bool>>)(() => Task.FromResult(false)));
            window.Close();
            await Task.Delay(100);
            Require(window.AppWindow.IsVisible && Timer(vm).IsRunning,
                "A failed settings flush leaves the visible dashboard sampling.");

            flushProperty.SetValue(window, null);
            window.Close();
            await WaitUntil(() => !window.AppWindow.IsVisible && !Timer(vm).IsRunning);
            var original = vm.DirectEgress;
            var originalProxy = vm.ProxyEgress;
            var refreshStamp = typeof(DashboardViewModel).GetField("lastOverviewRefresh", PrivateInstance);
            long completedBeforeClose = (long)refreshStamp.GetValue(vm);
            await Task.Delay(10500);
            Require(ReferenceEquals(original, vm.DirectEgress) && ReferenceEquals(originalProxy, vm.ProxyEgress)
                && (long)refreshStamp.GetValue(vm) == completedBeforeClose && !Timer(vm).IsRunning,
                "Closing to background work keeps the hidden dashboard idle past a polling interval.");

            bool resumedProbeStarted = false;
            void OnResumeChanged(object sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(DashboardViewModel.CanRefreshOverview) && !vm.CanRefreshOverview)
                {
                    resumedProbeStarted = true;
                }
            }

            vm.PropertyChanged += OnResumeChanged;
            try
            {
                window.AppWindow.Show();
                await WaitUntil(() => Timer(vm).IsRunning);
                Require(new[] { "uptimeTimer", "telemetryTimer", "storageTimer" }.All(field =>
                    ((DispatcherQueueTimer)typeof(DashboardViewModel).GetField(field, PrivateInstance).GetValue(vm)).IsRunning),
                    "Showing Home restores all system telemetry timers.");

                // Activation starts timers immediately, then queues stale-snapshot work at
                // Low priority. Ready flags may therefore be true before that work begins.
                // Suppress the periodic tick so it cannot hide a missing activation refresh.
                Timer(vm).Stop();
                await WaitUntil(() => resumedProbeStarted && !ReferenceEquals(original, vm.DirectEgress)
                    && !ReferenceEquals(originalProxy, vm.ProxyEgress)
                    && (long)refreshStamp.GetValue(vm) > completedBeforeClose
                    && vm.CanRefreshOverview && vm.CanRefreshSystem,
                    "The queued activation refresh finishes both egress probes while periodic network polling is stopped.");
                Require(resumedProbeStarted && !ReferenceEquals(original, vm.DirectEgress)
                    && !ReferenceEquals(originalProxy, vm.ProxyEgress)
                    && (long)refreshStamp.GetValue(vm) > completedBeforeClose,
                    "Showing Home after a handled close completes a fresh direct and proxy observation before the periodic timer resumes.");
            }
            finally
            {
                vm.PropertyChanged -= OnResumeChanged;
                if (window.AppWindow.IsVisible) Timer(vm).Start();
            }

            int originalWidth = window.AppWindow.Size.Width;
            int width = originalWidth > 800 ? originalWidth - 160 : originalWidth + 160;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, window.AppWindow.Size.Height));
            await Task.Delay(100);
            int actualWidth = window.AppWindow.Size.Width;
            Require(actualWidth != originalWidth, "The reopened window accepts a new size.");
            string placementPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit", "settings-placement.json");
            bool HasSavedWidth()
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(placementPath));
                    var rectangle = json.RootElement.GetProperty("NormalPosition");
                    return rectangle.GetProperty("Right").GetInt32() - rectangle.GetProperty("Left").GetInt32() == actualWidth;
                }
                catch (IOException) { return false; }
                catch (JsonException) { return false; }
            }
            await WaitUntil(HasSavedWidth);
            Require(HasSavedWidth(), "Resizing after a handled close still persists the new window placement.");
        }
        finally
        {
            flushProperty.SetValue(window, previousFlush);
            keepAliveProperty.SetValue(window, previousKeepAlive);
            if (!window.AppWindow.IsVisible) window.AppWindow.Show();
            window.AppWindow.Resize(previousSize);
        }
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
        var formatDriver = info.GetMethod("FormatDriverVersion", staticFlags);
        foreach (var (raw, maker, expected) in new[]
        {
            ("32.0.15.6636", "NVIDIA", "566.36"),
            ("31.0.15.6601", "NVIDIA", "566.01"),
            ("31.0.15.900", "NVIDIA", "509.00"),
            ("566.03", "NVIDIA", "566.03"),
            ("566", "NVIDIA", "566"),
            ("32.0.21037.1004", "AMD", "32.0.21037.1004"),
            ("32.0.101.6557", "Intel", "32.0.101.6557"),
            ("not-a-version", "NVIDIA", "not-a-version"),
            ("—", "NVIDIA", "—"),
        })
        {
            Require((string)formatDriver.Invoke(null, new object[] { raw, maker }) == expected,
                $"GPU driver preserves the detailed version: {maker} {raw} -> {expected}");
        }
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
        var telemetryType = assembly.GetType("Kit.Settings.UI.Helpers.GpuTelemetry");
        var snapshotType = assembly.GetType("Kit.Settings.UI.Helpers.GpuSnapshot");
        var telemetryDevice = Activator.CreateInstance(telemetryType, "NVIDIA Test", "nv-driver", null, null, null, null);
        var telemetryDevices = Array.CreateInstance(telemetryType, 1);
        telemetryDevices.SetValue(telemetryDevice, 0);
        var telemetry = Activator.CreateInstance(snapshotType, telemetryDevices, null);
        var readings = ((System.Collections.IEnumerable)matching.Invoke(null, new object[] { adapters, telemetry })).Cast<object>().ToArray();
        object Sample(object value) => value.GetType().GetProperty("Telemetry").GetValue(value);
        Require(Sample(readings[0]) != null && Sample(readings[1]) == null && Sample(readings[2]) == null, "NVIDIA telemetry does not leak onto AMD or Intel adapters.");
        adapters.SetValue(Activator.CreateInstance(adapterType, "four", "NVIDIA Test", "nv-driver", "NVIDIA"), 1);
        readings = ((System.Collections.IEnumerable)matching.Invoke(null, new object[] { adapters, telemetry })).Cast<object>().ToArray();
        Require(Sample(readings[0]) == null && Sample(readings[1]) == null, "Duplicate NVIDIA model names do not get an arbitrary adapter's telemetry.");
    }

    private static async Task WaitUntil(Func<bool> done, string message = "Bounded refresh completes.")
    {
        for (int i = 0; i < 160 && !done(); i++) await Task.Delay(100);
        Require(done(), message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        File.AppendAllText(Path.Combine(Output, "page-smoke.log"), message + "\n");
    }
    private static async Task VerifyHomeIcons(DashboardPage page, Window window)
    {
        var root = window.Content as ShellPage ?? Descendants(window.Content).OfType<ShellPage>().Single();
        var quick = (QuickAccessList)page.FindName("QuickAccessItemsControl");
        var emptyState = ((Grid)quick.Parent).Children.OfType<FrameworkElement>().Single(child => !ReferenceEquals(child, quick));
        var originalItems = quick.ItemsSource;
        var originalVisibility = quick.Visibility;
        var originalEmptyVisibility = emptyState.Visibility;
        var originalTheme = root.RequestedTheme;
        var originalSize = window.AppWindow.Size;
        var modules = new[] { ModuleType.AIHub, ModuleType.Awake, ModuleType.LightSwitch, ModuleType.Localserver, ModuleType.NetMap, ModuleType.UDPtest, ModuleType.Winget };
        try
        {
            // These display-only items never invoke module launchers or change settings.
            quick.ItemsSource = modules.Select(module => new QuickAccessItem
            {
                Title = module.ToString(),
                Description = "Open " + module,
                Tag = module,
                Icon = ModuleHelper.GetModuleTypeFluentIconName(module),
            }).ToArray();
            quick.Visibility = Visibility.Visible;
            emptyState.Visibility = Visibility.Collapsed;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1100));
            ((ScrollViewer)page.FindName("MainScrollViewer")).ChangeView(null, 0, null, true);
            int transition = 0;
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                root.RequestedTheme = theme;
                await Task.Delay(400);
                root.UpdateLayout();
                var frame = await Capture(root, $"home-icons-{++transition}-{theme}-1280.png");
                foreach (var module in modules)
                {
                    var item = (NavigationViewItem)root.FindName(module + "NavigationItem");
                    Require(item.Icon is ImageIcon, $"Sidebar {module} uses its colorful module ImageIcon.");
                    await VerifyIconPixels(item.Icon, root, frame, $"Sidebar {module} ({theme}, transition {transition})", verifyContrast: false);
                }

                var quickButtons = Descendants(quick).OfType<Button>().ToArray();
                var quickIcons = Descendants(quick).OfType<Image>().ToArray();
                Require(quick.IsCompact && quickButtons.Length == modules.Length && quickIcons.Length == modules.Length,
                    "Home Quick access displays every active module as an icon button with its module image.");
                foreach (var module in modules)
                {
                    var button = quickButtons.Single(item => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(item) == module.ToString());
                    var icon = Descendants(button).OfType<Image>().Single();
                    string description = $"Quick access {module} ({theme}, transition {transition})";
                    Require(button.GetType() == typeof(Button) && Math.Abs(button.ActualWidth - 48) < 1 && Math.Abs(button.ActualHeight - 48) < 1,
                        description + " uses a native button with a 48px square click target.");
                    Require(icon.Width == 32 && icon.Height == 32 && Math.Abs(icon.ActualWidth - 32) < 1 && Math.Abs(icon.ActualHeight - 32) < 1,
                        description + " displays its native 32px module image.");
                    Require(icon.Source is BitmapImage bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0
                        && bitmap.UriSource == new Uri(ModuleHelper.GetModuleTypeFluentIconName(module)),
                        description + " loads the correct module bitmap.");
                    Require(!Descendants(button).OfType<TextBlock>().Any(text => text.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(text.Text)),
                        description + " has no visible text label.");
                    var iconBounds = icon.TransformToVisual(button).TransformBounds(new Windows.Foundation.Rect(0, 0, icon.ActualWidth, icon.ActualHeight));
                    Require(Math.Abs(iconBounds.X + iconBounds.Width / 2 - button.ActualWidth / 2) < 1
                        && Math.Abs(iconBounds.Y + iconBounds.Height / 2 - button.ActualHeight / 2) < 1,
                        description + " centers its image inside the click target.");
                    var tooltip = ToolTipService.GetToolTip(button) as ToolTip;
                    var tooltipContent = tooltip?.Content as StackPanel;
                    Require(tooltipContent != null
                        && tooltipContent.Children.OfType<TextBlock>().Any(text => text.Text == module.ToString())
                        && tooltipContent.Children.OfType<TextBlock>().Any(text => text.Text == "Open " + module && text.Visibility == Visibility.Visible),
                        description + " retains its title and description in the tooltip.");
                    await VerifyIconPixels(icon, root, frame, description, verifyContrast: false);
                    Require(button.IsTabStop && button.Focus(FocusState.Keyboard), description + " retains its accessible name and accepts keyboard focus.");
                }
            }
        }
        finally
        {
            quick.ItemsSource = originalItems;
            quick.Visibility = originalVisibility;
            emptyState.Visibility = originalEmptyVisibility;
            root.RequestedTheme = originalTheme;
            window.AppWindow.Resize(originalSize);
        }
    }

    private static async Task VerifyIconPixels(FrameworkElement icon, FrameworkElement root,
        (byte[] Bytes, int Width, int Height) frame, string description, bool verifyContrast)
    {
        Require(icon != null && icon.ActualWidth > 0 && icon.ActualHeight > 0, description + " has visible bounds.");
        var rendered = new RenderTargetBitmap();
        await rendered.RenderAsync(icon);
        var buffer = await rendered.GetPixelsAsync();
        var pixels = new byte[buffer.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        int painted = 0;
        for (int offset = 3; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] >= 32) painted++;
        }

        Require(painted >= 8, $"{description} paints nontransparent pixels ({painted}).");
        if (!verifyContrast) return;

        var transform = icon.TransformToVisual(root);
        var bounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, icon.ActualWidth, icon.ActualHeight));
        double scaleX = frame.Width / root.ActualWidth;
        double scaleY = frame.Height / root.ActualHeight;
        File.AppendAllText(Path.Combine(Output, "page-smoke.log"),
            $"{description}: element={icon.ActualWidth:F2}x{icon.ActualHeight:F2}, RTB={rendered.PixelWidth}x{rendered.PixelHeight}, " +
            $"bounds=({bounds.X:F2},{bounds.Y:F2},{bounds.Width:F2},{bounds.Height:F2}), frameScale={scaleX:F3}x{scaleY:F3}\n");
        double background = LuminanceAt(bounds.X - 2, bounds.Y + bounds.Height / 2);
        // Small curved glyphs can be entirely antialiased. Measure their visible
        // strokes in the composited frame rather than requiring opaque source pixels.
        // Bitmap icons retain their existing near-opaque alpha threshold.
        int coreAlpha = icon is FontIcon ? 32 : 245;
        var contrasts = new System.Collections.Generic.List<double>();
        for (int y = 0; y < rendered.PixelHeight; y++)
        {
            for (int x = 0; x < rendered.PixelWidth; x++)
            {
                if (pixels[(y * rendered.PixelWidth + x) * 4 + 3] < coreAlpha) continue;
                // NavigationView scales ImageIcon inside its template. Convert every local
                // pixel through the full visual transform, not just the element's origin.
                var position = transform.TransformPoint(new Windows.Foundation.Point(
                    (x + 0.5) * icon.ActualWidth / rendered.PixelWidth,
                    (y + 0.5) * icon.ActualHeight / rendered.PixelHeight));
                double foreground = LuminanceAt(position.X, position.Y);
                contrasts.Add((Math.Max(foreground, background) + 0.05) / (Math.Min(foreground, background) + 0.05));
            }
        }

        if (icon is FontIcon)
        {
            int readablePixels = contrasts.Count(value => value >= 3);
            Require(readablePixels >= 8, $"{description} has {readablePixels} visible stroke pixels with at least 3:1 contrast (minimum 8).");
            return;
        }

        Require(contrasts.Count >= 8, description + " has opaque foreground pixels for contrast measurement.");
        contrasts.Sort();
        double contrast = contrasts[contrasts.Count / 2];
        Require(contrast >= 3, $"{description} foreground/background contrast is {contrast:F2}:1 (minimum 3:1).");

        double LuminanceAt(double x, double y)
        {
            int px = Math.Clamp((int)(x * scaleX), 0, frame.Width - 1);
            int py = Math.Clamp((int)(y * scaleY), 0, frame.Height - 1);
            int offset = (py * frame.Width + px) * 4;
            return 0.2126 * Linear(frame.Bytes[offset + 2]) + 0.7152 * Linear(frame.Bytes[offset + 1]) + 0.0722 * Linear(frame.Bytes[offset]);
        }

        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
    }

    private static async Task<(byte[] Bytes, int Width, int Height)> Capture(FrameworkElement element, string name)
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
        var bytes = new byte[pixels.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels))
        {
            reader.ReadBytes(bytes);
        }

        // Encode in memory and save through System.IO: WinRT StorageFolder/CreateFileAsync
        // can fail with access-denied in host environments that restrict freshly built exes.
        var encoded = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
        var png = new byte[encoded.Size];
        using (var reader = new Windows.Storage.Streams.DataReader(encoded.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)encoded.Size);
            reader.ReadBytes(png);
        }

        File.WriteAllBytes(Path.Combine(Output, name), png);
        return (bytes, bitmap.PixelWidth, bitmap.PixelHeight);
    }
}
