extern alias QuickAccess;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI.Controls;
using Kit.Settings.UI.Library;
using ManagedCommon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using QuickApp = QuickAccess::Kit.QuickAccess.App;
using QuickWindow = QuickAccess::Kit.QuickAccess.MainWindow;
using QuickContext = QuickAccess::Kit.QuickAccess.QuickAccessLaunchContext;
using QuickShell = QuickAccess::Kit.QuickAccess.Flyout.ShellPage;
using QuickLaunchPage = QuickAccess::Kit.QuickAccess.Flyout.LaunchPage;
using QuickAppsPage = QuickAccess::Kit.QuickAccess.Flyout.AppsListPage;
using QuickLauncherViewModel = QuickAccess::Kit.QuickAccess.ViewModels.LauncherViewModel;
using QuickAllAppsViewModel = QuickAccess::Kit.QuickAccess.ViewModels.AllAppsViewModel;
using QuickCoordinator = QuickAccess::Kit.QuickAccess.Services.IQuickAccessCoordinator;
using Typography = QuickAccess::Kit.Settings.UI.Services.TypographyResources;
using QuickMetadataProvider = QuickAccess::Kit.QuickAccess.Kit_QuickAccess_XamlTypeInfo.XamlMetaDataProvider;

internal static class QuickAccessTypographySmoke
{
    private static readonly string Output = Environment.GetEnvironmentVariable("KIT_QUICKACCESS_TYPOGRAPHY_OUTPUT");
    private static readonly string Report = Path.Combine(Output, "quickaccess-typography-smoke.log");
    private static readonly string StartupFamily = Environment.GetEnvironmentVariable("KIT_QUICKACCESS_TYPOGRAPHY_FAMILY");
    private static readonly int StartupSize = int.Parse(Environment.GetEnvironmentVariable("KIT_QUICKACCESS_TYPOGRAPHY_SIZE"), CultureInfo.InvariantCulture);
    private static readonly bool DiagnoseResources = Environment.GetEnvironmentVariable("KIT_QUICKACCESS_TYPOGRAPHY_DIAGNOSE") == "1";
    private static readonly Dictionary<FontIcon, double> IconSizes = new();
    private static readonly QuickCoordinator Coordinator = new InertCoordinator();
    private static readonly QuickAccessItem[] TileFixtures =
    {
        new() { Title = "AI Hub", Tag = ModuleType.AIHub },
        new() { Title = "NetMap", Tag = ModuleType.NetMap },
        new() { Title = "WinGet", Tag = ModuleType.Winget },
    };
    private static QuickWindow Window;
    private static QuickShell Shell;
    private static Frame Frame;
    private static QuickLauncherViewModel Launcher;
    private static QuickAllAppsViewModel AllApps;
    private static SettingsRepository<GeneralSettings> Repository;
    private static string Family;
    private static int Size;

    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();

    [STAThread]
    private static void Main()
    {
        File.WriteAllText(Report, "Real QuickAccess App resources and MainWindow; empty owned launch context, inert navigation coordinator, disabled modules. Only More/Back buttons are invoked.\n");
        try
        {
            Require(Environment.GetCommandLineArgs().Length == 1, "Harness must have no runner, pipe or event arguments.");
            if (DiagnoseResources)
                AppDomain.CurrentDomain.AssemblyLoad += (_, args) => Log("ASSEMBLY: " + args.LoadedAssembly.FullName + " @ " + args.LoadedAssembly.Location);
            AppDomain.CurrentDomain.UnhandledException += (_, args) => Log("CLR UNHANDLED: " + args.ExceptionObject);
            AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
            {
                if (args.Exception is COMException || args.Exception.GetType().Name.Contains("Xaml", StringComparison.OrdinalIgnoreCase))
                    Log("FIRST CHANCE: " + args.Exception + "\nMANAGED CALL SITE:\n" + Environment.StackTrace);
            };
            Log("BOOT: loading Windows App Runtime.");
            Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
            Marshal.ThrowExceptionForHR(WindowsAppRuntime_EnsureIsLoaded());
            Log("BOOT: initializing WinRT wrappers.");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Log("BOOT: starting XAML Application.");
            Application.Start(_ =>
            {
                Log("BOOT: XAML callback entered.");
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                if (DiagnoseResources)
                {
                    Log("DIAGNOSTIC: constructing independent resource probe; no production window or acceptance assertions.");
                    var diagnostic = new ResourceDiagnosticApp();
                    diagnostic.UnhandledException += (_, args) => Finish(4, "XAML DIAGNOSTIC UNHANDLED: " + args.Exception);
                    return;
                }
                Log("BOOT: constructing QuickAccess App resources.");
                var app = new QuickApp();
                Log("BOOT: QuickAccess App constructed.");
                app.UnhandledException += (_, args) => Finish(4, "UNHANDLED: " + args.Exception);
                DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
                {
                    try { await AttachToOwnedWindow(); await Run(); Finish(0, "PASS"); }
                    catch (Exception ex) { Finish(1, ex.ToString()); }
                });
            });
        }
        catch (Exception ex) { Finish(2, ex.ToString()); }
    }

    private sealed class ResourceDiagnosticApp : Application, IXamlMetadataProvider
    {
        private readonly IXamlMetadataProvider _metadata = new QuickMetadataProvider();

        public IXamlType GetXamlType(Type type)
        {
            Log("METADATA type: " + type.AssemblyQualifiedName);
            return _metadata.GetXamlType(type);
        }

        public IXamlType GetXamlType(string name)
        {
            Log("METADATA name: " + name);
            return _metadata.GetXamlType(name);
        }

        public XmlnsDefinition[] GetXmlnsDefinitions() => _metadata.GetXmlnsDefinitions();

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                DebugSettings.BindingFailed += (_, details) => Log("BINDING: " + details.Message);
                DebugSettings.XamlResourceReferenceFailed += (_, details) => Log("RESOURCE REFERENCE: " + details.Message);
                Log("RESOURCE 1: native XamlControlsResources.");
                Resources.MergedDictionaries.Add(new XamlControlsResources());
                Log("RESOURCE 1 OK.");
                Log("RESOURCE 2a: construct shared typography via C#.");
                var nativeTypography = new Typography();
                Log("RESOURCE 2a OK: family=" + Typography.AppliedFontFamily + "; size=" + Typography.AppliedFontSize);
                Log("RESOURCE 2b: construct shared typography through XAML metadata.");
                var xamlTypography = (ResourceDictionary)XamlReader.Load("<services:TypographyResources xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:services=\"using:Kit.Settings.UI.Services\" />");
                Require(xamlTypography.GetType() == typeof(Typography), "XAML resolves typography from the QuickAccess assembly.");
                Resources.MergedDictionaries.Add(xamlTypography);
                Log("RESOURCE 2b OK.");
                LoadDictionary("RESOURCE 3", "ms-appx:///Resources/Styles/Button.xaml");
                LoadDictionary("RESOURCE 4", "ms-appx:///Resources/Styles/TextBlock.xaml");
                LoadDictionary("RESOURCE 5", "ms-appx:///Resources/Themes/Colors.xaml");
                Log("RESOURCE 6: install native typography wrappers.");
                Typography.ApplyNativeTextStyles(Resources);
                Log("RESOURCE 6 OK.");
                Log("RESOURCE 7: converter metadata.");
                _ = XamlReader.Load("<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:c=\"using:CommunityToolkit.WinUI.Converters\"><c:BoolToVisibilityConverter x:Key=\"Visibility\" FalseValue=\"Collapsed\" TrueValue=\"Visible\" /><c:BoolNegationConverter x:Key=\"Negation\" /><c:StringVisibilityConverter x:Key=\"String\" /></ResourceDictionary>");
                Log("RESOURCE 7 OK. Individual resources loaded; the production App startup still requires its separate acceptance run.");
                Finish(0, "DIAGNOSTIC PASS");
            }
            catch (Exception ex) { Finish(5, "RESOURCE DIAGNOSTIC FAILURE: " + ex); }
        }

        private void LoadDictionary(string stage, string uri)
        {
            Log(stage + ": loading compiled resource " + uri);
            var dictionary = new ResourceDictionary { Source = new Uri(uri) };
            Resources.MergedDictionaries.Add(dictionary);
            Log(stage + " MERGED; materializing its entries.");
            foreach (var key in dictionary.Keys.ToArray())
            {
                Log(stage + " KEY: " + key);
                _ = dictionary[key];
            }
            Log(stage + " OK.");
        }
    }

    private static async Task AttachToOwnedWindow()
    {
        Log("LAUNCH: awaiting production OnLaunched window.");
        var windowField = typeof(QuickApp).GetField("_window", BindingFlags.NonPublic | BindingFlags.Static);
        await WaitUntil(() => windowField.GetValue(null) is QuickWindow, "production QuickAccess window");
        Window = (QuickWindow)windowField.GetValue(null);
        var context = (QuickContext)typeof(QuickWindow).GetField("_launchContext", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window);
        Require(context.ShowEventName == null && context.ExitEventName == null && context.RunnerPipeName == null && context.AppPipeName == null,
            "Production launch context has no runner, pipe or named event.");
        Log("LAUNCH: production window has an empty launch context; installing inert fixture.");
        Repository = SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default);
        Repository.StopWatching();
        Require(Repository.SettingsConfig.UiFontFamily == StartupFamily && Repository.SettingsConfig.UiFontSize == StartupSize, "Startup fixture typography.");
        Require(!Repository.SettingsConfig.Enabled.AiHub && !Repository.SettingsConfig.Enabled.Winget, "Workers are disabled.");
        Family = StartupFamily;
        Size = StartupSize;
        Require(Window.Content is FrameworkElement, "Production window has a XAML content root.");
        // Attach before Loaded can realize the visual tree. Use the production
        // x:Name field so neither a root ShellPage nor an unrealized wrapper
        // depends on VisualTreeHelper already exposing its children.
        var shellField = typeof(QuickWindow).GetField("ShellHost", BindingFlags.NonPublic | BindingFlags.Instance);
        Require(shellField != null && shellField.FieldType == typeof(QuickShell), "Production window owns its named ShellHost.");
        Shell = (QuickShell)shellField.GetValue(Window);
        Require(Shell != null && Shell.Name == "ShellHost", "Production ShellHost has been initialized.");
        Launcher = (QuickLauncherViewModel)typeof(QuickWindow).GetField("_launcherViewModel", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window);
        AllApps = (QuickAllAppsViewModel)typeof(QuickWindow).GetField("_allAppsViewModel", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Window);
        typeof(QuickLauncherViewModel).GetField("_coordinator", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(Launcher, Coordinator);
        typeof(QuickAllAppsViewModel).GetField("_coordinator", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(AllApps, Coordinator);
        PopulateInertTiles();
        Shell.Initialize(Coordinator, Launcher, AllApps);
        Frame = (Frame)Shell.FindName("ContentFrame");
        Require(Frame != null, "Production ShellHost owns its named navigation frame.");
        // A cached Back navigation may restore its original context. Replace that
        // page's coordinator again before any test-owned button invocation.
        Frame.Navigated += (_, args) => SetInertPageCoordinator(args.Content);
        SetInertPageCoordinator(Frame.Content);
        Log("LAUNCH: fixture attached to the production window and original view models.");
    }

    private static void SetInertPageCoordinator(object page)
    {
        if (page is QuickLaunchPage launch)
            typeof(QuickLaunchPage).GetField("_coordinator", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(launch, Coordinator);
    }

    private static async Task Run()
    {
        Log("RUN: waiting for initial page.");
        typeof(QuickWindow).GetMethod("ShowWindow", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Window, null);
        await WaitUntil(() => Frame.Content is QuickLaunchPage { IsLoaded: true }, "initial LaunchPage");
        await Settle();
        var launchPage = (QuickLaunchPage)Frame.Content;
        Require(ReferenceEquals(Window.Content, Shell) || Descendants((DependencyObject)Window.Content).Contains(Shell),
            "Named production ShellHost belongs to the realized window visual tree.");
        Require(ReferenceEquals(launchPage.Frame, Frame), "Initial LaunchPage belongs to the production navigation frame.");
        VerifyPage(launchPage);
        await Capture("startup-launch.png");
        await ChangeTypography("system", 14);
        VerifyPage(launchPage);
        await ChangeTypography("Noto Sans SC", 20);
        VerifyPage(launchPage);
        await Capture("noto20-launch.png");
        Require(Frame.BackStackDepth == 0 && Frame.ForwardStack.Count == 0, "App list has not been visited before the live switch to 20.");
        await More();
        var appsPage = (QuickAppsPage)Frame.Content;
        VerifyPage(appsPage);
        await Capture("noto20-first-apps.png");
        await Back();
        Require(ReferenceEquals(Frame.Content, launchPage), "Back retains the cached launch page.");
        VerifyPage(launchPage);
        await More();
        Require(ReferenceEquals(Frame.Content, appsPage), "More retains the cached app list.");
        VerifyPage(appsPage);
        await Capture("noto20-cached-apps.png");
        await ChangeTypography("system", 14);
        VerifyPage(appsPage);
        await Back();
        VerifyPage((FrameworkElement)Frame.Content);
        await Capture("restored14-launch.png");
        await More();
        VerifyPage((FrameworkElement)Frame.Content);
        await Capture("restored14-apps.png");
        Require(Frame.BackStackDepth <= 1, "More/Back does not accumulate navigation history.");
        Require(!Process.GetProcesses().Any(p => p.ProcessName is "Kit.AIHubWorker" or "Kit.WingetService" or "winget"), "No worker was started.");
        Log("Verified startup, inert shortcut tiles, live 14 -> 20 -> 14, first app-list creation after scaling, cached More/Back, module labels, font family, and fixed icon sizes.");
    }

    private static void PopulateInertTiles()
    {
        var quickAccess = (QuickAccessViewModel)typeof(QuickLauncherViewModel)
            .GetField("_quickAccessViewModel", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Launcher);
        // Freeze only this fixture's visibility source. General settings remain
        // disabled and real typography/repository notifications still run.
        quickAccess.Dispose();
        var allItems = (List<QuickAccessItem>)typeof(QuickAccessViewModel)
            .GetField("_allItems", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(quickAccess);
        allItems.Clear();
        quickAccess.Items.Clear();
        foreach (var item in TileFixtures)
        {
            item.Icon = Kit.Settings.UI.Library.Helpers.ModuleHelper.GetModuleTypeFluentIconName((ModuleType)item.Tag);
            Require(item.Command == null, "Shortcut fixture cannot launch a module.");
            allItems.Add(item);
            quickAccess.Items.Add(item);
        }
        typeof(QuickAccessViewModel).GetProperty("VisibleItemCount").SetValue(quickAccess, TileFixtures.Length);
        Require(Launcher.HasVisibleItems, "Inert shortcut tiles are visible in the production view model.");
    }

    private static async Task ChangeTypography(string family, int size)
    {
        var settings = Repository.SettingsConfig;
        settings.UiFontFamily = family;
        settings.UiFontSize = size;
        SettingsUtils.Default.SaveSettings(settings.ToJsonString());
        Require(await Task.Run(() => Repository.ReloadSettings()), "Reload typography through the production repository event.");
        Family = family;
        Size = size;
        await WaitUntil(() => Typography.AppliedFontFamily == family && Typography.AppliedFontSize == size, "typography event applied");
        await Settle();
        Log($"Applied family={family}; size={size}.");
    }

    private static void VerifyPage(FrameworkElement page)
    {
        Require(Typography.AppliedFontFamily == Family && Typography.AppliedFontSize == Size, "QuickAccess owns its typography state.");
        string expectedFamily = new FontFamily(Family == "system" ? "XamlAutoFontFamily" : "ms-appx:///Assets/Fonts/NotoSansSC-Variable.ttf#Noto Sans SC").Source;
        Require(((FontFamily)Application.Current.Resources["KitUiFontFamily"]).Source == expectedFamily, "Live font resource.");
        Near((double)Application.Current.Resources["KitFontSize14"], Size, "Live size resource");
        var header = Descendants(page).OfType<TextBlock>().FirstOrDefault(text => Typography.GetBaseFontSize(text) == 14 && !string.IsNullOrWhiteSpace(text.Text));
        Require(header != null, "Native body heading is realized.");
        VerifyText(header, Size, expectedFamily, "page heading");
        if (page is QuickLaunchPage)
        {
            var more = Button(page, "QuickAccess_MoreButton");
            var caption = Descendants(more).OfType<TextBlock>().First(text => !string.IsNullOrWhiteSpace(text.Text));
            VerifyText(caption, Math.Round(12 * Size / 14d, 3), expectedFamily, "More caption");
            Near(Descendants(more).OfType<FontIcon>().Single().FontSize, 12, "More chevron");
            var shortcuts = Descendants(page).OfType<QuickAccessList>().Single();
            var tileLabels = Descendants(shortcuts).OfType<TextBlock>()
                .Where(text => TileFixtures.Any(item => item.Title == text.Text)).ToList();
            Require(tileLabels.Count == TileFixtures.Length, "All inert shortcut labels are realized.");
            foreach (var label in tileLabels) VerifyText(label, Math.Round(12 * Size / 14d, 3), expectedFamily, "shortcut " + label.Text);
            var tileImages = Descendants(shortcuts).OfType<Image>().ToList();
            Require(tileImages.Count == TileFixtures.Length, "All inert shortcut icons are realized.");
            foreach (var icon in tileImages)
            {
                Near(icon.ActualWidth, 32, "Shortcut icon width");
                Near(icon.ActualHeight, 32, "Shortcut icon height");
            }
        }
        else
        {
            var list = Descendants(page).OfType<ModuleList>().Single();
            var labels = AllApps.FlyoutMenuItems.Select(item => item.Label).Where(label => !string.IsNullOrWhiteSpace(label)).ToHashSet();
            var renderedLabels = Descendants(list).OfType<TextBlock>().Where(text => labels.Contains(text.Text)).ToList();
            Require(renderedLabels.Count > 0, "App labels are realized.");
            foreach (var label in renderedLabels) VerifyText(label, Size, expectedFamily, "module " + label.Text);
            Near(Descendants(Button(page, "QuickAccess_BackButton")).OfType<FontIcon>().Single().FontSize, 14, "Back icon");
        }

        foreach (var icon in Descendants(page).OfType<FontIcon>())
        {
            if (IconSizes.TryGetValue(icon, out double initial)) Near(icon.FontSize, initial, "Icon preserved " + icon.Glyph);
            else IconSizes.Add(icon, icon.FontSize);
        }
        Log($"Verified {page.GetType().Name}, family={Family}, size={Size}, icons={IconSizes.Count}.");
    }

    private static void VerifyText(TextBlock text, double size, string family, string label)
    {
        Near(text.FontSize, size, label + " size");
        Require(text.FontFamily.Source == family, label + " family: " + text.FontFamily.Source);
    }

    private static async Task More()
    {
        Invoke(Button((FrameworkElement)Frame.Content, "QuickAccess_MoreButton"));
        await WaitUntil(() => Frame.Content is QuickAppsPage { IsLoaded: true }, "More navigation");
        await Settle();
    }

    private static async Task Back()
    {
        Invoke(Button((FrameworkElement)Frame.Content, "QuickAccess_BackButton"));
        await WaitUntil(() => Frame.Content is QuickLaunchPage { IsLoaded: true }, "Back navigation");
        await Settle();
    }

    private static Button Button(FrameworkElement page, string id) => Descendants(page).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == id);

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static async Task Settle()
    {
        await Task.Delay(350);
        ((FrameworkElement)Window.Content).UpdateLayout();
        var ready = new TaskCompletionSource<bool>();
        DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, () => ready.SetResult(true));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntil(Func<bool> condition, string label)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(label);
            await Task.Delay(30);
        }
    }

    private static async Task Capture(string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync((FrameworkElement)Window.Content);
        Require(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "Capture contains the QuickAccess window.");
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
        Log($"Captured {name}: {bitmap.PixelWidth}x{bitmap.PixelHeight}.");
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

    private static void Near(double actual, double expected, string label) => Require(Math.Abs(actual - expected) < 0.01, $"{label}: expected {expected}, actual {actual}.");

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static void Log(string message) => File.AppendAllText(Report, message + "\n");

    private static void Finish(int code, string message)
    {
        Log(message);
        Launcher?.Dispose();
        AllApps?.Dispose();
        Window?.Dispose();
        Environment.Exit(code);
    }

    private sealed class InertCoordinator : QuickCoordinator
    {
        public void HideFlyout() => throw new InvalidOperationException("Unexpected hide action.");
        public void OpenSettings() => throw new InvalidOperationException("Unexpected Settings action.");
        public void OpenModuleSettings(ModuleType type) => throw new InvalidOperationException("Unexpected module action.");
        public void OpenGeneralSettingsForUpdates() => throw new InvalidOperationException("Unexpected update action.");
        public Task<bool> ShowDocumentationAsync() => throw new InvalidOperationException("Unexpected documentation action.");
        public bool UpdateModuleEnabled(ModuleType type, bool enabled) => throw new InvalidOperationException("Unexpected module toggle.");
        public void SendSortOrderUpdate(GeneralSettings settings) => throw new InvalidOperationException("Unexpected sort write.");
        public void OnModuleLaunched(ModuleType type) => throw new InvalidOperationException("Unexpected module launch.");
    }
}
