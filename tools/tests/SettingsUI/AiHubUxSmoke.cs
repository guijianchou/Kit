using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.Settings.UI;
using Kit.Settings.UI.Controls;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Kit.Settings.UI.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

internal static class AiHubUxSmoke
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string Output = Environment.GetEnvironmentVariable("KIT_AI_HUB_UX_TEST_OUTPUT");
    private static readonly string Report = Path.Combine(Output, "aihub-ux-smoke.log");

    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();

    [STAThread]
    private static void Main()
    {
        File.WriteAllText(Report, "Real WinUI; synthetic in-memory audit/candidate/history data. No scans, AI requests, confirmed file operations, policy saves or clipboard writes.\n");
        try
        {
            Require(!new Kit.AiHub.Storage.AiHubSettingsStore().Load().IsEnabled, "Wrapper must disable the shared AI service before startup.");
            EnsureNoWorker();
            Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
            WindowsAppRuntime_EnsureIsLoaded();
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                var app = new App { StartupPage = typeof(GeneralPage) };
                DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
                {
                    try { await Task.Delay(350); await Run(); Log("PASS"); Environment.Exit(0); }
                    catch (Exception ex) { Log(ex.ToString()); Environment.Exit(1); }
                });
            });
        }
        catch (Exception ex) { Log(ex.ToString()); Environment.Exit(2); }
    }

    private static async Task Run()
    {
        var general = SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default);
        general.StopWatching();
        general.SettingsConfig = new GeneralSettings();
        general.SettingsConfig.Enabled.AiHub = false;
        general.NotifySettingsChanged();
        var module = SettingsRepository<AIHubSettings>.GetInstance(SettingsUtils.Default);
        module.StopWatching();
        module.SettingsConfig = new AIHubSettings();
        ShellPage.SetDefaultSndMessageCallback(_ => { });
        var window = App.GetSettingsWindow();
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1000));
        await Navigate(typeof(AIHubPage));
        var page = (AIHubPage)NavigationService.Frame.Content;
        var vm = page.ViewModel;
        await vm.AiHub.Initialization;
        await WaitUntil(() => !Field<bool>(vm, "_loadingOptimizationHistory"));
        Require(!vm.IsEnabled && !vm.AiHub.IsEnabled, "AI Hub and shared service must start disabled.");
        Require(!vm.FastScanCommand.CanExecute(null) && !vm.ScanAllCommand.CanExecute(null), "Disabled module must block scans.");
        Require(!Descendants(page).OfType<TextBox>().Any(box => box.Name == "AiTaskPolicyContentBox"), "Task editor must remain unloaded before its first visit.");
        await CheckPolicyDrafts(page);

        // This changes only the stopped in-memory repository. Never toggle the page's
        // persisted module switch, and never invoke a scan or accept an operation dialog.
        general.SettingsConfig.Enabled.AiHub = true;
        vm.RefreshEnabledState();
        Require(vm.IsEnabled && !vm.AiHub.IsEnabled, "Synthetic selection needs only the local module gate.");
        var items = InjectCandidates(vm);
        await CheckSelectionAndFilters(page, items);
        await CheckConfirmation(page);
        await CheckAuditSnapshotIsolation(page);
        AddHistoryFixture(vm);
        await CheckScrollAndLayouts(page);
        EnsureNoWorker();
        Require(!vm.IsOptimizing && !vm.IsAuditing && !vm.IsAiAnalyzing, "The smoke must finish with no work running.");
    }

    private static async Task CheckPolicyDrafts(AIHubPage page)
    {
        var vm = page.ViewModel;
        vm.ActiveTabIndex = 2;
        await WaitUntil(() => vm.AiHub.PoliciesLoaded && !vm.AiHub.IsBusy);
        await NextRendering();
        var editor = (TextBox)page.FindName("AiTaskPolicyContentBox");
        Require(editor != null && !editor.IsReadOnly && vm.AiHub.CanEditPolicies, "Disabled AI service must still permit local policy editing.");
        vm.AiHub.TaskPolicyIndex = 0;
        string saved = vm.AiHub.CurrentPolicyContent;
        string draft = saved + "\n<!-- UI smoke draft; never saved -->";
        vm.AiHub.CurrentPolicyContent = draft;
        Log($"Policy before switch: active={vm.AiHub.ActivePolicyIndex}, task={vm.AiHub.TaskPolicyIndex}, auditLength={vm.AiHub.SecurityAuditPolicyContent.Length}, optimizationLength={vm.AiHub.OptimizationPolicyContent.Length}, selfTestLength={vm.AiHub.SelfTestPolicyContent.Length}");
        Require(vm.AiHub.IsCurrentPolicyDirty && vm.AiHub.SaveActivePolicyCommand.CanExecute(null), "A task draft must enable Save while AI is disabled.");
        vm.AiHub.TaskPolicyIndex = 2;
        Require(vm.AiHub.IsSelfTestPolicyTab && vm.AiHub.CurrentPolicyFilePath.EndsWith(Path.Combine("self-test", "AGENTS.md"), StringComparison.OrdinalIgnoreCase), "Self-test must select its own task policy.");
        vm.AiHub.TaskPolicyIndex = 0;
        Log($"Policy after switch: active={vm.AiHub.ActivePolicyIndex}, task={vm.AiHub.TaskPolicyIndex}, auditLength={vm.AiHub.SecurityAuditPolicyContent.Length}, optimizationLength={vm.AiHub.OptimizationPolicyContent.Length}, selfTestLength={vm.AiHub.SelfTestPolicyContent.Length}, expectedLength={draft.Length}, actualLength={vm.AiHub.CurrentPolicyContent.Length}, normalizedEqual={NormalizeNewlines(vm.AiHub.CurrentPolicyContent) == NormalizeNewlines(draft)}, markerRetained={vm.AiHub.CurrentPolicyContent.Contains("UI smoke draft; never saved", StringComparison.Ordinal)}");
        Require(NormalizeNewlines(vm.AiHub.CurrentPolicyContent) == NormalizeNewlines(draft), "Switching task policies must preserve the unsaved draft.");
        vm.AiHub.DiscardActivePolicyCommand.Execute(null);
        Require(NormalizeNewlines(vm.AiHub.CurrentPolicyContent) == NormalizeNewlines(saved) && !vm.AiHub.IsCurrentPolicyDirty, "Discard must restore only the current saved draft.");
        Log("Policy lazy construction, disabled-service editing, self-test mapping and per-file drafts PASS");
    }

    private static List<TempFileInfo> InjectCandidates(AIHubPageViewModel vm)
    {
        var items = Enumerable.Range(0, 256).Select(index => new TempFileInfo
        {
            ItemId = "synthetic-" + index,
            FileName = (index < 128 ? "visible-" : "hidden-") + index.ToString("D4") + ".cache",
            FilePath = Path.Combine(@"C:\SyntheticOnly", index < 128 ? "A" : "B", index + ".cache"),
            CacheRoot = Path.Combine(@"C:\SyntheticOnly", index < 128 ? "A" : "B"),
            Category = "Synthetic cache",
            SizeInBytes = 1024L * (index + 1),
            ReviewState = "approved",
            IsSelected = false,
            ReasonEn = "Synthetic review fixture; no real file exists.",
            ReasonZh = "仅用于界面测试的内存数据，不存在真实文件。",
        }).ToList();
        items.Add(new TempFileInfo { FileName = "skipped.cache", FilePath = @"C:\SyntheticOnly\A\skipped.cache", CacheRoot = @"C:\SyntheticOnly\A", Category = "Synthetic cache", ReviewState = "skipped", Action = "delete", IsSelected = false });
        items.Add(new TempFileInfo { FileName = "failed.cache", FilePath = @"C:\SyntheticOnly\A\failed.cache", CacheRoot = @"C:\SyntheticOnly\A", Category = "Synthetic cache", ReviewState = "failed", IsSelected = false });
        Field<List<TempFileInfo>>(vm, "_observedOptimizationItems").Clear();
        Field<List<TempFileInfo>>(vm, "_observedOptimizationItems").AddRange(items);
        vm.OptimizationCandidates.Clear();
        var changed = (PropertyChangedEventHandler)Delegate.CreateDelegate(typeof(PropertyChangedEventHandler), vm,
            typeof(AIHubPageViewModel).GetMethod("Candidate_PropertyChanged", PrivateInstance));
        foreach (var item in items.Where(item => item.IsActionable))
        {
            item.PropertyChanged += changed;
            vm.OptimizationCandidates.Add(item);
        }
        Invoke(vm, "RecalculateCandidateMetrics");
        return items;
    }

    private static async Task CheckSelectionAndFilters(AIHubPage page, List<TempFileInfo> items)
    {
        var vm = page.ViewModel;
        vm.ActiveTabIndex = 1;
        var category = vm.OptimizationCategories[0];
        var group = vm.CacheGroups[0];
        category.IsExpanded = group.IsExpanded = true;
        vm.SelectAllCandidatesCommand.Execute(null);
        Require(vm.SelectedCandidatesCount == 256, "Select visible must select all approved rows in the unfiltered view.");
        items[0].IsSelected = false;
        Require(vm.SelectedCandidatesCount == 255, "A checkbox must update the selection totals.");
        Require(ReferenceEquals(category, vm.OptimizationCategories[0]) && ReferenceEquals(group, vm.CacheGroups[0]) && category.IsExpanded && group.IsExpanded,
            "Selection changes must retain category/group references and expansion state.");
        vm.DeselectAllCandidatesCommand.Execute(null);
        items[128].IsSelected = true;
        vm.OptimizationSearchText = "visible-";
        Require(vm.OptimizationCategories.Sum(row => row.VisibleItems.Count) == 128, "Search must filter leaf rows.");
        vm.SelectAllCandidatesCommand.Execute(null);
        Require(items.Take(128).All(item => item.IsSelected) && items[128].IsSelected && items.Skip(129).All(item => !item.IsSelected), "Select visible must preserve every hidden selection.");
        vm.DeselectAllCandidatesCommand.Execute(null);
        Require(items.Take(128).All(item => !item.IsSelected) && items[128].IsSelected, "Deselect visible must preserve hidden rows.");
        vm.OptimizationSearchText = string.Empty;
        vm.OptimizationFilterIndex = 2;
        Require(vm.OptimizationCategories.Sum(row => row.VisibleItems.Count) == 1 && !vm.OptimizationCategories.SelectMany(row => row.VisibleItems).Single().IsActionable, "Skipped filter must show a non-actionable review row.");
        vm.OptimizationFilterIndex = 4;
        Require(vm.OptimizationCategories.SelectMany(row => row.VisibleItems).Single() == items[128], "Selected filter must project the actual selection.");
        vm.OptimizationSearchText = "fixture-no-match";
        await NextRendering();
        Require(((FrameworkElement)page.FindName("OptimizationNoFilterMatches")).Visibility == Visibility.Visible, "No-match filters need a dedicated empty state.");
        vm.OptimizationSearchText = string.Empty;
        vm.OptimizationFilterIndex = 0;
        vm.SelectAllCandidatesCommand.Execute(null);
        foreach (var row in vm.OptimizationCategories) row.IsExpanded = true;
        await NextRendering();
        var realized = Descendants(page).OfType<CheckBox>().Count();
        Require(realized > 0 && realized < items.Count, "Bounded leaf ListViews must realize some rows without constructing every checkbox.");
        Log($"Selection/filter/reference/expansion and leaf virtualization PASS ({realized} checkboxes for {items.Count} candidates)");
    }

    private static async Task CheckConfirmation(AIHubPage page)
    {
        var vm = page.ViewModel;
        int count = vm.OptimizationCandidates.Count;
        int historyCount = vm.OptimizationHistory.Count;
        vm.ExecuteOptimizationCommand.Execute(null);
        await WaitUntil(() => Field<ContentDialog>(page, "_activeDialog") != null);
        var first = Field<ContentDialog>(page, "_activeDialog");
        vm.ExecuteOptimizationCommand.Execute(null);
        await NextRendering();
        Require(ReferenceEquals(first, Field<ContentDialog>(page, "_activeDialog")), "A second click must not create a second confirmation.");
        Require(!vm.IsOptimizing && !vm.CanSelectOptimizationItems && first.DefaultButton == ContentDialogButton.Close, "Confirmation must pause selection and default to cancellation before execution.");
        await Capture(page, "aihub-confirmation-page.png");
        first.Hide();
        await WaitUntil(() => Field<ContentDialog>(page, "_activeDialog") == null && vm.CanSelectOptimizationItems);
        Require(!vm.IsOptimizing && vm.OptimizationCandidates.Count == count && vm.OptimizationHistory.Count == historyCount, "Dismissing confirmation must leave candidates/history unchanged and never start execution.");
        Log("Real confirmation dialog, double-click exclusion and reject-without-execution PASS");
    }

    private static async Task CheckAuditSnapshotIsolation(AIHubPage page)
    {
        var vm = page.ViewModel;
        typeof(AIHubPageViewModel).GetProperty(nameof(vm.HasAiReport)).SetValue(vm, true);
        vm.AiReportFindings.Add(new AiReportFinding("Old report", "High", "Previous scan", "Previous recommendation", "1"));
        Field<List<SecurityEvent>>(vm, "_lastAuditEvents").Add(new SecurityEvent(1, "System", "Synthetic", 2, DateTime.UtcNow, "Previous scan fixture"));
        var history = new AuditResult { HealthScore = 75, Timestamp = DateTime.UtcNow.AddDays(-2), Findings = new() { new AuditIssueEnhanced { Title = "Synthetic historical finding", Severity = "Medium", LogName = "System" } } };
        Invoke(vm, "ApplyHistoricalAudit", history);
        Require(vm.AllIssues.Count == 1 && !vm.HasAiReport && vm.AiReportFindings.Count == 0 && Field<List<SecurityEvent>>(vm, "_lastAuditEvents").Count == 0, "Historical snapshot must clear unrelated AI report and event evidence.");
        await (Task)Invoke(vm, "LoadAuditForDateAsync", new DateTime(1901, 1, 1));
        await NextRendering();
        Require(vm.AllIssues.Count == 0 && !vm.HasAuditData && vm.HealthScoreText == "—", "A date without a snapshot must clear old findings and score.");
        Log("Historical report/evidence isolation and empty-date reset PASS");
    }

    private static void AddHistoryFixture(AIHubPageViewModel vm)
    {
        var record = new OptimizationExecutionRecord
        {
            State = "failed", StartedUtc = DateTime.UtcNow.AddMinutes(-2), CompletedUtc = DateTime.UtcNow,
            TotalCount = 3, ProcessedCount = 3, SucceededCount = 2, FailedCount = 1, DeletedCount = 1, MovedCount = 1, FreedBytes = 65536,
            Items = new()
            {
                new() { FileName = "fixture.cache", SourcePath = @"C:\SyntheticOnly\fixture.cache", Action = "delete", State = "succeeded", Bytes = 65536 },
                new() { FileName = "fixture.pdf", SourcePath = @"C:\SyntheticOnly\fixture.pdf", DestinationPath = @"C:\SyntheticOnly\Documents\fixture.pdf", Action = "move", State = "succeeded" },
                new() { FileName = "in-use.cache", SourcePath = @"C:\SyntheticOnly\in-use.cache", Action = "delete", State = "failed", Reason = "Synthetic file-in-use result." },
            },
        };
        var run = new OptimizationRunViewModel(record);
        vm.OptimizationHistory.Add(run);
        vm.SelectedOptimizationRun = run;
        Invoke(vm, "OnPropertyChanged", nameof(vm.HasOptimizationHistory));
        using var exported = JsonDocument.Parse(run.ExportJson);
        Require(run.Details.Count == 3 && exported.RootElement.ValueKind == JsonValueKind.Object, "History must expose individual results and a valid local report.");
    }

    private static async Task CheckScrollAndLayouts(AIHubPage page)
    {
        var vm = page.ViewModel;
        var scroll = (ScrollViewer)page.FindName("AiHubContentScroll");
        vm.ActiveTabIndex = 0;
        await NextRendering();
        scroll.ChangeView(null, 240, null, true);
        await NextRendering();
        double auditOffset = scroll.VerticalOffset;
        vm.ActiveTabIndex = 1;
        await NextRendering();
        scroll.ChangeView(null, 360, null, true);
        await NextRendering();
        double optimizationOffset = scroll.VerticalOffset;
        vm.ActiveTabIndex = 0;
        await NextRendering();
        await NextRendering();
        Require(Math.Abs(scroll.VerticalOffset - auditOffset) < 2, "Audit tab must restore its own scroll offset.");
        vm.ActiveTabIndex = 1;
        await NextRendering();
        await NextRendering();
        Require(Math.Abs(scroll.VerticalOffset - optimizationOffset) < 2, "Optimization tab must restore its own scroll offset.");
        var tabs = (FrameworkElement)page.FindName("AiHubSections");
        Require(!IsDescendantOf(tabs, scroll), "Tab navigation must stay outside the scrolling content.");
        var master = (FrameworkElement)page.FindName("AiHubMasterCardSurface");
        Require(!IsDescendantOf(master, scroll), "The module switch must remain visible above tab content.");
        Require(master.TransformToVisual(page).TransformPoint(new(0, master.ActualHeight)).Y <= tabs.TransformToVisual(page).TransformPoint(new(0, 0)).Y,
            "The first module control must be the whole-plugin switch, before the tabs.");
        await Navigate(typeof(GeneralPage));
        var referencePage = (GeneralPage)NavigationService.Frame.Content;
        var referenceControl = Descendants(referencePage).OfType<SettingsPageControl>().Single();
        var referenceContent = (FrameworkElement)referenceControl.FindName("ModuleContentPresenter");
        var referenceTitle = (FrameworkElement)referenceControl.FindName("Header");
        var contentBounds = new Dictionary<int, (double Left, double Width, double TitleLeft)>();
        foreach (int width in new[] { 1280, 760 })
        {
            App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1000));
            await NextRendering();
            await NextRendering();
            contentBounds[width] = (referenceContent.TransformToVisual(referencePage).TransformPoint(new(0, 0)).X,
                referenceContent.ActualWidth, referenceTitle.TransformToVisual(referencePage).TransformPoint(new(0, 0)).X);
        }
        await Navigate(typeof(AIHubPage));
        Require(ReferenceEquals(page, NavigationService.Frame.Content), "Navigation must retain the cached page.");
        var root = (FrameworkElement)App.GetSettingsWindow().Content;
        var themeBefore = root.RequestedTheme;
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (int width in new[] { 1280, 760 })
            foreach (int tab in new[] { 0, 1, 2 })
            {
                root.RequestedTheme = theme;
                App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1000));
                vm.ActiveTabIndex = tab;
                await NextRendering();
                await NextRendering();
                scroll.ChangeView(null, 0, null, true);
                await NextRendering();
                page.UpdateLayout();
                Require(scroll.ExtentWidth <= scroll.ViewportWidth + 2, $"Horizontal overflow at {theme}/{width}/tab{tab}.");
                var expected = contentBounds[width];
                var content = (FrameworkElement)page.FindName("AiHubTabContent");
                var title = (FrameworkElement)page.FindName("AiHubPageTitle");
                Require(Math.Abs(content.TransformToVisual(page).TransformPoint(new(0, 0)).X - expected.Left) < 2 &&
                    Math.Abs(content.ActualWidth - expected.Width) < 2 &&
                    Math.Abs(title.TransformToVisual(page).TransformPoint(new(0, 0)).X - expected.TitleLeft) < 2,
                    $"AI Hub content/title must match SettingsPageControl margins at {width}.");
                Require(Math.Abs(master.ActualWidth - content.ActualWidth) < 2 && Math.Abs(tabs.ActualWidth - content.ActualWidth) < 2,
                    "The module switch, tabs and tab content must share the same full-width column.");
                if (tab == 1)
                {
                    var panel = (FrameworkElement)page.FindName("OptimizationPanel");
                    Require(Math.Abs(panel.ActualWidth - content.ActualWidth) < 2, "Optimization must fill the tab's width.");
                    var groups = (ItemsControl)page.FindName("OptimizationCacheGroups");
                    var family = Descendants(groups).OfType<Expander>().First();
                    var header = (FrameworkElement)family.Header;
                    var presenter = (FrameworkElement)VisualTreeHelper.GetParent(header);
                    Require(Math.Abs(family.ActualWidth - groups.ActualWidth) < 4 && Math.Abs(header.ActualWidth - presenter.ActualWidth) < 2,
                        "Optimization family rows and their headers must stretch across their containers.");
                }
                await Capture(page, $"aihub-{theme}-{width}-tab{tab}.png");
            }
        }
        finally { root.RequestedTheme = themeBefore; }
        Log("Independent tab scroll, master switch before fixed navigation, shared page margins, stretched optimization groups, cached page and 12 theme/width/tab renders PASS");
    }

    private static bool IsDescendantOf(DependencyObject item, DependencyObject expected)
    {
        for (var current = VisualTreeHelper.GetParent(item); current != null; current = VisualTreeHelper.GetParent(current)) if (ReferenceEquals(current, expected)) return true;
        return false;
    }
    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, PrivateInstance).GetValue(owner);
    private static object Invoke(object owner, string name, params object[] args)
    {
        for (var type = owner.GetType(); type != null; type = type.BaseType)
        {
            var method = type.GetMethods(PrivateInstance | BindingFlags.DeclaredOnly).SingleOrDefault(item => item.Name == name && item.GetParameters().Length == args.Length);
            if (method != null) return method.Invoke(owner, args);
        }
        throw new MissingMethodException(owner.GetType().FullName, name);
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("UI condition did not settle."); await Task.Delay(25); }
    }
    private static async Task Navigate(Type type)
    {
        Require(NavigationService.Navigate(type), "Could not navigate to " + type.Name);
        await WaitUntil(() => NavigationService.Frame.Content is FrameworkElement { IsLoaded: true });
        await NextRendering();
    }
    private static async Task NextRendering()
    {
        var done = new TaskCompletionSource<bool>();
        EventHandler<object> handler = (_, _) => done.TrySetResult(true);
        CompositionTarget.Rendering += handler;
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { CompositionTarget.Rendering -= handler; }
    }
    private static async Task Capture(AIHubPage page, string name)
    {
        var background = page.Background;
        page.Background = new SolidColorBrush(page.ActualTheme == ElementTheme.Dark ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32) : Microsoft.UI.ColorHelper.FromArgb(255, 243, 243, 243));
        var bitmap = new RenderTargetBitmap();
        try { await bitmap.RenderAsync(page); } finally { page.Background = background; }
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
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void EnsureNoWorker()
    {
        foreach (string name in new[] { "Kit.AIHubWorker", "Kit.WingetService", "winget" })
        {
            var processes = Process.GetProcessesByName(name);
            try { Require(processes.Length == 0, "Synthetic test must not create " + name); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Log(string text) => File.AppendAllText(Report, text + "\n");
}
