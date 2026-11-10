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
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
        File.WriteAllText(Report, "Real WinUI; synthetic in-memory audit/candidate/inventory/history data. No scans, AI requests, confirmed file operations, policy saves or clipboard writes.\n");
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
        general.SettingsConfig.Enabled.Awake = false;
        general.SettingsConfig.Enabled.LightSwitch = false;
        general.NotifySettingsChanged();
        var module = SettingsRepository<AIHubSettings>.GetInstance(SettingsUtils.Default);
        module.StopWatching();
        module.SettingsConfig = new AIHubSettings();
        ShellPage.SetDefaultSndMessageCallback(_ => { });
        var window = App.GetSettingsWindow();
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1000));
        if (Environment.GetEnvironmentVariable("KIT_NAVIGATION_PERSISTENCE_PHASE") is string phase && !string.IsNullOrEmpty(phase))
        {
            await NavigationPanePersistenceChecks.RunAsync(Output, phase);
            EnsureNoWorker();
            return;
        }
        if (Environment.GetEnvironmentVariable("KIT_SHELL_PALETTE_ONLY") == "1")
        {
            await Navigate(typeof(AIHubPage));
            await ShellPaletteChecks.RunAsync(Output);
            EnsureNoWorker();
            return;
        }
        await CheckPoliciesRequestedDuringInitialization();
        await Navigate(typeof(AIHubPage));
        var page = (AIHubPage)NavigationService.Frame.Content;
        var vm = page.ViewModel;
        await vm.AiHub.Initialization;
        await WaitUntil(() => !Field<bool>(vm, "_loadingOptimizationHistory"));
        Require(!vm.IsEnabled && !vm.AiHub.IsEnabled, "AI Hub and shared service must start disabled.");
        Require(!vm.FastScanCommand.CanExecute(null) && !vm.ScanAllCommand.CanExecute(null), "Disabled module must block scans.");
        Require(!vm.CanAnalyzeStorage && !vm.AnalyzeStorageCommand.CanExecute(null), "Disabled module must block local space analysis.");
        await CheckScanButtonBoundState(page, "initial-disabled");
        await CheckEnableCardAndHealthLayout(page, "initial-disabled");
        Require(!Descendants(page).OfType<TextBox>().Any(box => box.Name == "AiTaskPolicyContentBox"), "Task editor must remain unloaded before its first visit.");
        await CheckPolicyDrafts(page);
        await CheckPoliciesRequestedDuringBusyOperation();

        // This changes only the stopped in-memory repository. Never toggle the page's
        // persisted module switch, and never invoke a scan or accept an operation dialog.
        general.SettingsConfig.Enabled.AiHub = true;
        vm.RefreshEnabledState();
        Require(vm.IsEnabled && !vm.AiHub.IsEnabled, "Synthetic selection needs only the local module gate.");
        await CheckScanButtonBoundState(page, "module-enabled");
        await CheckEnableCardAndHealthLayout(page, "module-enabled");
        vm.SelectedScanModeIndex = 0;
        await CheckScanButtonBoundState(page, "fast-scan-selected");
        vm.SelectedScanModeIndex = 1;
        foreach (string busyProperty in new[] { nameof(vm.IsAuditing), nameof(vm.IsAiAnalyzing) })
        {
            typeof(AIHubPageViewModel).GetProperty(busyProperty).SetValue(vm, true);
            try { await CheckScanButtonBoundState(page, busyProperty + "-busy"); }
            finally { typeof(AIHubPageViewModel).GetProperty(busyProperty).SetValue(vm, false); }
            await CheckScanButtonBoundState(page, busyProperty + "-completed");
        }
        var items = InjectCandidates(vm);
        await CheckSelectionAndFilters(page, items);
        CheckSeparateOperationCapacity(vm, items);
        await CheckOptimizationPillRepeat(page);
        await CheckInventoryIsolation(page);
        await CheckConfirmation(page);
        await CheckAuditSnapshotIsolation(page);
        await CheckAuditFailureLayout(page);
        await CheckFluentOverviewAndDetails(page);
        await CheckAuditSemanticStates(page);
        await CheckSplitButtonPalette(page);
        AddHistoryFixture(vm);
        await CheckScrollAndLayouts(page);
        await CheckSharedModuleHeaders(page);
        await ShellPaletteChecks.RunAsync(Output);
        Log("Shell module palettes, native interaction states, Light/Dark/Light restoration, contrast, keyboard focus and responsive pane renders PASS");
        EnsureNoWorker();
        Require(!vm.IsOptimizing && !vm.IsInventoryScanning && !vm.IsAuditing && !vm.IsAiAnalyzing, "The smoke must finish with no work running.");
    }

    private static async Task CheckPoliciesRequestedDuringInitialization()
    {
        using var pending = new AiHubViewModel(DispatcherQueue.GetForCurrentThread());
        Require(!pending.Initialization.IsCompleted, "The early policy request must exercise pending service initialization.");
        pending.EnsurePoliciesLoaded();
        pending.EnsurePoliciesLoaded();
        await pending.Initialization;
        await WaitUntil(() => pending.PoliciesLoaded && !pending.IsBusy);
        Require(pending.CanEditPolicies && !string.IsNullOrWhiteSpace(pending.SecurityAuditPolicyContent)
            && !string.IsNullOrWhiteSpace(pending.OptimizationPolicyContent),
            "Policy requests made during initialization must finish without another visit or request.");
        Require(!pending.HasUnsavedPolicyChanges, "Loading policies must not create draft changes.");
        Log("Policy request during pending initialization PASS");
    }

    private static async Task CheckPoliciesRequestedDuringBusyOperation()
    {
        using var pending = new AiHubViewModel(DispatcherQueue.GetForCurrentThread());
        await pending.Initialization;
        Require(!pending.PoliciesLoaded, "The busy-operation regression must begin with unloaded policies.");
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task> holdOperation = token => release.Task.WaitAsync(token);
        var operation = (Task)Invoke(pending, "RunOperationAsync", holdOperation, false);
        try
        {
            Require(pending.IsBusy, "The in-memory operation must hold the service busy before requesting policies.");
            pending.EnsurePoliciesLoaded();
        }
        finally
        {
            release.TrySetResult(true);
        }

        await operation;
        await WaitUntil(() => pending.PoliciesLoaded && !pending.IsBusy);
        Require(pending.CanEditPolicies && !string.IsNullOrWhiteSpace(pending.SecurityAuditPolicyContent)
            && !string.IsNullOrWhiteSpace(pending.OptimizationPolicyContent) && !pending.HasUnsavedPolicyChanges,
            "A policy request during another operation must finish after it becomes idle without a second request.");
        Log("Policy request during another service operation PASS");
    }

    private static async Task CheckPolicyDrafts(AIHubPage page)
    {
        var vm = page.ViewModel;
        var sections = (Selector)page.FindName("AiHubSections");
        sections.SelectedIndex = 2;
        await WaitUntil(() => vm.ActiveTabIndex == 2 && vm.AiHub.PoliciesLoaded && !vm.AiHub.IsBusy);
        await NextRendering();
        page.UpdateLayout();
        var panel = (FrameworkElement)page.FindName("TaskPolicyPanel");
        var editor = (TextBox)page.FindName("AiTaskPolicyContentBox");
        var policies = (Selector)page.FindName("AiTaskPolicySegmented");
        Log($"First policy visit: activeTab={vm.ActiveTabIndex}, panel={panel?.Visibility}, size={panel?.ActualWidth:F0}x{panel?.ActualHeight:F0}, editorLength={editor?.Text.Length}, currentLength={vm.AiHub.CurrentPolicyContent.Length}, canEdit={vm.AiHub.CanEditPolicies}");
        Require(panel != null && panel.Visibility == Visibility.Visible && panel.ActualWidth > 0 && panel.ActualHeight > 0,
            "The first Task policies selection must render its panel without switching away and back.");
        Require(editor != null && !editor.IsReadOnly && vm.AiHub.CanEditPolicies, "Disabled AI service must still permit local policy editing.");
        Require(vm.AiHub.TaskPolicyIndex == 0 && !string.IsNullOrWhiteSpace(editor.Text)
            && NormalizeNewlines(editor.Text) == NormalizeNewlines(vm.AiHub.SecurityAuditPolicyContent),
            "The first visible editor must contain the saved Security audit policy.");
        Require(!vm.AiHub.HasUnsavedPolicyChanges, "Initial policy editor synchronization must not create unsaved changes.");
        Require(policies.Items.Count == 2, "Task policies must expose only Security audit and Optimization.");
        string saved = vm.AiHub.CurrentPolicyContent;
        string draft = saved + "\n<!-- UI smoke draft; never saved -->";
        editor.Text = draft;
        Require(vm.AiHub.IsCurrentPolicyDirty && vm.AiHub.SaveActivePolicyCommand.CanExecute(null), "A task draft must enable Save while AI is disabled.");
        policies.SelectedIndex = 1;
        Require(vm.AiHub.IsOptimizationPolicyTab && vm.AiHub.CurrentPolicyFilePath.EndsWith(Path.Combine("system-optimization", "AGENTS.md"), StringComparison.OrdinalIgnoreCase), "Optimization must select its own task policy.");
        string optimizationSaved = vm.AiHub.CurrentPolicyContent;
        string optimizationDraft = optimizationSaved + "\n<!-- Independent optimization draft; never saved -->";
        editor.Text = optimizationDraft;
        policies.SelectedIndex = 0;
        Require(NormalizeNewlines(editor.Text) == NormalizeNewlines(draft), "Switching task policies must display the unsaved audit draft.");
        vm.AiHub.DiscardActivePolicyCommand.Execute(null);
        Require(NormalizeNewlines(editor.Text) == NormalizeNewlines(saved) && !vm.AiHub.IsCurrentPolicyDirty,
            "Discard must restore the current editor's saved content.");
        policies.SelectedIndex = 1;
        Require(NormalizeNewlines(editor.Text) == NormalizeNewlines(optimizationDraft) && vm.AiHub.IsCurrentPolicyDirty,
            "Discarding the audit draft must preserve the independent optimization draft.");
        vm.AiHub.DiscardActivePolicyCommand.Execute(null);
        Require(NormalizeNewlines(editor.Text) == NormalizeNewlines(optimizationSaved) && !vm.AiHub.HasUnsavedPolicyChanges,
            "Discarding both drafts must leave the policy editor clean.");
        await Navigate(typeof(GeneralPage));
        await Navigate(typeof(AIHubPage));
        Require(ReferenceEquals(NavigationService.Frame.Content, page), "Policy revisit must exercise the cached page.");
        Require(vm.ActiveTabIndex == 2 && panel.Visibility == Visibility.Visible && editor.ActualHeight > 0
            && NormalizeNewlines(editor.Text) == NormalizeNewlines(optimizationSaved),
            "Returning to the cached Task policies page must retain its selected policy and visible content.");
        Log("Policy first-selection rendering, disabled-service editing, two task policies, per-file drafts and cached revisit PASS");
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

    private static void CheckSeparateOperationCapacity(AIHubPageViewModel vm, List<TempFileInfo> items)
    {
        var selection = items.Select(item => item.IsSelected).ToArray();
        long originalSize = items[1].SizeInBytes;
        string originalAction = items[1].Action;
        try
        {
            foreach (var item in items) item.IsSelected = false;
            items[1].Action = "move";
            items[1].SizeInBytes = 8 * 1024;
            Invoke(vm, "RecalculateCandidateMetrics");
            string deleteCapacity = vm.CleanableCapacityText;
            items[0].IsSelected = items[1].IsSelected = true;
            Require(vm.SelectedDeleteSizeText == "1.0 KB" && vm.SelectedMoveSizeText == "8.0 KB" &&
                vm.HasSelectedDeletes && vm.HasSelectedMoves && vm.SelectedCandidatesCount == 2,
                "Selected deletion and movement must expose their own byte totals.");
            items[1].SizeInBytes = 12L * 1024 * 1024;
            Invoke(vm, "RecalculateCandidateMetrics");
            Require(vm.CleanableCapacityText == deleteCapacity && vm.SelectedDeleteSizeText == "1.0 KB" && vm.SelectedMoveSizeText == "12.0 MB",
                "Moving larger files must never inflate reclaimable capacity or deletion totals.");
            items[0].IsSelected = false;
            Require(!vm.HasSelectedDeletes && vm.HasSelectedMoves && vm.SelectedDeleteSizeText == "0 B" && vm.SelectedMoveSizeText == "12.0 MB",
                "A move-only selection must show no reclaimed capacity.");
        }
        finally
        {
            items[1].Action = originalAction;
            items[1].SizeInBytes = originalSize;
            for (int index = 0; index < items.Count; index++) items[index].IsSelected = selection[index];
            Invoke(vm, "RecalculateCandidateMetrics");
        }
        Log("Delete/move selection totals and reclaimable-capacity separation PASS");
    }

    private static async Task CheckOptimizationPillRepeat(AIHubPage page)
    {
        var vm = page.ViewModel;
        int previous = vm.OptimizationFilterIndex;
        vm.ActiveTabIndex = 1;
        await NextRendering();
        var pill = Descendants(page).OfType<ToggleButton>().Single(button => string.Equals(button.Tag?.ToString(), "1", StringComparison.Ordinal));
        Invoke(page, "OnOptFilterPillClick", pill, new RoutedEventArgs());
        Require(vm.OptimizationFilterIndex == 1 && pill.IsChecked == true, "Selecting an optimization filter must check its pill.");
        // ToggleButton toggles itself before Click. Reproduce that transition so
        // the handler must repair a repeated selection whose VM value is unchanged.
        pill.IsChecked = false;
        Invoke(page, "OnOptFilterPillClick", pill, new RoutedEventArgs());
        Require(vm.OptimizationFilterIndex == 1 && pill.IsChecked == true, "Clicking the active optimization pill again must not visually uncheck it.");
        vm.OptimizationFilterIndex = previous;
        await NextRendering();
        Log("Repeated active optimization filter click retains the checked state PASS");
    }

    private static OptimizationInventoryReport InventoryFixture()
    {
        const long gib = 1024L * 1024 * 1024;
        string deep = @"C:\SyntheticInventoryOnly\Profile with a long localized display name 用户配置\AppData\Local\Browser\User Data\Profile 12\Cache\";
        return new OptimizationInventoryReport
        {
            CompletedUtc = DateTimeOffset.UtcNow, ElapsedMilliseconds = 1234, ExaminedEntries = 25000, IsPartial = true,
            Scopes = new[]
            {
                new OptimizationInventoryScope
                {
                    Id = "cache", FamilyId = "browsers", NameEn = "Browser caches across several profiles", NameZh = "多个用户配置中的浏览器缓存",
                    RootPath = deep, Disposition = "cache", State = "complete", ObservedBytes = 12 * gib, FileCount = 123456,
                    RecentBytes = 3 * gib, OlderBytes = 5 * gib,
                    ReasonEn = "Observed cache bytes include files that are recent or in use.", ReasonZh = "已观测缓存包括近期修改或正在使用的文件。",
                    GuidanceEn = "Cleanup is a separate review; this inventory cannot delete files.", GuidanceZh = "清理需要单独审核；此处仅分析占用，不会删除文件。",
                    TopFiles = Enumerable.Range(0, 5).Select(index => new OptimizationInventoryFile
                    {
                        Name = "largest-synthetic-cache-file-with-long-name-" + index + ".bin", Path = deep + @"nested\" + index + ".bin",
                        SizeInBytes = gib - index * 1024, LastModifiedUtc = DateTimeOffset.UtcNow.AddDays(-40 - index),
                    }).ToArray(),
                },
                new OptimizationInventoryScope
                {
                    Id = "diagnostics", FamilyId = "diagnostics", NameEn = "Crash dumps", NameZh = "崩溃转储",
                    RootPath = @"C:\SyntheticInventoryOnly\CrashDumps", Disposition = "analysis-only", State = "partial", ObservedBytes = 3 * gib,
                    FileCount = 25000, RecentBytes = gib, OlderBytes = gib, SkippedEntries = 2, AccessFailures = 1,
                    ReasonEn = "Partial inventory: some files could not be measured.", ReasonZh = "部分统计：部分文件无法计量。",
                    GuidanceEn = "Review diagnostic retention in the owning application.", GuidanceZh = "请在所属应用中检查诊断数据保留设置。",
                },
                new OptimizationInventoryScope
                {
                    Id = "managed", FamilyId = "system", NameEn = "Windows Update downloads", NameZh = "Windows 更新下载",
                    RootPath = @"C:\SyntheticInventoryOnly\Windows\SoftwareDistribution\Download", Disposition = "system-managed", State = "complete",
                    ObservedBytes = 4 * gib, FileCount = 128, RecentBytes = gib, OlderBytes = 2 * gib,
                    ReasonEn = "Windows manages these downloads.", ReasonZh = "这些下载由 Windows 管理。",
                    GuidanceEn = "Use Windows Storage settings to review this space.", GuidanceZh = "请使用 Windows 存储设置检查此空间。",
                },
                new OptimizationInventoryScope
                {
                    Id = "protected", FamilyId = "system", NameEn = "Windows component store", NameZh = "Windows 组件存储",
                    RootPath = @"C:\SyntheticInventoryOnly\Windows\WinSxS", Disposition = "protected", State = "protected",
                    ReasonEn = "Protected installation files were not enumerated.", ReasonZh = "受保护的安装文件未被遍历。",
                    GuidanceEn = "Use Windows servicing tools; no direct cleanup is offered.", GuidanceZh = "请使用 Windows 维护工具，此处不提供直接清理。",
                },
                new OptimizationInventoryScope
                {
                    Id = "unavailable", FamilyId = "applications", NameEn = "Application diagnostics", NameZh = "应用诊断",
                    RootPath = @"C:\SyntheticInventoryOnly\AccessDenied", Disposition = "analysis-only", State = "unavailable", AccessFailures = 1,
                    ReasonEn = "Access was denied.", ReasonZh = "没有访问权限。",
                    GuidanceEn = "No size estimate is available for inaccessible contents.", GuidanceZh = "无法访问的内容没有可用容量估计。",
                },
                new OptimizationInventoryScope
                {
                    Id = "missing", FamilyId = "packages", NameEn = "Optional package cache", NameZh = "可选包缓存",
                    RootPath = @"C:\SyntheticInventoryOnly\NotInstalled", Disposition = "cache", State = "missing",
                },
            },
        };
    }

    private static async Task CheckInventoryIsolation(AIHubPage page)
    {
        var vm = page.ViewModel;
        var inventoryExpander = (Expander)page.FindName("InventoryExpander");
        Require(!inventoryExpander.IsExpanded, "Read-only inventory must begin collapsed.");
        inventoryExpander.IsExpanded = true;
        await NextRendering();
        Require(vm.CanAnalyzeStorage && vm.AnalyzeStorageCommand.CanExecute(null) && !vm.AiHub.IsEnabled,
            "Local inventory must be available without enabling the shared AI service.");
        var candidates = vm.OptimizationCandidates.ToArray();
        var selected = candidates.Select(item => item.IsSelected).ToArray();
        var report = InventoryFixture();
        Invoke(vm, "ApplyInventoryReport", report);
        await NextRendering();
        Require(vm.HasInventory && vm.Inventory.IsPartial && vm.Inventory.Rows.Count == 6 && vm.Inventory.FilteredRows.Count == 5,
            "Inventory must retain coverage states, hide missing locations, and publish partial results.");
        Require(vm.Inventory.ObservedBytes == 19L * 1024 * 1024 * 1024 && vm.Inventory.FilteredRows[0].Id == "cache",
            "Observed totals must include measured locations once and sort largest first.");
        var protectedRow = vm.Inventory.Rows.Single(row => row.Id == "protected");
        var unavailable = vm.Inventory.Rows.Single(row => row.Id == "unavailable");
        Require(!protectedRow.HasMeasuredSize && !unavailable.HasMeasuredSize && protectedRow.SizeText == unavailable.SizeText &&
            protectedRow.SizeText != "0 B", "Unknown and protected sizes must not be presented as zero bytes.");
        Require(vm.Inventory.Rows.Single(row => row.Id == "cache").TopFiles.Count == 5, "Largest-file detail must stay bounded.");
        Require(vm.Inventory.Rows.All(row => !row.HasBeenExpanded), "Inventory file details must remain unloaded until expansion.");
        var search = (TextBox)page.FindName("InventorySearchBox");
        var filter = (ComboBox)page.FindName("InventoryFilterComboBox");
        Require(search != null && filter != null, "Inventory needs its own search and disposition filter controls.");
        search.Text = "AccessDenied";
        await NextRendering();
        Require(vm.Inventory.FilteredRows.Count == 1 && vm.Inventory.FilteredRows[0].Id == "unavailable", "Inventory search must include local paths.");
        search.Text = string.Empty;
        foreach (var value in new[] { (1, "cache", 1), (2, "analysis-only", 2), (3, "system-managed", 1), (4, "protected", 1) })
        {
            filter.SelectedIndex = value.Item1;
            await NextRendering();
            Require(vm.Inventory.FilteredRows.Count == value.Item3 && vm.Inventory.FilteredRows.All(row => row.Scope.Disposition == value.Item2),
                "Inventory filtering must preserve independent disposition categories.");
            AssertCandidatesUntouched();
        }
        search.Text = "inventory-no-match";
        await NextRendering();
        Require(!vm.Inventory.HasFilteredRows && vm.Inventory.HasRows, "Empty inventory search must not erase the report.");
        Require(((FrameworkElement)page.FindName("InventoryNoMatches")).Visibility == Visibility.Visible,
            "An empty inventory filter must display a helpful state.");
        search.Text = string.Empty;
        filter.SelectedIndex = 0;
        await NextRendering();

        // Replace only the in-memory confirmation delegate, capture the proposed plan,
        // and reject it. Neither command can write a real or synthetic file.
        var confirmationField = typeof(AIHubPageViewModel).GetField("RequestOptimizationConfirmation", PrivateInstance);
        var previousConfirmation = confirmationField.GetValue(vm);
        IReadOnlyList<TempFileInfo> requested = null;
        confirmationField.SetValue(vm, new Func<IReadOnlyList<TempFileInfo>, Task<bool>>(plan =>
        {
            requested = plan;
            return Task.FromResult(false);
        }));
        try
        {
            foreach (var command in new[] { vm.CleanAllCommand, vm.ExecuteOptimizationCommand })
            {
                requested = null;
                command.Execute(null);
                await WaitUntil(() => requested != null && vm.CanSelectOptimizationItems);
                Require(requested.Count == candidates.Count(item => item.IsSelected && item.IsActionable) &&
                    requested.All(candidates.Contains), "Inventory rows must never enter Clean all or Execute selected plans.");
                AssertCandidatesUntouched();
            }
        }
        finally { confirmationField.SetValue(vm, previousConfirmation); }

        await CheckInventoryLifecycle(page, report);
        AssertCandidatesUntouched();
        inventoryExpander.IsExpanded = false;
        Log("Inventory synthetic coverage, unknown sizes, bounded details, local-only availability, search/filter isolation and rejected cleanup plans PASS");

        void AssertCandidatesUntouched()
        {
            Require(candidates.SequenceEqual(vm.OptimizationCandidates) && candidates.Select(item => item.IsSelected).SequenceEqual(selected),
                "Read-only inventory must not replace, select or reorder cleanup candidates.");
        }
    }

    private static async Task CheckInventoryLifecycle(AIHubPage page, OptimizationInventoryReport report)
    {
        var vm = page.ViewModel;
        var originalRows = vm.Inventory.Rows;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = (Task)Invoke(vm, "RunStorageInventoryAsync", new Func<CancellationToken, Task<OptimizationInventoryReport>>(async token =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return report;
        }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(vm.IsInventoryScanning && vm.IsOptimizing && !vm.CanAnalyzeStorage && !vm.CleanAllCommand.CanExecute(null) &&
            !vm.ExecuteOptimizationCommand.CanExecute(null), "Active inventory must block a second scan and cleanup execution.");
        bool duplicateRan = false;
        await (Task)Invoke(vm, "RunStorageInventoryAsync", new Func<CancellationToken, Task<OptimizationInventoryReport>>(_ =>
        {
            duplicateRan = true;
            return Task.FromResult(report);
        }));
        Require(!duplicateRan, "A duplicate inventory request must not start.");
        Require(vm.CancelInventoryCommand.CanExecute(null), "Inventory must expose cancellation while busy.");
        vm.CancelInventoryCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntil(() => !vm.IsInventoryScanning && !vm.IsOptimizing && vm.CanAnalyzeStorage);
        Require(ReferenceEquals(originalRows, vm.Inventory.Rows) && !string.IsNullOrWhiteSpace(vm.InventoryStatusText),
            "Cancellation must preserve the previous report and recover controls.");

        var failed = (Task)Invoke(vm, "RunStorageInventoryAsync", new Func<CancellationToken, Task<OptimizationInventoryReport>>(_ =>
            Task.FromException<OptimizationInventoryReport>(new IOException("Synthetic inventory failure."))));
        await failed.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntil(() => !vm.IsInventoryScanning && !vm.IsOptimizing);
        Require(ReferenceEquals(originalRows, vm.Inventory.Rows) && vm.CanAnalyzeStorage && vm.ScanPhase == Kit.AIHubLib.Models.ScanPhase.Failed,
            "Failed inventory must retain the last report and restore scanning controls.");

        var updated = report with { CompletedUtc = report.CompletedUtc.AddMinutes(1), ExaminedEntries = report.ExaminedEntries + 1 };
        var succeeded = (Task)Invoke(vm, "RunStorageInventoryAsync", new Func<CancellationToken, Task<OptimizationInventoryReport>>(_ => Task.FromResult(updated)));
        await succeeded.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntil(() => !vm.IsInventoryScanning && !vm.IsOptimizing && !ReferenceEquals(originalRows, vm.Inventory.Rows));
        Require(vm.HasInventory && vm.CanAnalyzeStorage && vm.Inventory.Rows.Count == report.Scopes.Count,
            "A completed synthetic inventory must replace the report and recover controls.");
        Log("Inventory cancellation/failure retention, duplicate exclusion, cleanup busy gate and successful refresh PASS");
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

    private static async Task CheckAuditFailureLayout(AIHubPage page)
    {
        var vm = page.ViewModel;
        var root = (FrameworkElement)App.GetSettingsWindow().Content;
        var originalTheme = root.RequestedTheme;
        int originalTab = vm.ActiveTabIndex;
        var percentField = typeof(AIHubPageViewModel).GetField("_auditDetailedPercent", PrivateInstance);
        object originalPercent = percentField.GetValue(vm);
        var incompleteField = typeof(AIHubPageViewModel).GetField("_auditIncomplete", PrivateInstance);
        object originalIncomplete = incompleteField.GetValue(vm);
        var scroll = (ScrollViewer)page.FindName("AiHubContentScroll");
        var card = (FrameworkElement)page.FindName("AuditWorkflowCard");
        var detailScroll = (ScrollViewer)page.FindName("AuditWorkflowDetailsScroll");
        var status = (TextBlock)page.FindName("AuditWorkflowStatus");
        var aiResult = (TextBlock)page.FindName("AuditWorkflowAiResult");
        string failure = "AI call failed / AI 分析失败: HTTP 401 Unauthorized. Native Codex main and fallback attempts failed. " +
            string.Join("\n", Enumerable.Range(1, 8).Select(attempt => $"Attempt {attempt}: authentication rejected / 身份验证失败。Check the selected provider credentials. " +
                "https://example.invalid/native/responses/" + new string('x', 160))) + "\nEND OF SYNTHETIC FAILURE";
        try
        {
            vm.ActiveTabIndex = 0;
            vm.IsStatusOpen = false;
            Invoke(vm, "ApplyHistoricalAudit", new AuditResult
            {
                HealthScore = 75, Timestamp = DateTime.UtcNow,
                Findings = new() { new AuditIssueEnhanced { Title = "Retained local finding", Severity = "Medium", LogName = "System" } },
            });
            incompleteField.SetValue(vm, true);
            percentField.SetValue(vm, 30d);
            typeof(AIHubPageViewModel).GetProperty(nameof(vm.AiAnalysisRanText)).SetValue(vm, failure);
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (int width in new[] { 1280, 760 })
            foreach (var phase in new[] { Kit.AIHubLib.Models.ScanPhase.SelectingTargets, Kit.AIHubLib.Models.ScanPhase.Failed })
            {
                root.RequestedTheme = theme;
                App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1000));
                typeof(AIHubPageViewModel).GetProperty(nameof(vm.AuditStatusText)).SetValue(vm,
                    phase == Kit.AIHubLib.Models.ScanPhase.Failed ? failure : "Local scan complete / 本地扫描完成。238 events; retained local findings.");
                typeof(AIHubPageViewModel).GetProperty(nameof(vm.AuditPhase)).SetValue(vm, phase);
                Invoke(vm, "PublishScanProgress");
                await NextRendering();
                await NextRendering();
                scroll.ChangeView(null, 0, null, true);
                detailScroll.ChangeView(null, 0, null, true);
                await NextRendering();
                page.UpdateLayout();
                string context = $"{theme}/{width}/{phase}";
                var statusSlot = LayoutInformation.GetLayoutSlot(status);
                var aiResultSlot = LayoutInformation.GetLayoutSlot(aiResult);
                Log($"AUDIT FAILURE {context}: card={card.ActualWidth:F1}x{card.ActualHeight:F1}; details={detailScroll.ActualWidth:F1}x{detailScroll.ActualHeight:F1}, viewport={detailScroll.ViewportWidth:F1}x{detailScroll.ViewportHeight:F1}, extent={detailScroll.ExtentWidth:F1}x{detailScroll.ExtentHeight:F1}; status={status.ActualWidth:F1}x{status.ActualHeight:F1}, slot={statusSlot}@{status.TransformToVisual(card).TransformPoint(new(0, 0))}; aiResult={aiResult.ActualWidth:F1}x{aiResult.ActualHeight:F1}, slot={aiResultSlot}@{aiResult.TransformToVisual(card).TransformPoint(new(0, 0))}; outerViewport={scroll.ViewportWidth:F1}x{scroll.ViewportHeight:F1}");
                await Capture(page, $"aihub-audit-failure-{theme}-{width}-{phase}.png");
                Require(card.Visibility == Visibility.Visible && card.ActualHeight > 80 && card.ActualHeight <= 300,
                    context + ": a long native failure must produce a bounded workflow card, not push results hundreds of pixels down.");
                // TextBlock.ActualWidth can describe only the rendered text;
                // its allocated layout slot is the available wrapping width.
                Require(detailScroll.ActualWidth >= card.ActualWidth - 50 && statusSlot.Width >= detailScroll.ViewportWidth - 4 &&
                    aiResultSlot.Width >= detailScroll.ViewportWidth - 4 && statusSlot.Left >= -1 && aiResultSlot.Left >= -1 &&
                    statusSlot.Right <= detailScroll.ViewportWidth + 1 && aiResultSlot.Right <= detailScroll.ViewportWidth + 1,
                    context + ": both workflow messages must retain the full readable column width.");
                Require(scroll.ExtentWidth <= scroll.ViewportWidth + 2 && detailScroll.ScrollableWidth <= 1 && detailScroll.ActualHeight <= 145,
                    context + ": failure details must scroll vertically within their finite viewport without horizontal overflow.");
                double cardTop = card.TransformToVisual(scroll).TransformPoint(new(0, 0)).Y;
                double cardBottom = card.TransformToVisual(scroll).TransformPoint(new(0, card.ActualHeight)).Y;
                Require(cardTop >= -2 && cardBottom <= scroll.ViewportHeight + 2,
                    context + ": the bounded workflow card must remain entirely visible in the initial viewport.");
                Require(vm.SidebarWorkflowPercent == 30 && !vm.IsAuditProgressIndeterminate && vm.AllIssues.Count == 1 && vm.HasAuditData,
                    context + ": layout must preserve the partial progress and retained local findings.");
                Require(aiResult.Text == failure && aiResult.IsTextSelectionEnabled && status.IsTextSelectionEnabled &&
                    aiResult.TextWrapping == TextWrapping.Wrap && status.TextWrapping == TextWrapping.Wrap,
                    context + ": complete diagnostics must remain wrapping selectable text.");
                Require(detailScroll.ScrollableHeight > 0, context + ": long diagnostics must provide a bounded reading region.");
                // Keyboard focus can queue BringIntoView on both nested viewers.
                // Finish that movement before requesting the diagnostic tail.
                await WaitForScrollSettled(new[] { detailScroll, scroll }, () =>
                    Require(aiResult.Focus(FocusState.Keyboard) && ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(page.XamlRoot), aiResult),
                        context + ": selectable native diagnostics must accept real keyboard focus."), context + "/focus");
                double tailTarget = detailScroll.ScrollableHeight;
                bool tailAccepted = false;
                await WaitForScrollSettled(new[] { detailScroll, scroll }, () =>
                    tailAccepted = detailScroll.ChangeView(null, tailTarget, null, true), context + "/tail");
                Log($"AUDIT FAILURE TAIL {context}: accepted={tailAccepted}, requested={tailTarget:F1}, offset={detailScroll.VerticalOffset:F1}, max={detailScroll.ScrollableHeight:F1}, viewport={detailScroll.ViewportHeight:F1}, extent={detailScroll.ExtentHeight:F1}, outerOffset={scroll.VerticalOffset:F1}");
                Require(Math.Abs(detailScroll.VerticalOffset - detailScroll.ScrollableHeight) < 2 && aiResult.Text.EndsWith("END OF SYNTHETIC FAILURE", StringComparison.Ordinal),
                    context + ": the complete native diagnostic tail must remain reachable without expanding the outer card.");
            }
        }
        finally
        {
            Invoke(vm, "ClearAuditSnapshot");
            incompleteField.SetValue(vm, originalIncomplete);
            percentField.SetValue(vm, originalPercent);
            Invoke(vm, "PublishScanProgress");
            root.RequestedTheme = originalTheme;
            vm.ActiveTabIndex = originalTab;
        }
        EnsureNoWorker();
        Log("Long native 401 failures keep bounded full-width partial/failed workflow details and retained local results in light/dark wide/narrow layouts PASS");
    }

    private static AuditResult FluentAuditFixture()
    {
        var timestamp = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        return new AuditResult
        {
            Timestamp = timestamp, ScanStart = timestamp.AddDays(-2), ScanEnd = timestamp,
            HealthScore = 76, EventsScanned = 238,
            Findings = Enumerable.Range(0, 6).Select(index => new AuditIssueEnhanced
            {
                Key = "synthetic-finding-" + index, EventRef = "synthetic-event-" + index,
                EventId = (1000 + index).ToString(), EventTimestamp = timestamp.AddMinutes(-index).ToString("O"),
                Source = "Synthetic source", LogName = index < 4 ? "System" : "Application",
                Title = "Synthetic finding " + index, TitleZh = "合成审计发现 " + index,
                Description = "Synthetic evidence for overview and details testing.", DescriptionZh = "仅用于概览与详情测试的合成证据。",
                RootCause = "Review the recorded event before taking action.", RootCauseZh = "采取措施前先核对所记录的事件。",
                Recommendation = "Inspect the event source and related references.", RecommendationZh = "检查事件来源和关联记录。",
                Severity = index % 3 == 1 ? "High" : index % 3 == 0 ? "Medium" : "Low",
                Category = index < 4 ? "System" : "Application", Occurrences = index + 1,
            }).ToList(),
        };
    }

    private static async Task CheckFluentOverviewAndDetails(AIHubPage page)
    {
        var vm = page.ViewModel;
        vm.ActiveTabIndex = 0;
        vm.ResetFindingFilters();
        vm.SetViewMode("Priority");
        var fixture = FluentAuditFixture();
        Invoke(vm, "ApplyHistoricalAudit", fixture);
        await NextRendering();
        var originalRange = vm.WindowText;
        var originalLifetime = Field<CancellationTokenSource>(vm, "_auditCts");
        int originalScope = vm.FullScanRangeIndex;
        int originalMode = vm.SelectedScanModeIndex;
        Require(page.FindName("AuditScanButton") is SplitButton, "The primary scan action must expose its mode/range flyout.");
        foreach (int mode in new[] { 1, 0 })
        foreach (int range in new[] { 2, 0, 1 })
        {
            vm.SelectedScanModeIndex = mode;
            vm.FullScanRangeIndex = range;
            Require(vm.SelectedScanLabel == (mode == 1 ? vm.FullScanLabel : vm.FastScanLabel) &&
                vm.WindowText == originalRange && vm.AllIssues.SequenceEqual(fixture.Findings) &&
                ReferenceEquals(originalLifetime, Field<CancellationTokenSource>(vm, "_auditCts")) &&
                !vm.IsAuditing && !vm.IsAiAnalyzing,
                "Changing the next scan mode/range must preserve the displayed snapshot and start no work.");
        }
        vm.FullScanRangeIndex = -1;
        vm.SelectedScanModeIndex = 2;
        Require(vm.FullScanRangeIndex == 1 && vm.SelectedScanModeIndex == 0, "Invalid scan drafts must be rejected.");
        vm.FullScanRangeIndex = originalScope;
        vm.SelectedScanModeIndex = originalMode;
        Require(vm.OverviewFindings.Select(issue => issue.Key).SequenceEqual(new[] { "synthetic-finding-4", "synthetic-finding-1", "synthetic-finding-3" }) && vm.CanToggleOverviewFindings,
            "The default overview must show three findings ordered by severity and frequency.");
        vm.ToggleOverviewFindingsCommand.Execute(null);
        Require(vm.OverviewFindings.Count == 6 && vm.ShowAllFindings, "Show all must expose the complete current result.");
        vm.ToggleOverviewFindingsCommand.Execute(null);
        vm.SetSourceFilter("System");
        Require(vm.OverviewFindings.Count == 4 && !vm.CanToggleOverviewFindings, "Active filters must show all matches, including matches beyond the priority preview.");
        vm.SetSeverityFilter("Low");
        Require(vm.OverviewFindings.Count == 1 && vm.OverviewFindings[0].EventId == "1002", "Source and severity filters must compose.");
        vm.ResetFindingFilters();
        vm.FindingSearchText = "1005";
        Require(vm.OverviewFindings.Count == 1 && vm.OverviewFindings[0].EventId == "1005", "Search must find a finding outside the default preview.");
        vm.FindingSearchText = "synthetic-no-match";
        Require(vm.OverviewFindings.Count == 0 && vm.ShowNoFilterMatches && vm.HasAuditData, "No matches must retain the audited snapshot.");
        vm.ResetFindingFilters();
        await NextRendering();
        var host = (SplitView)page.FindName("AiHubDetailsHost");
        var pane = (FindingDetailsPane)page.FindName("AiHubFindingDetails");
        var title = (TextBlock)pane.FindName("FindingTitleText");
        var previous = (Button)pane.FindName("PreviousButton");
        var next = (Button)pane.FindName("NextButton");
        var trigger = (Control)page.FindName("AiHubSections");
        var detailScroll = (ScrollViewer)pane.FindName("DetailsScroll");
        var detailButtons = new[] { "CloseDetailsButton", "PreviousButton", "NextButton", "CopyButton", "AnalyzeButton" }
            .Select(name => (Button)pane.FindName(name)).ToArray();
        var priority = vm.OverviewFindings[0];
        await WaitForPaneLayout(true, "initial-open", () => Invoke(page, "OpenFinding", priority, trigger));
        Require(host.IsPaneOpen && title.Text == priority.DisplayTitle && !previous.IsEnabled && next.IsEnabled &&
            ((TextBlock)pane.FindName("RangeText")).Text == originalRange &&
            ((TextBlock)pane.FindName("EvidenceText")).Text.Contains(priority.EventRef, StringComparison.Ordinal),
            "The detail pane must show the selected finding, its actual result range and recorded references.");
        Invoke(pane, "OnNextClick", next, new RoutedEventArgs());
        Require(title.Text == fixture.Findings[1].DisplayTitle && previous.IsEnabled, "Next must follow severity ordering across the matching result.");
        Invoke(pane, "OnPreviousClick", previous, new RoutedEventArgs());
        Require(title.Text == priority.DisplayTitle && !previous.IsEnabled, "Previous must return to the same first finding.");
        typeof(AIHubPageViewModel).GetProperty(nameof(vm.IsAuditing)).SetValue(vm, true);
        try
        {
            await NextRendering();
            Require(host.IsPaneOpen && !vm.CanConfigureAudit && !vm.StartSelectedScanCommand.CanExecute(null) &&
                !((Button)pane.FindName("AnalyzeButton")).IsEnabled,
                "Busy state must keep the previous evidence readable and disable overlapping audit actions.");
        }
        finally { typeof(AIHubPageViewModel).GetProperty(nameof(vm.IsAuditing)).SetValue(vm, false); }

        var root = (FrameworkElement)App.GetSettingsWindow().Content;
        var themeBefore = root.RequestedTheme;
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (int width in new[] { 1280, 760, 480 })
            {
                string context = $"{theme}-{width}";
                await WaitForPaneLayout(null, context + "-resize", () =>
                {
                    root.RequestedTheme = theme;
                    App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(width, width == 480 ? 480 : 1000));
                });
                // A native resize can dismiss an Overlay pane. Open only after
                // that transition settles, so an old PaneClosed cannot invalidate
                // the next opening while its layout is being inspected.
                if (!host.IsPaneOpen)
                {
                    await WaitForPaneLayout(true, context + "-reopen", () => Invoke(page, "OpenFinding", priority, trigger));
                }
                await Capture(page, $"aihub-details-{theme}-{width}.png");
                LogPaneBounds(context);
                var paneBounds = Bounds(pane, host);
                Require(host.IsPaneOpen && host.OpenPaneLength <= host.ActualWidth + 1 && pane.ActualWidth > 0 && pane.ActualWidth <= host.ActualWidth + 1,
                    $"Finding details must fit the available content width at {theme}/{width}.");
                Require(Inside(paneBounds, host.ActualWidth, host.ActualHeight),
                    $"The open detail surface must be fully visible inside its host at {theme}/{width}.");
                Require(detailScroll.ViewportHeight >= 80 && detailScroll.ViewportHeight <= host.ActualHeight + 2 &&
                    detailScroll.ExtentWidth <= detailScroll.ViewportWidth + 2 && Inside(Bounds(detailScroll, host), host.ActualWidth, host.ActualHeight),
                    $"Details must retain at least 80 DIPs of visible reading area without horizontal overflow at {theme}/{width}.");
                foreach (var button in detailButtons)
                {
                    Require(button.Visibility == Visibility.Visible && button.ActualWidth >= 24 && button.ActualHeight >= 24 &&
                        Inside(Bounds(button, host), host.ActualWidth, host.ActualHeight) &&
                        Inside(Bounds(button, pane), pane.ActualWidth, pane.ActualHeight),
                        $"{button.Name} must remain fully visible and reachable inside the detail pane at {theme}/{width}.");
                }
                Require(detailButtons[0].Focus(FocusState.Keyboard), $"The visible close action must accept keyboard focus at {theme}/{width}.");
            }
        }
        finally { root.RequestedTheme = themeBefore; }
        await WaitForPaneLayout(null, "snapshot-resize", () => App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1000)));
        await WaitForPaneLayout(true, "snapshot-open", () => Invoke(page, "OpenFinding", priority, trigger));
        await Capture(page, "aihub-details-snapshot-open.png");
        LogPaneBounds("snapshot-open");
        Require(host.IsPaneOpen && title.Text == priority.DisplayTitle, "The snapshot-refresh regression must begin with an open matching detail pane.");
        var updated = FluentAuditFixture();
        updated.Findings[4].Title = "Updated same evidence";
        updated.Findings[4].TitleZh = "同一证据的新摘要";
        await WaitForPaneLayout(true, "snapshot-refresh", () => Invoke(vm, "ApplyHistoricalAudit", updated));
        if (title.Text != updated.Findings[4].DisplayTitle)
        {
            await Capture(page, "aihub-details-snapshot-refresh-failed.png");
            LogPaneBounds("snapshot-refresh-failed");
        }
        Require(host.IsPaneOpen && title.Text == updated.Findings[4].DisplayTitle, "Refreshing the same evidence must update the open pane.");
        updated.Findings.RemoveAt(4);
        await WaitForPaneLayout(false, "snapshot-removed", () => Invoke(vm, "ApplyHistoricalAudit", updated));
        Invoke(vm, "ApplyHistoricalAudit", fixture);
        await NextRendering();
        await WaitForPaneLayout(true, "close-button-open", () => Invoke(page, "OpenFinding", fixture.Findings[1], trigger));
        await WaitForPaneLayout(false, "close-button", () => Invoke(pane, "OnCloseClick", pane.FindName("CloseDetailsButton"), new RoutedEventArgs()));
        await WaitForPaneLayout(true, "tab-change-open", () => Invoke(page, "OpenFinding", fixture.Findings[1], trigger));
        await WaitForPaneLayout(false, "tab-change", () => vm.ActiveTabIndex = 1);
        Require(!host.IsPaneOpen, "Changing tabs must close a finding pane from the previous audit view.");
        vm.ActiveTabIndex = 0;
        Require(!((Expander)page.FindName("AuditReportExpander")).IsExpanded &&
            !((Expander)page.FindName("OptimizationHistoryExpander")).IsExpanded,
            "Full reports and operation history must begin collapsed.");
        EnsureNoWorker();
        Log("Fluent scan drafts, priority/all/search/filter projection, detail navigation/busy state/snapshot refresh/close and six detail renders PASS");

        static Windows.Foundation.Rect Bounds(FrameworkElement element, UIElement relativeTo) =>
            element.TransformToVisual(relativeTo).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

        static bool Inside(Windows.Foundation.Rect bounds, double width, double height) =>
            bounds.X >= -2 && bounds.Y >= -2 && bounds.Right <= width + 2 && bounds.Bottom <= height + 2;

        void LogPaneBounds(string context)
        {
            Log($"DETAILS {context}: window={App.GetSettingsWindow().AppWindow.Size.Width}x{App.GetSettingsWindow().AppWindow.Size.Height}, open={host.IsPaneOpen}, requestedWidth={host.OpenPaneLength:F1}, hostInPage={Bounds(host, page)}, paneInHost={Bounds(pane, host)}, readingInHost={Bounds(detailScroll, host)}, readingViewport={detailScroll.ViewportWidth:F1}x{detailScroll.ViewportHeight:F1}, readingExtent={detailScroll.ExtentWidth:F1}x{detailScroll.ExtentHeight:F1}");
            foreach (var button in detailButtons)
            {
                Log($"DETAILS {context}/{button.Name}: hostBounds={Bounds(button, host)}, paneBounds={Bounds(button, pane)}, visibility={button.Visibility}, enabled={button.IsEnabled}, focus={button.FocusState}");
            }
        }

        async Task WaitForPaneLayout(bool? expectedOpen, string context, Action change)
        {
            var elapsed = Stopwatch.StartNew();
            var quiet = Stopwatch.StartNew();
            double[] previousGeometry = null;
            int completedTransitions = 0;
            void TransitionCompleted(SplitView sender, object args)
            {
                completedTransitions++;
                quiet.Restart();
            }
            host.PaneOpened += TransitionCompleted;
            host.PaneClosed += TransitionCompleted;
            try
            {
                change();
                while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
                {
                    await Task.Delay(25);
                    page.UpdateLayout();
                    var paneBounds = Bounds(pane, host);
                    var geometry = new List<double>
                    {
                        host.IsPaneOpen ? 1 : 0, host.ActualWidth, host.ActualHeight,
                        paneBounds.X, paneBounds.Y, paneBounds.Width, paneBounds.Height,
                        detailScroll.ViewportWidth, detailScroll.ViewportHeight,
                    };
                    foreach (var button in detailButtons)
                    {
                        var bounds = Bounds(button, host);
                        geometry.AddRange(new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height });
                    }
                    var currentGeometry = geometry.Select(value => Math.Round(value, 1)).ToArray();
                    if (previousGeometry == null || !currentGeometry.SequenceEqual(previousGeometry))
                    {
                        previousGeometry = currentGeometry;
                        quiet.Restart();
                    }
                    // IsPaneOpen changes before SplitView's compositor animation
                    // ends. Observe completion events and stable transformed bounds;
                    // two render ticks alone can still see an offscreen pane.
                    if ((!expectedOpen.HasValue || host.IsPaneOpen == expectedOpen.Value) && host.ActualWidth > 0 && host.ActualHeight > 0 &&
                        elapsed.Elapsed >= TimeSpan.FromMilliseconds(500) && quiet.Elapsed >= TimeSpan.FromMilliseconds(200))
                    {
                        Log($"DETAILS SETTLED {context}: elapsed={elapsed.ElapsedMilliseconds}ms, completedTransitions={completedTransitions}, open={host.IsPaneOpen}");
                        return;
                    }
                }
                await Capture(page, $"aihub-details-{context}-unsettled.png");
                LogPaneBounds(context + "-unsettled");
                throw new TimeoutException($"Detail layout did not settle at {context}; expectedOpen={expectedOpen}, completedTransitions={completedTransitions}.");
            }
            finally
            {
                host.PaneOpened -= TransitionCompleted;
                host.PaneClosed -= TransitionCompleted;
            }
        }
    }

    private static async Task CheckAuditSemanticStates(AIHubPage page)
    {
        var vm = page.ViewModel;
        var fixture = FluentAuditFixture();
        vm.ActiveTabIndex = 0;
        Invoke(vm, "ApplyHistoricalAudit", fixture);
        try
        {
            typeof(AIHubPageViewModel).GetField("_fluentAiReportPartial", PrivateInstance).SetValue(vm, true);
            Invoke(vm, "RefreshAiSummary");
            Require(!vm.HasAiReport && vm.IsAiSummaryNeutral && !vm.IsAiSummaryCaution && !vm.IsAiSummaryCompleted,
                "A partial flag without an AI report must retain the neutral pending badge.");
            await NextRendering();
            var summary = (FrameworkElement)page.FindName("AuditSummaryCard");
            var badges = Descendants(summary).OfType<Border>().Where(border => border.Child is TextBlock text && text.Text == vm.AiSummaryStatusText).ToArray();
            Require(badges.Length == 3 && badges.Count(border => border.Visibility == Visibility.Visible) == 1 &&
                badges.Single(border => border.Visibility == Visibility.Visible).Child is TextBlock { FontWeight.Weight: 400 },
                "The actual no-report summary must render only its neutral badge.");
            await Capture(page, "aihub-ai-summary-no-report-neutral.png");
            typeof(AIHubPageViewModel).GetProperty(nameof(vm.HasAiReport)).SetValue(vm, true);
            Require(vm.IsAiSummaryCaution && !vm.IsAiSummaryCompleted && !vm.IsAiSummaryNeutral,
                "An existing partial AI report must use the caution badge.");
            Require(!vm.HasAiSummaryPoint1 && !vm.HasAiSummaryPoint2 && vm.AiSummaryPoint1 == string.Empty && vm.AiSummaryPoint2 == string.Empty &&
                vm.RelatedEvidenceFindings.Count == 0, "An AI report without recommendations must not borrow local findings or their advice.");
            vm.AiReportFindings.Add(new AiReportFinding("Synthetic AI finding", "High", "Synthetic AI evidence", "Only this AI recommendation", "1001"));
            Require(vm.AiSummaryPoint1 == "1. Only this AI recommendation" && !vm.HasAiSummaryPoint2 && vm.AiSummaryPoint2 == string.Empty &&
                vm.RelatedEvidenceFindings.SequenceEqual(new[] { fixture.Findings[1] }),
                "A single AI recommendation must expose one point and only its matching recorded evidence.");
            vm.FindingSearchText = "synthetic-no-match";
            Require(vm.RelatedEvidenceFindings.SequenceEqual(new[] { fixture.Findings[1] }) && !vm.HasAiSummaryPoint2,
                "Filtering local findings must not replace AI evidence or fill a missing recommendation.");
            vm.ResetFindingFilters();
            await NextRendering();
            await Capture(page, "aihub-ai-summary-one-recommendation.png");

            var source = new AuditIssue
            {
                Key = "evidence-projection-key", EventRef = "event-record-11", EventId = "4625",
                EventTimestamp = "2026-11-07T12:34:56.0000000Z", Source = "Synthetic provider", LogName = "Security",
                Category = "Authentication", Title = "Projection fixture", TitleZh = "证据投影测试",
                Description = "Recorded event", DescriptionZh = "事件记录", RootCause = "Synthetic reason", RootCauseZh = "合成原因",
                Recommendation = "Inspect evidence", RecommendationZh = "检查证据", Affected = "Synthetic account",
                Severity = "High", Confidence = "Medium", Occurrences = 4, RelatedEventRefs = new() { "event-record-11", "event-record-12" },
            };
            var projected = (AuditIssueEnhanced)typeof(AIHubPageViewModel).GetMethod("ToEnhanced", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { source });
            Require(projected.Key == source.Key && projected.EventRef == source.EventRef && projected.EventId == source.EventId &&
                projected.EventTimestamp == source.EventTimestamp && projected.Source == source.Source && projected.LogName == source.LogName &&
                projected.Category == source.Category && projected.Occurrences == source.Occurrences && projected.Affected == source.Affected &&
                projected.RelatedEventRefs.SequenceEqual(source.RelatedEventRefs) && !ReferenceEquals(projected.RelatedEventRefs, source.RelatedEventRefs),
                "AI finding projection must preserve all recorded evidence identifiers and copy the related-reference collection.");
            source.RelatedEventRefs.Add("later-source-mutation");
            Require(projected.RelatedEventRefs.Count == 2 && projected.TitleZh == source.TitleZh && projected.DescriptionZh == source.DescriptionZh &&
                projected.RootCauseZh == source.RootCauseZh && projected.RecommendationZh == source.RecommendationZh,
                "Evidence projection must remain independent and preserve localized analysis.");
            source.Affected = null;
            source.RelatedEventRefs = null;
            var sparse = (AuditIssueEnhanced)typeof(AIHubPageViewModel).GetMethod("ToEnhanced", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { source });
            Require(sparse.Affected == string.Empty && sparse.RelatedEventRefs.Count == 0,
                "Nullable AI evidence fields must remain safe for filtering and detail rendering.");
        }
        finally
        {
            vm.ResetFindingFilters();
            Invoke(vm, "ApplyHistoricalAudit", fixture);
        }
        Log("AI summary partial/no-report neutrality, zero/one recommendation isolation, report-evidence filtering and evidence projection PASS");
    }

    private static async Task CheckScanButtonBoundState(AIHubPage page, string context)
    {
        page.ViewModel.ActiveTabIndex = 0;
        await NextRendering();
        await NextRendering();
        page.UpdateLayout();
        var scan = (SplitButton)page.FindName("AuditScanButton");
        var primary = Descendants(scan).OfType<Button>().Single(button => button.Name == "PrimaryButton");
        var secondary = Descendants(scan).OfType<Button>().Single(button => button.Name == "SecondaryButton");
        var primaryGrid = Descendants(scan).OfType<Grid>().Single(grid => grid.Name == "PrimaryBackgroundGrid");
        var secondaryGrid = Descendants(scan).OfType<Grid>().Single(grid => grid.Name == "SecondaryBackgroundGrid");
        var label = Descendants(primary).OfType<TextBlock>().Single(block => block.Text == page.ViewModel.SelectedScanLabel);
        var arrow = Descendants(secondary).OfType<AnimatedIcon>().Single();
        byte backdropChannel = scan.ActualTheme == ElementTheme.Dark ? (byte)32 : (byte)243;
        var backdrop = Windows.UI.Color.FromArgb(255, backdropChannel, backdropChannel, backdropChannel);
        var primaryFill = CompositeBrush(Descendants(primary).OfType<Grid>().Single(grid => grid.Name == "RootGrid").Background, CompositeBrush(primaryGrid.Background, backdrop));
        var secondaryFill = CompositeBrush(Descendants(secondary).OfType<Grid>().Single(grid => grid.Name == "RootGrid").Background, CompositeBrush(secondaryGrid.Background, backdrop));
        double textContrast = Contrast(BrushColor(label.Foreground), primaryFill);
        double arrowContrast = Contrast(BrushColor(arrow.Foreground), secondaryFill);
        var templateRoot = (FrameworkElement)VisualTreeHelper.GetChild(scan, 0);
        string state = string.Join(",", VisualStateManager.GetVisualStateGroups(templateRoot).Select(group => group.Name + "=" + group.CurrentState?.Name));
        string commonState = VisualStateManager.GetVisualStateGroups(templateRoot).Single(group => group.Name == "CommonStates").CurrentState?.Name;
        Log($"BOUND STATE {context}: {state}; outer={BrushColor(scan.Background)}, primaryGrid={BrushColor(primaryGrid.Background)}, primaryInner={BrushColor(Descendants(primary).OfType<Grid>().Single(grid => grid.Name == "RootGrid").Background)}");
        Log($"BOUND SPLITBUTTON {context}: theme={scan.ActualTheme}, enabled={scan.IsEnabled}/{page.ViewModel.CanConfigureAudit}, primary={primaryFill}, secondary={secondaryFill}, text={BrushColor(label.Foreground)}, arrow={BrushColor(arrow.Foreground)}, contrast={textContrast:F2}/{arrowContrast:F2}");
        await Capture(page, $"aihub-scan-bound-{context}.png");
        Require(scan.IsEnabled == page.ViewModel.CanConfigureAudit && (commonState == "Disabled") == !scan.IsEnabled &&
            textContrast >= (scan.IsEnabled ? 4.5 : 3) && arrowContrast >= 3,
            "The scan button must remain readable through real bound state changes: " + context);
    }

    private static async Task CheckSplitButtonPalette(AIHubPage page)
    {
        var vm = page.ViewModel;
        var root = (FrameworkElement)App.GetSettingsWindow().Content;
        var themeBefore = root.RequestedTheme;
        var scan = (SplitButton)page.FindName("AuditScanButton");
        var firstLight = new Dictionary<string, string>();
        var paletteFailures = new List<string>();
        vm.ActiveTabIndex = 0;
        ((ScrollViewer)page.FindName("AiHubContentScroll")).ChangeView(null, 0, null, true);
        try
        {
            int pass = 0;
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
            {
                pass++;
                root.RequestedTheme = theme;
                scan.IsEnabled = true;
                await NextRendering();
                await NextRendering();
                scan.ApplyTemplate();
                page.UpdateLayout();
                var primary = Descendants(scan).OfType<Button>().Single(button => button.Name == "PrimaryButton");
                var secondary = Descendants(scan).OfType<Button>().Single(button => button.Name == "SecondaryButton");
                var primaryBackground = Descendants(scan).OfType<Grid>().Single(grid => grid.Name == "PrimaryBackgroundGrid");
                var secondaryBackground = Descendants(scan).OfType<Grid>().Single(grid => grid.Name == "SecondaryBackgroundGrid");
                var label = Descendants(primary).OfType<TextBlock>().Single(block => block.Text == vm.SelectedScanLabel);
                var glyph = Descendants(primary).OfType<FontIcon>().Single();
                var arrow = Descendants(secondary).OfType<AnimatedIcon>().Single();
                var primaryInner = Descendants(primary).OfType<Grid>().Single(grid => grid.Name == "RootGrid");
                var secondaryInner = Descendants(secondary).OfType<Grid>().Single(grid => grid.Name == "RootGrid");
                var backdrop = Windows.UI.Color.FromArgb(255, theme == ElementTheme.Dark ? (byte)32 : (byte)243,
                    theme == ElementTheme.Dark ? (byte)32 : (byte)243, theme == ElementTheme.Dark ? (byte)32 : (byte)243);
                foreach (string state in new[] { "Normal", "PrimaryPointerOver", "PrimaryPressed", "SecondaryPointerOver", "SecondaryPressed", "FlyoutOpen", "Disabled" })
                {
                    scan.IsEnabled = state != "Disabled";
                    Require(VisualStateManager.GoToState(scan, state, false), "The real SplitButton template must expose " + state + ".");
                    if (state == "Disabled")
                    {
                        Require(VisualStateManager.GoToState(primary, "Disabled", false) && VisualStateManager.GoToState(secondary, "Disabled", false),
                            "Both real inner buttons must enter their disabled visual state.");
                    }
                    await NextRendering();
                    page.UpdateLayout();
                    var primaryFill = CompositeBrush(primaryInner.Background, CompositeBrush(primaryBackground.Background, backdrop));
                    var secondaryFill = CompositeBrush(secondaryInner.Background, CompositeBrush(secondaryBackground.Background, backdrop));
                    var textColor = BrushColor(label.Foreground);
                    var glyphColor = BrushColor(glyph.Foreground);
                    var arrowColor = BrushColor(arrow.Foreground);
                    double textContrast = Contrast(textColor, primaryFill);
                    double glyphContrast = Contrast(glyphColor, primaryFill);
                    double arrowContrast = Contrast(arrowColor, secondaryFill);
                    string context = $"{theme}/{pass}/{state}";
                    string palette = $"primary={primaryFill}, secondary={secondaryFill}, text={textColor}, glyph={glyphColor}, arrow={arrowColor}";
                    Log($"SPLITBUTTON {context}: {palette}; contrasts text={textContrast:F2}, glyph={glyphContrast:F2}, arrow={arrowContrast:F2}; outer={BrushColor(scan.Background)}; enabled={scan.IsEnabled}");
                    await Capture(page, $"aihub-scan-palette-{theme}-{pass}-{state}.png");
                    double minimumText = state == "Disabled" ? 3 : 4.5;
                    if (textContrast < minimumText || glyphContrast < 3 || arrowContrast < 3)
                    {
                        paletteFailures.Add($"{context}: text={textContrast:F2}/{minimumText:F1}, glyph={glyphContrast:F2}/3, arrow={arrowContrast:F2}/3");
                    }
                    if (pass == 1) firstLight[state] = palette;
                    if (pass == 3 && firstLight[state] != palette) paletteFailures.Add("Light roundtrip changed " + state + ": " + palette);
                }
            }
        }
        finally
        {
            scan.IsEnabled = vm.CanConfigureAudit;
            VisualStateManager.GoToState(scan, "Normal", false);
            root.RequestedTheme = themeBefore;
            await NextRendering();
        }
        EnsureNoWorker();
        Require(!vm.IsAuditing && !vm.IsAiAnalyzing, "Palette synthesis must never execute a scan or AI request.");
        Require(paletteFailures.Count == 0, "SplitButton actual template palette failures (check state resources and cached theme aliases): " + string.Join("; ", paletteFailures));
        Log("Real SplitButton inner surfaces, label/icon alpha-composited contrast, seven states and Light/Dark/Light theme restoration PASS");
    }

    private static Windows.UI.Color BrushColor(Brush brush)
    {
        if (brush == null) return Windows.UI.Color.FromArgb(0, 0, 0, 0);
        Require(brush is SolidColorBrush, "Palette verification requires an actual solid color brush, got " + brush.GetType().Name + ".");
        var color = ((SolidColorBrush)brush).Color;
        return Windows.UI.Color.FromArgb((byte)Math.Round(color.A * brush.Opacity), color.R, color.G, color.B);
    }

    private static Windows.UI.Color CompositeBrush(Brush foreground, Windows.UI.Color background) => Composite(BrushColor(foreground), background);

    private static Windows.UI.Color Composite(Windows.UI.Color foreground, Windows.UI.Color background)
    {
        double alpha = foreground.A / 255.0;
        return Windows.UI.Color.FromArgb(255,
            (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }

    private static double Contrast(Windows.UI.Color foreground, Windows.UI.Color background)
    {
        static double Linear(byte channel) => channel <= 10 ? channel / 3294.6 : Math.Pow((channel / 255.0 + 0.055) / 1.055, 2.4);
        static double Luminance(Windows.UI.Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        double text = Luminance(Composite(foreground, background));
        double fill = Luminance(background);
        return (Math.Max(text, fill) + 0.05) / (Math.Min(text, fill) + 0.05);
    }

    private static async Task WaitForScrollSettled(ScrollViewer[] viewers, Action action, string context, Func<bool> condition = null)
    {
        var intermediate = new HashSet<ScrollViewer>();
        var elapsed = Stopwatch.StartNew();
        var quiet = Stopwatch.StartNew();
        var previous = viewers.Select(viewer => (viewer.VerticalOffset, viewer.ScrollableHeight, viewer.ViewportWidth, viewer.ViewportHeight)).ToArray();
        void Changed(object sender, ScrollViewerViewChangedEventArgs args)
        {
            if (args.IsIntermediate) intermediate.Add((ScrollViewer)sender);
            else intermediate.Remove((ScrollViewer)sender);
            quiet.Restart();
        }
        foreach (var viewer in viewers) viewer.ViewChanged += Changed;
        try
        {
            action();
            // Tab restoration is queued at Low priority. A rendering callback can
            // run before that work, so do not mistake a pre-restore frame for idle.
            var dispatched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Require(DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, () => dispatched.TrySetResult(true)),
                context + ": could not observe the low-priority dispatcher queue.");
            await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(4));
            while (elapsed.Elapsed < TimeSpan.FromSeconds(4))
            {
                await NextRendering();
                var current = viewers.Select(viewer => (viewer.VerticalOffset, viewer.ScrollableHeight, viewer.ViewportWidth, viewer.ViewportHeight)).ToArray();
                if (!current.SequenceEqual(previous))
                {
                    quiet.Restart();
                    previous = current;
                }
                if (intermediate.Count == 0 && quiet.Elapsed >= TimeSpan.FromMilliseconds(150) && (condition == null || condition())) return;
                await Task.Delay(25);
            }
            throw new TimeoutException(context + ": scrolling did not settle at its expected state; " + string.Join("; ", viewers.Select(viewer =>
                $"{viewer.Name} offset={viewer.VerticalOffset:F1}, max={viewer.ScrollableHeight:F1}, viewport={viewer.ViewportHeight:F1}, extent={viewer.ExtentHeight:F1}")));
        }
        finally
        {
            foreach (var viewer in viewers) viewer.ViewChanged -= Changed;
        }
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
        bool HistoryLoaded() => !Field<bool>(vm, "_loadingOptimizationHistory");
        await WaitForScrollSettled(new[] { scroll }, () => vm.ActiveTabIndex = 0, "audit tab initial", HistoryLoaded);
        double auditTarget = Math.Min(240, scroll.ScrollableHeight);
        Require(auditTarget > 0, "The audit scroll fixture must have scrollable content.");
        await WaitForScrollSettled(new[] { scroll }, () => scroll.ChangeView(null, auditTarget, null, true),
            $"audit scroll request {auditTarget:F1}", () => Math.Abs(scroll.VerticalOffset - auditTarget) < 2);
        double auditOffset = scroll.VerticalOffset;
        await WaitForScrollSettled(new[] { scroll }, () => vm.ActiveTabIndex = 1, "optimization tab initial", HistoryLoaded);
        double optimizationTarget = Math.Min(360, scroll.ScrollableHeight);
        Require(optimizationTarget > 0, "The optimization scroll fixture must have scrollable content.");
        await WaitForScrollSettled(new[] { scroll }, () => scroll.ChangeView(null, optimizationTarget, null, true),
            $"optimization scroll request {optimizationTarget:F1}", () => Math.Abs(scroll.VerticalOffset - optimizationTarget) < 2);
        double optimizationOffset = scroll.VerticalOffset;
        Log($"TAB SCROLL saved: audit={auditOffset:F1}, optimization={optimizationOffset:F1}");
        await WaitForScrollSettled(new[] { scroll }, () => vm.ActiveTabIndex = 0,
            $"audit tab restore {auditOffset:F1}", () => HistoryLoaded() && Math.Abs(scroll.VerticalOffset - auditOffset) < 2);
        Log($"TAB SCROLL audit restored: expected={auditOffset:F1}, actual={scroll.VerticalOffset:F1}, max={scroll.ScrollableHeight:F1}");
        Require(Math.Abs(scroll.VerticalOffset - auditOffset) < 2, "Audit tab must restore its own scroll offset.");
        await WaitForScrollSettled(new[] { scroll }, () => vm.ActiveTabIndex = 1,
            $"optimization tab restore {optimizationOffset:F1}", () => HistoryLoaded() && Math.Abs(scroll.VerticalOffset - optimizationOffset) < 2);
        Log($"TAB SCROLL optimization restored: expected={optimizationOffset:F1}, actual={scroll.VerticalOffset:F1}, max={scroll.ScrollableHeight:F1}");
        Require(Math.Abs(scroll.VerticalOffset - optimizationOffset) < 2, "Optimization tab must restore its own scroll offset.");
        var tabs = (FrameworkElement)page.FindName("AiHubSections");
        Require(!IsDescendantOf(tabs, scroll), "Tab navigation must stay outside the scrolling content.");
        var master = (FrameworkElement)page.FindName("AiHubMasterCardSurface");
        Require(!IsDescendantOf(master, scroll), "Service status must remain visible above tab content.");
        Require(master.TransformToVisual(page).TransformPoint(new(0, master.ActualHeight)).Y <= tabs.TransformToVisual(page).TransformPoint(new(0, 0)).Y,
            "Service status must precede the fixed tab navigation.");
        await Navigate(typeof(GeneralPage));
        var referencePage = (GeneralPage)NavigationService.Frame.Content;
        var referenceControl = Descendants(referencePage).OfType<SettingsPageControl>().Single();
        var referenceContent = (FrameworkElement)referenceControl.FindName("ModuleContentPresenter");
        var referenceTitle = (FrameworkElement)referenceControl.FindName("Header");
        var contentBounds = new Dictionary<int, (double Left, double Width, double TitleLeft)>();
        foreach (int width in new[] { 1280, 760, 480 })
        {
            App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(width, width == 480 ? 480 : 1000));
            await NextRendering();
            await NextRendering();
            contentBounds[width] = (referenceContent.TransformToVisual(referencePage).TransformPoint(new(0, 0)).X,
                referenceContent.ActualWidth, referenceTitle.TransformToVisual(referencePage).TransformPoint(new(0, 0)).X);
        }
        await Navigate(typeof(AIHubPage));
        Require(ReferenceEquals(page, NavigationService.Frame.Content), "Navigation must retain the cached page.");
        var root = (FrameworkElement)App.GetSettingsWindow().Content;
        var themeBefore = root.RequestedTheme;
        bool sawNarrowPolicyLayout = false;
        bool sawWidePolicyLayout = false;
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (int width in new[] { 1280, 760, 480 })
            foreach (int tab in new[] { 0, 1, 2 })
            {
                root.RequestedTheme = theme;
                App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(width, width == 480 ? 480 : 1000));
                vm.ActiveTabIndex = tab;
                await NextRendering();
                await NextRendering();
                scroll.ChangeView(null, 0, null, true);
                await NextRendering();
                page.UpdateLayout();
                await CheckEnableCardAndHealthLayout(page, $"{theme}/{width}/tab{tab}");
                Require(scroll.ExtentWidth <= scroll.ViewportWidth + 2, $"Horizontal overflow at {theme}/{width}/tab{tab}.");
                var expected = contentBounds[width];
                var content = (FrameworkElement)page.FindName("AiHubTabContent");
                var title = (FrameworkElement)page.FindName("AiHubPageTitle");
                Require(Math.Abs(content.TransformToVisual(page).TransformPoint(new(0, 0)).X - expected.Left) < 2 &&
                    Math.Abs(content.ActualWidth - expected.Width) < 2 &&
                    Math.Abs(title.TransformToVisual(page).TransformPoint(new(0, 0)).X - expected.TitleLeft) < 2,
                    $"AI Hub content/title must match SettingsPageControl margins at {width}.");
                Require(Math.Abs(master.ActualWidth - content.ActualWidth) < 2 && tabs.ActualWidth <= content.ActualWidth + 2 &&
                    Math.Abs(tabs.TransformToVisual(page).TransformPoint(new(0, 0)).X - expected.Left) < 2,
                    "Service status must fill the content column, with navigation aligned to its left edge.");
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
                if (tab == 2)
                {
                    var columns = (Grid)page.FindName("TaskPolicyColumnsGrid");
                    var editor = (FrameworkElement)page.FindName("TaskPolicyEditorCard");
                    var scope = (FrameworkElement)page.FindName("TaskPolicyScopeCard");
                    bool narrow = columns.ActualWidth < 720;
                    sawNarrowPolicyLayout |= narrow;
                    sawWidePolicyLayout |= !narrow;
                    var editorBounds = HeaderBounds(editor, columns);
                    var scopeBounds = HeaderBounds(scope, columns);
                    Require(narrow
                        ? scopeBounds.Top >= editorBounds.Bottom - 2 && Math.Abs(scopeBounds.Left - editorBounds.Left) < 2
                        : scopeBounds.Left >= editorBounds.Right - 2 && Math.Abs(scopeBounds.Top - editorBounds.Top) < 2,
                        $"Policy scope must move below the editor in narrow content and beside it in wide content at {theme}/{width}.");
                }
                await Capture(page, $"aihub-{theme}-{width}-tab{tab}.png");
                if (tab == 1) await CheckInventoryLayout(page, theme, width);
            }
            Require(sawNarrowPolicyLayout && sawWidePolicyLayout, "Policy layout verification must exercise both sides of its content-width breakpoint.");
        }
        finally { root.RequestedTheme = themeBefore; }
        Log("Independent tab scroll, shared introduction, ordered reachable enable card/fixed service status/navigation, unique module toggle, localized 96-DIP health label, readable content viewport, shared page margins, stretched optimization groups, responsive policy columns, cached page and 18 theme/width/tab renders PASS");
    }

    private static async Task CheckEnableCardAndHealthLayout(AIHubPage page, string context)
    {
        var introduction = page.FindName("AiHubIntroduction") as SettingsPageIntro;
        var introScroll = page.FindName("AiHubIntroScroll") as ScrollViewer;
        Require(introduction != null && introScroll != null && IsDescendantOf(introduction, introScroll),
            context + ": the shared introduction must use its own bounded header scroll region.");
        introScroll.ChangeView(null, 0, null, true);
        await NextRendering();
        await NextRendering();
        await CheckIntroduction(introduction, "AiHub.png", context);
        page.UpdateLayout();
        var surface = page.FindName("AiHubEnableCardSurface") as Border;
        var card = page.FindName("AiHubEnableSettingsCard") as CommunityToolkit.WinUI.Controls.SettingsCard;
        Require(surface != null && card != null && IsDescendantOf(card, surface) &&
            ReferenceEquals(surface.Style, Application.Current.Resources["KitHeroSurfaceStyle"]) && card.Template != null,
            context + ": the enable surface must render the shared hero style and a real SettingsCard.");
        Require(card.HeaderIcon != null && !string.IsNullOrWhiteSpace(Convert.ToString(card.Header)) &&
            Descendants(card).OfType<TextBlock>().Any(block => block.Text == Convert.ToString(card.Header) && block.ActualWidth > 0),
            context + ": the enable SettingsCard must render its localized header and module icon.");
        var toggles = Descendants(page).OfType<ToggleSwitch>()
            .Where(toggle => AutomationProperties.GetAutomationId(toggle) == "Toggle_AIHub").ToArray();
        Require(toggles.Length == 1 && ReferenceEquals(card.Content, toggles[0]) && IsDescendantOf(toggles[0], card) &&
            toggles[0].IsOn == page.ViewModel.IsEnabled,
            context + ": exactly one module toggle must live in the enable SettingsCard and reflect the actual module gate.");

        var title = (TextBlock)page.FindName("AiHubPageTitle");
        Require(!string.IsNullOrWhiteSpace(title.Text) && ReferenceEquals(title.Style, Application.Current.Resources["TitleTextBlockStyle"]) &&
            AutomationProperties.GetHeadingLevel(title) == AutomationHeadingLevel.Level1,
            context + ": the module title must retain the shared title style and heading semantics.");
        var master = (FrameworkElement)page.FindName("AiHubMasterCardSurface");
        var tabs = (FrameworkElement)page.FindName("AiHubSections");
        var scroll = (ScrollViewer)page.FindName("AiHubContentScroll");
        var introBounds = HeaderBounds(introduction, page);
        var introScrollBounds = HeaderBounds(introScroll, page);
        var enableBounds = HeaderBounds(surface, page);
        var readinessBounds = HeaderBounds(master, page);
        var tabBounds = HeaderBounds(tabs, page);
        var scrollBounds = HeaderBounds(scroll, page);
        Log($"HEADER {context}: page={page.ActualWidth:F1}x{page.ActualHeight:F1}, intro={introBounds}, introViewport={introScrollBounds}, introExtent={introScroll.ExtentHeight:F1}, enable={enableBounds}, readiness={readinessBounds}, tabs={tabBounds}, content={scrollBounds}, viewport={scroll.ViewportWidth:F1}x{scroll.ViewportHeight:F1}");
        Require(surface.Visibility == Visibility.Visible && surface.ActualHeight > 0 && card.ActualHeight > 0 &&
            IsDescendantOf(surface, introScroll) && !IsDescendantOf(surface, scroll) &&
            !IsDescendantOf(master, introScroll) && !IsDescendantOf(tabs, introScroll) &&
            HeaderBounds(title, page).Bottom <= introScrollBounds.Top + 2 && introBounds.Bottom <= enableBounds.Top + 2 &&
            introScrollBounds.Bottom <= readinessBounds.Top + 2 && readinessBounds.Bottom <= tabBounds.Top + 2 &&
            tabBounds.Bottom <= scrollBounds.Top + 2,
            context + ": title, introduction/enable viewport, fixed readiness and navigation must stay in their order without overlapping.");
        Require(HeaderInside(introScrollBounds, page) && HeaderInside(readinessBounds, page) && HeaderInside(tabBounds, page) &&
            HeaderInside(HeaderBounds(toggles[0], surface), surface) && Math.Abs(surface.ActualWidth - master.ActualWidth) < 2 &&
            introScroll.ViewportHeight >= 64 && introScroll.ScrollableWidth <= 1,
            context + ": the bounded introduction and fixed navigation must fit the shared content column without horizontal scrolling.");
        Require(scroll.ViewportHeight >= 80 && HeaderInside(scrollBounds, page),
            context + ": the fixed header must leave at least 80 DIPs of visible tab content, including at a 480-pixel window height.");
        if (introScroll.ScrollableHeight <= 1)
        {
            Require(HeaderInside(HeaderBounds(introduction, introScroll), introScroll) && HeaderInside(HeaderBounds(surface, introScroll), introScroll),
                context + ": a normal-height window must display the complete introduction and enable card.");
        }
        else
        {
            introScroll.ChangeView(null, introScroll.ScrollableHeight, null, true);
            await WaitUntil(() => Math.Abs(introScroll.VerticalOffset - introScroll.ScrollableHeight) <= 1);
            await NextRendering();
            Require(HeaderInside(HeaderBounds(toggles[0], introScroll), introScroll) && HeaderInside(HeaderBounds(toggles[0], page), page),
                context + ": scrolling the short header must reveal the entire enable switch.");
            await Capture(page, $"aihub-header-scroll-{context.Replace('/', '-')}.png");

            // Move focus outside the header before returning to the top so this
            // tests automatic keyboard bring-into-view rather than an existing focus.
            var focusAnchor = Descendants(master).OfType<HyperlinkButton>().Single();
            Require(focusAnchor.Focus(FocusState.Keyboard), context + ": fixed service settings must accept keyboard focus.");
            introScroll.ChangeView(null, 0, null, true);
            await WaitUntil(() => introScroll.VerticalOffset <= 1);
            await NextRendering();
            Require(toggles[0].Focus(FocusState.Keyboard), context + ": the enable switch must accept keyboard focus.");
            await WaitUntil(() => HeaderInside(HeaderBounds(toggles[0], introScroll), introScroll));
            Require(HeaderInside(HeaderBounds(toggles[0], page), page), context + ": keyboard focus must bring the enable switch into the page viewport.");
            await Capture(page, $"aihub-header-focus-{context.Replace('/', '-')}.png");
            Require(Math.Abs(HeaderBounds(master, page).Top - readinessBounds.Top) < 2 &&
                Math.Abs(HeaderBounds(tabs, page).Top - tabBounds.Top) < 2 &&
                Math.Abs(HeaderBounds(scroll, page).Top - scrollBounds.Top) < 2 && scroll.ViewportHeight >= 80,
                context + ": scrolling or focusing the introduction must not move fixed navigation or shrink the reading viewport.");
            Require(focusAnchor.Focus(FocusState.Programmatic), context + ": focus must return outside the introduction before restoring its offset.");
            introScroll.ChangeView(null, 0, null, true);
            await WaitUntil(() => introScroll.VerticalOffset <= 1);
            await NextRendering();
        }

        if (page.ViewModel.ActiveTabIndex == 0)
        {
            var score = (FrameworkElement)page.FindName("AuditScoreCard");
            var labels = Descendants(score).OfType<TextBlock>()
                .Where(block => block.Text == page.ViewModel.HealthScoreLabel).ToArray();
            Require(labels.Length == 1, context + ": the health ring must contain one localized score label.");
            var label = labels[0];
            if (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en")
                Require(label.Text == "Health", context + ": the English health ring label must be the compact 'Health'.");
            Require(Math.Abs(score.ActualWidth - 96) < 1 && Math.Abs(score.ActualHeight - 96) < 1 &&
                label.ActualWidth > 0 && label.ActualHeight > 0 && label.ActualWidth <= 96 &&
                !label.IsTextTrimmed && HeaderInside(HeaderBounds(label, score), score),
                context + ": the complete health label must fit inside the 96-DIP score ring without clipping or trimming.");
        }
    }

    private static async Task CheckIntroduction(SettingsPageIntro introduction, string assetName, string context, int expectedPixels = 0)
    {
        var iconSurface = introduction.FindName("ModuleIconSurface") as Border;
        var image = introduction.FindName("ModuleIcon") as Image;
        var description = introduction.FindName("AboutDescription") as TextBlock;
        var links = introduction.FindName("PrimaryLinksControl") as ItemsControl;
        Require(iconSurface != null && image != null && description != null && links != null,
            context + ": shared introduction elements must exist in its own namescope.");
        await WaitUntil(() => image.Source is BitmapImage { PixelWidth: > 0, PixelHeight: > 0 });
        var bitmap = (BitmapImage)image.Source;
        Require(introduction.ModuleImageSource != null && bitmap.UriSource == introduction.ModuleImageSource &&
            bitmap.UriSource.AbsolutePath.EndsWith("/Assets/Settings/Modules/" + assetName, StringComparison.OrdinalIgnoreCase),
            context + ": the visible module image must load from the expected packaged module asset.");
        if (expectedPixels > 0)
            Require(bitmap.PixelWidth == expectedPixels && bitmap.PixelHeight == expectedPixels,
                context + ": the visible logo must load the replacement square asset, not the old settings screenshot.");
        Require(Math.Abs(iconSurface.ActualWidth - 64) < 1 && Math.Abs(iconSurface.ActualHeight - 64) < 1 &&
            image.ActualWidth > 0 && image.ActualHeight > 0 && HeaderInside(HeaderBounds(image, iconSurface), iconSurface),
            context + ": the loaded image must fit the shared 64-DIP logo surface.");
        Require(!string.IsNullOrWhiteSpace(introduction.ModuleDescription) && description.Text == introduction.ModuleDescription &&
            description.FontSize >= 14 && description.TextWrapping == TextWrapping.Wrap && !description.IsTextTrimmed &&
            description.Foreground is SolidColorBrush { Color: { A: > 0 } } &&
            description.ActualWidth > 0 && description.ActualHeight > 0,
            context + ": the full module introduction must use readable wrapping body text and the shared secondary foreground.");
        Require(ReferenceEquals(links.ItemsSource, introduction.PrimaryLinks) &&
            links.Visibility == (introduction.PrimaryLinks.Count > 0 ? Visibility.Visible : Visibility.Collapsed),
            context + ": optional primary links must reflect this introduction's collection without an empty link strip.");
        Log($"INTRO {context}: image={bitmap.UriSource}, pixels={bitmap.PixelWidth}x{bitmap.PixelHeight}, icon={iconSurface.ActualWidth:F1}x{iconSurface.ActualHeight:F1}, description={description.ActualWidth:F1}x{description.ActualHeight:F1}, font={description.FontSize:F1}, links={introduction.PrimaryLinks.Count}");
    }

    private static async Task CheckSharedModuleHeaders(AIHubPage originalPage)
    {
        var window = App.GetSettingsWindow();
        var shell = (FrameworkElement)window.Content;
        var originalTheme = shell.RequestedTheme;
        var originalSize = window.AppWindow.Size;
        try
        {
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1000));
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (var module in new[] { (Page: typeof(AwakePage), Key: "Awake"), (Page: typeof(LightSwitchPage), Key: "LightSwitch") })
            {
                shell.RequestedTheme = theme;
                await Navigate(module.Page);
                await NextRendering();
                var page = (Page)NavigationService.Frame.Content;
                var shared = Descendants(page).OfType<SettingsPageControl>().Single();
                var introduction = shared.FindName("PageIntro") as SettingsPageIntro;
                var title = shared.FindName("Header") as TextBlock;
                var content = (FrameworkElement)shared.FindName("ModuleContentPresenter");
                var scroll = Descendants(shared).OfType<ScrollViewer>().Single(item => AutomationProperties.GetAutomationId(item) == "PageScrollViewer");
                scroll.ChangeView(null, 0, null, true);
                await NextRendering();
                await NextRendering();
                string context = module.Key + "/" + theme;
                Require(introduction != null && title != null && !string.IsNullOrWhiteSpace(title.Text) &&
                    title.Text == shared.ModuleTitle && ReferenceEquals(title.Style, Application.Current.Resources["TitleTextBlockStyle"]) &&
                    AutomationProperties.GetHeadingLevel(title) == AutomationHeadingLevel.Level1,
                    context + ": the standard module header must retain its localized title and heading style.");
                await CheckIntroduction(introduction, module.Key + ".png", context, expectedPixels: 192);
                Require(introduction.ModuleDescription == shared.ModuleDescription && ReferenceEquals(introduction.PrimaryLinks, shared.PrimaryLinks),
                    context + ": the standard page must forward its real description and primary links to the shared introduction.");
                var toggle = Descendants(page).OfType<ToggleSwitch>().Single(item => AutomationProperties.GetAutomationId(item) == "Toggle_" + module.Key);
                var enableCard = Descendants(content).OfType<CommunityToolkit.WinUI.Controls.SettingsCard>().Single(item => ReferenceEquals(item.Content, toggle));
                var introBounds = HeaderBounds(introduction, page);
                var contentBounds = HeaderBounds(content, page);
                Require(!toggle.IsOn && HeaderBounds(title, page).Bottom <= introBounds.Top + 2 &&
                    introBounds.Bottom <= HeaderBounds(enableCard, page).Top + 2 &&
                    HeaderInside(introBounds, page) && HeaderInside(HeaderBounds(enableCard, page), page) &&
                    Math.Abs(introBounds.Left - contentBounds.Left) < 2 && Math.Abs(introduction.ActualWidth - content.ActualWidth) < 2 &&
                    !IsDescendantOf(title, scroll) && IsDescendantOf(introduction, scroll),
                    context + ": the normal page must show title, logo/description and disabled enable card in the shared column while preserving original scrolling behavior.");
                await Capture(page, $"module-header-{module.Key}-{theme}.png");
                EnsureNoWorker();
            }
        }
        finally
        {
            shell.RequestedTheme = originalTheme;
            window.AppWindow.Resize(originalSize);
            await Navigate(typeof(AIHubPage));
            Require(ReferenceEquals(originalPage, NavigationService.Frame.Content), "Header asset navigation must retain the cached AI Hub page and its fixtures.");
        }
        Log("Awake and LightSwitch shared title/introduction layout, real 192x192 replacement logo loading, primary links and normal header renders in both themes PASS");
    }

    private static Windows.Foundation.Rect HeaderBounds(FrameworkElement element, UIElement relativeTo) =>
        element.TransformToVisual(relativeTo).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static bool HeaderInside(Windows.Foundation.Rect bounds, FrameworkElement container) =>
        bounds.X >= -2 && bounds.Y >= -2 && bounds.Right <= container.ActualWidth + 2 && bounds.Bottom <= container.ActualHeight + 2;

    private static async Task CheckInventoryLayout(AIHubPage page, ElementTheme theme, int width)
    {
        var vm = page.ViewModel;
        var scroll = (ScrollViewer)page.FindName("AiHubContentScroll");
        var inventoryExpander = (Expander)page.FindName("InventoryExpander");
        inventoryExpander.IsExpanded = true;
        await NextRendering();
        await NextRendering();
        var panel = (FrameworkElement)page.FindName("InventoryPanel");
        var list = (ListView)page.FindName("InventoryScopeList");
        var content = (FrameworkElement)page.FindName("AiHubTabContent");
        Require(panel.Visibility == Visibility.Visible && Math.Abs(inventoryExpander.ActualWidth - content.ActualWidth) < 2 &&
            panel.ActualWidth > content.ActualWidth - 64 && panel.ActualWidth <= content.ActualWidth,
            "Expanded inventory must fit the full-width disclosure with its normal content padding.");
        Require(list.SelectionMode == ListViewSelectionMode.None && list.MaxHeight <= 600 && list.ActualHeight <= 602,
            "Read-only inventory rows must use a bounded, nonselectable list.");
        panel.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
        await NextRendering();
        await Capture(page, $"aihub-inventory-{theme}-{width}.png");

        var first = vm.Inventory.FilteredRows[0];
        first.IsExpanded = true;
        list.ScrollIntoView(first, ScrollIntoViewAlignment.Leading);
        list.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
        await NextRendering();
        await NextRendering();
        page.UpdateLayout();
        Require(first.HasBeenExpanded && first.TopFiles.Count == 5, "Expanding an inventory row must reveal its bounded details.");
        Require(!Descendants(list).OfType<CheckBox>().Any() && !Descendants(list).OfType<Button>().Any(button => button.Command != null),
            "Inventory details must not expose file selection or execution commands.");
        var item = (FrameworkElement)list.ContainerFromItem(first);
        Require(item != null, "The expanded inventory row must be realized.");
        var expander = Descendants(item).OfType<Expander>().Single();
        Require(expander.IsExpanded && Descendants(expander).OfType<TextBlock>().Any(block => block.Text == first.TopFiles[0].Path),
            "Expanded inventory must render actual largest-file paths.");
        Require(scroll.ExtentWidth <= scroll.ViewportWidth + 2 && list.ActualWidth <= panel.ActualWidth,
            $"Long inventory paths must not cause horizontal overflow at {theme}/{width}.");
        foreach (var text in Descendants(expander).OfType<TextBlock>().Where(block => block.ActualWidth > 0))
        {
            var left = text.TransformToVisual(list).TransformPoint(new(0, 0)).X;
            Require(left >= -2 && left + text.ActualWidth <= list.ActualWidth + 2,
                $"Inventory text must stay inside the list column at {theme}/{width}.");
        }
        await Capture(page, $"aihub-inventory-details-{theme}-{width}.png");
        first.IsExpanded = false;
        inventoryExpander.IsExpanded = false;
        await NextRendering();
        Log($"Read-only inventory layout and long-path details {theme}/{width} PASS");
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
    private static async Task Capture(Page page, string name)
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
