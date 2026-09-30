using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Kit.Settings.UI.Views;
using ManagedCommon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using CommunityToolkit.WinUI.Controls;

internal static class ModulePageSmoke
{
    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();

    private static readonly string Report = Path.Combine(Environment.GetEnvironmentVariable("KIT_MODULE_PAGE_TEST_OUTPUT"), "module-pages-smoke.log");
    private static readonly (ModuleType Module, Type Page)[] Modules =
    {
        (ModuleType.Awake, typeof(AwakePage)),
        (ModuleType.LightSwitch, typeof(LightSwitchPage)),
        (ModuleType.Localserver, typeof(LocalserverPage)),
        (ModuleType.UDPtest, typeof(UDPtestPage)),
        (ModuleType.AIHub, typeof(AIHubPage)),
    };

    [STAThread]
    private static void Main()
    {
        new Kit.AiHub.Storage.AiHubSettingsStore().Update(config => config.IsEnabled = false);
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        WindowsAppRuntime_EnsureIsLoaded();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        File.WriteAllText(Report, "Real WinUI page navigation and Utilities binding callbacks; in-memory Runner replies.\n");
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            var app = new App();
            DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
            {
                await Task.Delay(1500);
                try
                {
                    await Run();
                    File.AppendAllText(Report, "PASS\n");
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    File.AppendAllText(Report, ex.GetType().FullName + ": " + ex.Message + "\n" + ex.StackTrace + "\n");
                    Environment.Exit(1);
                }
            });
        });
    }

    private static async Task Run()
    {
        var repository = SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default);
        repository.StopWatching();
        repository.SettingsConfig = new GeneralSettings();
        foreach (var entry in Modules)
        {
            ModuleHelper.SetIsModuleEnabled(repository.SettingsConfig, entry.Module, false);
        }

        int toggleMessages = 0;
        ShellPage.SetDefaultSndMessageCallback(message =>
        {
            using var json = JsonDocument.Parse(message);
            if (json.RootElement.TryGetProperty("module_status", out var states))
            {
                toggleMessages++;
                repository.SettingsConfig.Enabled.MergeFrom(JsonSerializer.Deserialize<EnabledModules>(states.GetRawText()));
                repository.NotifySettingsChanged();
            }
            else if (json.RootElement.TryGetProperty("general", out _))
            {
                throw new InvalidOperationException("A page refresh sent a general-settings command.");
            }
        });

        // First visit all pages while off, retaining UDPtest/AIHub/Localserver instances.
        foreach (var entry in Modules)
        {
            await NavigateAndCheck(entry.Page, false);
        }

        for (int cycle = 0; cycle < 2; cycle++)
        {
            foreach (bool enabled in new[] { true, false })
            {
                NavigationService.Navigate(typeof(DashboardPage));
                await Task.Delay(150);
                var dashboard = ((DashboardPage)NavigationService.Frame.Content).ViewModel;
                foreach (var entry in Modules)
                {
                    dashboard.AllModules.Single(item => item.Tag == entry.Module).IsEnabled = enabled;
                }

                await Task.Delay(150);
                foreach (var entry in Modules)
                {
                    await NavigateAndCheck(entry.Page, enabled);
                }
            }
        }

        if (toggleMessages != 20) throw new InvalidOperationException("Expected exactly 20 Utilities commands, got " + toggleMessages);
        File.AppendAllText(Report, "20 Utilities commands; 25 real page-state assertions; no commands from page refresh.\n");

        ShellPage.SetDefaultSndMessageCallback(_ => { });
        NavigationService.Navigate(typeof(GeneralPage));
        await Task.Delay(200);
        var aiService = ((GeneralPage)NavigationService.Frame.Content).ViewModel.AiServices;
        if (!aiService.CanToggle) throw new InvalidOperationException("The shared AI service toggle is disabled.");
        File.AppendAllText(Report, "General page AI service toggle: available.\n");
        await CheckAiServiceControls((GeneralPage)NavigationService.Frame.Content);
        await CheckAiHubPage(repository);
    }

    private static async Task CheckAiHubPage(SettingsRepository<GeneralSettings> repository)
    {
        var store = new Kit.AiHub.Storage.AiHubSettingsStore();
        byte[] serviceSettings = File.ReadAllBytes(store.SettingsFilePath);
        byte[] secrets = File.ReadAllBytes(Path.Combine(store.DataDirectory, "secrets.dat"));
        int pluginWrites = 0;
        ShellPage.SetDefaultSndMessageCallback(message =>
        {
            using var json = JsonDocument.Parse(message);
            if (json.RootElement.TryGetProperty("AIHub", out var plugin))
            {
                // Reproduce the native module's actual settings-file writeback.
                SettingsUtils.Default.SaveSettings(plugin.GetRawText(), AIHubSettings.ModuleName);
                pluginWrites++;
            }
        });
        ModuleHelper.SetIsModuleEnabled(repository.SettingsConfig, ModuleType.AIHub, true);
        NavigationService.Navigate(typeof(AIHubPage));
        await Task.Delay(200);
        var page = (AIHubPage)NavigationService.Frame.Content;
        var vm = page.ViewModel;
        vm.RefreshEnabledState();
        if (vm.AiHub.ActivePolicyIndex != 1) throw new InvalidOperationException("Task policy editor initially selects the global policy.");

        var presenter = App.GetSettingsWindow().AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        presenter?.Restore();
        foreach (int width in new[] { 1350, 1050 })
        {
            App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1000));
            for (int tab = 0; tab < 2; tab++)
            {
                vm.ActiveTabIndex = tab;
                await Task.Delay(250);
                foreach (var viewer in Descendants(page).OfType<ScrollViewer>()) viewer.ChangeView(null, 0, null, true);
                await Task.Delay(150);
                await RenderPage(page, $"aihub-tab-{tab}-{width}.png");
            }
        }

        vm.ActiveTabIndex = 2;
        await Task.Delay(150);
        vm.AiHub.CurrentPolicyContent += "\nSmoke test policy save.";
        vm.AiHub.SaveActivePolicyCommand.Execute(null);
        for (int attempt = 0; attempt < 50 && vm.AiHub.IsBusy; attempt++) await Task.Delay(100);
        var policyStatus = (InfoBar)page.FindName("AiTaskPolicyStatus");
        if (!policyStatus.IsOpen || policyStatus.Severity != InfoBarSeverity.Success) throw new InvalidOperationException("Task policy save feedback is missing.");
        vm.AiHub.TaskPolicyIndex = 1;
        if (vm.AiHub.ActivePolicyIndex != 2) throw new InvalidOperationException("Optimization policy selection is incorrect.");

        if (pluginWrites < 3 || !serviceSettings.SequenceEqual(File.ReadAllBytes(store.SettingsFilePath)) ||
            !secrets.SequenceEqual(File.ReadAllBytes(Path.Combine(store.DataDirectory, "secrets.dat"))))
            throw new InvalidOperationException("Plugin settings writeback changed AI service configuration or credentials.");

        ModuleHelper.SetIsModuleEnabled(repository.SettingsConfig, ModuleType.AIHub, false);
        vm.RefreshEnabledState();
        await vm.RefreshAiReadinessAsync();
        var recheck = Descendants(page).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "AIHub_RecheckAiButton");
        if (recheck.IsEnabled || vm.IsCheckingAiReadiness) throw new InvalidOperationException("Disabled plugin allowed a readiness probe.");

        var open = Descendants(page).OfType<HyperlinkButton>().Single(button => AutomationProperties.GetAutomationId(button) == "AIHub_OpenServiceSettingsButton");
        new Microsoft.UI.Xaml.Automation.Peers.HyperlinkButtonAutomationPeer(open).Invoke();
        await Task.Delay(200);
        if (NavigationService.Frame.Content is not GeneralPage general || !general.ViewModel.AiServices.CanEdit)
            throw new InvalidOperationException("AI service settings navigation did not open an editable service.");
        File.AppendAllText(Report, "AI Hub: native-style plugin writeback, policy selection/save feedback, disabled re-check, service navigation, and four layout renders passed.\n");
    }

    private static async Task RenderPage(Page page, string name)
    {
        page.UpdateLayout();
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(page);
        var buffer = await bitmap.GetPixelsAsync();
        byte[] pixels = new byte[buffer.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(Report));
        var file = await folder.CreateFileAsync(name, Windows.Storage.CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    private static async Task CheckAiServiceControls(GeneralPage page)
    {
        var service = page.ViewModel.AiServices;
        var toggle = Descendants(page).OfType<ToggleSwitch>().Single(control => AutomationProperties.GetAutomationId(control) == "Toggle_AiHub");
        var status = (SettingsCard)page.FindName("AiServiceStatusCard");
        var kernel = (SettingsExpander)page.FindName("AiKernelSettingsExpander");
        var main = (SettingsExpander)page.FindName("AiMainEndpointExpander");
        var fallback = (SettingsExpander)page.FindName("AiFallbackEndpointExpander");
        var policy = (SettingsExpander)page.FindName("AiPolicyRulesExpander");

        for (int cycle = 0; cycle < 2; cycle++)
        {
            toggle.IsOn = false;
            await Task.Delay(200);
            if (service.IsEnabled || service.HubContentVisibility != Visibility.Collapsed) throw new InvalidOperationException("AI service did not switch off.");
            if (service.SelfTestCommand.CanExecute(null) || service.MainEndpoint.TestCommand.CanExecute(null)) throw new InvalidOperationException("AI service commands remain enabled while off.");
            var toggled = new TaskCompletionSource();
            page.DispatcherQueue.TryEnqueue(() =>
            {
                var previousContext = SynchronizationContext.Current;
                try
                {
                    // Native UI callbacks must also work without an ambient managed context.
                    if (cycle == 1) SynchronizationContext.SetSynchronizationContext(null);
                    File.AppendAllText(Report, "Toggle callback context=" + (SynchronizationContext.Current?.GetType().Name ?? "null") + "\n");
                    toggle.IsOn = true;
                    toggled.SetResult();
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            });
            await toggled.Task;
            for (int attempt = 0; attempt < 50 && service.IsBusy; attempt++) await Task.Delay(100);
            await Task.Delay(200);
            foreach (var expander in new[] { kernel, main, fallback, policy }) expander.IsExpanded = true;
            await Task.Delay(200);
            File.AppendAllText(Report, $"AI service cycle={cycle} enabled={service.IsEnabled} busy={service.IsBusy} canEdit={service.CanEdit}\n");
            var required = new List<Control>();
            foreach (var root in new DependencyObject[] { status, kernel, main, fallback, policy })
            {
                ((FrameworkElement)root).StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                page.UpdateLayout();
                await Task.Delay(250);
                required.AddRange(Descendants(root).OfType<Control>().Where(control =>
                    control is TextBox or PasswordBox or ComboBox ||
                    control is Button button && button.Command != null && button.Visibility == Visibility.Visible));
            }

            foreach (var control in required)
            {
                File.AppendAllText(Report, $"  {control.GetType().Name} {control.Name} enabled={control.IsEnabled}\n");
            }

            if (!service.CanEdit || required.Count < 15 || required.Any(control => !control.IsEnabled)) throw new InvalidOperationException("AI service settings contain disabled controls after enabling.");
            if (!Descendants(policy).OfType<TextBox>().Any()) throw new InvalidOperationException("Policy editor was not checked.");

            foreach (var entry in new[] { (Expander: main, Endpoint: service.MainEndpoint), (Expander: fallback, Endpoint: service.FallbackEndpoint) })
            {
                var modelBox = entry.Expander.Items.OfType<SettingsCard>().Select(card => card.Content).OfType<TextBox>()
                    .Single(box => box.PlaceholderText == "gpt-5.6-luna");
                string savedModel = entry.Endpoint.Model;
                modelBox.Text = "ai-settings-smoke";
                if (entry.Endpoint.Model != modelBox.Text || !service.SaveEndpointsCommand.CanExecute(null)) throw new InvalidOperationException("Endpoint edit did not enable saving.");
                service.SaveEndpointsCommand.Execute(null);
                var saved = new Kit.AiHub.Storage.AiHubSettingsStore().Load();
                if (saved.Targets.Single(target => target.Name == entry.Endpoint.Settings.Name).Model != modelBox.Text) throw new InvalidOperationException("Endpoint edit was not saved.");
                modelBox.Text = savedModel;
                service.SaveEndpointsCommand.Execute(null);
                if (service.HasUnsavedEndpointChanges) throw new InvalidOperationException("Endpoint restore was not saved.");
            }

            var completion = new TaskCompletionSource();
            Task operation;
            var operationContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                operation = (Task)typeof(AiHubViewModel).GetMethod("RunOperationAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(service, new object[] { new Func<CancellationToken, Task>(_ => completion.Task) });
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(operationContext);
            }
            if (!service.IsBusy || service.SelfTestCommand.CanExecute(null)) throw new InvalidOperationException("Self-test remains enabled while an operation is running.");
            await Task.Run(() => completion.SetResult());
            await operation;
            if (required.Any(control => !control.IsEnabled)) throw new InvalidOperationException("AI service controls did not recover after the operation.");

            NavigationService.Navigate(typeof(DashboardPage));
            await Task.Delay(150);
            NavigationService.Navigate(typeof(GeneralPage));
            await Task.Delay(150);
        }
        File.AppendAllText(Report, "AI service controls: off/on twice, expanded editors, endpoint editing/saving, busy recovery, and cached navigation passed.\n");
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

    private static async Task NavigateAndCheck(Type pageType, bool expected)
    {
        if (!NavigationService.Navigate(pageType)) throw new InvalidOperationException("Navigation failed: " + pageType.Name);
        await Task.Delay(200);
        var page = (Page)NavigationService.Frame.Content;
        var viewModel = page.DataContext;
        bool actual = (bool)viewModel.GetType().GetProperty("IsEnabled").GetValue(viewModel);
        File.AppendAllText(Report, pageType.Name + " expected=" + expected + " actual=" + actual + " cache=" + page.NavigationCacheMode + "\n");
        if (actual != expected) throw new InvalidOperationException("Stale enabled state: " + pageType.Name);
    }
}
