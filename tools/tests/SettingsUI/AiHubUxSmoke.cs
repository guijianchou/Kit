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
        Require(!vm.CanAnalyzeStorage && !vm.AnalyzeStorageCommand.CanExecute(null), "Disabled module must block local space analysis.");
        Require(!Descendants(page).OfType<TextBox>().Any(box => box.Name == "AiTaskPolicyContentBox"), "Task editor must remain unloaded before its first visit.");
        await CheckPolicyDrafts(page);

        // This changes only the stopped in-memory repository. Never toggle the page's
        // persisted module switch, and never invoke a scan or accept an operation dialog.
        general.SettingsConfig.Enabled.AiHub = true;
        vm.RefreshEnabledState();
        Require(vm.IsEnabled && !vm.AiHub.IsEnabled, "Synthetic selection needs only the local module gate.");
        var items = InjectCandidates(vm);
        await CheckSelectionAndFilters(page, items);
        await CheckInventoryIsolation(page);
        await CheckConfirmation(page);
        await CheckAuditSnapshotIsolation(page);
        await CheckAuditFailureLayout(page);
        AddHistoryFixture(vm);
        await CheckScrollAndLayouts(page);
        EnsureNoWorker();
        Require(!vm.IsOptimizing && !vm.IsInventoryScanning && !vm.IsAuditing && !vm.IsAiAnalyzing, "The smoke must finish with no work running.");
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
        var commandHeader = (FrameworkElement)page.FindName("AuditCommandHeader");
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
                Require(card.Visibility == Visibility.Visible && card.ActualHeight > 80 && card.ActualHeight <= 250,
                    context + ": a long native failure must produce a bounded workflow card, not push results hundreds of pixels down.");
                // TextBlock.ActualWidth can describe only the rendered text;
                // its allocated layout slot is the available wrapping width.
                Require(detailScroll.ActualWidth >= card.ActualWidth - 50 && statusSlot.Width >= detailScroll.ViewportWidth - 4 &&
                    aiResultSlot.Width >= detailScroll.ViewportWidth - 4 && statusSlot.Left >= -1 && aiResultSlot.Left >= -1 &&
                    statusSlot.Right <= detailScroll.ViewportWidth + 1 && aiResultSlot.Right <= detailScroll.ViewportWidth + 1,
                    context + ": both workflow messages must retain the full readable column width.");
                Require(scroll.ExtentWidth <= scroll.ViewportWidth + 2 && detailScroll.ScrollableWidth <= 1 && detailScroll.ActualHeight <= 145,
                    context + ": failure details must scroll vertically within their finite viewport without horizontal overflow.");
                double cardBottom = card.TransformToVisual(scroll).TransformPoint(new(0, card.ActualHeight)).Y;
                double nextTop = commandHeader.TransformToVisual(scroll).TransformPoint(new(0, 0)).Y;
                Require(nextTop >= cardBottom && nextTop - cardBottom <= 24 && nextTop < scroll.ViewportHeight,
                    context + ": scan controls must remain immediately below the workflow card and visible in the outer viewport.");
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

    private static async Task WaitForScrollSettled(ScrollViewer[] viewers, Action action, string context)
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
            while (elapsed.Elapsed < TimeSpan.FromSeconds(4))
            {
                await NextRendering();
                var current = viewers.Select(viewer => (viewer.VerticalOffset, viewer.ScrollableHeight, viewer.ViewportWidth, viewer.ViewportHeight)).ToArray();
                if (!current.SequenceEqual(previous))
                {
                    quiet.Restart();
                    previous = current;
                }
                if (intermediate.Count == 0 && quiet.Elapsed >= TimeSpan.FromMilliseconds(150)) return;
                await Task.Delay(25);
            }
            throw new TimeoutException(context + ": nested scrolling did not settle; " + string.Join("; ", viewers.Select(viewer =>
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
                if (tab == 1) await CheckInventoryLayout(page, theme, width);
            }
        }
        finally { root.RequestedTheme = themeBefore; }
        Log("Independent tab scroll, master switch before fixed navigation, shared page margins, stretched optimization groups, cached page and 12 theme/width/tab renders PASS");
    }

    private static async Task CheckInventoryLayout(AIHubPage page, ElementTheme theme, int width)
    {
        var vm = page.ViewModel;
        var scroll = (ScrollViewer)page.FindName("AiHubContentScroll");
        var panel = (FrameworkElement)page.FindName("InventoryPanel");
        var list = (ListView)page.FindName("InventoryScopeList");
        var content = (FrameworkElement)page.FindName("AiHubTabContent");
        Require(panel.Visibility == Visibility.Visible && Math.Abs(panel.ActualWidth - content.ActualWidth) < 2,
            "Inventory must share the plugin's full-width content column.");
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
