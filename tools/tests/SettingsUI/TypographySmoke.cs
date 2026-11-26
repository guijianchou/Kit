using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Kit.Settings.UI.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using NetMapLib;
using ManagedCommon;
using Windows.Graphics.Imaging;

internal static partial class TypographySmoke
{
    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();

    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly string Output = Environment.GetEnvironmentVariable("KIT_TYPOGRAPHY_SMOKE_OUTPUT");
    private static readonly string Report = Path.Combine(Output, "typography-smoke.log");
    private static readonly string RequestedFamily = Environment.GetEnvironmentVariable("KIT_TYPOGRAPHY_SMOKE_FONT_FAMILY");
    private static readonly int RequestedSize = int.Parse(Environment.GetEnvironmentVariable("KIT_TYPOGRAPHY_SMOKE_FONT_SIZE"), CultureInfo.InvariantCulture);
    private static readonly string RequestedLanguage = Environment.GetEnvironmentVariable("KIT_TYPOGRAPHY_SMOKE_LANGUAGE") ?? string.Empty;
    private static string ActiveFamily = RequestedFamily;
    private static int ActiveSize = RequestedSize;
    private static string ActiveLanguage = RequestedLanguage;
    private static string StartupEffectiveLanguage;
    private static string ExpectedFamily => new FontFamily(ActiveFamily == "system" ? "XamlAutoFontFamily" : "ms-appx:///Assets/Fonts/NotoSansSC-Variable.ttf#Noto Sans SC").Source;
    private static readonly List<string> Messages = new();
    private static readonly List<string> ForbiddenMessages = new();
    private static readonly List<string> TypographyFailures = new();
    private static readonly JsonDocument ExpectedStrings = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "expected-strings.json")));

    [STAThread]
    private static void Main()
    {
        File.WriteAllText(Report, "Native Settings UI smoke. Startup uses wrapper-written settings. All modules disabled; no operation or real IPC. Suites use inert fixtures; Dashboard activity is isolated before Loaded when that page is included. PNGs capture the complete Settings visual tree with an opaque Mica-color substitute because RenderTargetBitmap omits the compositor backdrop.\n");
        try
        {
            string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit", "settings.json");
            using (var settings = JsonDocument.Parse(File.ReadAllText(settingsPath)))
            {
                foreach (string module in new[] { "Awake", "LightSwitch", "Localserver", "UDPtest", "NetMap", "AIHub", "Winget" })
                    Require(!settings.RootElement.GetProperty("enabled").GetProperty(module).GetBoolean(), "Wrapper must disable " + module + " before startup.");
                Require(settings.RootElement.GetProperty("ui_font_family").GetString() == RequestedFamily &&
                    settings.RootElement.GetProperty("ui_font_size").GetInt32() == RequestedSize, "Wrapper must write requested typography before startup.");
            }
            Require(!new Kit.AiHub.Storage.AiHubSettingsStore().Load().IsEnabled, "Shared AI service must remain disabled.");
            Require(Kit.AiHub.Engine.AiHubEngine.ExistingEngine == null, "Observing AI usage before startup does not initialize the AI engine.");
            EnsureNoWorker();
            Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
            WindowsAppRuntime_EnsureIsLoaded();
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                    var app = new App { StartupPage = typeof(GeneralPage) };
                    app.UnhandledException += (_, args) =>
                    {
                        Log("UNHANDLED XAML: " + args.Message + "\n" + args.Exception);
                        Environment.Exit(4);
                    };
                    DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
                    {
                        try { await Task.Delay(350); await Run(); Log("PASS"); Environment.Exit(0); }
                        catch (Exception ex) { Log(ex.ToString()); Environment.Exit(1); }
                    });
                }
                catch (Exception ex) { Log("App construction failed: " + ex); Environment.Exit(3); }
            });
        }
        catch (Exception ex) { Log(ex.ToString()); Environment.Exit(2); }
    }

    private static async Task Run()
    {
        ShellPage.SetDefaultSndMessageCallback(message => Messages.Add(message));
        ShellPage.SetRestartAdminSndMessageCallback(message => CaptureForbidden("restart", message));
        ShellPage.SetCheckForUpdatesMessageCallback(message => CaptureForbidden("update", message));
        var general = SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default);
        general.StopWatching();
        var aiSettings = SettingsRepository<AIHubSettings>.GetInstance(SettingsUtils.Default);
        aiSettings.StopWatching();
        // Pick the audit display in memory before the page is constructed, so
        // a user's saved tab cannot cause a settings write just for a fixture.
        aiSettings.SettingsConfig.Properties.ActiveTabIndex.Value = 0;
        SettingsRepository<WingetSettings>.GetInstance(SettingsUtils.Default).StopWatching();
        Require(general.SettingsConfig.UiFontFamily == RequestedFamily && general.SettingsConfig.UiFontSize == RequestedSize,
            "Startup repository retains wrapper typography.");
        Require(!general.SettingsConfig.Enabled.AiHub && !general.SettingsConfig.Enabled.Winget, "Navigation fixtures cannot enable modules.");
        StartupEffectiveLanguage = RuntimeLocalization.EffectiveLanguage;
        ActiveLanguage = StartupEffectiveLanguage;
        Require(RuntimeLocalization.AppliedLanguage.Equals(RequestedLanguage, StringComparison.OrdinalIgnoreCase), "App retains the requested language selection, including system default.");
        Require(CultureInfo.CurrentUICulture.Name.Equals(StartupEffectiveLanguage, StringComparison.OrdinalIgnoreCase) &&
            (StartupEffectiveLanguage.Equals("en-US", StringComparison.OrdinalIgnoreCase) || StartupEffectiveLanguage.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)),
            "App starts in its supported effective UI language.");
        if (!string.IsNullOrEmpty(RequestedLanguage))
            Require(StartupEffectiveLanguage.Equals(RequestedLanguage, StringComparison.OrdinalIgnoreCase), "Explicit startup language resolves to that requested language.");
        Require(((FontFamily)Application.Current.Resources["KitUiFontFamily"]).Source == ExpectedFamily,
            "Startup font resource matches requested family: expected " + ExpectedFamily + ", got " + ((FontFamily)Application.Current.Resources["KitUiFontFamily"]).Source);
        Require(Near((double)Application.Current.Resources["KitFontSize14"], RequestedSize), "Startup base size matches requested size.");
        if (RequestedFamily != "system")
            Require(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "NotoSansSC-Variable.ttf")), "Bundled Noto Sans SC font is present in the runtime.");
        Log($"Startup family={RequestedFamily}, base={RequestedSize}, requestedLanguage={(string.IsNullOrEmpty(RequestedLanguage) ? "system-default" : RequestedLanguage)}, effectiveLanguage={StartupEffectiveLanguage}, source={ExpectedFamily}");

        App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1100));
        var generalPage = NavigationService.Frame.Content as GeneralPage ?? throw new InvalidOperationException("Startup must navigate directly to General.");
        await WaitUntil(() => generalPage.IsLoaded, "General page loaded");
        var generalSurface = Element<Control>(generalPage, "GeneralSettingsSurface");
        Require(generalSurface.IsEnabled && generalSurface.IsHitTestVisible && generalSurface.Opacity == 1,
            "General controls are available without waiting for the AI section.");
        await Settle();
        if (Environment.GetEnvironmentVariable("KIT_TYPOGRAPHY_SMOKE_SUITE") == "Plugins")
        {
            await RunPlugins();
            return;
        }
        await CaptureShell("general-startup-full-shell.png");
        LogGeneralRowAlignment(generalPage);
        await VerifyGeneral(generalPage);
        await VerifyShellAiUsage(generalPage);
        await VerifyNativeControls(generalPage);
        await VerifyBundledFontLoading();
        await CaptureGeneral(generalPage);
        int messagesAfterTypography = Messages.Count;

        // Keep the VM alive for localization/state assertions. Disable only the
        // dashboard page activity source before every Loaded event, so neither
        // initial navigation nor cached visits can start real sampling.
        bool dashboardIntercepted = false;
        var isolatedDashboards = new HashSet<DashboardPage>();
        NavigatedEventHandler isolateDashboard = (_, args) =>
        {
            if (args.Content is not DashboardPage dashboard) return;
            Log("Dashboard Navigated fixture isolation entered.");
            Require(!dashboard.IsLoaded, "Dashboard must be isolated before Loaded.");
            Require(!(bool)typeof(DashboardViewModel).GetField("overviewActive", PrivateInstance).GetValue(dashboard.ViewModel), "Dashboard has not started live sampling.");
            typeof(DashboardPage).GetField("isCurrentPage", PrivateInstance).SetValue(dashboard, false);
            if (isolatedDashboards.Add(dashboard))
            {
                var loaded = (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), dashboard,
                    typeof(DashboardPage).GetMethod("Page_Loaded", PrivateInstance));
                dashboard.Loaded -= loaded;
                dashboard.Loaded += (sender, loadedArgs) =>
                {
                    Log("Dashboard Loaded fixture wrapper entered.");
                    typeof(DashboardPage).GetField("isCurrentPage", PrivateInstance).SetValue(dashboard, false);
                    // Preserve real typography subscription/layout initialization;
                    // only the live sampling/window activity source is isolated.
                    loaded(sender, loadedArgs);
                    Log("Dashboard original Loaded handler returned.");
                    typeof(DashboardPage).GetMethod("UnsubscribeWindow", PrivateInstance).Invoke(dashboard, null);
                    Require(!(bool)typeof(DashboardViewModel).GetField("overviewActive", PrivateInstance).GetValue(dashboard.ViewModel), "Real dashboard Loaded keeps isolated sampling inactive.");
                };
            }
            dashboardIntercepted = true;
        };
        NavigationService.Frame.Navigated += isolateDashboard;
        await Navigate(typeof(DashboardPage));
        Require(dashboardIntercepted, "Dashboard navigation passed through pre-Loaded isolation.");
        var dashboardPage = (DashboardPage)NavigationService.Frame.Content;
        PopulateDashboard(dashboardPage.ViewModel);
        await Settle();
        VerifyDashboard(dashboardPage);
        await VerifyNavigationStability(dashboardPage, generalPage);
        await VerifyAppearanceDiscovery(dashboardPage);
        await VerifyQuickAccessModes(dashboardPage);
        await CaptureDashboard(dashboardPage);

        // First-created plugin pages must use the current size, including native
        // styles instantiated after an earlier appearance update.
        await Navigate(typeof(GeneralPage));
        ActiveSize = 20;
        Element<NumberBox>(generalPage, "FontSizeSelector").Value = ActiveSize;
        await AppearanceSettled();
        messagesAfterTypography = Messages.Count;
        App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1100));
        await Navigate(typeof(AIHubPage));
        var aiPage = (AIHubPage)NavigationService.Frame.Content;
        await aiPage.ViewModel.AiHub.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Require(!aiPage.ViewModel.IsEnabled, "AI Hub remains disabled while inspecting typography.");
        aiPage.ViewModel.ActiveTabIndex = 0;
        await Settle();
        VerifyAiHubHeader(aiPage);
        VerifyText(Element<TextBlock>(aiPage, "AuditScoreText"), 30, "AI Hub score", mono: true);
        VerifyIcon(Element<FontIcon>(aiPage, "AiReadinessIcon"), 14, "AI Hub readiness icon");
        await CaptureShell("aihub-typography-full-shell.png");

        await Navigate(typeof(WingetPage));
        var wingetPage = (WingetPage)NavigationService.Frame.Content;
        Require(!wingetPage.ViewModel.IsEnabled, "Winget remains disabled while inspecting typography.");
        VerifyText(Element<TextBlock>(wingetPage, "WingetPageTitle"), 28, "Winget title");
        VerifyText(Element<TextBlock>(wingetPage, "WingetDescription"), 13, "Winget description");
        VerifyIcon(Descendants(Element<FrameworkElement>(wingetPage, "WorkspaceBadge")).OfType<FontIcon>().Single(), 16, "Winget workspace icon");
        wingetPage.ViewModel.ActiveTabIndex = 2;
        await Settle();
        VerifyWingetWorkspace(wingetPage);
        VerifyControl(Element<TextBox>(wingetPage, "DiscoverSearchBox"), 18, "Winget Discover TextBox");
        await CaptureShell("winget-typography-full-shell.png");
        wingetPage.ViewModel.SetPageActive(false);
        Require(Messages.Count == messagesAfterTypography, "Read-only page navigation emits no general/module settings IPC.");
        await Navigate(typeof(GeneralPage));
        ActiveSize = RequestedSize;
        Element<NumberBox>(generalPage, "FontSizeSelector").Value = ActiveSize;
        await AppearanceSettled();
        VerifyGeneralTypography(generalPage);
        await Navigate(typeof(WingetPage));
        await VerifyCachedLiveUpdates(generalPage, dashboardPage, aiPage, wingetPage);
        NavigationService.Frame.Navigated -= isolateDashboard;
        dashboardPage.ViewModel.Dispose();
        Require(ForbiddenMessages.Count == 0, "Typography checks do not restart Settings or check for updates.");
        EnsureNoWorker();
        Require(TypographyFailures.Count == 0, "Typography failures:\n" + string.Join("\n", TypographyFailures));
        Log("General, Dashboard, AI Hub, Winget: actual realized fonts, scaled sizes, icon fonts and monospace PASS");
    }

    private static async Task VerifyGeneral(GeneralPage page)
    {
        await VerifySettingsHeaderLayout(page);
        var vm = page.ViewModel;
        var family = Element<ComboBox>(page, "FontFamilySelector");
        var size = Element<NumberBox>(page, "FontSizeSelector");
        var appearance = Element<FrameworkElement>(page, "AppearanceAndBehaviorGroup");
        var restartNotices = Descendants(appearance).OfType<InfoBar>().Where(bar => !bar.IsClosable && bar.ActionButton != null).ToArray();
        Require(family.Items.Count == 2 && family.Items.OfType<ComboBoxItem>().Select(item => item.Content as string).SequenceEqual(new[] { "System UI", "Noto Sans SC" }),
            "Font selector contains exactly System UI and Noto Sans SC.");
        int initialFamily = RequestedFamily == "system" ? 0 : 1;
        Require(family.SelectedIndex == initialFamily && vm.FontFamilyIndex == initialFamily && Near(size.Value, RequestedSize) && Near(vm.UiFontSize, RequestedSize),
            "General selection and NumberBox reflect persisted startup values.");
        Require(size.Minimum == 12 && size.Maximum == 20 && size.SmallChange == 1, "Font size supports 12-20 in integer steps.");
        Require(!vm.AppearanceRestartRequired && restartNotices.All(bar => !bar.IsOpen || bar.Visibility == Visibility.Collapsed), "Live appearance settings do not show a restart notice.");
        VerifyAppearanceOrder(page);
        VerifyControl(family, 14, "General font ComboBox");
        VerifyControl(Element<ComboBox>(page, "Languages_ComboBox"), 14, "General language ComboBox");
        VerifyControl(size, 14, "General NumberBox");
        var fontCard = Element<CommunityToolkit.WinUI.Controls.SettingsCard>(page, "FontFamilyCard");
        foreach (var entry in new[] { (fontCard.Header?.ToString(), 14.0, "header"), (fontCard.Description?.ToString(), 12.0, "description") })
        {
            Require(!string.IsNullOrWhiteSpace(entry.Item1), "Font family card " + entry.Item3 + " is localized.");
            var cardText = Descendants(fontCard).OfType<TextBlock>().FirstOrDefault(text => text.Text == entry.Item1);
            Require(cardText != null, "Font family card " + entry.Item3 + " is realized.");
            VerifyText(cardText, entry.Item2, "General font card " + entry.Item3);
        }
        VerifyAppearanceControlAlignment(page);

        int pid = Environment.ProcessId;
        object viewModel = page.ViewModel;
        family.SelectedIndex = 1 - initialFamily;
        ActiveFamily = initialFamily == 0 ? "Noto Sans SC" : "system";
        await AppearanceSettled();
        Require(vm.FontFamilyIndex == 1 - initialFamily && !vm.AppearanceRestartRequired, "Changing the real font ComboBox applies immediately without a restart.");
        VerifyLastTypographyMessage(ActiveFamily, RequestedSize);
        VerifyGeneralTypography(page);
        family.SelectedIndex = initialFamily;
        ActiveFamily = RequestedFamily;
        await AppearanceSettled();
        VerifyGeneralTypography(page);

        int alternateSize = RequestedSize == 20 ? 14 : 20;
        size.Value = alternateSize;
        ActiveSize = alternateSize;
        await AppearanceSettled();
        Require(vm.UiFontSize == alternateSize && !vm.AppearanceRestartRequired, "Changing the real NumberBox applies immediately.");
        VerifyLastTypographyMessage(RequestedFamily, alternateSize);
        VerifyGeneralTypography(page);
        size.Value = RequestedSize;
        ActiveSize = RequestedSize;
        await AppearanceSettled();
        VerifyGeneralTypography(page);

        int beforeInvalid = Messages.Count;
        foreach (double invalid in new[] { double.NaN, 14.5 })
        {
            size.Value = invalid;
            await AppearanceSettled();
            Require(size.Value == RequestedSize && vm.UiFontSize == RequestedSize, "NumberBox rejects and restores invalid value " + invalid.ToString(CultureInfo.InvariantCulture));
        }
        foreach (double invalid in new[] { double.PositiveInfinity, double.NegativeInfinity, 11.0, 21.0, 19.25 })
        {
            vm.UiFontSize = invalid;
            await AppearanceSettled();
            Require(size.Value == RequestedSize && vm.UiFontSize == RequestedSize, "Rejected model value restores the real NumberBox: " + invalid.ToString(CultureInfo.InvariantCulture));
        }
        Require(Messages.Count == beforeInvalid && !vm.AppearanceRestartRequired, "Invalid font values neither emit IPC nor show a restart notice.");

        await SwitchLanguage(page, IsChinese ? "en-US" : "zh-CN");
        VerifyGeneralLocalization(page);
        await SwitchLanguage(page, RequestedLanguage);
        VerifyGeneralLocalization(page);
        Require(Environment.ProcessId == pid && ReferenceEquals(page, NavigationService.Frame.Content) && ReferenceEquals(viewModel, page.ViewModel),
            "Font, size and language changes keep the process, page and view model instances.");
        Require(restartNotices.All(bar => !bar.IsOpen || bar.Visibility == Visibility.Collapsed), "No obsolete restart notice appears after live changes.");
        await VerifyKeyboardAndAccessibility(page);
        Log("General live font/size/language round trips, exact choices, invalid-value restoration and captured IPC PASS");
    }

    private static async Task VerifyShellAiUsage(GeneralPage page)
    {
        await page.ViewModel.AiServices.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        var root = (FrameworkElement)App.GetSettingsWindow().Content;
        var shell = root as ShellPage ?? Descendants(root).OfType<ShellPage>().Single();
        var item = Element<NavigationViewItem>(shell, "AiUsageNavigationItem");
        var navigation = Element<NavigationView>(shell, "navigationView");
        var flyout = Element<Flyout>(shell, "AiUsageFlyout");
        var content = Element<FrameworkElement>(shell, "AiUsageContent");
        var engine = Kit.AiHub.Engine.AiHubEngine.ExistingEngine;
        var snapshotField = typeof(ShellPage).GetField("_aiUsageSnapshot", PrivateInstance);
        var refresh = typeof(ShellPage).GetMethod("RefreshAiUsageText", PrivateInstance);
        var stop = typeof(ShellPage).GetMethod("StopAiUsageObservation", PrivateInstance);
        object existingSnapshot = snapshotField.GetValue(shell);
        bool paneWasOpen = navigation.IsPaneOpen;
        ElementTheme originalTheme = root.RequestedTheme;
        object selected = navigation.SelectedItem;
        Require(!item.SelectsOnInvoked && navigation.FooterMenuItems.IndexOf(item) < navigation.FooterMenuItems.IndexOf(Element<NavigationViewItem>(shell, "GeneralNavigationItem")),
            "AI usage is a non-navigation footer action above Settings.");

        bool Field(string name) => (bool)typeof(ShellPage).GetField(name, PrivateInstance).GetValue(shell);
        async Task Settled() => await WaitUntil(() => !Field("_aiUsageWorkerRunning") && !Field("_aiUsageUiQueued"), "AI usage observation settled");
        int openedCount = 0;
        int closedCount = 0;
        var lifecycleTimer = Stopwatch.StartNew();
        void TraceLifecycle(string name) => Log($"SHELL_AI_USAGE_{name}: elapsedMs={lifecycleTimer.ElapsedMilliseconds}, isOpen={flyout.IsOpen}, loaded={content.IsLoaded}, active={Field("_aiUsageActive")}, windowVisible={App.GetSettingsWindow().AppWindow.IsVisible}, opened={openedCount}, closed={closedCount}");
        void OnOpening(object sender, object args) => TraceLifecycle("OPENING");
        void OnOpened(object sender, object args) { openedCount++; TraceLifecycle("OPENED"); }
        void OnClosing(object sender, object args) => TraceLifecycle("CLOSING");
        void OnClosed(object sender, object args) { closedCount++; TraceLifecycle("CLOSED"); }
        flyout.Opening += OnOpening;
        flyout.Opened += OnOpened;
        flyout.Closing += OnClosing;
        flyout.Closed += OnClosed;
        async Task OpenUsage(string description)
        {
            int before = openedCount;
            flyout.ShowAt(item);
            await WaitUntil(() => openedCount > before && flyout.IsOpen && content.IsLoaded && Field("_aiUsageActive"), description);
        }
        async Task CloseUsage(string description)
        {
            int before = closedCount;
            bool wasOpen = flyout.IsOpen;
            flyout.Hide();
            await WaitUntil(() => !flyout.IsOpen && (!wasOpen || closedCount > before), description);
        }
        try
        {
            await OpenUsage("AI usage flyout loaded");
            await Settled();
            Require(ReferenceEquals(engine, Kit.AiHub.Engine.AiHubEngine.ExistingEngine) && ReferenceEquals(selected, navigation.SelectedItem),
                "Opening usage neither creates/replaces the AI engine nor changes the current page.");
            if (engine != null)
            {
                Require(ReferenceEquals(typeof(ShellPage).GetField("_aiUsageStore", PrivateInstance).GetValue(shell), engine.Diagnostics),
                    "The flyout observes the existing shared diagnostics store.");
                long before = (long)typeof(ShellPage).GetField("_aiUsageRequestedVersion", PrivateInstance).GetValue(shell);
                typeof(Kit.AiHub.Storage.AiDiagnosticsStore).GetMethod("NotifyChanged", PrivateInstance).Invoke(engine.Diagnostics, null);
                await Settled();
                Require((long)typeof(ShellPage).GetField("_aiUsageRequestedVersion", PrivateInstance).GetValue(shell) > before,
                    "A real diagnostics notification refreshes the open flyout without polling.");
            }

            var window = App.GetSettingsWindow();
            int closedBeforeHide = closedCount;
            try
            {
                // Exercise the same hidden-but-retained window lifetime used by
                // background work, without starting any work or closing the app.
                window.AppWindow.Hide();
                await WaitUntil(() => !window.AppWindow.IsVisible, "Settings window hidden with AI usage open");
                Log($"SHELL_AI_USAGE_HIDE: windowVisible={window.AppWindow.IsVisible}, shellLoaded={shell.IsLoaded}, observerActive={Field("_aiUsageActive")}, storeAttached={typeof(ShellPage).GetField("_aiUsageStore", PrivateInstance).GetValue(shell) != null}");
                await WaitUntil(() => !Field("_aiUsageActive") && typeof(ShellPage).GetField("_aiUsageStore", PrivateInstance).GetValue(shell) == null,
                    "Hiding the Settings window closes the AI usage observation");
                Require(typeof(ShellPage).GetField("_aiUsageWindow", PrivateInstance).GetValue(shell) == null,
                    "Hiding Settings also releases the usage window event subscription.");
                await Settled();
                Require(ReferenceEquals(page, NavigationService.Frame.Content) && ReferenceEquals(engine, Kit.AiHub.Engine.AiHubEngine.ExistingEngine),
                    "Hiding the window retains its page and existing AI engine.");
            }
            finally
            {
                window.AppWindow.Show();
                window.Activate();
                await Settle();
            }

            // Subscription teardown is synchronous; the native close animation can
            // finish only after the window is visible again. Wait for this close
            // event before issuing a single new ShowAt request.
            await WaitUntil(() => closedCount > closedBeforeHide && !flyout.IsOpen,
                "The hidden usage flyout finishes closing after the window is restored");
            await OpenUsage("AI usage reopens after restoring the window");
            await Settled();
            Require(window.AppWindow.IsVisible && ReferenceEquals(engine, Kit.AiHub.Engine.AiHubEngine.ExistingEngine),
                "The restored window can observe usage again without replacing the service.");
            if (window.AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter presenter)
                throw new InvalidOperationException("The Settings smoke window must use its normal overlapped presenter.");
            int closedBeforeMinimize = closedCount;
            try
            {
                presenter.Minimize();
                await WaitUntil(() => presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized,
                    "Settings window minimized with AI usage open");
                await WaitUntil(() => !Field("_aiUsageActive") && typeof(ShellPage).GetField("_aiUsageStore", PrivateInstance).GetValue(shell) == null &&
                    typeof(ShellPage).GetField("_aiUsageWindow", PrivateInstance).GetValue(shell) == null,
                    "Minimizing Settings stops AI usage and releases both subscriptions");
                await Settled();
            }
            finally
            {
                presenter.Restore();
                window.Activate();
                await Settle();
            }
            await WaitUntil(() => closedCount > closedBeforeMinimize && !flyout.IsOpen,
                "The minimized usage flyout finishes closing after the window is restored");
            await OpenUsage("AI usage reopens after restoring from minimized");
            await Settled();
            await CloseUsage("The restored usage flyout closes");
            Require(!Field("_aiUsageActive") && typeof(ShellPage).GetField("_aiUsageStore", PrivateInstance).GetValue(shell) == null,
                "Closing the flyout releases its diagnostics subscription.");

            var fixture = new Kit.AiHub.Models.AiDiagnosticsSnapshot
            {
                RequestCount = 3, Succeeded = 1, Failed = 1, Partial = 1, RouteAttempts = 3,
                InputTokens = 1200, ReportedInputRoutes = 2, OutputTokens = 0, ReportedOutputRoutes = 0,
                CachedInputTokens = 0, ReportedCachedInputRoutes = 1, CacheWriteTokens = 0, ReportedCacheWriteRoutes = 0,
            };
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                foreach (int width in new[] { 1280, 760 })
                {
                    App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1100));
                    navigation.IsPaneOpen = width == 1280;
                    await Settle();
                    await OpenUsage("AI usage fixture flyout loaded");
                    await Settled();
                    // Render-only fixture: no diagnostic records, settings or model calls are written.
                    stop.Invoke(shell, null);
                    snapshotField.SetValue(shell, fixture);
                    refresh.Invoke(shell, null);
                    await Settle();
                    Require(Element<TextBlock>(shell, "AiUsageInputText").Text == 1200L.ToString("N0", CultureInfo.CurrentCulture), "Reported input tokens retain their actual total.");
                    Require(Element<TextBlock>(shell, "AiUsageOutputText").Text == (IsChinese ? "未上报" : "Not reported") &&
                        Element<TextBlock>(shell, "AiUsageCacheReadText").Text == 0L.ToString("N0", CultureInfo.CurrentCulture),
                        "Missing output is distinct from a reported cache-read zero.");
                    Require(Equals(item.Content, IsChinese ? "AI 用量" : "AI usage"), "The usage action uses the current language.");
                    Require(Element<TextBlock>(shell, "AiUsageInputCoverageText").Text == (IsChinese ? "3 次调用中有 2 次上报" : "Reported by 2 of 3 calls") &&
                        Element<TextBlock>(shell, "AiUsageOutputCoverageText").Text == (IsChinese ? "3 次调用中有 0 次上报" : "Reported by 0 of 3 calls"),
                        "Each token value shows its own reported-call coverage.");
                    Require(!flyout.ShouldConstrainToRootBounds, "AI usage flyout detaches into a native backdrop-capable popup.");
                    Require(flyout.Placement == FlyoutPlacementMode.TopEdgeAlignedLeft, "AI usage flyout anchors on the left adjacent to the AI usage navigation item.");
                    if (Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported() || Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
                    {
                        Require(flyout.SystemBackdrop is not null, "AI usage flyout attaches a translucent system backdrop when supported.");
                    }
                    foreach (var text in Descendants(content).OfType<TextBlock>().Where(text => text.Visibility == Visibility.Visible && !string.IsNullOrEmpty(text.Text)))
                    {
                        double attached = TypographyResources.GetBaseFontSize(text);
                        double baseline = attached > 0 ? attached :
                            text.Name.EndsWith("CoverageText", StringComparison.Ordinal) ? 12 :
                            RuntimeLocalization.GetUid(text) == "Shell_AiUsageTitle" ? 18 : 14;
                        VerifyText(text, baseline, "Shell AI usage " + text.Name);
                        var bounds = text.TransformToVisual(content).TransformBounds(new Windows.Foundation.Rect(0, 0, text.ActualWidth, text.ActualHeight));
                        Require(bounds.Left >= -1 && bounds.Right <= content.ActualWidth + 1, "Usage text fits its flyout at " + theme + "/" + width);
                    }
                    await CaptureShell($"shell-ai-usage-{theme}-{width}-{ActiveSize}.png", content);
                    if (theme == ElementTheme.Light && width == 1280 && int.TryParse(Environment.GetEnvironmentVariable("KIT_SHELL_AI_USAGE_HOLD_SECONDS"), out int hold) && hold > 0)
                    {
                        Log("SHELL_AI_USAGE_NATIVE_CAPTURE_READY");
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(hold, 60)));
                    }
                    await CloseUsage("The usage fixture flyout closes");
                }
            }
        }
        finally
        {
            flyout.Hide();
            flyout.Opening -= OnOpening;
            flyout.Opened -= OnOpened;
            flyout.Closing -= OnClosing;
            flyout.Closed -= OnClosed;
            stop.Invoke(shell, null);
            snapshotField.SetValue(shell, existingSnapshot);
            root.RequestedTheme = originalTheme;
            navigation.IsPaneOpen = paneWasOpen;
            App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1100));
        }
        Log("Shell AI usage: existing engine only, event refresh, hide/minimize/restore, detach, unknown/zero tokens and responsive flyout PASS");
    }

    private static async Task VerifySettingsHeaderLayout(GeneralPage page)
    {
        var surface = Element<SettingsPageControl>(page, "GeneralSettingsSurface");
        var subtitle = (FrameworkElement)surface.FindName("CompactHeaderSubtitlePanel");
        var header = (Grid)surface.FindName("PageHeaderGrid");
        var title = (FrameworkElement)surface.FindName("Header");
        var action = (FrameworkElement)surface.FindName("HeaderActionPresenter");
        Require(subtitle.Visibility == Visibility.Collapsed && header.RowSpacing == 0 &&
            Math.Abs(header.ActualHeight - Math.Max(title.ActualHeight, action.ActualHeight)) <= 1,
            "A settings title without introduction or links does not reserve an empty subtitle row.");
        foreach (var group in Descendants(page).OfType<Kit.Settings.UI.Controls.SettingsGroup>())
        {
            var heading = Descendants(group).OfType<TextBlock>().First(text => text.Name == "HeaderPresenter" && AutomationProperties.GetHeadingLevel(text) == AutomationHeadingLevel.Level2);
            var description = Descendants(group).OfType<ContentPresenter>().First(item => item.Name == "DescriptionPresenter");
            Require(heading.Text == group.Header && AutomationProperties.GetHeadingLevel(heading) == AutomationHeadingLevel.Level2 &&
                description.Visibility == Visibility.Collapsed && group.ItemsPanelRoot.ChildrenTransitions?.Count is not > 0,
                "Settings groups use accessible section headings without redundant introductions or moving rows.");
        }

        string originalDescription = surface.ModuleDescription;
        var temporaryLink = new PageLink { Text = "Details", Link = new Uri("https://example.com/") };
        try
        {
            surface.ModuleDescription = "Settings details";
            await Settle();
            Require(subtitle.Visibility == Visibility.Visible && header.ActualHeight > title.ActualHeight + 6,
                "Shared compact headers retain descriptions when the page provides one.");
            surface.ModuleDescription = originalDescription;
            surface.PrimaryLinks.Add(temporaryLink);
            await Settle();
            Require(subtitle.Visibility == Visibility.Visible &&
                ((ItemsControl)surface.FindName("CompactPrimaryLinksControl")).Visibility == Visibility.Visible,
                "A primary link keeps its subtitle row when there is no description.");
        }
        finally
        {
            surface.ModuleDescription = originalDescription;
            surface.PrimaryLinks.Remove(temporaryLink);
        }
        await Settle();
        Require(subtitle.Visibility == Visibility.Collapsed && Math.Abs(header.ActualHeight - Math.Max(title.ActualHeight, action.ActualHeight)) <= 1,
            "Removing the last subtitle item restores the title-only height.");
    }

    private static void LogGeneralRowAlignment(GeneralPage page)
    {
        foreach (string name in new[] { "FontFamilyCard", "ShowSystemTrayIconCard" })
        {
            var control = Element<Control>(page, name);
            var card = control as CommunityToolkit.WinUI.Controls.SettingsCard ?? Descendants(control).OfType<CommunityToolkit.WinUI.Controls.SettingsCard>().FirstOrDefault();
            if (card == null) continue;
            var header = Descendants(card).OfType<TextBlock>().FirstOrDefault(text => text.Text == card.Header?.ToString());
            var icon = Descendants(card).OfType<FontIcon>().FirstOrDefault();
            Log($"General row geometry {name}: padding={card.Padding}, headerX={header?.TransformToVisual(page).TransformPoint(new()).X}, iconX={icon?.TransformToVisual(page).TransformPoint(new()).X}");
        }
    }

    private static void VerifyGeneralTypography(GeneralPage page)
    {
        VerifyAppearanceControlAlignment(page);
        VerifyControl(Element<ComboBox>(page, "FontFamilySelector"), 14, "Live General font ComboBox");
        VerifyControl(Element<ComboBox>(page, "Languages_ComboBox"), 14, "Live General language ComboBox");
        VerifyControl(Element<NumberBox>(page, "FontSizeSelector"), 14, "Live General NumberBox");
        var card = Element<CommunityToolkit.WinUI.Controls.SettingsCard>(page, "FontFamilyCard");
        foreach (var entry in new[] { (card.Header?.ToString(), 14.0), (card.Description?.ToString(), 12.0) })
        {
            var text = Descendants(card).OfType<TextBlock>().FirstOrDefault(item => item.Text == entry.Item1);
            Require(text != null, "Live font card text is realized.");
            VerifyText(text, entry.Item2, "Live General font card text");
        }
        VerifyText(Descendants(page).OfType<TextBlock>().First(text => (int)AutomationProperties.GetHeadingLevel(text) == 1), 28, "Live General title");
        Require(TypographyResources.AppliedFontFamily == ActiveFamily && TypographyResources.AppliedFontSize == ActiveSize, "Runtime reports the actual selected typography.");
    }

    private static async Task AppearanceSettled()
    {
        await AppearanceService.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await Settle();
    }

    private static bool IsChinese => ActiveLanguage.Equals("zh-CN", StringComparison.OrdinalIgnoreCase);
    private static string Expected(string key) => ExpectedStrings.RootElement.GetProperty(IsChinese ? "zh-CN" : "en-us").GetProperty(key).GetString();

    private static async Task SwitchLanguage(GeneralPage page, string language)
    {
        var combo = Element<ComboBox>(page, "Languages_ComboBox");
        var theme = Element<ComboBox>(page, "ThemeSelector");
        int originalThemeIndex = theme.SelectedIndex;
        object originalThemeItem = theme.SelectedItem;
        var root = (FrameworkElement)App.GetSettingsWindow().Content;
        ElementTheme originalTheme = root.RequestedTheme;
        object languageItems = combo.ItemsSource;
        string languageDisplayMember = combo.DisplayMemberPath;
        var languageTemplate = combo.ItemTemplate;
        int themeSelectionChanges = 0;
        SelectionChangedEventHandler themeChanged = (_, _) => themeSelectionChanges++;
        theme.SelectionChanged += themeChanged;
        try
        {
            int index = page.ViewModel.Languages.Select((item, position) => (item, position)).Single(entry => entry.item.Tag.Equals(language, StringComparison.OrdinalIgnoreCase)).position;
            combo.SelectedIndex = index;
            await AppearanceSettled();
            ActiveLanguage = RuntimeLocalization.EffectiveLanguage;
            Require(RuntimeLocalization.AppliedLanguage.Equals(language, StringComparison.OrdinalIgnoreCase), "Runtime applies the requested language without restarting.");
            Require(RuntimeLocalization.EffectiveLanguage.Equals(string.IsNullOrEmpty(language) ? StartupEffectiveLanguage : language, StringComparison.OrdinalIgnoreCase),
                "Runtime resolves the selected UI culture, including system default, in the current process.");
            Require(!page.ViewModel.AppearanceRestartRequired, "Language changes never request restart.");
            Require(theme.SelectedIndex == originalThemeIndex && ReferenceEquals(theme.SelectedItem, originalThemeItem) &&
                page.ViewModel.ThemeIndex == originalThemeIndex && root.RequestedTheme == originalTheme && themeSelectionChanges == 0,
                "Changing language preserves theme selection identity, actual theme and SelectionChanged count.");
            Require(ReferenceEquals(combo.ItemsSource, languageItems) && combo.DisplayMemberPath == languageDisplayMember && ReferenceEquals(combo.ItemTemplate, languageTemplate),
                "Localized text refresh leaves the language ComboBox ItemsSource and display template intact.");
            VerifyThemeSelection(page);
        }
        finally { theme.SelectionChanged -= themeChanged; }
    }

    private static void VerifyThemeSelection(GeneralPage page)
    {
        var theme = Element<ComboBox>(page, "ThemeSelector");
        string[] keys = { "Radio_Theme_Dark.Content", "Radio_Theme_Light.Content", "Radio_Theme_Default.Content" };
        for (int index = 0; index < keys.Length; index++)
            Require(Equals(((ComboBoxItem)theme.Items[index]).Content, Expected(keys[index])), "Theme choice content updates to the selected language.");
        var presenter = Descendants(theme).OfType<ContentPresenter>().Single(item => item.Name == "ContentPresenter" && ReferenceEquals(NearestControl(item), theme));
        Require(Equals(presenter.Content, Expected(keys[theme.SelectedIndex])), "Closed theme selector displays its selected choice in the current language.");
        Log($"Closed ThemeSelector index={theme.SelectedIndex}, text={presenter.Content} PASS");
    }

    private static Control NearestControl(DependencyObject element)
    {
        for (var owner = VisualTreeHelper.GetParent(element); owner != null; owner = VisualTreeHelper.GetParent(owner))
            if (owner is Control control) return control;
        return null;
    }

    private static void VerifyGeneralLocalization(GeneralPage page)
    {
        var card = Element<CommunityToolkit.WinUI.Controls.SettingsCard>(page, "FontFamilyCard");
        Require(Equals(card.Header, Expected("General_FontFamily.Header")) && Equals(card.Description, Expected("General_FontFamily.Description")), "Actual x:Uid font card text matches the selected language.");
        var language = Element<CommunityToolkit.WinUI.Controls.SettingsCard>(page, "LanguageHeader");
        Require(Equals(language.Header, Expected("LanguageHeader.Header")), "Language card x:Uid header changes immediately.");
        var surface = Element<SettingsPageControl>(page, "GeneralSettingsSurface");
        Require(surface.ModuleTitle == Expected("General.ModuleTitle") && string.IsNullOrEmpty(surface.ModuleDescription), "General title updates without a redundant page introduction.");
        var title = Descendants(surface).OfType<TextBlock>().First(text => (int)AutomationProperties.GetHeadingLevel(text) == 1);
        Require(title.Text == Expected("General.ModuleTitle"), "Visible General title reflects the changed control property.");
        VerifyShellLocalization();
    }

    private static void VerifyShellLocalization()
    {
        var root = (FrameworkElement)App.GetSettingsWindow().Content;
        var shell = root as ShellPage ?? Descendants(root).OfType<ShellPage>().Single();
        Require(Equals(Element<NavigationViewItem>(shell, "DashboardNavigationItem").Content, Expected("Shell_Dashboard.Content")), "Existing Home navigation label changes immediately.");
        Require(Equals(Element<NavigationViewItem>(shell, "GeneralNavigationItem").Content, Expected("Shell_Settings.Content")), "Existing Settings navigation label changes immediately.");
        Require(Element<AutoSuggestBox>(shell, "SearchBox").PlaceholderText == Expected("Shell_SearchBox.PlaceholderText"), "Existing search box placeholder changes immediately.");
        Require(App.GetSettingsWindow().Title == Expected("SettingsWindow_Title"), "Native window title changes language in the same process.");
        Require(Element<TitleBar>(shell, "AppTitleBar").Title == Expected("SettingsWindow_Title"), "Visible title-bar title changes language in the same process.");
        string fontHeader = Expected("General_FontFamily.Header");
        var results = SearchIndexService.Search(fontHeader);
        Require(results.Any(result => result.ElementUid == "General_FontFamily" && result.Header == fontHeader), "Search returns the font setting with its newly localized header.");
    }

    private static async Task VerifyKeyboardAndAccessibility(GeneralPage page)
    {
        foreach (string name in new[] { "Languages_ComboBox", "FontFamilySelector", "FontSizeSelector", "SettingsSectionNavigation" })
        {
            var control = Element<Control>(page, name);
            Require(control.IsEnabled && control.Focus(FocusState.Keyboard), "Keyboard can focus " + name);
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(control);
            Require(peer != null && !string.IsNullOrWhiteSpace(peer.GetName()), "Accessibility exposes a localized name for " + name);
        }
        var navigation = Element<DropDownButton>(page, "SettingsSectionNavigation");
        var navigationPeer = FrameworkElementAutomationPeer.CreatePeerForElement(navigation);
        if (navigationPeer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
            invoke.Invoke();
        else if (navigationPeer.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider expand)
            expand.Expand();
        else
            throw new InvalidOperationException("Section navigation must expose an accessible invoke or expand action.");
        await Settle();
        Require(navigation.Flyout is MenuFlyout menu && menu.Items.Count > 0, "Keyboard-accessible section button opens its real section menu.");
        navigation.Flyout.Hide();
        await Settle();
    }

    private static async Task VerifyCachedLiveUpdates(GeneralPage general, DashboardPage dashboard, AIHubPage ai, WingetPage winget)
    {
        int pid = Environment.ProcessId;
        var generalVm = general.ViewModel;
        var dashboardVm = dashboard.ViewModel;
        var systemSnapshot = dashboardVm.SystemDetails;
        var aiVm = ai.ViewModel;
        var wingetVm = winget.ViewModel;
        var originalSearchQuery = wingetVm.SearchQuery;
        wingetVm.SearchQuery = "Typography smoke unsent package query";
        await Navigate(typeof(AIHubPage));
        int originalAiTab = aiVm.ActiveTabIndex;
        aiVm.ActiveTabIndex = 2;
        await WaitUntil(() => aiVm.AiHub.PoliciesLoaded && !aiVm.AiHub.IsBusy, "local AI policy editor");
        await Settle();
        var policyEditor = Element<TextBox>(ai, "AiTaskPolicyContentBox");
        Require(!policyEditor.IsReadOnly && aiVm.AiHub.CanEditPolicies, "Local policy drafts are editable while AI service stays disabled.");
        string originalPolicy = policyEditor.Text;
        string policyDraft = "# Typography smoke draft / 未保存的界面测试草稿\nThis local text is never saved or executed.\n";
        policyEditor.Text = policyDraft;
        await Settle();
        Log($"Policy fixture after entry: textMatches={policyEditor.Text == policyDraft}, modelMatches={aiVm.AiHub.CurrentPolicyContent == policyEditor.Text}, dirty={aiVm.AiHub.IsCurrentPolicyDirty}, expectedLength={policyDraft.Length}, actualLength={policyEditor.Text.Length}");
        Require(policyEditor.Text.Replace("\r\n", "\n").Replace('\r', '\n') == policyDraft,
            "The real policy editor accepts the complete multiline fixture draft.");
        // WinUI TextBox normalizes line endings; retain the actual accepted
        // editor text as the exact value that every later refresh must preserve.
        policyDraft = policyEditor.Text;
        Require(aiVm.AiHub.IsCurrentPolicyDirty, "The real local editor retains an unsaved draft without enabling workers.");

        await Navigate(typeof(DashboardPage));
        Require(ReferenceEquals(dashboard, NavigationService.Frame.Content), "Dashboard is cached before live refresh checks.");
        var dashboardScroll = Element<ScrollViewer>(dashboard, "MainScrollViewer");
        dashboardScroll.ChangeView(null, Math.Min(180, dashboardScroll.ScrollableHeight), null, true);
        await Settle();
        double dashboardOffset = dashboardScroll.VerticalOffset;

        await Navigate(typeof(GeneralPage));
        Require(ReferenceEquals(general, NavigationService.Frame.Content), "General is cached before live refresh checks.");
        var endpoint = generalVm.AiServices.MainEndpoint;
        string originalUrl = endpoint.BaseUrl;
        string originalModel = endpoint.Model;
        bool originallyDirty = generalVm.AiServices.HasUnsavedEndpointChanges;
        var expander = Element<CommunityToolkit.WinUI.Controls.SettingsExpander>(general, "ShowSystemTrayIconCard");
        bool originallyExpanded = expander.IsExpanded;
        expander.IsExpanded = true;
        await Settle();
        // Endpoint models remain alive while their service-disabled editor is
        // collapsed; the visible policy editor above supplies the real form draft.
        endpoint.BaseUrl = "https://typography-smoke.invalid/v1";
        endpoint.Model = "unsaved typography draft";
        Require(generalVm.AiServices.HasUnsavedEndpointChanges, "Endpoint fixture remains an unsaved in-memory draft.");
        var generalScroll = Descendants(Element<FrameworkElement>(general, "GeneralSettingsSurface")).OfType<ScrollViewer>()
            .Single(scroll => AutomationProperties.GetAutomationId(scroll) == "PageScrollViewer");
        generalScroll.ChangeView(null, Math.Min(240, generalScroll.ScrollableHeight), null, true);
        await Settle();
        double generalOffset = generalScroll.VerticalOffset;
        var root = (FrameworkElement)App.GetSettingsWindow().Content;
        var shell = root as ShellPage ?? Descendants(root).OfType<ShellPage>().Single();
        var search = Element<AutoSuggestBox>(shell, "SearchBox");
        string searchDraft = "unsent settings search";
        search.Text = searchDraft;

        try
        {
            ActiveFamily = RequestedFamily == "system" ? "Noto Sans SC" : "system";
            Element<ComboBox>(general, "FontFamilySelector").SelectedIndex = ActiveFamily == "system" ? 0 : 1;
            await AppearanceSettled();
            VerifyDraftAndIdentity();
            ActiveSize = RequestedSize == 20 ? 14 : 20;
            Element<NumberBox>(general, "FontSizeSelector").Value = ActiveSize;
            await AppearanceSettled();
            VerifyDraftAndIdentity();
            await SwitchLanguage(general, IsChinese ? "en-US" : "zh-CN");
            Log($"Cached live update: family={ActiveFamily}, base={ActiveSize}, language={ActiveLanguage}");
            VerifyDraftAndIdentity();
            VerifyGeneralTypography(general);
            VerifyGeneralLocalization(general);

            // Clear only the temporary shell search before screenshots; the draft
            // preservation assertion above has already exercised the live refresh.
            search.Text = string.Empty;
            generalScroll.ChangeView(null, 0, null, true);
            await Settle();
            await CaptureGeneral(general, "-live-switched");
            await Navigate(typeof(DashboardPage));
            Require(ReferenceEquals(dashboard, NavigationService.Frame.Content) && ReferenceEquals(dashboardVm, dashboard.ViewModel) && ReferenceEquals(systemSnapshot, dashboard.ViewModel.SystemDetails),
                "Cached Dashboard page, view model and telemetry snapshot survive live appearance changes.");
            Require(Math.Abs(dashboardScroll.VerticalOffset - Math.Min(dashboardOffset, dashboardScroll.ScrollableHeight)) < 3,
                "Cached Dashboard keeps its scroll offset after live appearance changes.");
            VerifyDashboard(dashboard);
            var dashboardTitle = Descendants(dashboard).OfType<TextBlock>().First(text => (int)AutomationProperties.GetHeadingLevel(text) == 1);
            Require(dashboardTitle.Text == Expected("DashboardTitle.Text"), "Cached Dashboard x:Uid title updates to the selected language.");
            dashboardScroll.ChangeView(null, 0, null, true);
            await Settle();
            await CaptureDashboard(dashboard, "-live-switched");

            await Navigate(typeof(AIHubPage));
            Require(ReferenceEquals(ai, NavigationService.Frame.Content) && ReferenceEquals(aiVm, ai.ViewModel) && aiVm.ActiveTabIndex == 2, "Cached AI Hub state and selected policy tab survive live changes.");
            Require(policyEditor.Text == policyDraft && aiVm.AiHub.IsCurrentPolicyDirty, "The actual policy editor keeps its unsaved text after live language/font refresh.");
            Require(Element<TextBlock>(ai, "AiHubPageTitle").Text == Expected("AIHub_PageTitle.Text"), "Cached AI Hub localized title refreshes.");
            VerifyAiHubHeader(ai);
            VerifyText(Element<TextBlock>(ai, "AuditScoreText"), 30, "Live cached AI Hub monospace score", mono: true);
            await CaptureShell("aihub-live-switched-full-shell.png");

            await Navigate(typeof(WingetPage));
            Require(ReferenceEquals(winget, NavigationService.Frame.Content) && ReferenceEquals(wingetVm, winget.ViewModel) && wingetVm.ActiveTabIndex == 2 && wingetVm.SearchQuery == "Typography smoke unsent package query",
                "Cached Winget tab and unsent search survive live refresh.");
            VerifyText(Element<TextBlock>(winget, "WingetPageTitle"), 28, "Live cached Winget title");
            VerifyText(Element<TextBlock>(winget, "WingetDescription"), 13, "Live cached Winget description");
            VerifyWingetWorkspace(winget);
            VerifyControl(Element<TextBox>(winget, "DiscoverSearchBox"), 18, "Live cached Winget search");
            await CaptureShell("winget-live-switched-full-shell.png");

            await Navigate(typeof(GeneralPage));
            Require(ReferenceEquals(general, NavigationService.Frame.Content) && ReferenceEquals(generalVm, general.ViewModel), "General remains the same cached page and view model after plugin visits.");
            Require(endpoint.BaseUrl == "https://typography-smoke.invalid/v1" && endpoint.Model == "unsaved typography draft" && expander.IsExpanded,
                "Unsaved endpoint model and General expansion remain intact after plugin visits.");
            ActiveFamily = RequestedFamily;
            Element<ComboBox>(general, "FontFamilySelector").SelectedIndex = ActiveFamily == "system" ? 0 : 1;
            ActiveSize = RequestedSize;
            Element<NumberBox>(general, "FontSizeSelector").Value = ActiveSize;
            await AppearanceSettled();
            await SwitchLanguage(general, RequestedLanguage);
            VerifyGeneralTypography(general);
            VerifyGeneralLocalization(general);
            await Navigate(typeof(DashboardPage));
            App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1100));
            await Settle();
            VerifyDashboard(dashboard);
            VerifyDashboardLayout(dashboard, 1280);
            Require(Environment.ProcessId == pid, "All live changes retain the original Settings process.");
            Require(ForbiddenMessages.Count == 0, "Live changes invoke no restart or update actions: " + string.Join(" | ", ForbiddenMessages));
            EnsureNoWorker();
            Log("Live changes preserve process/page/VM identities, unsaved forms, expansion, scroll and cached plugin state PASS");
        }
        finally
        {
            // Return fixture drafts to their original in-memory content. No save,
            // test-connection, package search, worker or restart action is invoked.
            endpoint.BaseUrl = originalUrl;
            endpoint.Model = originalModel;
            typeof(AiHubViewModel).GetProperty(nameof(AiHubViewModel.HasUnsavedEndpointChanges)).SetValue(generalVm.AiServices, originallyDirty);
            expander.IsExpanded = originallyExpanded;
            aiVm.AiHub.CurrentPolicyContent = originalPolicy;
            aiVm.ActiveTabIndex = originalAiTab;
            wingetVm.SearchQuery = originalSearchQuery;
            wingetVm.SetPageActive(false);
            search.Text = string.Empty;
        }

        void VerifyDraftAndIdentity()
        {
            Log($"Policy fixture live check: textMatches={policyEditor.Text == policyDraft}, modelMatches={aiVm.AiHub.CurrentPolicyContent == policyEditor.Text}, dirty={aiVm.AiHub.IsCurrentPolicyDirty}, expectedLength={policyDraft.Length}, actualLength={policyEditor.Text.Length}");
            Require(Environment.ProcessId == pid && ReferenceEquals(general, NavigationService.Frame.Content) && ReferenceEquals(generalVm, general.ViewModel) &&
                ReferenceEquals(endpoint, generalVm.AiServices.MainEndpoint), "Live refresh keeps the current page, view model and endpoint objects.");
            Require(endpoint.BaseUrl == "https://typography-smoke.invalid/v1" && endpoint.Model == "unsaved typography draft" && generalVm.AiServices.HasUnsavedEndpointChanges && expander.IsExpanded,
                "Live refresh retains endpoint drafts and General expansion state.");
            Require(policyEditor.Text == policyDraft && aiVm.AiHub.IsCurrentPolicyDirty, "Live refresh retains the cached actual policy editor draft.");
            Require(search.Text == searchDraft, "Changing appearance/language retains the current unsent settings search.");
            Require(Math.Abs(generalScroll.VerticalOffset - Math.Min(generalOffset, generalScroll.ScrollableHeight)) < 3,
                "Live refresh retains the current General scroll offset.");
        }
    }

    private static async Task VerifyNativeControls(GeneralPage page)
    {
        // Real controls attach to the page and resolve native templates/styles.
        // They are removed before screenshots and never write settings.
        var surface = (SettingsPageControl)page.Content;
        var host = (Grid)surface.Content;
        var probes = new StackPanel { Width = 360, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Opacity = 0.01 };
        var plain = new TextBlock { Text = "Default text / 默认文字" };
        var body = new TextBlock { Text = "Body / 正文", Style = (Style)Application.Current.Resources["BodyTextBlockStyle"] };
        var strong = new TextBlock { Text = "Strong / 加粗正文", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        var title = new TextBlock { Text = "Title / 标题", Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] };
        var input = new TextBox { Text = "Input / 文本输入" };
        var combo = new ComboBox { ItemsSource = new[] { "Selected / 已选择" }, SelectedIndex = 0 };
        foreach (UIElement item in new UIElement[] { plain, body, strong, title, input, combo }) probes.Children.Add(item);
        host.Children.Add(probes);
        try
        {
            await Settle();
            Require(probes.IsLoaded && plain.ActualHeight > 0 && input.ActualHeight > 0 && combo.ActualHeight > 0, "Native probes are realized and measured.");
            VerifyText(plain, 14, "Native default TextBlock");
            VerifyText(body, 14, "Native BodyTextBlockStyle");
            VerifyText(strong, 14, "Native BodyStrongTextBlockStyle");
            VerifyText(title, 28, "Native TitleTextBlockStyle");
            Require(strong.FontWeight.Weight >= 600, "BodyStrong preserves its native semibold weight.");
            VerifyControl(input, 14, "Native TextBox");
            VerifyControl(combo, 14, "Native ComboBox");
            var selectedText = Descendants(combo).OfType<TextBlock>().FirstOrDefault(text => text.Text == "Selected / 已选择");
            if (selectedText != null)
            {
                VerifyText(selectedText, 14, "Native ComboBox selected text");
            }
            else
            {
                // WinUI can render a string directly in ContentPresenter without
                // creating a child TextBlock. Inspect that actual text host too.
                var presenter = Descendants(combo).OfType<ContentPresenter>().FirstOrDefault(item => Equals(item.Content, combo.SelectedItem));
                Require(presenter != null && presenter.ActualHeight > 0, "ComboBox selected text presenter is realized.");
                Require(presenter.FontFamily.Source == ExpectedFamily && Near(presenter.FontSize, Scaled(14)), "ComboBox selected text presenter inherits selected typography.");
                Log($"Native ComboBox selected presenter: {presenter.FontFamily.Source}, {presenter.FontSize.ToString(CultureInfo.InvariantCulture)}px PASS");
            }
        }
        finally { host.Children.Remove(probes); }
    }

    private static async Task CaptureGeneral(GeneralPage page, string suffix = "")
    {
        var navigation = Descendants((FrameworkElement)App.GetSettingsWindow().Content).OfType<NavigationView>().Single();
        bool paneWasOpen = navigation.IsPaneOpen;
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                ((FrameworkElement)App.GetSettingsWindow().Content).RequestedTheme = theme;
                foreach (int width in new[] { 1280, 760 })
                {
                    App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1100));
                    await Settle();
                    navigation.IsPaneOpen = width == 1280;
                    await Settle();
                    Require(navigation.IsPaneOpen == (width == 1280), $"General capture pane matches {width}px fixture.");
                    foreach (var scroll in Descendants(Element<FrameworkElement>(page, "GeneralSettingsSurface")).OfType<ScrollViewer>().Where(scroll => scroll.ViewportWidth > 0))
                        Require(scroll.ExtentWidth <= scroll.ViewportWidth + 1, $"General has no horizontal overflow at {theme}/{width}.");
                    VerifyAppearanceOrder(page);
                    VerifyAppearanceControlAlignment(page);
                    VerifyControl(Element<ComboBox>(page, "FontFamilySelector"), 14, "General font at " + theme + "/" + width);
                    await CaptureShell($"general-{theme}-{width}{suffix}-full-shell.png");
                }
            }
        }
        finally { navigation.IsPaneOpen = paneWasOpen; }
    }

    private static async Task VerifyBundledFontLoading()
    {
        const string sample = "Kit typography ABCDEFG abcdefg 0123456789 中文界面字号验证";
        const string notoSource = "ms-appx:///Assets/Fonts/NotoSansSC-Variable.ttf#Noto Sans SC";
        const string missingSource = "ms-appx:///Assets/Fonts/TypographySmoke-Missing.ttf#Typography Smoke Missing Font";
        var stack = new StackPanel { Spacing = 12 };
        var sampleHost = new Border
        {
            Width = 1000,
            Padding = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RequestedTheme = ElementTheme.Light,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            Child = stack,
            IsHitTestVisible = false,
        };
        var samples = new List<TextBlock>();
        foreach (var entry in new[] { ("Bundled Noto Sans SC", notoSource), ("System UI", "XamlAutoFontFamily"), ("Missing font URI: fallback control", missingSource) })
        {
            stack.Children.Add(new TextBlock { Text = entry.Item1, FontFamily = new FontFamily("XamlAutoFontFamily"), FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black) });
            var text = new TextBlock
            {
                Text = sample,
                FontFamily = new FontFamily(entry.Item2),
                FontSize = 20,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Left,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
            };
            samples.Add(text);
            stack.Children.Add(text);
        }
        // Keep this independent glyph-loading comparison outside registered
        // product roots; normal page scans intentionally unify UI font families.
        var comparisonWindow = new Window { Title = "Kit font loading verification", Content = sampleHost };
        try
        {
            comparisonWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1060, 360));
            comparisonWindow.Activate();
            await WaitUntil(() => sampleHost.IsLoaded, "independent font comparison window");
            await Settle();
            Require(samples[0].FontFamily.Source == notoSource && samples[1].FontFamily.Source == new FontFamily("XamlAutoFontFamily").Source &&
                samples[2].FontFamily.Source == missingSource, "Independent font comparison retains its three explicit families.");
            foreach (var text in samples) text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double noto = samples[0].DesiredSize.Width;
            double system = samples[1].DesiredSize.Width;
            double missing = samples[2].DesiredSize.Width;
            Log(FormattableString.Invariant($"Actual mixed-text measurements at 20px: Noto={noto:F3}, System={system:F3}, missing-URI fallback={missing:F3}"));
            Require(samples.All(text => text.IsLoaded && text.ActualHeight > 0), "All font comparison samples are realized.");
            Require(Math.Abs(noto - system) > 1 && Math.Abs(noto - missing) > 1,
                "Bundled Noto font must produce different real glyph metrics from System UI and an absent bundled font fallback.");
            sampleHost.UpdateLayout();
            await CaptureShell("bundled-font-loading-comparison.png", sampleHost);
            Log("Bundled Noto actual glyph measurement differs from both fallback controls PASS");
        }
        finally { comparisonWindow.Close(); }
    }

    private static void VerifyAppearanceOrder(GeneralPage page)
    {
        var language = Element<FrameworkElement>(page, "LanguageHeader");
        var family = Element<FrameworkElement>(page, "FontFamilyCard");
        var size = Element<FrameworkElement>(page, "FontSizeCard");
        double languageY = language.TransformToVisual(page).TransformPoint(new()).Y;
        double familyY = family.TransformToVisual(page).TransformPoint(new()).Y;
        double sizeY = size.TransformToVisual(page).TransformPoint(new()).Y;
        Require(language.ActualHeight > 0 && family.ActualHeight > 0 && size.ActualHeight > 0 &&
            languageY + language.ActualHeight <= familyY + 1 && familyY + family.ActualHeight <= sizeY + 1,
            "General cards follow Language > Font family > Font size without overlap.");
    }

    private static void VerifyAppearanceControlAlignment(GeneralPage page)
    {
        var size = Element<NumberBox>(page, "FontSizeSelector");
        double sizeRight = size.TransformToVisual(page).TransformPoint(new()).X + size.ActualWidth;
        Require(size.ActualWidth > 0, "Font size input is realized.");
        foreach (string name in new[] { "ThemeSelector", "Languages_ComboBox", "FontFamilySelector" })
        {
            var selector = Element<ComboBox>(page, name);
            double selectorRight = selector.TransformToVisual(page).TransformPoint(new()).X + selector.ActualWidth;
            Require(Math.Abs(size.ActualWidth - selector.ActualWidth) <= 1 && Math.Abs(sizeRight - selectorRight) <= 1,
                $"Font size input matches {name} width and right edge: size={size.ActualWidth:F1}/{sizeRight:F1}, selector={selector.ActualWidth:F1}/{selectorRight:F1}.");
        }

        var card = Element<FrameworkElement>(page, "FontSizeCard");
        var bounds = size.TransformToVisual(card).TransformBounds(new Windows.Foundation.Rect(0, 0, size.ActualWidth, size.ActualHeight));
        Require(bounds.Left >= -1 && bounds.Right <= card.ActualWidth + 1 && bounds.Top >= -1 && bounds.Bottom <= card.ActualHeight + 1,
            "Font size input remains inside its card at the current font size and window width.");
    }

    private static void PopulateDashboard(DashboardViewModel vm)
    {
        bool chinese = IsChinese;
        // Show inert icon actions without enabling modules or invoking launch commands.
        vm.QuickAccessItems.Clear();
        var fixtures = new[]
        {
            new QuickAccessItem { Title = "AI Hub", Tag = ModuleType.AIHub },
            new QuickAccessItem { Title = "WinGet", Tag = ModuleType.Winget },
            new QuickAccessItem { Title = "NetMap", Tag = ModuleType.NetMap },
        };
        foreach (var item in fixtures)
            item.Icon = Kit.Settings.UI.Library.Helpers.ModuleHelper.GetModuleTypeFluentIconName((ModuleType)item.Tag);
        foreach (var item in fixtures) vm.QuickAccessItems.Add(item);
        var quickAccess = (QuickAccessViewModel)typeof(DashboardViewModel).GetField("quickAccessViewModel", PrivateInstance).GetValue(vm);
        var allQuickAccessItems = (List<QuickAccessItem>)typeof(QuickAccessViewModel).GetField("_allItems", PrivateInstance).GetValue(quickAccess);
        allQuickAccessItems.Clear();
        allQuickAccessItems.AddRange(fixtures);
        typeof(QuickAccessViewModel).GetProperty("VisibleItemCount").SetValue(quickAccess, vm.QuickAccessItems.Count);
        NotifyDashboard(vm, nameof(DashboardViewModel.VisibleQuickAccessItemsCount));
        SetDashboard(vm, "SystemDetails", new DashboardDetail[]
        {
            new(chinese ? "设备" : "Device", "KIT-DESKTOP · X64"),
            new("Windows", "Windows 11 Enterprise LTSC 2024 24H2\n26100.0000"),
            new(chinese ? "处理器" : "Processor", "AMD Ryzen 9 9950X 16-Core Processor"),
            new(chinese ? "逻辑处理器" : "Logical CPUs", "32"),
            new(chinese ? "CPU 使用率" : "CPU load", "12%", 12),
            new(chinese ? "内存" : "Memory", "23.4 / 64.0 GB", 23.4 / 64 * 100),
            new(chinese ? "运行时间" : "Uptime", "2d 04:32:10"),
        });
        SetDashboard(vm, "StorageDetails", new DashboardDetail[]
        {
            new("C:\\", "320 / 953 GB", 320d / 953 * 100),
            new("D:\\", "6,400.0 / 18,630.0 GB", 6400d / 18630 * 100),
        });
        SetDashboard(vm, "GraphicsCards", new DashboardGpuCard[]
        {
            new("GPU 1 · NVIDIA GeForce RTX 5080", new DashboardDetail[]
            {
                new(chinese ? "驱动" : "Driver", "32.0.0000.0"),
                new(chinese ? "显存" : "VRAM", "3.2 / 16.0 GB", 20),
                new(chinese ? "使用率" : "Usage", "12%", 12),
                new(chinese ? "温度" : "Temperature", "48 °C"),
            }, string.Empty),
        });
        var now = DateTimeOffset.Now;
        var direct = new IdentityResult(new("192.0.2.16", "CN", "", Region: "江苏省", City: "南京市"), ProbeError.None, now, "Direct");
        var proxy = new IdentityResult(new("2001:db8:1234:5678:abcd:ef01:2345:6789", "JP", ""), ProbeError.None, now, "SystemBypass");
        SetDashboard(vm, "DirectEgress", new NetMapCard("Direct", new(direct, direct), true));
        SetDashboard(vm, "ProxyEgress", new NetMapCard("Proxy", new(proxy, proxy), true));
    }

    private static void VerifyDashboard(DashboardPage page)
    {
        var title = Descendants(page).OfType<TextBlock>().FirstOrDefault(text => (int)AutomationProperties.GetHeadingLevel(text) == 1);
        Require(title != null, "Dashboard title is realized.");
        VerifyText(title, 28, "Dashboard title");
        var introduction = Descendants(page).OfType<TextBlock>().Single(text => RuntimeLocalization.GetUid(text) == "DashboardIntro");
        Require(introduction.Text == Expected("DashboardIntro.Text"), "Dashboard introduction matches the current language.");
        VerifyText(introduction, 14, "Dashboard introduction");
        var details = Descendants(Element<FrameworkElement>(page, "SystemOverviewCard")).OfType<TextBlock>()
            .Where(text => text.Name is "DetailLabel" or "DetailValue" or "DetailUsageValue").ToArray();
        Require(details.Length >= 14, "Dashboard fixture realizes system, storage and graphics detail text.");
        foreach (var text in details) VerifyText(text, 14, "Dashboard " + text.Name, log: false);
        VerifyText(Element<TextBlock>(page, "DirectRouteTitle"), 14, "Dashboard BodyStrong route label");
        VerifyText(Element<TextBlock>(page, "DirectIpText"), 14, "Dashboard IPv4", mono: true);
        VerifyText(Element<TextBlock>(page, "ProxyIpText"), 14, "Dashboard IPv6", mono: true);
        VerifyText(Element<TextBlock>(page, "DirectLocationText"), 13, "Dashboard location");
        foreach (string name in new[] { "QuickAccessCard", "UtilitiesCard" })
        {
            var card = Element<Card>(page, name);
            var cardTitle = Descendants(card).OfType<TextBlock>().FirstOrDefault(text => text.Text == card.Title && (int)AutomationProperties.GetHeadingLevel(text) == 2);
            Require(cardTitle != null, "Shared card title is realized: " + name);
            VerifyText(cardTitle, 16, "Dashboard shared card title " + name);
        }
        var compactActions = Element<FrameworkElement>(page, "QuickAccessItemsControl");
        var iconButtons = Descendants(compactActions).OfType<Button>().ToArray();
        Require(iconButtons.Length == 3, "Three inert quick-access icon buttons are realized.");
        Require(!Descendants(compactActions).OfType<TextBlock>().Any(text => IsVisible(text, compactActions) && !string.IsNullOrEmpty(text.Text)),
            "Quick access displays icons without persistent text labels.");
        foreach (var button in iconButtons)
        {
            Require(page.ViewModel.QuickAccessItems.Any(item => item.Title == AutomationProperties.GetName(button)), "Icon buttons retain accessible module names.");
            var icon = Descendants(button).OfType<Image>().Single();
            Require(icon.Source is BitmapImage bitmap && bitmap.PixelWidth > 0, "Module bitmap is loaded.");
            Require(ToolTipService.GetToolTip(button) is ToolTip, "Icon buttons retain hover descriptions.");
        }
        Log($"Dashboard {details.Length} real detail TextBlocks use selected typography PASS");
    }

    private static async Task VerifyNavigationStability(DashboardPage dashboard, GeneralPage general)
    {
        var window = App.GetSettingsWindow();
        var originalSize = window.AppWindow.Size;
        var originalItems = dashboard.ViewModel.QuickAccessItems;
        var originalEntries = originalItems.ToArray();
        var scroll = Element<ScrollViewer>(dashboard, "MainScrollViewer");
        var quick = Element<QuickAccessList>(dashboard, "QuickAccessItemsControl");
        var iconEntries = Element<ItemsControl>(quick, "IconEntries");
        Button[] expectedButtons = null;
        Image[] expectedImages = null;
        var clock = Stopwatch.StartNew();
        double previousTick = clock.Elapsed.TotalMilliseconds;
        double maximumTickGap = 0;
        string phase = "initial", worstPhase = phase;
        int ticks = 0;
        var heartbeat = window.DispatcherQueue.CreateTimer();
        heartbeat.Interval = TimeSpan.FromMilliseconds(16);
        heartbeat.IsRepeating = true;
        heartbeat.Tick += (_, _) =>
        {
            double now = clock.Elapsed.TotalMilliseconds;
            double gap = now - previousTick;
            if (gap > maximumTickGap) { maximumTickGap = gap; worstPhase = phase; }
            previousTick = now;
            ticks++;
        };
        heartbeat.Start();
        Log("NAV_STABILITY begin: 12 cached navigation cycles; inert collection refreshed while cached from cycle 2; unchanged fixture entries, typography and theme; no Settle/UpdateLayout.");
        try
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1100));
            await Task.Delay(700);
            await InspectHome("initial", requireIcons: true);
            for (int cycle = 1; cycle <= 12; cycle++)
            {
                string prefix = "cycle-" + cycle.ToString("D2", CultureInfo.InvariantCulture);
                if (cycle % 4 == 0)
                {
                    int width = cycle % 8 == 0 ? 1280 : 760;
                    phase = prefix + "-resize-" + width;
                    window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1100));
                    await Task.Delay(700);
                    await InspectHome(phase, requireIcons: true);
                }

                bool testScroll = cycle % 3 == 0;
                if (testScroll)
                {
                    phase = prefix + "-scroll-bottom";
                    scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
                    await Task.Delay(250);
                    await InspectHome(phase, requireIcons: false);
                }
                double retainedOffset = scroll.VerticalOffset;
                await NavigateProbe(typeof(AwakePage), prefix + "-awake");
                if (cycle >= 2)
                {
                    phase = prefix + "-refresh-inert-collection-while-cached";
                    originalItems.Clear();
                    await Task.Delay(25);
                    foreach (var entry in originalEntries) originalItems.Add(entry);
                    expectedButtons = null;
                    expectedImages = null;
                    Log($"NAV_FIXTURE {prefix}: inert collection refreshed while cached; count={originalItems.Count}, dashboardLoaded={dashboard.IsLoaded}, original entry order retained.");
                }
                await NavigateProbe(typeof(DashboardPage), prefix + "-home-from-awake");
                await InspectHome(prefix + "-home-from-awake", requireIcons: !testScroll);
                if (testScroll)
                {
                    Log($"NAV_SCROLL {prefix}: before={retainedOffset:F2}, returned={scroll.VerticalOffset:F2}, extent={scroll.ScrollableHeight:F2}");
                    phase = prefix + "-scroll-top";
                    scroll.ChangeView(null, 0, null, disableAnimation: true);
                    await Task.Delay(250);
                    await InspectHome(phase, requireIcons: true);
                }
                await NavigateProbe(typeof(GeneralPage), prefix + "-general");
                Require(ReferenceEquals(NavigationService.Frame.Content, general), "Navigation stability reuses cached General.");
                await NavigateProbe(typeof(DashboardPage), prefix + "-home-from-general");
                await InspectHome(prefix + "-home-from-general", requireIcons: true);
            }

            phase = "restore-window";
            window.AppWindow.Resize(originalSize);
            await Task.Delay(700);
            await InspectHome(phase, requireIcons: true);
        }
        finally
        {
            heartbeat.Stop();
            Log($"NAV_HEARTBEAT interval=16ms, ticks={ticks}, maxGap={maximumTickGap:F3}ms, maxDelay={Math.Max(0, maximumTickGap - 16):F3}ms, phase={worstPhase}");
            window.AppWindow.Resize(originalSize);
        }

        async Task NavigateProbe(Type pageType, string label)
        {
            phase = label;
            var watch = Stopwatch.StartNew();
            double loadedAt = -1;
            FrameworkElement target = null;
            RoutedEventHandler loaded = (_, _) => loadedAt = watch.Elapsed.TotalMilliseconds;
            NavigatedEventHandler navigated = (_, args) =>
            {
                if (args.Content is FrameworkElement page && page.GetType() == pageType)
                {
                    target = page;
                    if (page.IsLoaded) loadedAt = watch.Elapsed.TotalMilliseconds;
                    else page.Loaded += loaded;
                }
            };
            NavigationService.Frame.Navigated += navigated;
            try
            {
                bool accepted = NavigationService.Navigate(pageType);
                double synchronousMs = watch.Elapsed.TotalMilliseconds;
                Log($"NAV_PROBE {label}: Navigate synchronous={synchronousMs:F3}ms, accepted={accepted}");
                Require(accepted, label + " navigation succeeds.");
                await WaitUntil(() => target?.IsLoaded == true, label + " Loaded");
                if (loadedAt < 0) loadedAt = watch.Elapsed.TotalMilliseconds;
                FrameworkElement surface = target is GeneralPage
                    ? Element<FrameworkElement>(target, "GeneralSettingsSurface")
                    : ((target as Page)?.Content as FrameworkElement ?? target);
                await WaitUntil(() => surface.Visibility == Visibility.Visible && surface.Opacity > 0.99
                    && surface.ActualWidth > 0 && surface.ActualHeight > 0
                    && (surface is not Control control || control.IsEnabled), label + " visible surface");
                Log($"NAV_PROBE {label}: Loaded={loadedAt:F3}ms, Loaded-to-visible={watch.Elapsed.TotalMilliseconds - loadedAt:F3}ms, surface={surface.ActualWidth:F1}x{surface.ActualHeight:F1}, opacity={surface.Opacity:F3}, compositionOpacity={VisualOpacity(surface):F3}");
                await Task.Delay(250);
            }
            catch
            {
                await CaptureUnmodifiedShell("navigation-" + label + "-failure.png");
                throw;
            }
            finally
            {
                NavigationService.Frame.Navigated -= navigated;
                if (target != null) target.Loaded -= loaded;
            }
        }

        async Task InspectHome(string label, bool requireIcons)
        {
            phase = label;
            var buttons = Descendants(quick).OfType<Button>().ToArray();
            var images = Descendants(quick).OfType<Image>().ToArray();
            bool intact = ReferenceEquals(NavigationService.Frame.Content, dashboard)
                && ReferenceEquals(originalItems, dashboard.ViewModel.QuickAccessItems)
                && originalEntries.SequenceEqual(dashboard.ViewModel.QuickAccessItems)
                && ReferenceEquals(iconEntries, quick.FindName("IconEntries"))
                && ReferenceEquals(iconEntries.ItemsSource, originalItems)
                && originalEntries.Cast<object>().SequenceEqual(iconEntries.Items.Cast<object>());
            bool realized = buttons.Length == originalEntries.Length && images.Length == originalEntries.Length;
            bool loaded = images.All(image => image.Source is BitmapImage bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0
                && image.ActualWidth > 0 && image.ActualHeight > 0);
            bool ordered = realized && buttons.Select((button, index) =>
                ReferenceEquals(button.DataContext, originalEntries[index])
                && AutomationProperties.GetName(button) == originalEntries[index].Title
                && ReferenceEquals(button.Content, images[index])
                && images[index].Source is BitmapImage bitmap && bitmap.UriSource == new Uri(originalEntries[index].Icon)).All(value => value);
            bool sameVisuals = expectedButtons == null || (expectedButtons.SequenceEqual(buttons) && expectedImages.SequenceEqual(images));
            bool onlyIconMode = !Descendants(quick).OfType<ItemsRepeater>().Any();
            Log($"NAV_HOME {label}: items={dashboard.ViewModel.QuickAccessItems.Count}, visibleItems={dashboard.ViewModel.VisibleQuickAccessItemsCount}, sourceCount={iconEntries.Items.Count}, buttons={buttons.Length}, images={images.Length}, intact={intact}, ordered={ordered}, sameVisuals={sameVisuals}, resetVisualBaseline={expectedButtons == null}, iconModeOnly={onlyIconMode}, quick={quick.ActualWidth:F1}x{quick.ActualHeight:F1}, iconEntries={iconEntries.ActualWidth:F1}x{iconEntries.ActualHeight:F1}, scroll={scroll.VerticalOffset:F2}/{scroll.ScrollableHeight:F2}, pageOpacity={dashboard.Opacity:F3}/{VisualOpacity(dashboard):F3}, quickOpacity={quick.Opacity:F3}/{VisualOpacity(quick):F3}");
            for (int index = 0; index < buttons.Length; index++)
            {
                var button = buttons[index];
                Log($"NAV_BUTTON {label}: index={index}, name={AutomationProperties.GetName(button)}, size={button.ActualWidth:F1}x{button.ActualHeight:F1}, visibility={button.Visibility}, opacity={button.Opacity:F3}/{VisualOpacity(button):F3}");
            }
            foreach (var image in images)
            {
                var bitmap = image.Source as BitmapImage;
                Log($"NAV_IMAGE {label}: uri={bitmap?.UriSource}, pixels={bitmap?.PixelWidth}x{bitmap?.PixelHeight}, actual={image.ActualWidth:F1}x{image.ActualHeight:F1}, visibility={image.Visibility}, opacity={image.Opacity:F3}/{VisualOpacity(image):F3}");
            }
            // Save the untouched complete visual before any failure. No layout invalidation,
            // temporary background, source replacement, or visibility change is allowed here.
            var frame = await CaptureUnmodifiedShell("navigation-" + label + ".png");
            bool painted = true;
            if (requireIcons)
            {
                var root = (FrameworkElement)window.Content;
                foreach (var image in images)
                {
                    var bounds = image.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, image.ActualWidth, image.ActualHeight));
                    double sx = frame.Width / root.ActualWidth, sy = frame.Height / root.ActualHeight;
                    int left = Math.Clamp((int)Math.Floor(bounds.Left * sx), 0, frame.Width - 1);
                    int top = Math.Clamp((int)Math.Floor(bounds.Top * sy), 0, frame.Height - 1);
                    int right = Math.Clamp((int)Math.Ceiling(bounds.Right * sx), left, frame.Width);
                    int bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom * sy), top, frame.Height);
                    int background = (top * frame.Width + left) * 4, distinct = 0;
                    for (int y = top; y < bottom; y++)
                        for (int x = left; x < right; x++)
                        {
                            int offset = (y * frame.Width + x) * 4;
                            int delta = Math.Max(Math.Abs(frame.Bytes[offset] - frame.Bytes[background]),
                                Math.Max(Math.Abs(frame.Bytes[offset + 1] - frame.Bytes[background + 1]), Math.Abs(frame.Bytes[offset + 2] - frame.Bytes[background + 2])));
                            if (frame.Bytes[offset + 3] >= 32 && delta >= 24) distinct++;
                        }
                    Log($"NAV_PIXELS {label}: uri={(image.Source as BitmapImage)?.UriSource}, crop={left},{top},{right - left},{bottom - top}, distinctPixels={distinct}");
                    painted &= distinct >= 8 && bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
                }
            }
            Require(intact, label + " preserves the cached dashboard and exact quick-access collection entries.");
            Require(onlyIconMode && realized && loaded && ordered && sameVisuals,
                label + " retains ordered quick-access buttons/images and matching bitmap URIs, with stable visual references between explicit collection refreshes.");
            Require(!requireIcons || painted, label + " retains painted quick-access icons; inspect navigation screenshot and NAV_HOME/NAV_IMAGE/NAV_PIXELS.");
            expectedButtons = buttons;
            expectedImages = images;
        }

        static float VisualOpacity(UIElement element) => Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element).Opacity;
    }

    private static async Task<(byte[] Bytes, int Width, int Height)> CaptureUnmodifiedShell(string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync((FrameworkElement)App.GetSettingsWindow().Content);
        var pixels = await bitmap.GetPixelsAsync();
        var bytes = new byte[pixels.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
        using var encoded = new Windows.Storage.Streams.InMemoryRandomAccessStream();
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
        Log($"Captured unchanged {name}: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
        return (bytes, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private static async Task CaptureDashboard(DashboardPage page, string suffix = "")
    {
        var navigation = Descendants((FrameworkElement)App.GetSettingsWindow().Content).OfType<NavigationView>().Single();
        bool paneWasOpen = navigation.IsPaneOpen;
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                ((FrameworkElement)App.GetSettingsWindow().Content).RequestedTheme = theme;
                foreach (int width in new[] { 1280, 760 })
                {
                    App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1100));
                    await Settle();
                    navigation.IsPaneOpen = width == 1280;
                    await Settle();
                    Require(navigation.IsPaneOpen == (width == 1280), $"Dashboard capture pane matches {width}px fixture.");
                    Require(!(bool)typeof(DashboardViewModel).GetField("overviewActive", PrivateInstance).GetValue(page.ViewModel), "Resize cannot reactivate isolated dashboard probes.");
                    var scroll = Element<ScrollViewer>(page, "MainScrollViewer");
                    Require(scroll.ExtentWidth <= scroll.ViewportWidth + 1, $"Dashboard has no horizontal overflow at {theme}/{width}.");
                    VerifyDashboardLayout(page, width);
                    VerifyDashboardDensity(page);
                    foreach (var row in Descendants(Element<FrameworkElement>(page, "SystemOverviewCard")).OfType<Grid>().Where(grid => grid.Name == "DetailRow"))
                    {
                        var label = Descendants(row).OfType<TextBlock>().Single(text => text.Name == "DetailLabel");
                        var value = Descendants(row).OfType<TextBlock>().First(text => (text.Name is "DetailValue" or "DetailUsageValue") && IsVisible(text, row));
                        var labelPoint = label.TransformToVisual(row).TransformPoint(new());
                        var valuePoint = value.TransformToVisual(row).TransformPoint(new());
                        Require(labelPoint.X + label.ActualWidth <= valuePoint.X + 1 && valuePoint.X + value.ActualWidth <= row.ActualWidth + 1,
                            $"Dashboard detail columns do not overlap at {theme}/{width}: {label.Text}.");
                        Require(label.ActualHeight + 6 <= row.ActualHeight + 1 && value.ActualHeight + 6 <= row.ActualHeight + 1,
                            $"Dashboard rows grow to fit selected text size at {theme}/{width}: {label.Text}.");
                    if (value.Text == "6,400.0 / 18,630.0 GB")
                    {
                        CheckTypography(value.ActualHeight <= Scaled(20) + 1,
                            $"Dashboard disk usage stays on one line at {theme}/{width}/base{ActiveSize}: actual height={value.ActualHeight}, line height={Scaled(20)}.");
                    }
                    }
                    await CaptureShell($"dashboard-{theme}-{width}{suffix}-full-shell.png");
                }
            }
        }
        finally { navigation.IsPaneOpen = paneWasOpen; }
    }

    private static void VerifyDashboardDensity(DashboardPage page)
    {
        var quick = Element<FrameworkElement>(page, "QuickAccessItemsControl");
        var icons = Descendants(quick).OfType<Button>().ToArray();
        Require(icons.Length == 3, "Quick access realizes all icon fixtures.");
        foreach (var button in icons)
            Require(Near(button.ActualWidth, 48) && Near(button.ActualHeight, 48), "Quick access keeps a 48px square hit area.");
        Require(quick.ActualHeight <= 50, "Three quick-access icons fit on one compact row at both window widths.");
        foreach (string name in new[] { "ModulesCard" })
        {
            var rows = Descendants(Element<FrameworkElement>(page, name))
                .OfType<CommunityToolkit.WinUI.Controls.SettingsCard>().Where(row => IsVisible(row, page)).ToArray();
            Require(rows.Length > 0, name + " realizes rows for density checks.");
            var rowContainer = Element<FrameworkElement>(page, name);
            double occupiedHeight = rows.Sum(row => row.ActualHeight);
            CheckTypography(rowContainer.ActualHeight <= occupiedHeight + 2,
                $"{name} list allocation must not add empty vertical space: list={rowContainer.ActualHeight:F1}, rows={occupiedHeight:F1}.");
            foreach (var row in rows)
            {
                if (ActiveSize == 14)
                    CheckTypography(row.ActualHeight >= 48 && row.ActualHeight <= 52,
                        $"{name} default rows stay compact with native hit targets: height={row.ActualHeight:F1}.");
                foreach (var toggle in Descendants(row).OfType<ToggleSwitch>())
                {
                    var bounds = toggle.TransformToVisual(row).TransformBounds(new Windows.Foundation.Rect(0, 0, toggle.ActualWidth, toggle.ActualHeight));
                    CheckTypography(bounds.Top >= 0 && bounds.Bottom <= row.ActualHeight + 1,
                        name + " preserves the full native toggle hit area at every font size.");
                }
            }
            Log($"{name} base{ActiveSize}: {rows.Length} rows, height {rows.Min(row => row.ActualHeight):F1}–{rows.Max(row => row.ActualHeight):F1}.");
        }
    }

    private static void VerifyDashboardLayout(DashboardPage page, int windowWidth)
    {
        object twoColumns = page.FindName("DashboardTwoColumnsTrigger") ?? throw new InvalidOperationException("Missing DashboardTwoColumnsTrigger.");
        object singleColumn = page.FindName("DashboardSingleColumnTrigger") ?? throw new InvalidOperationException("Missing DashboardSingleColumnTrigger.");
        double minimum = Convert.ToDouble(twoColumns.GetType().GetProperty("MinWidth").GetValue(twoColumns), CultureInfo.InvariantCulture);
        double maximum = Convert.ToDouble(singleColumn.GetType().GetProperty("MaxWidth").GetValue(singleColumn), CultureInfo.InvariantCulture);
        CheckTypography(Near(minimum, Scaled(720)) && Near(maximum, Scaled(720)),
            $"Dashboard live breakpoint uses its original 720px baseline: expected {Scaled(720)}, got {minimum}/{maximum}.");
        var rows = Descendants(Element<FrameworkElement>(page, "SystemOverviewCard")).OfType<Grid>().Where(grid => grid.Name == "DetailRow").ToArray();
        Require(rows.Length > 0, "Dashboard detail rows are realized for live column assertions.");
        foreach (var row in rows)
        {
            CheckTypography(Near(row.ColumnDefinitions[0].Width.Value, Scaled(92)),
                $"Dashboard label column is {row.ColumnDefinitions[0].Width.Value}; expected baseline-scaled {Scaled(92)}.");
            var usage = row.FindName("UsageRow") as Grid ?? throw new InvalidOperationException("Dashboard detail row has no UsageRow.");
            CheckTypography(Near(usage.ColumnDefinitions[0].Width.Value, Scaled(144)),
                $"Dashboard usage column is {usage.ColumnDefinitions[0].Width.Value}; expected baseline-scaled {Scaled(144)}.");
        }
        if (windowWidth == 1280 && ActiveSize is 14 or 20)
        {
            int column = Grid.GetColumn(Element<FrameworkElement>(page, "SecondaryColumn"));
            CheckTypography(column == (ActiveSize == 20 ? 0 : 1),
                $"Dashboard at 1280/base{ActiveSize} uses {(ActiveSize == 20 ? "one column" : "two columns")}; actual secondary column={column}.");
        }
        Log($"Dashboard layout checked: base={ActiveSize}, width={windowWidth}, breakpoint={minimum}, label={Scaled(92)}, usage={Scaled(144)}");
    }

    private static bool IsVisible(FrameworkElement element, DependencyObject boundary)
    {
        for (DependencyObject node = element; node != null && node != boundary; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement ui && ui.Visibility != Visibility.Visible) return false;
        return true;
    }

    private static void VerifyAiHubHeader(AIHubPage page)
    {
        Require(page.FindName("AiHubDescription") == null && !Descendants(page).OfType<FrameworkElement>().Any(element => element.Name == "AiHubDescription"),
            "AI Hub omits the redundant page introduction.");
        VerifyText(Element<TextBlock>(page, "AiHubPageTitle"), 28, "AI Hub title");
        var readiness = Element<TextBlock>(page, "AiReadinessText");
        Require(!string.IsNullOrWhiteSpace(readiness.Text) && readiness.Text == page.ViewModel.AiReadinessCompactText,
            "AI Hub retains the current service readiness text.");
        VerifyText(readiness, 12, "AI Hub service readiness");
        var settingsLink = Element<TextBlock>(page, "AiReadinessSettingsText");
        Require(!string.IsNullOrWhiteSpace(settingsLink.Text) && settingsLink.Text == page.ViewModel.AiServiceSettingsLabel,
            "AI Hub retains its service settings action label.");
        VerifyText(settingsLink, 12, "AI Hub service settings label");
    }

    private static void VerifyWingetWorkspace(WingetPage page)
    {
        var workspace = Element<FrameworkElement>(page, "WorkspaceHeaderGrid");
        var title = Descendants(workspace).OfType<TextBlock>().Single(text => TypographyResources.GetBaseFontSize(text) == 18);
        var subtitle = Descendants(workspace).OfType<TextBlock>().Single(text => TypographyResources.GetBaseFontSize(text) == 12);
        Require(!string.IsNullOrWhiteSpace(title.Text) && title.Text == page.ViewModel.WorkspaceTitle,
            "Winget workspace heading follows the selected tab.");
        Require(subtitle.Text == page.ViewModel.WorkspaceSubtitle, "Winget workspace summary reflects current package state.");
        VerifyText(title, 18, "Winget workspace heading");
        VerifyText(subtitle, 12, "Winget workspace summary");
    }

    private static void VerifyText(TextBlock text, double baseline, string description, bool mono = false, bool log = true)
    {
        string family = mono ? ((FontFamily)Application.Current.Resources["KitMonoFontFamily"]).Source : ExpectedFamily;
        if (!CheckTypography(text.FontFamily.Source == family && Near(text.FontSize, Scaled(baseline)),
            $"{description}: expected {family} / {Scaled(baseline)}, got {text.FontFamily.Source} / {text.FontSize}.")) return;
        if (log) Log($"{description}: {text.FontFamily.Source}, {text.FontSize.ToString(CultureInfo.InvariantCulture)}px PASS");
    }

    private static void VerifyControl(Control control, double baseline, string description)
    {
        if (!CheckTypography(control.FontFamily.Source == ExpectedFamily && Near(control.FontSize, Scaled(baseline)),
            $"{description}: expected {ExpectedFamily} / {Scaled(baseline)}, got {control.FontFamily.Source} / {control.FontSize}.")) return;
        Log($"{description}: {control.FontFamily.Source}, {control.FontSize.ToString(CultureInfo.InvariantCulture)}px PASS");
    }

    private static void VerifyIcon(FontIcon icon, double unscaledSize, string description)
    {
        if (!CheckTypography(icon.FontFamily.Source.Contains("Icons", StringComparison.OrdinalIgnoreCase) || icon.FontFamily.Source.Contains("Assets", StringComparison.OrdinalIgnoreCase),
            description + " retains its symbol font, got " + icon.FontFamily.Source)) return;
        if (!CheckTypography(Near(icon.FontSize, unscaledSize), description + " retains unscaled icon size.")) return;
        Log($"{description}: {icon.FontFamily.Source}, {icon.FontSize.ToString(CultureInfo.InvariantCulture)}px PASS");
    }

    private static void VerifyLastTypographyMessage(string family, int size)
    {
        Require(Messages.Count > 0, "Changing a typography control sends its general-settings message.");
        using var message = JsonDocument.Parse(Messages.Last());
        var general = message.RootElement.GetProperty("general");
        Require(general.GetProperty("ui_font_family").GetString() == family && general.GetProperty("ui_font_size").GetInt32() == size,
            "Captured IPC contains the exact selected typography.");
    }

    private static void SetDashboard(DashboardViewModel vm, string property, object value)
    {
        typeof(DashboardViewModel).GetProperty(property).SetValue(vm, value);
        NotifyDashboard(vm, property);
    }

    private static void NotifyDashboard(DashboardViewModel vm, string property)
    {
        typeof(DashboardViewModel).GetMethod("OnPropertyChanged", PrivateInstance, null, new[] { typeof(string) }, null).Invoke(vm, new object[] { property });
    }

    private static T Element<T>(FrameworkElement page, string name) where T : class
        => page.FindName(name) as T ?? throw new InvalidOperationException("Missing " + name);

    private static async Task Navigate(Type pageType)
    {
        Log("Navigate entering " + pageType.Name);
        Require(NavigationService.Navigate(pageType), "Could not navigate to " + pageType.Name);
        Log("Navigate returned " + pageType.Name);
        await WaitUntil(() => NavigationService.Frame.Content is FrameworkElement page && page.IsLoaded, pageType.Name + " Loaded");
        await Settle();
        Log("Navigate settled " + pageType.Name);
    }

    private static async Task WaitUntil(Func<bool> ready, string description)
    {
        var timer = Stopwatch.StartNew();
        while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
        Require(ready(), "Timed out waiting for " + description);
    }

    private static async Task Settle()
    {
        await Task.Delay(100);
        ((FrameworkElement)App.GetSettingsWindow().Content).UpdateLayout();
        var rendered = new TaskCompletionSource<bool>();
        EventHandler<object> handler = (_, _) => rendered.TrySetResult(true);
        CompositionTarget.Rendering += handler;
        try { await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= handler; }
    }

    private static async Task CaptureShell(string name, FrameworkElement captureRoot = null)
    {
        var windowRoot = (FrameworkElement)App.GetSettingsWindow().Content;
        var shell = windowRoot as ShellPage ?? Descendants(windowRoot).OfType<ShellPage>().Single();
        var root = (Grid)shell.FindName("RootGrid");
        var background = root.Background;
        root.Background = new SolidColorBrush(shell.ActualTheme == ElementTheme.Dark ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32) : Microsoft.UI.ColorHelper.FromArgb(255, 243, 243, 243));
        var target = captureRoot ?? windowRoot;
        var bitmap = new RenderTargetBitmap();
        try { await bitmap.RenderAsync(target); }
        finally { root.Background = background; }
        Require(bitmap.PixelWidth >= target.ActualWidth - 2 && bitmap.PixelHeight >= target.ActualHeight - 2, "Screenshot captures the complete requested visual.");
        if (Environment.GetEnvironmentVariable("KIT_TYPOGRAPHY_SMOKE_SUITE") == "Plugins")
            Require(bitmap.PixelWidth <= target.ActualWidth + 2 && bitmap.PixelHeight <= target.ActualHeight + 2,
                $"Plugin capture must not include animation overflow outside its visual: PNG={bitmap.PixelWidth}x{bitmap.PixelHeight}, visual={target.ActualWidth}x{target.ActualHeight}.");
        var pixels = await bitmap.GetPixelsAsync();
        var bytes = new byte[pixels.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
        using var encoded = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
        var png = new byte[encoded.Size];
        using (var reader = new Windows.Storage.Streams.DataReader(encoded.GetInputStreamAt(0))) { await reader.LoadAsync((uint)encoded.Size); reader.ReadBytes(png); }
        File.WriteAllBytes(Path.Combine(Output, name), png);
        Log($"Captured {name}: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
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

    private static async Task VerifyAppearanceDiscovery(DashboardPage page)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var queueScan = typeof(AppearanceService).GetMethod("QueueScan", flags);
        var scanField = typeof(AppearanceService).GetField("discoveryScan", flags);
        var followupField = typeof(AppearanceService).GetField("scanFollowupRequested", flags);
        var timerField = typeof(AppearanceService).GetField("layoutDiscoveryTimer", flags);
        var layoutUpdated = typeof(AppearanceService).GetMethod("OnLayoutUpdated", flags);
        Require(queueScan != null && scanField != null && followupField != null, "Incremental appearance discovery is available.");
        bool Idle() => scanField.GetValue(null) == null && !(bool)followupField.GetValue(null);
        bool FullyIdle() => Idle() && (timerField.GetValue(null) is not DispatcherQueueTimer timer || !timer.IsRunning);
        await WaitUntil(FullyIdle, "Appearance discovery settled before layout burst");
        for (int index = 0; index < 60; index++) layoutUpdated.Invoke(null, new object[] { null, null });
        Require(scanField.GetValue(null) == null, "A burst of layout changes does not start a visual-tree scan synchronously.");
        Require(timerField.GetValue(null) is DispatcherQueueTimer { IsRunning: true }, "Layout discovery is scheduled once without requiring more layout events.");
        await WaitUntil(FullyIdle, "Coalesced layout discovery completes without starvation");
        var root = (Grid)page.Content;
        var host = new StackPanel { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false };
        var first = new TextBlock { Text = "Discovery first", FontFamily = new FontFamily("Arial") };
        var late = new TextBlock { Text = "Discovery late", FontFamily = new FontFamily("Arial") };
        host.Children.Add(first);
        root.Children.Add(host);
        try
        {
            // No force-layout or explicit appearance update: normal discovery must
            // find new controls and eventually stop when the layout is stable.
            await WaitUntil(() => first.IsLoaded && first.FontFamily.Source == ExpectedFamily && FullyIdle(), "Initial dynamic text discovery and idle");
            var dispatch = page.DispatcherQueue;
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var watch = Stopwatch.StartNew();
            queueScan.Invoke(null, null);
            double queueMs = watch.Elapsed.TotalMilliseconds;
            int callbacks = 0;
            bool yieldedWithWorkPending = false, insertedAfterVisit = false, stopped = false;
            double previous = watch.Elapsed.TotalMilliseconds, maxGap = 0;
            void Probe()
            {
                if (stopped) return;
                try
                {
                    double now = watch.Elapsed.TotalMilliseconds;
                    maxGap = Math.Max(maxGap, now - previous);
                    previous = now;
                    callbacks++;
                    var scan = scanField.GetValue(null);
                    if (scan != null)
                    {
                        var visited = (HashSet<DependencyObject>)scan.GetType().GetProperty("Visited").GetValue(scan);
                        yieldedWithWorkPending |= visited.Count > 0;
                        if (!insertedAfterVisit && visited.Contains(host))
                        {
                            // Insertion behind the traversal must be discovered
                            // by the coalesced follow-up pass after LayoutUpdated.
                            host.Children.Add(late);
                            insertedAfterVisit = true;
                        }
                    }
                    if (Idle()) finished.TrySetResult();
                    else if (!dispatch.TryEnqueue(DispatcherQueuePriority.Low, Probe)) finished.TrySetException(new InvalidOperationException("Could not queue discovery probe."));
                }
                catch (Exception ex) { finished.TrySetException(ex); }
            }
            Require(dispatch.TryEnqueue(DispatcherQueuePriority.Low, Probe), "Discovery probe queued behind the first batch.");
            try { await finished.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { stopped = true; }
            Require(yieldedWithWorkPending && callbacks > 1, "Appearance discovery yields to another dispatcher callback while traversal remains unfinished.");
            Require(insertedAfterVisit, "Late insertion occurs after its parent was visited.");
            await WaitUntil(() => late.IsLoaded && late.FontFamily.Source == ExpectedFamily && FullyIdle(), "Late text discovery and idle");
            Log($"NAV_DISCOVERY queue={queueMs:F3}ms, dispatcherCallbacks={callbacks}, maxProbeGap={maxGap:F3}ms, total={watch.Elapsed.TotalMilliseconds:F3}ms; yielded during traversal, late insertion applied, idle reached. Probe gap includes rendering and scheduling.");
        }
        finally { root.Children.Remove(host); }
    }

    private static async Task VerifyQuickAccessModes(DashboardPage page)
    {
        var root = (Grid)page.Content;
        var probe = new QuickAccessList
        {
            Width = 360,
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            ItemsSource = page.ViewModel.QuickAccessItems,
        };
        Grid.SetRowSpan(probe, Math.Max(1, root.RowDefinitions.Count));
        root.Children.Add(probe);
        try
        {
            Require(!probe.IsCompact && page.ViewModel.QuickAccessItems.Count == 3, "Shared quick access starts in tray mode with three inert fixtures.");
            await WaitUntil(() => probe.IsLoaded && TileModeRealized(), "Default tray quick-access mode");
            VerifyTileLayout("default");

            probe.IsCompact = true;
            await WaitUntil(() => !Descendants(probe).OfType<ItemsRepeater>().Any()
                && Descendants(probe).OfType<ItemsControl>().Count(control => control.Name == "IconEntries") == 1
                && Descendants(probe).OfType<Button>().Count() == 3
                && Descendants(probe).OfType<Image>().Count() == 3, "Compact quick-access mode");
            await Task.Delay(250);
            Require(!Descendants(probe).OfType<FlyoutMenuButton>().Any(), "Compact mode unloads every tray tile.");
            Require(Descendants(probe).OfType<Button>().All(button => button.GetType() == typeof(Button)
                && Near(button.ActualWidth, 48) && Near(button.ActualHeight, 48)), "Compact mode realizes three native 48px buttons.");
            Require(Descendants(probe).OfType<Image>().All(image => Near(image.ActualWidth, 32) && Near(image.ActualHeight, 32)),
                "Compact mode realizes three 32px module images.");
            Log("QUICK_ACCESS_MODES compact: only IconEntries, three 48px buttons and 32px images PASS");

            probe.IsCompact = false;
            await WaitUntil(TileModeRealized, "Restored tray quick-access mode");
            VerifyTileLayout("restored");
        }
        finally { root.Children.Remove(probe); }

        bool TileModeRealized() => Descendants(probe).OfType<ItemsRepeater>().Count() == 1
            && !Descendants(probe).OfType<ItemsControl>().Any(control => control.Name == "IconEntries")
            && Descendants(probe).OfType<FlyoutMenuButton>().Count() == 3
            && Descendants(probe).OfType<Button>().Count() == 3;

        void VerifyTileLayout(string label)
        {
            var repeater = Descendants(probe).OfType<ItemsRepeater>().Single();
            Require(ReferenceEquals(repeater.ItemsSource, page.ViewModel.QuickAccessItems)
                && repeater.Layout is UniformGridLayout layout && layout.MaximumRowsOrColumns == 3
                && Near(layout.MinItemHeight, 84) && Near(layout.MinItemWidth, 96)
                && layout.ItemsStretch == UniformGridLayoutItemsStretch.Fill,
                label + " tray mode preserves the shared source and original three-column 84px tile layout.");
            Log("QUICK_ACCESS_MODES " + label + ": only ItemsRepeater, three FlyoutMenuButton tiles, original layout PASS");
        }
    }

    private static void EnsureNoWorker()
    {
        foreach (string name in new[] { "Kit.AIHubWorker", "Kit.WingetService", "winget" })
        {
            var processes = Process.GetProcessesByName(name);
            try { Require(processes.Length == 0, "Typography smoke must not create " + name + "."); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }

    private static double Scaled(double baseline) => Math.Round(baseline * ActiveSize / 14.0, 3);
    private static void CaptureForbidden(string kind, string message)
    {
        string action = string.Empty;
        if (!string.IsNullOrEmpty(message))
        {
            try
            {
                using var payload = JsonDocument.Parse(message);
                if (payload.RootElement.TryGetProperty("action", out var actionElement) &&
                    actionElement.TryGetProperty("general", out var generalElement) &&
                    generalElement.TryGetProperty("action_name", out var nameElement)) action = nameElement.GetString();
            }
            catch (JsonException) { }
        }
        if (kind == "update" && action == "refresh_update_state")
        {
            Log("Captured read-only refresh_update_state request on General navigation; no real IPC.");
            return;
        }
        ForbiddenMessages.Add(kind + "/" + action);
        Log($"FORBIDDEN callback {kind}/{action}; payload length={message?.Length ?? 0}\n{Environment.StackTrace}");
    }

    private static bool CheckTypography(bool condition, string message)
    {
        if (!condition)
        {
            TypographyFailures.Add(message);
            Log("FAIL: " + message);
        }
        return condition;
    }
    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 0.005;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Log(string text) => File.AppendAllText(Report, text + "\n");
}
