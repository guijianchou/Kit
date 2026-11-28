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
using Kit.AiHub.Contract;
using Kit.AiHub.Models;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Kit.AIHubLib.Storage;
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
        File.WriteAllText(Report, "Real WinUI; synthetic in-memory audit/candidate/inventory/history data and injected scan/model delegates. No real Windows log collection, AI requests, confirmed file operations, policy saves or clipboard writes.\n");
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
        SetAuditEvidenceSession(vm,
            new AuditEvidenceSession((_, _, _, _, _) => Task.FromResult(new AuditEvidenceBatch([], [], []))));
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
        // persisted module switch or accept an operation dialog. Scan coverage below
        // uses injected in-memory collectors and model delegates only.
        general.SettingsConfig.Enabled.AiHub = true;
        vm.RefreshEnabledState();
        Require(vm.IsEnabled && !vm.AiHub.IsEnabled, "Synthetic selection needs only the local module gate.");
        await CheckScanButtonBoundState(page, "module-enabled");
        await CheckEnableCardAndHealthLayout(page, "module-enabled");
        await CheckWeeklyReport(page);
        await CheckWorkflowTimers(page);
        await CheckAuditAnalysisLifecycle(page);
        await CheckAuditReadinessOrdering(page);
        await CheckSystemDiagnosis(page);
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
        await CheckPeriodOverview(page);
        await CheckFullScanAnalysis(page);
        await CheckReadableOverview(page);
        await CheckAuditWorkspace(page);
        await CheckFluentOverviewAndDetails(page);
        await CheckAuditSemanticStates(page);
        await CheckAiFinalAssessment(page);
        await CheckSplitButtonPalette(page);
        AddHistoryFixture(vm);
        await CheckScrollAndLayouts(page);
        await CheckSharedModuleHeaders(page);
        await ShellPaletteChecks.RunAsync(Output);
        Log("Shell module palettes, native interaction states, Light/Dark/Light restoration, contrast, keyboard focus and responsive pane renders PASS");
        EnsureNoWorker();
        Require(!vm.IsOptimizing && !vm.IsInventoryScanning && !vm.IsAuditing && !vm.IsAiAnalyzing, "The smoke must finish with no work running.");
    }

    private static async Task CheckSystemDiagnosis(AIHubPage page)
    {
        var vm = page.ViewModel;
        var general = SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default);
        bool previousEnabled = general.SettingsConfig.Enabled.AiHub;
        int previousTab = vm.ActiveTabIndex;
        var resultField = typeof(AIHubPageViewModel).GetField("_systemDiagnosis", PrivateInstance);
        var previousResult = resultField.GetValue(vm);
        bool chinese = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
        var snapshot = new OptimizationEvidenceSnapshot
        {
            CollectedUtc = DateTimeOffset.UtcNow,
            CollectionElapsedMilliseconds = 750,
            Evidence = new()
            {
                new() { Id = "item-000001", Domain = "performance", Coverage = "sampled", Observation = "Synthetic CPU sample: 12 percent for 750 ms. No sustained-load baseline." },
                new() { Id = "item-000002", Domain = "startup", Coverage = "unavailable", Observation = "Synthetic startup evidence unavailable." },
            },
            LocalLabels = new(StringComparer.Ordinal) { ["item-000001"] = "Synthetic process / 测试进程" },
        };
        var report = new OptimizationDiagnosticReport
        {
            Summary = chinese ? "这是一次只读诊断，短时样本尚不能证明持续负载。" : "This advisory diagnosis does not establish sustained load from a brief sample.",
            Findings = new()
            {
                new()
                {
                    Title = chinese ? "继续观察负载" : "Observe resource use",
                    Priority = "monitor",
                    Diagnosis = chinese ? "本次只有一个短时样本。" : "Only one brief sample is available.",
                    Recommendation = chinese ? "重现卡顿时查看任务管理器。" : "Observe Task Manager while reproducing the slowdown.",
                    ExpectedBenefit = chinese ? "确认是否存在持续资源压力。" : "Establish whether resource pressure persists.",
                    Tradeoff = chinese ? "观察需要时间；尚未改动任何设置。" : "Observation takes time; no settings have been changed.",
                    Verification = chinese ? "在相同工作负载下比较多次采样。" : "Compare repeated samples under the same workload.",
                    EvidenceIds = new() { "item-000001" },
                },
            },
            Limitations = new() { chinese ? "启动项不可用，不能据此判断其状态正常。" : "Startup evidence is unavailable; its health cannot be inferred." },
        };
        var success = new OptimizationDiagnosticResult(snapshot, report, Kit.AiHub.Contract.AiErrorCode.None, 900);
        var failure = new OptimizationDiagnosticResult(snapshot, null, Kit.AiHub.Contract.AiErrorCode.Timeout, 90000);
        TempFileInfo[] candidates = vm.OptimizationCandidates.ToArray();
        int selected = vm.SelectedCandidatesCount;

        void Present(OptimizationDiagnosticResult result)
        {
            resultField.SetValue(vm, result);
            Invoke(vm, "OnPropertyChanged", nameof(vm.HasSystemDiagnosis));
            Invoke(vm, "OnPropertyChanged", nameof(vm.SystemDiagnosisText));
        }

        void RequireNoFileActions()
        {
            Require(vm.OptimizationCandidates.SequenceEqual(candidates) && vm.SelectedCandidatesCount == selected &&
                !vm.CanExecuteOptimization && !vm.CanCleanAll && !vm.IsOptimizing && !vm.IsInventoryScanning,
                "Displaying a read-only diagnosis must not create, select, enable or execute file operations.");
        }

        try
        {
            Require(candidates.Length == 0 && selected == 0, "Diagnosis UI fixtures must run before synthetic cleanup candidates are inserted.");
            vm.ActiveTabIndex = 1;
            await NextRendering();
            var card = (FrameworkElement)page.FindName("OptimizationDiagnosisCard");
            Require(card != null && card.Visibility == Visibility.Visible && card.ActualWidth > 0,
                "The system diagnosis card must be rendered in Optimization.");
            var analyze = Descendants(card).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "AIHub_DiagnoseSystemButton");
            var copy = Descendants(card).OfType<Button>().Single(button => Equals(button.Content, vm.CopyReportLabel));
            var output = Descendants(card).OfType<ScrollViewer>().Single();
            var body = (TextBlock)output.Content;
            Require(Equals(analyze.Content, chinese ? "分析系统" : "Analyze system") &&
                Descendants(card).OfType<TextBlock>().Any(text => text.Text == (chinese ? "系统诊断" : "System diagnosis")),
                "Diagnosis title and action must render in the selected English or Chinese UI language.");

            Present(null);
            await NextRendering();
            Require(!vm.HasSystemDiagnosis && output.Visibility == Visibility.Collapsed && copy.Visibility == Visibility.Collapsed,
                "An empty diagnosis must not expose report or copy controls.");
            Require(vm.DiagnoseSystemCommand.CanExecute(null) && analyze.IsEnabled,
                "The enabled local module may collect evidence even while the shared AI service is disabled.");
            RequireNoFileActions();

            Present(success);
            await NextRendering();
            Require(vm.HasSystemDiagnosis && output.Visibility == Visibility.Visible && copy.Visibility == Visibility.Visible && copy.IsEnabled && body.IsTextSelectionEnabled,
                "A completed diagnosis must display selectable report text and a copy entry.");
            Require(body.Text.Contains(report.Summary, StringComparison.Ordinal) && body.Text.Contains(report.Findings[0].Recommendation, StringComparison.Ordinal) &&
                body.Text.Contains(report.Findings[0].Tradeoff, StringComparison.Ordinal) && body.Text.Contains(report.Findings[0].Verification, StringComparison.Ordinal) &&
                body.Text.Contains("Synthetic process / 测试进程", StringComparison.Ordinal) && body.Text.Contains("unavailable", StringComparison.Ordinal) &&
                body.Text.Contains(chinese ? "只读建议" : "Advisory only", StringComparison.Ordinal),
                "The rendered diagnosis must include actions to consider, tradeoffs, verification, local evidence names and unavailable coverage without execution authority.");
            RequireNoFileActions();
            await Capture(page, "aihub-system-diagnosis-success.png");

            Present(failure);
            await NextRendering();
            Require(body.Text.Contains("Timeout", StringComparison.Ordinal) && body.Text.Contains("Synthetic CPU sample", StringComparison.Ordinal) &&
                !body.Text.Contains(report.Summary, StringComparison.Ordinal) && output.Visibility == Visibility.Visible && copy.Visibility == Visibility.Visible,
                "A failed first diagnosis must retain copyable evidence and show failure without fabricating a successful AI report.");
            RequireNoFileActions();

            general.SettingsConfig.Enabled.AiHub = false;
            vm.RefreshEnabledState();
            await NextRendering();
            Require(!vm.CanDiagnoseSystem && !vm.DiagnoseSystemCommand.CanExecute(null) && !analyze.IsEnabled,
                "Disabling the module must disable the diagnosis command and its rendered button.");
            RequireNoFileActions();
            EnsureNoWorker();
        }
        finally
        {
            general.SettingsConfig.Enabled.AiHub = previousEnabled;
            vm.RefreshEnabledState();
            resultField.SetValue(vm, previousResult);
            Invoke(vm, "OnPropertyChanged", nameof(vm.HasSystemDiagnosis));
            Invoke(vm, "OnPropertyChanged", nameof(vm.SystemDiagnosisText));
            vm.ActiveTabIndex = previousTab;
            await NextRendering();
        }
        Log("System diagnosis empty/success/failure rendering, localized advisory report, copy readiness, disabled-module gate and no executable actions PASS; no scan or AI call invoked");
    }

    private static async Task CheckWeeklyReport(AIHubPage page)
    {
        var vm = page.ViewModel;
        var storeField = typeof(AIHubPageViewModel).GetField("_auditHistoryStorage", PrivateInstance);
        var original = storeField.GetValue(vm);
        var directory = Directory.CreateTempSubdirectory("KitWeeklyReport-").FullName;
        var store = new AuditHistoryStorage(directory);
        var end = (vm.SelectedAuditDate?.Date ?? DateTime.Today).AddHours(12);
        bool chinese = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
        var history = (DropDownButton)page.FindName("AuditHistoryDropDown");
        var flyout = (Flyout)history.Flyout;
        async Task Inspect(bool monthly, string expected, bool canCopy, string coverage = null)
        {
            string title = monthly ? vm.MonthlyReportLabel : chinese ? "审计周报" : "Weekly audit report";
            string automationId = monthly ? "AIHub_MonthlyReportButton" : "AIHub_WeeklyReportButton";
            flyout.ShowAt(history);
            await NextRendering();
            var button = Descendants((FrameworkElement)flyout.Content).OfType<Button>().Single(item => AutomationProperties.GetAutomationId(item) == automationId);
            Require(Equals(button.Content, title), "The period report action must use the selected language.");
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            await WaitUntil(() => Field<ContentDialog>(page, "_activeDialog")?.IsLoaded == true);
            var dialog = Field<ContentDialog>(page, "_activeDialog");
            var reportContent = (StackPanel)((ScrollViewer)dialog.Content).Content;
            var body = (TextBlock)reportContent.Children[0];
            await WaitUntil(() => body.Text.Contains(expected, StringComparison.Ordinal));
            Require(Equals(dialog.Title, title) && dialog.PrimaryButtonText == vm.CopyReportLabel && dialog.IsPrimaryButtonEnabled == canCopy && body.IsTextSelectionEnabled,
                "Copy readiness and the localized dialog must follow the loaded report.");
            if (coverage != null)
            {
                Require(body.Text.Contains(coverage, StringComparison.Ordinal), "The report must state its actual calendar-day coverage.");
            }
            if (monthly && canCopy)
            {
                Require(body.Text.Contains(end.ToString("yyyy-MM-01"), StringComparison.Ordinal), "The monthly report must start on the first day of the selected month.");
            }
            dialog.Hide();
            await WaitUntil(() => Field<ContentDialog>(page, "_activeDialog") == null);
        }

        try
        {
            storeField.SetValue(vm, store);
            await Inspect(false, chinese ? "此时段没有保存的审计记录" : "No saved audits in this period", false);
            await Inspect(true, chinese ? "此时段没有保存的审计记录" : "No saved audits in this period", false);
            await store.SaveHistoryAsync(new AuditResult { Timestamp = end.AddMonths(-1).ToUniversalTime(), HealthScore = 60 }, retentionDays: 3650);
            await store.SaveHistoryAsync(new AuditResult { Timestamp = end.AddDays(-7).ToUniversalTime(), HealthScore = 55 }, retentionDays: 3650);
            await store.SaveHistoryAsync(new AuditResult { Timestamp = end.ToUniversalTime(), HealthScore = 80 }, retentionDays: 3650);
            await Inspect(false, chinese ? "未比较分数趋势" : "Score trend withheld", true, "1/7");
            int monthDaysObserved = end.AddDays(-7).Month == end.Month ? 2 : 1;
            await Inspect(true, chinese ? "平均健康分" : "Average health score", true, $"{monthDaysObserved}/{end.Day}");
            string monthlyExport = Path.Combine(store.ReportsDirectory, $"monthly-{end:yyyy-MM-dd}.md");
            string savedExport = File.ReadAllText(monthlyExport);
            File.WriteAllText(Path.Combine(store.ReportsDirectory, "daily-summary.json"), "invalid");
            await Inspect(false, chinese ? "历史摘要存档无法读取" : "The daily archive could not be read", true, "1/7");
            await Inspect(true, chinese ? "历史摘要存档无法读取" : "The daily archive could not be read", true, $"{monthDaysObserved}/{end.Day}");
            Require(File.ReadAllText(monthlyExport) == savedExport, "An unavailable derived archive must preserve the previously saved monthly export.");
            File.WriteAllText(Path.Combine(directory, "audit_history.json"), "invalid");
            await Inspect(false, chinese ? "无法读取审计历史" : "Audit history could not be read", false);
            await Inspect(true, chinese ? "无法读取审计历史" : "Audit history could not be read", false);
        }
        finally
        {
            Field<ContentDialog>(page, "_activeDialog")?.Hide();
            storeField.SetValue(vm, original);
            Directory.Delete(directory, recursive: true);
        }
        Log("Weekly/monthly report actions, empty history, calendar coverage, weekly trend, unavailable archive, preserved export, read error and copy-button readiness rendered PASS");
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
        var search = (AutoSuggestBox)page.FindName("InventorySearchBox");
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
        bool chinese = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
        var cache = report.Scopes.Single(scope => scope.Id == "cache");
        foreach (var sample in new[] { (0L, chinese ? "与上次扫描的占用相同" : "Same size"), (2048L, chinese ? "增加" : "Up "), (0L, chinese ? "减少" : "Down ") })
        {
            updated = updated with
            {
                CompletedUtc = updated.CompletedUtc.AddMinutes(1),
                Scopes = report.Scopes.Select(scope => scope.Id == cache.Id ? cache with { ObservedBytes = cache.ObservedBytes + sample.Item1 } : scope).ToArray(),
            };
            Invoke(vm, "ApplyInventoryReport", updated);
            var row = vm.Inventory.Rows.Single(item => item.Id == "cache");
            row.IsExpanded = true;
            ((ListView)page.FindName("InventoryScopeList")).ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
            await NextRendering();
            page.UpdateLayout();
            Require(row.ChangeText.Contains(sample.Item2, StringComparison.Ordinal) && Descendants(page).OfType<TextBlock>().Any(block => block.Text == row.ChangeText && block.ActualHeight > 0),
                "An expanded comparable location must render its localized size change.");
            row.IsExpanded = false;
        }
        Require(!vm.Inventory.Rows.Single(item => item.Id == "diagnostics").HasComparison, "Partial measurements must have no size-change label.");
        Log("Expanded storage row rendered unchanged, growth and shrinkage labels PASS");
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
        var phase = typeof(AIHubPageViewModel).GetProperty(nameof(vm.AuditPhase));
        phase.SetValue(vm, ScanPhase.Scanning);
        phase.SetValue(vm, ScanPhase.SelectingTargets);
        Require(!string.IsNullOrEmpty(vm.AuditElapsedText), "The historical reset fixture must begin with a previous scan clock.");
        var history = new AuditResult { HealthScore = 75, Timestamp = DateTime.UtcNow.AddDays(-2), Findings = new() { new AuditIssueEnhanced { Title = "Synthetic historical finding", Severity = "Medium", LogName = "System" } } };
        Invoke(vm, "ApplyHistoricalAudit", history);
        Require(vm.AllIssues.Count == 1 && !vm.HasAiReport && vm.AiReportFindings.Count == 0 && Field<List<SecurityEvent>>(vm, "_lastAuditEvents").Count == 0, "Historical snapshot must clear unrelated AI report and event evidence.");
        Require(string.IsNullOrEmpty(vm.AuditElapsedText), "Historical snapshots must not display elapsed time from the previous scan.");
        await (Task)Invoke(vm, "LoadAuditForDateAsync", new DateTime(1901, 1, 1));
        await NextRendering();
        Require(vm.AllIssues.Count == 0 && !vm.HasAuditData && vm.HealthScoreText == "—", "A date without a snapshot must clear old findings and score.");
        Log("Historical report/evidence isolation and empty-date reset PASS");
    }

    private static async Task CheckWorkflowTimers(AIHubPage page)
    {
        var vm = page.ViewModel;
        var auditPhase = typeof(AIHubPageViewModel).GetProperty(nameof(vm.AuditPhase));
        var auditing = typeof(AIHubPageViewModel).GetProperty(nameof(vm.IsAuditing));
        var optimizing = typeof(AIHubPageViewModel).GetProperty(nameof(vm.IsOptimizing));
        var scanPhase = typeof(AIHubPageViewModel).GetProperty(nameof(vm.ScanPhase));
        var status = typeof(AIHubPageViewModel).GetProperty(nameof(vm.AuditStatusText));
        var elapsed = (TextBlock)page.FindName("AuditElapsedText");
        var pinned = (FrameworkElement)page.FindName("AuditPinnedStatus");
        var pinnedElapsed = (TextBlock)page.FindName("AuditPinnedElapsed");
        var optimizationElapsed = (TextBlock)page.FindName("OptimizationElapsedText");
        var optimizationStatus = typeof(AIHubPageViewModel).GetProperty(nameof(vm.OptimizationWorkflowStatus));
        int originalTab = vm.ActiveTabIndex;
        string originalStatus = vm.AuditStatusText;
        string originalOptimizationStatus = vm.OptimizationWorkflowStatus;
        try
        {
            vm.ActiveTabIndex = 0;
            auditing.SetValue(vm, true);
            auditPhase.SetValue(vm, ScanPhase.Scanning);
            status.SetValue(vm, "Collecting fixture logs");
            await NextRendering();
            Require(elapsed.Visibility == Visibility.Visible && (elapsed.Text == "< 1 s" || elapsed.Text == "不足 1 秒"),
                "A scan must show its timer immediately, before any collection or AI progress.");
            string first = elapsed.Text;
            await WaitUntil(() => elapsed.Text != first);
            Require(vm.AuditStatusText == "Collecting fixture logs" && vm.SidebarWorkflowCountText.Contains(elapsed.Text),
                "A silent local scan must refresh page/sidebar elapsed time without changing its stage.");
            Require(pinned.Visibility == Visibility.Visible && pinnedElapsed.Text == elapsed.Text &&
                !IsDescendantOf(pinned, (ScrollViewer)page.FindName("AiHubContentScroll")),
                "The running task must keep its existing clock visible outside the scrolling results.");
            Require(((FrameworkElement)page.FindName("AuditWorkflowCard")).Visibility == Visibility.Collapsed,
                "The running task must not repeat the fixed progress strip in a second workflow card.");
            status.SetValue(vm, "Collecting fixture logs / 正在收集合成日志。 " + new string('x', 400));
            await NextRendering();
            var taskDetails = (ScrollViewer)page.FindName("AuditTaskDetails");
            var taskStatus = (TextBlock)page.FindName("AuditPinnedTaskStatus");
            var taskAiResult = (TextBlock)page.FindName("AuditPinnedAiResult");
            Require(taskDetails.Visibility == Visibility.Visible && taskDetails.ActualHeight > 0 &&
                IsDescendantOf(taskDetails, pinned) && IsDescendantOf(taskStatus, taskDetails) && taskStatus.Text == vm.AuditProgressActivityText &&
                taskStatus.Text == vm.AuditStatusText && !vm.HasAuditBatchProgress,
                "The complete current task status must be visible inline inside the fixed running card.");
            Require(taskStatus.TextWrapping == TextWrapping.Wrap && taskStatus.IsTextSelectionEnabled &&
                taskAiResult.TextWrapping == TextWrapping.Wrap && taskAiResult.IsTextSelectionEnabled &&
                taskDetails.ActualHeight <= 113 && taskDetails.ScrollableWidth <= 1 &&
                HeaderInside(HeaderBounds(taskDetails, pinned), pinned),
                "Inline audit details must remain selectable and wrap inside the bounded running card without width overflow.");

            optimizing.SetValue(vm, true);
            scanPhase.SetValue(vm, ScanPhase.Scanning);
            Require(vm.OptimizationElapsedText.EndsWith("0:00") && !vm.AuditElapsedText.EndsWith("0:00"),
                "Optimization and audit scans must keep independent clocks.");
            optimizationStatus.SetValue(vm, "Reviewing fixture files / 正在检查合成文件。 " + new string('x', 400));
            vm.ActiveTabIndex = 1;
            await NextRendering();
            var optimizationDetails = (ScrollViewer)page.FindName("OptimizationTaskDetails");
            var optimizationTaskStatus = (TextBlock)page.FindName("OptimizationWorkflowStatus");
            var optimizationCard = (FrameworkElement)page.FindName("OptimizationWorkflowCard");
            Require(optimizationDetails.Visibility == Visibility.Visible && optimizationDetails.ActualHeight > 0 &&
                IsDescendantOf(optimizationDetails, optimizationCard) && IsDescendantOf(optimizationTaskStatus, optimizationDetails) &&
                optimizationTaskStatus.Text == vm.OptimizationWorkflowStatus && optimizationTaskStatus.TextWrapping == TextWrapping.Wrap &&
                optimizationTaskStatus.IsTextSelectionEnabled && optimizationDetails.ActualHeight <= 113 &&
                optimizationDetails.ScrollableWidth <= 1 && HeaderInside(HeaderBounds(optimizationDetails, optimizationCard), optimizationCard),
                "Optimization task details must be visible inline, selectable, wrapping and bounded without width overflow.");
            vm.ActiveTabIndex = 0;
            await NextRendering();
            Invoke(vm, "ApplyAuditAiProgress", new Kit.AiHub.Contract.AiTaskProgress("Execute", "private-provider-text", 0, 2)
            {
                Activity = "waiting", ActiveBatches = 2, BatchIndex = 2, Route = "Main", QueueElapsedMilliseconds = 42,
            });
            string waiting = vm.AuditStatusText;
            await WaitUntil(() => vm.AuditStatusText != waiting && !optimizationElapsed.Text.EndsWith("0:00"));
            Require(!vm.AuditStatusText.Contains("private-provider-text") && vm.SidebarWorkflowPercent == 30,
                "Silent AI waiting advances elapsed time without fabricating work or exposing provider text.");
            await NextRendering();
            await CheckAuditProgressLines(page, "0/2", "workflow-timers");
            await Capture(page, "aihub-running-timer.png");

            auditPhase.SetValue(vm, ScanPhase.SelectingTargets);
            auditing.SetValue(vm, false);
            status.SetValue(vm, "Completed fixture scan");
            optimizing.SetValue(vm, false);
            scanPhase.SetValue(vm, ScanPhase.SelectingTargets);
            string completed = elapsed.Text;
            string optimized = optimizationElapsed.Text;
            await Task.Delay(1100);
            Require(elapsed.Text == completed && optimizationElapsed.Text == optimized && vm.AuditStatusText == "Completed fixture scan",
                "Completed workflows must freeze their timers and retain their final status.");
            Require(pinned.Visibility == Visibility.Collapsed, "Finished tasks must release the fixed reading space.");

            auditPhase.SetValue(vm, ScanPhase.Scanning);
            Require(vm.AuditElapsedText is "< 1 s" or "不足 1 秒", "A new audit must reset elapsed time.");
            auditPhase.SetValue(vm, ScanPhase.Failed);
            status.SetValue(vm, "Cancelled fixture scan");
            await Task.Delay(1100);
            Require((vm.AuditElapsedText is "< 1 s" or "不足 1 秒") && vm.AuditStatusText == "Cancelled fixture scan",
                "A stopped scan must not be refreshed by a late timer tick.");
            Log("Scan timers: immediate display, silent local/AI progress, independent clocks, completion, cancellation and restart PASS");
        }
        finally
        {
            auditing.SetValue(vm, false);
            optimizing.SetValue(vm, false);
            scanPhase.SetValue(vm, ScanPhase.Idle);
            auditPhase.SetValue(vm, ScanPhase.Idle);
            status.SetValue(vm, originalStatus);
            optimizationStatus.SetValue(vm, originalOptimizationStatus);
            vm.ActiveTabIndex = originalTab;
        }
    }

    private static async Task CheckAuditAnalysisLifecycle(AIHubPage page)
    {
        var vm = page.ViewModel;
        var auditPhase = typeof(AIHubPageViewModel).GetProperty(nameof(vm.AuditPhase));
        var analyzing = typeof(AIHubPageViewModel).GetProperty(nameof(vm.IsAiAnalyzing));
        var persistence = typeof(AIHubPageViewModel).GetField("_persistCurrentAudit", PrivateInstance);
        var deepAnalysis = typeof(AIHubPageViewModel).GetField("_auditWasDeepAnalysis", PrivateInstance);
        var percent = typeof(AIHubPageViewModel).GetField("_auditDetailedPercent", PrivateInstance);
        var pinned = (FrameworkElement)page.FindName("AuditPinnedStatus");
        int originalTab = vm.ActiveTabIndex;
        string originalStatus = vm.AuditStatusText;
        string originalBanner = Field<string>(vm, "_statusMessage");
        var originalSeverity = Field<InfoBarSeverity>(vm, "_statusSeverity");
        bool originalOpen = Field<bool>(vm, "_isStatusOpen");
        bool originalPersistence = (bool)persistence.GetValue(vm);
        object originalPercent = percent.GetValue(vm);
        var originalSnapshot = Field<AuditResult>(vm, "_auditOverviewSnapshot");
        var ruleIssues = new List<AuditIssueEnhanced>
        {
            new() { Title = "Local fixture finding", EventId = "1", LogName = "System", Severity = "Medium" },
            new() { Title = "Local finding enhanced by AI", EventId = "2", LogName = "System", Severity = "Low" },
        };
        var aiIssues = new[]
        {
            new AuditIssue { Title = "Validated AI fixture finding", EventId = "2", LogName = "System", Severity = "Low", Description = "Synthetic validated evidence", EventRef = "item-000002", RelatedEventRefs = ["item-000002"] },
        };
        SecurityEvent[] reviewedEvents =
        [
            new(1, "System", "Synthetic", 2, DateTime.UtcNow, "First fixture event"),
            new(2, "System", "Synthetic", 2, DateTime.UtcNow.AddMinutes(-1), "Second fixture event"),
        ];
        try
        {
            vm.ActiveTabIndex = 0;
            persistence.SetValue(vm, false);
            Invoke(vm, "FinishAuditStatus", 100, 0, "ok", Array.Empty<string>());
            string completedBanner = vm.StatusMessage;
            foreach (string outcome in new[] { "ok", "partial", "failed", "cancelled" })
            {
                Invoke(vm, "ClearAuditSnapshot");
                foreach (var issue in ruleIssues)
                {
                    vm.AllIssues.Add(issue);
                }
                typeof(AIHubPageViewModel).GetProperty(nameof(vm.HealthScore)).SetValue(vm, Kit.AIHubLib.Services.HealthScoreCalculator.Calculate(ruleIssues));
                deepAnalysis.SetValue(vm, true);
                percent.SetValue(vm, null);
                analyzing.SetValue(vm, true);
                auditPhase.SetValue(vm, ScanPhase.Scanning);
                Invoke(vm, "FinishAuditStatus", vm.HealthScore, ruleIssues.Count, "pending", Array.Empty<string>());
                await NextRendering();
                Require(vm.IsStatusOpen && vm.StatusSeverity == InfoBarSeverity.Informational && vm.StatusMessage != completedBanner &&
                    vm.AuditPhase == ScanPhase.Scanning && vm.IsAiAnalyzing && !vm.IsAiSummaryCompleted && pinned.Visibility == Visibility.Visible &&
                    Descendants(page).OfType<InfoBar>().Any(bar => bar.IsOpen && bar.Message == vm.StatusMessage && bar.Severity == InfoBarSeverity.Informational),
                    outcome + ": locally completed findings must show a running informational banner until AI analysis ends.");
                Require(string.IsNullOrEmpty(vm.AiAnalysisRanText) && ((TextBlock)page.FindName("AuditPinnedAiResult")).Visibility == Visibility.Collapsed,
                    outcome + ": pending AI work must not repeat the local-ready banner in an empty AI-result row.");
                Invoke(vm, "ApplyAuditAiProgress", new Kit.AiHub.Contract.AiTaskProgress("Validate", "private-provider-text", 1, 2)
                {
                    Activity = "validating", ActiveBatches = 1, BatchIndex = 1, Route = "Main", QueueElapsedMilliseconds = 42,
                });
                string firstElapsed = vm.AuditElapsedText;
                await WaitUntil(() => vm.AuditElapsedText != firstElapsed);
                Require(vm.AuditPhase == ScanPhase.Scanning && vm.IsAiAnalyzing && !vm.IsAiSummaryCompleted &&
                    vm.StatusSeverity != InfoBarSeverity.Success && vm.AuditStatusText == vm.AiStatusText,
                    outcome + ": active AI progress must advance elapsed time without changing the banner to completed.");

                var retained = new AiHubAuditAnalysisService.AuditAnalysisResult(aiIssues, true, "fixture partial");
                Invoke(vm, "ApplyAiAnalysis", retained, ruleIssues, reviewedEvents, null, true);
                await NextRendering();
                var inlineResult = (TextBlock)page.FindName("AuditPinnedAiResult");
                Require(vm.HasAiReport && !vm.IsAiSummaryCompleted && vm.AuditPhase == ScanPhase.Scanning && vm.StatusSeverity != InfoBarSeverity.Success,
                    outcome + ": an interim AI report must preserve the active scan and its pending banner.");
                Require(inlineResult.Visibility == Visibility.Visible && inlineResult.Text == vm.AiAnalysisRanText && IsDescendantOf(inlineResult, pinned),
                    outcome + ": retained interim AI results must appear inline with the running task details.");
                await CheckAuditProgressLines(page, "1/2", "interim-" + outcome);
                if (outcome == "ok")
                {
                    await CheckAuditRunningLayout(page);
                }
                if (outcome == "cancelled")
                {
                    Invoke(vm, "CompleteAiInterruption", null, true);
                }
                else
                {
                    var result = new AiHubAuditAnalysisService.AuditAnalysisResult(
                        outcome == "failed" ? null : aiIssues, outcome == "partial", outcome == "ok" ? null : "fixture incomplete")
                    {
                        UsedModel = "test-model", UsedRoute = "Main", MatchesRequestedModel = true,
                    };
                    Invoke(vm, "ApplyAiAnalysis", result, ruleIssues, reviewedEvents, null, false);
                    analyzing.SetValue(vm, false);
                }
                await NextRendering();
                var expectedSeverity = outcome == "ok" ? InfoBarSeverity.Success : outcome == "cancelled" ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
                var expectedPhase = outcome == "failed" ? ScanPhase.Failed : ScanPhase.SelectingTargets;
                Require(vm.IsStatusOpen && vm.StatusSeverity == expectedSeverity && vm.AuditPhase == expectedPhase && !vm.IsAiAnalyzing &&
                    pinned.Visibility == Visibility.Collapsed && vm.IsAiSummaryCompleted == (outcome == "ok") &&
                    vm.IsAiSummaryCaution == (outcome != "ok") && !string.IsNullOrWhiteSpace(vm.FinishedText) &&
                    vm.StatusMessage == vm.AuditStatusText &&
                    Descendants(page).OfType<InfoBar>().Any(bar => bar.IsOpen && bar.Message == vm.StatusMessage && bar.Severity == expectedSeverity),
                    outcome + ": the final banner, report state, scan phase and fixed card must agree on the AI outcome.");
                if (outcome == "ok")
                {
                    var resultDetail = (TextBlock)page.FindName("AuditWorkflowStatus");
                    var resultSupplement = (TextBlock)page.FindName("AuditWorkflowAiResult");
                    Require(resultDetail.Text == vm.AuditResultDetailText && resultDetail.Text != vm.StatusMessage &&
                        System.Text.RegularExpressions.Regex.Matches(resultDetail.Text, "[0-9]+").Cast<System.Text.RegularExpressions.Match>()
                            .Select(match => match.Value).SequenceEqual(new[] { "2", "1", "2" }) && vm.AllIssues.Count == 2 &&
                        vm.AiReportFindings.Count == 1 && string.IsNullOrEmpty(vm.AuditResultSupplementText) && resultSupplement.Visibility == Visibility.Collapsed,
                        "A successful result card must show local, AI and merged counts, without repeating the completed banner or AI summary.");
                    var completedProgress = (ProgressBar)page.FindName("AuditWorkflowProgress");
                    Require(completedProgress.Value == 100 && !completedProgress.IsIndeterminate,
                        "A successful result card must have determinate 100 percent progress.");
                }
                string finishedElapsed = vm.AuditElapsedText;
                string finishedStatus = vm.AuditStatusText;
                string finishedBanner = vm.StatusMessage;
                await Task.Delay(1100);
                Require(vm.AuditElapsedText == finishedElapsed && vm.AuditStatusText == finishedStatus && vm.StatusMessage == finishedBanner,
                    outcome + ": final elapsed time and status must remain frozen after late timer ticks.");
                if (outcome == "ok")
                {
                    // Capture after the built-in ProgressBar transition has settled.
                    await Capture(page, "aihub-audit-completed-merged-counts.png");
                }
            }
        }
        finally
        {
            analyzing.SetValue(vm, false);
            Invoke(vm, "ClearAuditSnapshot");
            if (originalSnapshot != null)
            {
                Invoke(vm, "ApplyHistoricalAudit", originalSnapshot);
            }
            persistence.SetValue(vm, originalPersistence);
            percent.SetValue(vm, originalPercent);
            typeof(AIHubPageViewModel).GetProperty(nameof(vm.AuditStatusText)).SetValue(vm, originalStatus);
            Invoke(vm, "ShowStatus", originalBanner, originalSeverity, false);
            vm.IsStatusOpen = originalOpen;
            vm.ActiveTabIndex = originalTab;
        }
        EnsureNoWorker();
        Log("Actual audit VM pending, interim, success, partial, failure and cancellation banners with frozen terminal timers PASS");
    }

    private static async Task CheckAuditProgressLines(AIHubPage page, string expectedCounts, string context)
    {
        var vm = page.ViewModel;
        var pinned = (FrameworkElement)page.FindName("AuditPinnedStatus");
        var details = (ScrollViewer)page.FindName("AuditTaskDetails");
        var summary = (TextBlock)page.FindName("AuditPinnedBatchSummary");
        var activity = (TextBlock)page.FindName("AuditPinnedTaskStatus");
        var queue = (TextBlock)page.FindName("AuditPinnedQueueStatus");
        var elapsed = (TextBlock)page.FindName("AuditPinnedElapsed");
        // Compare the rendered time with the value announced by an actual timer
        // notification. Reading the live Stopwatch after rendering can cross the
        // next second before its dispatcher tick and repeatedly miss that window.
        var bindingWait = Stopwatch.StartNew();
        string expectedElapsed = null;
        void OnElapsedChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(vm.AuditElapsedText)) expectedElapsed = vm.AuditElapsedText;
        }
        vm.PropertyChanged += OnElapsedChanged;
        try
        {
            while (true)
            {
                await NextRendering();
                if (expectedElapsed is not null && elapsed.Text == expectedElapsed && summary.Text == vm.AuditProgressSummaryText &&
                    activity.Text == vm.AuditProgressActivityText && queue.Text == vm.AuditProgressQueueText)
                {
                    page.UpdateLayout();
                    break;
                }
                if (bindingWait.Elapsed > TimeSpan.FromSeconds(3))
                {
                    throw new TimeoutException($"{context}: audit progress bindings did not catch up; " +
                        $"elapsed='{elapsed.Text}'/'{expectedElapsed}', " +
                        $"summary='{summary.Text}'/'{vm.AuditProgressSummaryText}', " +
                        $"activity='{activity.Text}'/'{vm.AuditProgressActivityText}', " +
                        $"queue='{queue.Text}'/'{vm.AuditProgressQueueText}'.");
                }
                await Task.Delay(25);
            }
        }
        finally { vm.PropertyChanged -= OnElapsedChanged; }
        Require(vm.HasAuditBatchProgress && summary.Visibility == Visibility.Visible && summary.Text == vm.AuditProgressSummaryText &&
            summary.Text.Contains(expectedCounts, StringComparison.Ordinal) && activity.Text == vm.AuditProgressActivityText &&
            (activity.Text.StartsWith("Latest update:", StringComparison.Ordinal) || activity.Text.StartsWith("最新进展：", StringComparison.Ordinal)) &&
            (activity.Text.Contains("Main service", StringComparison.Ordinal) || activity.Text.Contains("主服务", StringComparison.Ordinal)) &&
            HeaderBounds(summary, details).Bottom <= HeaderBounds(activity, details).Y + 1,
            "Batch totals and the latest per-batch main-service activity must render on distinct lines.");
        Require(string.IsNullOrEmpty(vm.AuditProgressQueueText) && queue.Visibility == Visibility.Collapsed,
            "A sub-second queue delay must not occupy a task-detail row.");
        Require(elapsed.Visibility == Visibility.Visible && elapsed.Text == expectedElapsed &&
            Descendants(pinned).OfType<TextBlock>().Count(text => text.Visibility == Visibility.Visible && text.Text == expectedElapsed) == 1 &&
            !Descendants(details).OfType<TextBlock>().Any(text => text.Visibility == Visibility.Visible &&
                (text.Text.Contains("Elapsed", StringComparison.OrdinalIgnoreCase) || text.Text.Contains("已用", StringComparison.Ordinal))),
            "The running task must show total elapsed time only in its header, without repeating it in progress details. " +
            $"Context={context}; rendered='{elapsed.Text}' ({elapsed.Visibility}); VM snapshot='{expectedElapsed}'; " +
            "pinned=[" + string.Join(" | ", Descendants(pinned).OfType<TextBlock>()
                .Where(text => text.Visibility == Visibility.Visible).Select(text => $"{text.Name}='{text.Text}'")) + "]; " +
            "details=[" + string.Join(" | ", Descendants(details).OfType<TextBlock>()
                .Where(text => text.Visibility == Visibility.Visible).Select(text => $"{text.Name}='{text.Text}'")) + "]");
    }

    private static async Task CheckAuditRunningLayout(AIHubPage page)
    {
        var vm = page.ViewModel;
        var window = App.GetSettingsWindow();
        var root = (FrameworkElement)window.Content;
        var originalTheme = root.RequestedTheme;
        var originalSize = window.AppWindow.Size;
        var pinned = (FrameworkElement)page.FindName("AuditPinnedStatus");
        var details = (ScrollViewer)page.FindName("AuditTaskDetails");
        var body = (ScrollViewer)page.FindName("AiHubContentScroll");
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (var size in new[] { new Windows.Graphics.SizeInt32(760, 1000), new Windows.Graphics.SizeInt32(760, 480), new Windows.Graphics.SizeInt32(480, 480) })
            {
                root.RequestedTheme = theme;
                window.AppWindow.Resize(size);
                await NextRendering();
                await NextRendering();
                body.ChangeView(null, 0, null, true);
                details.ChangeView(null, 0, null, true);
                await NextRendering();
                page.UpdateLayout();
                string context = $"{theme}-{size.Width}x{size.Height}";
                var pinnedBounds = HeaderBounds(pinned, page);
                var bodyBounds = HeaderBounds(body, page);
                Log($"AUDIT RUNNING {context}: window={window.AppWindow.Size.Width}x{window.AppWindow.Size.Height}, page={page.ActualWidth:F1}x{page.ActualHeight:F1}; pinned={pinnedBounds}; details={details.ViewportWidth:F1}x{details.ViewportHeight:F1}; body={bodyBounds}, viewport={body.ViewportWidth:F1}x{body.ViewportHeight:F1}");
                await Capture(page, $"aihub-audit-pending-with-results-{context}.png");
                Require(vm.IsAiAnalyzing && vm.HasAiReport && vm.AllIssues.Count == 2 && vm.AiReportFindings.Count == 1 &&
                    pinned.Visibility == Visibility.Visible && details.ViewportHeight > 0 && details.ActualHeight <= 113 &&
                    HeaderInside(pinnedBounds, page) && HeaderInside(HeaderBounds(details, pinned), pinned),
                    context + ": pending AI results must keep their running task card and bounded details inside the visible page.");
                Require(body.ViewportHeight >= 80 && bodyBounds.Y >= pinnedBounds.Bottom - 2 && HeaderInside(bodyBounds, page) &&
                    body.ExtentWidth <= body.ViewportWidth + 2 && details.ScrollableWidth <= 1,
                    context + ": the running task must leave at least 80 DIPs of usable result reading area without horizontal overflow.");
                await CheckAuditProgressLines(page, "1/2", context);
            }
        }
        finally
        {
            root.RequestedTheme = originalTheme;
            window.AppWindow.Resize(originalSize);
            await NextRendering();
            await NextRendering();
        }
        Log("Pending AI results preserve separate progress lines and usable body space in narrow and short windows PASS");
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
                var diagnostic = aiResult.Visibility == Visibility.Visible ? aiResult : status;
                Log($"AUDIT FAILURE {context}: card={card.ActualWidth:F1}x{card.ActualHeight:F1}; details={detailScroll.ActualWidth:F1}x{detailScroll.ActualHeight:F1}, viewport={detailScroll.ViewportWidth:F1}x{detailScroll.ViewportHeight:F1}, extent={detailScroll.ExtentWidth:F1}x{detailScroll.ExtentHeight:F1}; status={status.ActualWidth:F1}x{status.ActualHeight:F1}, slot={statusSlot}@{status.TransformToVisual(card).TransformPoint(new(0, 0))}; aiResult={aiResult.ActualWidth:F1}x{aiResult.ActualHeight:F1}, slot={aiResultSlot}@{aiResult.TransformToVisual(card).TransformPoint(new(0, 0))}; outerViewport={scroll.ViewportWidth:F1}x{scroll.ViewportHeight:F1}");
                await Capture(page, $"aihub-audit-failure-{theme}-{width}-{phase}.png");
                Require(card.Visibility == Visibility.Visible && card.ActualHeight > 80 && card.ActualHeight <= 300,
                    context + ": a long native failure must produce a bounded workflow card, not push results hundreds of pixels down.");
                // TextBlock.ActualWidth can describe only the rendered text;
                // its allocated layout slot is the available wrapping width.
                Require(detailScroll.ActualWidth >= card.ActualWidth - 50 && statusSlot.Width >= detailScroll.ViewportWidth - 4 &&
                    statusSlot.Left >= -1 && statusSlot.Right <= detailScroll.ViewportWidth + 1 &&
                    (aiResult.Visibility == Visibility.Collapsed || (aiResultSlot.Width >= detailScroll.ViewportWidth - 4 &&
                        aiResultSlot.Left >= -1 && aiResultSlot.Right <= detailScroll.ViewportWidth + 1)),
                    context + ": every visible workflow message must retain the full readable column width.");
                Require(scroll.ExtentWidth <= scroll.ViewportWidth + 2 && detailScroll.ScrollableWidth <= 1 && detailScroll.ActualHeight <= 145,
                    context + ": failure details must scroll vertically within their finite viewport without horizontal overflow.");
                double cardTop = card.TransformToVisual(scroll).TransformPoint(new(0, 0)).Y;
                double cardBottom = card.TransformToVisual(scroll).TransformPoint(new(0, card.ActualHeight)).Y;
                Require(cardTop >= -2 && cardBottom <= scroll.ViewportHeight + 2,
                    context + ": the bounded workflow card must remain entirely visible in the initial viewport.");
                Require(vm.SidebarWorkflowPercent == 30 && !vm.IsAuditProgressIndeterminate && vm.AllIssues.Count == 1 && vm.HasAuditData,
                    context + ": layout must preserve the partial progress and retained local findings.");
                Require(status.Text == vm.AuditResultDetailText && aiResult.Text == vm.AuditResultSupplementText &&
                    aiResult.Visibility == (string.IsNullOrEmpty(vm.AuditResultSupplementText) ? Visibility.Collapsed : Visibility.Visible) &&
                    diagnostic.Text == failure && diagnostic.IsTextSelectionEnabled && diagnostic.TextWrapping == TextWrapping.Wrap &&
                    new[] { status, aiResult }.Count(text => text.Visibility == Visibility.Visible && text.Text == failure) == 1,
                    context + ": complete diagnostics must remain wrapping selectable text and appear only once.");
                Require(detailScroll.ScrollableHeight > 0, context + ": long diagnostics must provide a bounded reading region.");
                // Keyboard focus can queue BringIntoView on both nested viewers.
                // Finish that movement before requesting the diagnostic tail.
                await WaitForScrollSettled(new[] { detailScroll, scroll }, () =>
                    Require(diagnostic.Focus(FocusState.Keyboard) && ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(page.XamlRoot), diagnostic),
                        context + ": selectable native diagnostics must accept real keyboard focus."), context + "/focus");
                double tailTarget = detailScroll.ScrollableHeight;
                bool tailAccepted = false;
                await WaitForScrollSettled(new[] { detailScroll, scroll }, () =>
                    tailAccepted = detailScroll.ChangeView(null, tailTarget, null, true), context + "/tail");
                Log($"AUDIT FAILURE TAIL {context}: accepted={tailAccepted}, requested={tailTarget:F1}, offset={detailScroll.VerticalOffset:F1}, max={detailScroll.ScrollableHeight:F1}, viewport={detailScroll.ViewportHeight:F1}, extent={detailScroll.ExtentHeight:F1}, outerOffset={scroll.VerticalOffset:F1}");
                Require(Math.Abs(detailScroll.VerticalOffset - detailScroll.ScrollableHeight) < 2 && diagnostic.Text.EndsWith("END OF SYNTHETIC FAILURE", StringComparison.Ordinal),
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

    private static async Task CheckAuditReadinessOrdering(AIHubPage page)
    {
        var vm = page.ViewModel;
        var originalReadiness = vm.AiReadinessLevel;
        string originalText = vm.AiReadinessText;
        string originalModel = vm.CurrentEngineAndModelLabel;
        string originalScope = Field<string>(vm, "_knownAuditAnalysisScope");
        var oldReadiness = new AiReadiness(AiReadinessLevel.Unverified, "codex", "old-model");
        var astraReadiness = new AiReadiness(AiReadinessLevel.Unverified, "codex", "astra");
        Task Refresh(Func<Task<AiReadiness>> read, Func<Task<string>> scope) =>
            (Task)Invoke(vm, "RefreshAiReadinessSnapshotAsync", read, scope);
        AuditResult Report(string scope)
        {
            var snapshot = FluentAuditFixture();
            snapshot.AnalysisOutcome = "ok";
            snapshot.AiAnalysisScope = scope;
            snapshot.AiReportSummary = "Complete synthetic report for " + scope;
            return snapshot;
        }
        try
        {
            Invoke(vm, "ApplyHistoricalAudit", Report("old-scope"));
            var delayedReadiness = new TaskCompletionSource<AiReadiness>(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldRead = Refresh(() => delayedReadiness.Task, () => Task.FromResult("old-scope"));
            await Refresh(() => Task.FromResult(astraReadiness), () => Task.FromResult("astra-scope"));
            delayedReadiness.SetResult(oldReadiness);
            await oldRead;
            Require(vm.AiReadinessText.Contains("astra", StringComparison.Ordinal)
                && Field<string>(vm, "_knownAuditAnalysisScope") == "astra-scope" && Field<bool>(vm, "_auditReportNeedsRefresh"),
                "A late old-model readiness read must not overwrite the newer model or its Update-report action.");

            var delayedScope = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldScope = Refresh(() => Task.FromResult(oldReadiness), () => delayedScope.Task);
            await Refresh(() => Task.FromResult(astraReadiness), () => Task.FromResult("astra-scope"));
            delayedScope.SetResult("old-scope");
            await oldScope;
            Require(Field<string>(vm, "_knownAuditAnalysisScope") == "astra-scope" && Field<bool>(vm, "_auditReportNeedsRefresh"),
                "A late scope response must not clear Update after a model change.");

            int scopeReads = 0;
            var replacedScope = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replaced = Refresh(() => Task.FromResult(astraReadiness), () => ++scopeReads == 1 ? replacedScope.Task : Task.FromResult("astra-scope"));
            var newReport = Report("astra-scope");
            Invoke(vm, "ApplyHistoricalAudit", newReport);
            replacedScope.SetResult("old-scope");
            await replaced;
            Require(scopeReads == 2 && ReferenceEquals(newReport, Field<AuditResult>(vm, "_auditOverviewSnapshot"))
                && !Field<bool>(vm, "_auditReportNeedsRefresh"),
                "A changed report must trigger a new scope read instead of applying the previous report's delayed result.");

            int busyReads = 0;
            var duringAnalysis = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var busy = Refresh(() => Task.FromResult(astraReadiness), () => ++busyReads == 1 ? duringAnalysis.Task : Task.FromResult("astra-new-policy"));
            typeof(AIHubPageViewModel).GetProperty(nameof(vm.IsAiAnalyzing)).SetValue(vm, true);
            duringAnalysis.SetResult("obsolete-during-analysis");
            await busy;
            Require(busyReads == 1 && Field<string>(vm, "_knownAuditAnalysisScope") == "astra-scope",
                "Readiness must not change report identity while its AI analysis is running.");
            typeof(AIHubPageViewModel).GetProperty(nameof(vm.IsAiAnalyzing)).SetValue(vm, false);
            await WaitUntil(() => busyReads == 2);
            Require(Field<string>(vm, "_knownAuditAnalysisScope") == "astra-new-policy" && Field<bool>(vm, "_auditReportNeedsRefresh"),
                "Finishing analysis must re-read current configuration and restore the correct Update-report action.");
        }
        finally
        {
            typeof(AIHubPageViewModel).GetProperty(nameof(vm.IsAiAnalyzing)).SetValue(vm, false);
            typeof(AIHubPageViewModel).GetProperty(nameof(vm.AiReadinessLevel)).SetValue(vm, originalReadiness);
            typeof(AIHubPageViewModel).GetProperty(nameof(vm.AiReadinessText)).SetValue(vm, originalText);
            typeof(AIHubPageViewModel).GetProperty(nameof(vm.CurrentEngineAndModelLabel)).SetValue(vm, originalModel);
            typeof(AIHubPageViewModel).GetField("_knownAuditAnalysisScope", PrivateInstance).SetValue(vm, originalScope);
            Invoke(vm, "ClearAuditSnapshot");
        }
        EnsureNoWorker();
        Log("Readiness and report identity keep newest model results, recheck replaced snapshots and defer scope publication during analysis PASS");
    }

    private static AuditResult FluentAuditFixture()
    {
        var timestamp = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        return new AuditResult
        {
            Timestamp = timestamp, ScanStart = timestamp.AddDays(-2), ScanEnd = timestamp,
            CollectionProfile = AuditCollectionProfile.ForMode(EventLogService.AuditMode.Extended),
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

    private static async Task CheckPeriodOverview(AIHubPage page)
    {
        var vm = page.ViewModel;
        vm.ActiveTabIndex = 0;
        vm.AuditResultViewIndex = 0;
        vm.ResetFindingFilters();
        var fixture = FluentAuditFixture();
        fixture.CollectionProfile = AuditCollectionProfile.ForMode(EventLogService.AuditMode.Extended);
        var end = fixture.ScanEnd.Value;
        var events = new List<SecurityEvent>
        {
            new(41, "System", "Kernel-Power", 1, end.AddDays(-1), "Recent interruption"),
            new(41, "System", "Kernel-Power", 1, end.AddDays(-5), "Earlier interruption"),
            new(41, "System", "Kernel-Power", 1, end.AddDays(-20), "Older interruption"),
        };
        var calls = new List<AuditEvidenceWindow>();
        var session = new AuditEvidenceSession((window, _, _, _, _) =>
        {
            calls.Add(window);
            return Task.FromResult(new AuditEvidenceBatch(events, [], []));
        });
        SetAuditEvidenceSession(vm, session);
        Invoke(vm, "ApplyHistoricalAudit", fixture);
        Invoke(vm, "StartAuditRangeLoad", true, true);
        await WaitUntil(() => !vm.IsAuditing);
        Require(vm.AllIssues.Sum(item => item.Occurrences) == 1 && calls.Count == 1, "Initial historical load must collect only the selected two days.");
        typeof(AIHubPageViewModel).GetField("_auditMergedSummary", PrivateInstance).SetValue(vm, "obsolete two-day AI totals");
        typeof(AIHubPageViewModel).GetProperty(nameof(vm.AiAnalysisRanText)).SetValue(vm, "obsolete two-day AI status");
        typeof(AIHubPageViewModel).GetProperty(nameof(vm.HealthScoreGrade)).SetValue(vm, "obsolete grade");
        vm.FullScanRangeIndex = 1;
        await WaitUntil(() => !vm.IsAuditing);
        Require(vm.AllIssues.Sum(item => item.Occurrences) == 2 && calls.Count == 2 && calls[1] == new AuditEvidenceWindow(end.AddDays(-7), end.AddDays(-2)),
            "Selecting seven days must automatically read only the missing older five days.");
        Require(!vm.AuditResultDetailText.Contains("obsolete", StringComparison.Ordinal) && string.IsNullOrEmpty(vm.AiAnalysisRanText)
            && vm.HealthScoreGrade != "obsolete grade", "A new period must reset previous AI totals/status and project its own score grade.");
        vm.FullScanRangeIndex = 2;
        await WaitUntil(() => !vm.IsAuditing);
        Require(vm.AllIssues.Sum(item => item.Occurrences) == 3 && calls.Count == 3 && calls[2] == new AuditEvidenceWindow(end.AddDays(-30), end.AddDays(-7)),
            "Selecting thirty days must automatically extend the shared evidence window.");
        Require(calls.All(window => window.ToUtc <= end), "Historical navigation must never read current-time events.");
        foreach (int index in new[] { 0, 1, 2, 0 })
        {
            typeof(AIHubPageViewModel).GetProperty(nameof(vm.AiAnalysisRanText)).SetValue(vm, "obsolete cached-view status");
            vm.FullScanRangeIndex = index;
            await WaitUntil(() => !vm.IsAuditing);
            Require(calls.Count == 3, "Returning to a loaded period must not re-read Windows logs.");
            Require(string.IsNullOrEmpty(vm.AiAnalysisRanText)
                && ReferenceEquals(Field<AuditResult>(vm, "_auditSourceSnapshot"), Field<AuditResult>(vm, "_auditOverviewSnapshot")),
                "Cached navigation must replace AI status and keep the source snapshot synchronized.");
            Require(vm.AuditPhase == ScanPhase.Idle && !vm.SidebarWorkflowVisible && string.IsNullOrEmpty(vm.AuditElapsedText),
                "Displaying a cached period must not claim a newly completed scan with a duration or 100% workflow.");
        }
        vm.FullScanRangeIndex = 1;
        await WaitUntil(() => !vm.IsAuditing);
        var oldEvent = events[1];
        events.RemoveAt(1);
        vm.SelectedScanModeIndex = 1;
        vm.StartSelectedScanCommand.Execute(null);
        await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
        Require(calls.Count == 4 && calls[^1] == new AuditEvidenceWindow(end.AddDays(-7), end)
            && vm.AllIssues.Sum(item => item.Occurrences) == 1 && vm.SidebarWorkflowVisible && vm.AuditElapsedText != "0s",
            "Manual Full scan must reread the entire selected historical window and replace stale cached evidence.");
        vm.FullScanRangeIndex = 2;
        await WaitUntil(() => !vm.IsAuditing);
        Require(calls.Count == 4 && vm.AllIssues.Sum(item => item.Occurrences) == 2 && !vm.SidebarWorkflowVisible,
            "A refreshed subrange must invalidate stale broader projections while reusing unaffected older raw evidence.");
        vm.FullScanRangeIndex = 1;
        await WaitUntil(() => !vm.IsAuditing);
        vm.SelectedScanModeIndex = 0;
        Require(vm.SelectedScanRangeText == vm.ScanRangeDescription
            && vm.SelectedScanHint.Contains(end.ToLocalTime().ToString("g"), StringComparison.Ordinal),
            "Quick refresh must show the selected period and the explicit historical cutoff.");
        vm.StartSelectedScanCommand.Execute(null);
        await WaitUntil(() => !vm.IsAuditing);
        Require(calls.Count == 5 && calls[^1] == new AuditEvidenceWindow(end.AddMinutes(-2), end)
            && vm.AllIssues.Sum(item => item.Occurrences) == 1,
            "Quick refresh must reread only the historical tail when the selected period is already covered.");
        events.Insert(1, oldEvent);
        vm.SelectedScanModeIndex = 1;
        Require(typeof(AIHubPageViewModel).GetProperty("FullDeepAnalyzeCommand") == null
            && typeof(AIHubPageViewModel).GetProperty("RefreshAuditRangeCommand") == null,
            "The removed regenerate and supplemental scan commands must have no remaining UI entry points.");
        await Capture(page, "aihub-auto-period-reuse.png");

        // A cancelled slow range must not publish over a later cached selection.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int slowCalls = 0;
        var slow = new AuditEvidenceSession(async (_, _, _, _, token) =>
        {
            if (Interlocked.Increment(ref slowCalls) == 2)
            {
                entered.SetResult();
                await release.Task;
                token.ThrowIfCancellationRequested();
            }
            return new AuditEvidenceBatch(events, [], []);
        });
        SetAuditEvidenceSession(vm, slow);
        Invoke(vm, "ApplyHistoricalAudit", fixture);
        Invoke(vm, "StartAuditRangeLoad", true, true);
        await WaitUntil(() => !vm.IsAuditing);
        vm.FullScanRangeIndex = 2;
        await entered.Task;
        vm.FullScanRangeIndex = 0;
        release.SetResult();
        await WaitUntil(() => !vm.IsAuditing);
        await Task.Delay(50);
        Require(vm.FullScanRangeIndex == 0 && vm.AllIssues.Sum(item => item.Occurrences) == 1,
            "A late cancelled month load must not overwrite the latest two-day selection.");

        SetAuditEvidenceSession(vm, new AuditEvidenceSession((_, _, _, _, _) =>
            Task.FromResult(new AuditEvidenceBatch(events, [], [])), maximumEvents: 2));
        Invoke(vm, "ApplyHistoricalAudit", fixture);
        Invoke(vm, "StartAuditRangeLoad", true, true);
        await WaitUntil(() => !vm.IsAuditing);
        vm.FullScanRangeIndex = 1;
        await WaitUntil(() => !vm.IsAuditing);
        vm.FullScanRangeIndex = 2;
        await WaitUntil(() => !vm.IsAuditing);
        var incomplete = Field<AuditResult>(vm, "_auditOverviewSnapshot");
        Require(incomplete.EvidenceIncomplete && incomplete.EventsScanned > 0 && vm.HasAuditOverviewCoverage,
            "Eviction must retain a structural evidence gap alongside the remaining findings.");
        var pendingWrites = Field<Task>(vm, "_auditHistoryWriteTask");
        var completedAnalysis = new AiHubAuditAnalysisService.AuditAnalysisResult(Array.Empty<AuditIssue>(), false, null)
        {
            UsedModel = "test-model", UsedRoute = "Main", MatchesRequestedModel = true,
        };
        Invoke(vm, "ApplyAiAnalysis", completedAnalysis, vm.AllIssues.ToList(), Field<List<SecurityEvent>>(vm, "_lastAuditEvents"), incomplete, false);
        typeof(AIHubPageViewModel).GetField("_persistCurrentAudit", PrivateInstance).SetValue(vm, true);
        Invoke(vm, "PersistCurrentAudit");
        Require(incomplete.EvidenceIncomplete && incomplete.AnalysisOutcome == "ok" && vm.HasAuditOverviewCoverage
            && ReferenceEquals(pendingWrites, Field<Task>(vm, "_auditHistoryWriteTask")),
            "A successful AI response must neither erase missing evidence nor save this incomplete window as a complete historical scan.");
        SetAuditEvidenceSession(vm,
            new AuditEvidenceSession((_, _, _, _, _) => Task.FromResult(new AuditEvidenceBatch([], [], []))));
        Invoke(vm, "ApplyHistoricalAudit", fixture);
        EnsureNoWorker();
        Log("Automatic period reuse, full-window reread, quick incremental refresh, historical bounds, truthful cached display and latest-selection wins PASS");
    }

    private static async Task CheckFullScanAnalysis(AIHubPage page)
    {
        var vm = page.ViewModel;
        var originalSnapshot = Field<AuditResult>(vm, "_auditOverviewSnapshot");
        var availableField = typeof(AIHubPageViewModel).GetField("_auditAiAvailable", PrivateInstance);
        var analyzeField = typeof(AIHubPageViewModel).GetField("_analyzeAuditEvents", PrivateInstance);
        var originalAvailable = availableField.GetValue(vm);
        var originalAnalyze = analyzeField.GetValue(vm);
        string originalScope = Field<string>(vm, "_knownAuditAnalysisScope");
        var pendingWrites = Field<Task>(vm, "_auditHistoryWriteTask");
        var end = FluentAuditFixture().ScanEnd.Value;
        SecurityEvent[] events =
        {
            new(41, "System", "Kernel-Power", 1, end.AddDays(-1), "Synthetic recent shutdown"),
            new(41, "System", "Kernel-Power", 1, end.AddDays(-5), "Synthetic earlier shutdown"),
        };
        const string previousSummary = "Previous DeepSeek synthetic complete report";
        const string currentScope = "synthetic-gpt-6-astra-scope";
        int modelCalls = 0;
        int collectionCalls = 0;
        bool available = true;
        string resultMode = "complete";
        IReadOnlyList<SecurityEvent> analyzedEvents = null;
        long analysisStartElapsed = 0;
        var collectionEntered = NewSignal();
        var releaseCollection = NewSignal();
        var analysisEntered = NewSignal();
        var releaseAnalysis = NewSignal();
        bool watchingBusy = false;
        bool lostBusy = false;
        PropertyChangedEventHandler busyChanged = (_, args) =>
        {
            if (watchingBusy && (args.PropertyName is nameof(vm.IsAuditing) or nameof(vm.IsAiAnalyzing))
                && !vm.IsAuditing && !vm.IsAiAnalyzing)
            {
                lostBusy = true;
            }
        };
        Func<IReadOnlyList<SecurityEvent>, IProgress<AiTaskProgress>, IProgress<AiHubAuditAnalysisService.AuditAnalysisResult>,
            AuditAnalysisCache, AiHubAuditAnalysisService.AuditAnalysisRetryState, CancellationToken,
            Task<AiHubAuditAnalysisService.AuditAnalysisResult>> analyze = async (input, progress, partial, _, _, token) =>
        {
            Interlocked.Increment(ref modelCalls);
            analysisStartElapsed = AuditElapsedMilliseconds();
            analyzedEvents = input.ToArray();
            string mode = resultMode;
            var release = releaseAnalysis;
            var result = new AiHubAuditAnalysisService.AuditAnalysisResult(Array.Empty<AuditIssue>(), false, null)
            {
                CompleteEvidenceCoverage = true,
                UsedModel = "test-model", UsedRoute = "Main", MatchesRequestedModel = true,
                AnalyzedEvents = input.Count,
                Scope = currentScope,
                EvidenceFingerprint = (string)typeof(AiHubAuditAnalysisService)
                    .GetMethod("CreateEvidenceFingerprint", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { input }),
            };
            progress.Report(new AiTaskProgress("Execute", "Synthetic model running", 0, 2));
            partial.Report(result with { CompleteEvidenceCoverage = false, IsPartial = true, ValidatedBatchCount = 1 });
            analysisEntered.TrySetResult();
            if (mode == "late") await release.Task;
            else await release.Task.WaitAsync(token);
            if (mode == "throw") throw new InvalidOperationException("Synthetic full-scan analysis failure");
            return mode == "partial"
                ? result with { CompleteEvidenceCoverage = false, IsPartial = true, Failure = "Synthetic incomplete batch" }
                : result;
        };
        availableField.SetValue(vm, (Func<bool>)(() => available));
        analyzeField.SetValue(vm, analyze);
        vm.PropertyChanged += busyChanged;
        try
        {
            Prepare(holdCollection: true);
            watchingBusy = true;
            vm.StartSelectedScanCommand.Execute(null);
            await WaitUntil(() => collectionEntered.Task.IsCompleted);
            var collectionClock = Field<object>(vm, "_auditClock");
            await Task.Delay(250);
            long collectedElapsed = AuditElapsedMilliseconds();
            releaseCollection.SetResult();
            await WaitUntil(() => analysisEntered.Task.IsCompleted);
            await NextRendering();
            var active = Field<AuditResult>(vm, "_auditOverviewSnapshot");
            Require(collectedElapsed >= 200 && ReferenceEquals(collectionClock, Field<object>(vm, "_auditClock"))
                && analysisStartElapsed >= collectedElapsed,
                "Full scan elapsed time must continue from log collection into AI analysis without resetting its clock.");
            Require(modelCalls == 1 && collectionCalls == 1 && analyzedEvents.Count == 2
                && active.ScanStart == end.AddDays(-7) && active.ScanEnd == end,
                "Full scan must automatically send the entire selected historical period to the current model exactly once.");
            Require(!lostBusy && vm.IsAiAnalyzing && vm.AuditPhase == ScanPhase.Scanning
                && vm.SidebarWorkflowPercent < 100 && vm.CancelAuditCommand.CanExecute(null)
                && active.AnalysisOutcome == "pending" && string.IsNullOrEmpty(active.AiAnalysisScope),
                "Collection-to-AI handoff must remain busy, cancellable and pending instead of claiming a completed old-model audit.");
            Require(vm.HasAiReport && vm.AiReportSummaryText == previousSummary,
                "The previous complete report must remain available while replacement analysis and partial batches are pending.");
            watchingBusy = false;
            releaseAnalysis.SetResult();
            await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
            Require(active.AnalysisOutcome == "ok" && active.AiAnalysisScope == currentScope && vm.HasAiReport
                && vm.AiReportSummaryText != previousSummary && vm.AllIssues.Count == 0 && vm.HealthScore == 100
                && active.Findings.Count == 0 && vm.SidebarWorkflowPercent == 100
                && active.UsedModel == "test-model" && vm.AiReportMetaText.Contains("test-model · Main", StringComparison.Ordinal),
                "A complete current-model result must replace the old report, findings, health score and scope before Full scan completes.");
            await Capture(page, "aihub-full-scan-ai-complete.png");

            vm.SelectedScanModeIndex = 0;
            vm.StartSelectedScanCommand.Execute(null);
            await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
            vm.FullScanRangeIndex = 0;
            await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
            vm.FullScanRangeIndex = 1;
            await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
            Require(modelCalls == 1, "Quick refresh and period navigation must reuse evidence without automatically invoking AI.");

            foreach (string mode in new[] { "partial", "throw", "cancel" })
            {
                Prepare(mode);
                vm.StartSelectedScanCommand.Execute(null);
                await WaitUntil(() => analysisEntered.Task.IsCompleted);
                if (mode == "cancel") vm.CancelAuditCommand.Execute(null);
                else releaseAnalysis.SetResult();
                await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
                var incomplete = Field<AuditResult>(vm, "_auditOverviewSnapshot");
                string expected = mode == "throw" ? "failed" : mode == "cancel" ? "cancelled" : "partial";
                Require(incomplete.AnalysisOutcome == expected && string.IsNullOrEmpty(incomplete.AiAnalysisScope)
                    && vm.AiReportSummaryText == previousSummary && vm.HasAiReport
                    && vm.AuditPhase != ScanPhase.Scanning && vm.SidebarWorkflowPercent < 100
                    && (string)typeof(AIHubPageViewModel).GetProperty("CurrentAuditAnalysisAction", PrivateInstance).GetValue(vm) == "retry",
                    $"Full scan {mode} must retain the last complete report, mark new evidence incomplete, and offer retry without claiming success.");

                vm.FullScanRangeIndex = 0;
                await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
                vm.FullScanRangeIndex = 1;
                await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
                var revisited = Field<AuditResult>(vm, "_auditOverviewSnapshot");
                Require(vm.HasAiReport && vm.AiReportSummaryText == previousSummary
                    && revisited.AnalysisOutcome == expected && string.IsNullOrEmpty(revisited.AiAnalysisScope),
                    $"Returning to seven days after Full scan {mode} must restore its prior complete report without assigning that report to new evidence.");

                int callsBeforeQuick = modelCalls;
                vm.FullScanRangeIndex = 0;
                await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
                vm.SelectedScanModeIndex = 0;
                vm.StartSelectedScanCommand.Execute(null);
                await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
                vm.FullScanRangeIndex = 1;
                await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
                var afterQuickRefresh = Field<AuditResult>(vm, "_auditOverviewSnapshot");
                Require(modelCalls == callsBeforeQuick && vm.HasAiReport && vm.AiReportSummaryText == previousSummary
                    && afterQuickRefresh.AnalysisOutcome != "ok" && string.IsNullOrEmpty(afterQuickRefresh.AiAnalysisScope),
                    $"Refreshing two days after Full scan {mode} must retain the seven-day complete report when period projections are rebuilt.");

                // Retry the existing failed view, without reinstalling the prior report fixture.
                analysisEntered = NewSignal();
                releaseAnalysis = NewSignal();
                vm.SelectedScanModeIndex = 1;
                vm.StartSelectedScanCommand.Execute(null);
                await WaitUntil(() => analysisEntered.Task.IsCompleted);
                var retried = Field<AuditResult>(vm, "_auditOverviewSnapshot");
                Require(vm.HasAiReport && vm.AiReportSummaryText == previousSummary && vm.IsAiAnalyzing
                    && retried.AnalysisOutcome == "pending" && string.IsNullOrEmpty(retried.AiAnalysisScope),
                    $"A repeated Full scan after {mode} must keep the prior complete report available during analysis.");
                if (mode == "cancel") vm.CancelAuditCommand.Execute(null);
                else releaseAnalysis.SetResult();
                await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
                Require(vm.HasAiReport && vm.AiReportSummaryText == previousSummary
                    && retried.AnalysisOutcome == expected && string.IsNullOrEmpty(retried.AiAnalysisScope),
                    $"Repeated Full scan {mode} must not discard the last complete report or copy its model scope to incomplete evidence.");
            }

            Prepare("throw", failCollectionOnce: true);
            int callsBeforeCollectionFailure = modelCalls;
            vm.StartSelectedScanCommand.Execute(null);
            await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
            Require(collectionCalls == 1 && modelCalls == callsBeforeCollectionFailure
                && vm.AuditPhase == ScanPhase.Failed && vm.HasAiReport && vm.AiReportSummaryText == previousSummary,
                "A collection failure must retain the previous complete report and must not invoke AI with missing evidence.");
            vm.StartSelectedScanCommand.Execute(null);
            await WaitUntil(() => analysisEntered.Task.IsCompleted);
            var afterCollectionRetry = Field<AuditResult>(vm, "_auditOverviewSnapshot");
            Require(collectionCalls == 2 && modelCalls == callsBeforeCollectionFailure + 1
                && vm.HasAiReport && vm.AiReportSummaryText == previousSummary && vm.IsAiAnalyzing
                && afterCollectionRetry.AnalysisOutcome == "pending" && string.IsNullOrEmpty(afterCollectionRetry.AiAnalysisScope),
                "Retrying after a collection failure must keep the previous complete report throughout the next AI analysis.");
            releaseAnalysis.SetResult();
            await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
            Require(afterCollectionRetry.AnalysisOutcome == "failed" && string.IsNullOrEmpty(afterCollectionRetry.AiAnalysisScope)
                && vm.HasAiReport && vm.AiReportSummaryText == previousSummary,
                "A model failure after successful recollection must still retain the report from before the collection failure.");

            Prepare();
            available = false;
            int callsBeforeUnavailable = modelCalls;
            vm.StartSelectedScanCommand.Execute(null);
            await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
            Require(modelCalls == callsBeforeUnavailable && collectionCalls == 1 && vm.AuditPhase == ScanPhase.Failed
                && Field<AuditResult>(vm, "_auditOverviewSnapshot").AnalysisOutcome == "failed"
                && vm.AiReportSummaryText == previousSummary && !string.IsNullOrWhiteSpace(vm.AiAnalysisRanText),
                "Unavailable AI must explicitly stop Full scan after collection while retaining the earlier report.");

            available = true;
            Prepare(noEvents: true);
            int callsBeforeEmpty = modelCalls;
            vm.StartSelectedScanCommand.Execute(null);
            await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
            Require(modelCalls == callsBeforeEmpty && collectionCalls == 1
                && Field<AuditResult>(vm, "_auditOverviewSnapshot").AnalysisOutcome == "skipped"
                && !string.IsNullOrWhiteSpace(vm.AiAnalysisRanText) && vm.SidebarWorkflowPercent < 100,
                "An empty Full scan must report that AI did not run, without recollecting Windows logs or requesting an empty analysis.");

            Prepare("late");
            vm.StartSelectedScanCommand.Execute(null);
            await WaitUntil(() => analysisEntered.Task.IsCompleted);
            vm.FullScanRangeIndex = 0;
            await WaitUntil(() => !vm.IsAuditing && !vm.IsAiAnalyzing);
            var selected = Field<AuditResult>(vm, "_auditOverviewSnapshot");
            releaseAnalysis.SetResult();
            await Task.Delay(100);
            await NextRendering();
            Require(ReferenceEquals(selected, Field<AuditResult>(vm, "_auditOverviewSnapshot"))
                && selected.ScanStart == end.AddDays(-2) && selected.AiAnalysisScope != currentScope
                && !vm.IsAiAnalyzing && vm.AuditPhase == ScanPhase.Idle,
                "A late model result from a cancelled Full scan must not overwrite a newly selected period.");
            Require(ReferenceEquals(pendingWrites, Field<Task>(vm, "_auditHistoryWriteTask")) && !vm.AiHub.IsEnabled,
                "Synthetic Full scan regression must neither save user audit history nor enable a real AI service.");
            EnsureNoWorker();
        }
        finally
        {
            watchingBusy = false;
            releaseCollection.TrySetResult();
            releaseAnalysis.TrySetResult();
            vm.PropertyChanged -= busyChanged;
            vm.CancelAuditCommand.Execute(null);
            Invoke(vm, "CancelAuditForNavigation");
            availableField.SetValue(vm, originalAvailable);
            analyzeField.SetValue(vm, originalAnalyze);
            typeof(AIHubPageViewModel).GetField("_knownAuditAnalysisScope", PrivateInstance).SetValue(vm, originalScope);
            SetAuditEvidenceSession(vm,
                new AuditEvidenceSession((_, _, _, _, _) => Task.FromResult(new AuditEvidenceBatch([], [], []))));
            if (originalSnapshot != null) Invoke(vm, "ApplyHistoricalAudit", originalSnapshot);
            else Invoke(vm, "ClearAuditSnapshot");
        }
        Log("Full scan automatic current-model analysis, continuous clock/busy state, final replacement, repeated failure and period-navigation report retention, unavailable/empty evidence and stale-result isolation PASS");

        void Prepare(string mode = "complete", bool noEvents = false, bool holdCollection = false, bool failCollectionOnce = false)
        {
            resultMode = mode;
            collectionEntered = NewSignal();
            releaseCollection = NewSignal();
            analysisEntered = NewSignal();
            releaseAnalysis = NewSignal();
            collectionCalls = 0;
            SetAuditEvidenceSession(vm, new AuditEvidenceSession(async (_, _, _, _, token) =>
            {
                int call = Interlocked.Increment(ref collectionCalls);
                collectionEntered.TrySetResult();
                if (holdCollection) await releaseCollection.Task.WaitAsync(token);
                if (failCollectionOnce && call == 1) throw new InvalidOperationException("Synthetic full-scan collection failure");
                return new AuditEvidenceBatch(noEvents ? [] : events, [], []);
            }));
            var previous = new AuditResult
            {
                Timestamp = end, ScanStart = end.AddDays(-7), ScanEnd = end, EventsScanned = events.Length,
                CollectionProfile = AuditCollectionProfile.ForMode(EventLogService.AuditMode.Extended),
                Findings = AuditRuleEngine.Analyze(events).Select(issue => new AuditIssueEnhanced(issue) { EventRef = "item-000001" }).ToList(),
                HealthScore = 50, AnalysisOutcome = "ok", AiAnalysisScope = "synthetic-deepseek-scope",
                AiReportSummary = previousSummary, AiReportMeta = "Previous model metadata", UsedModel = "deepseek",
            };
            Invoke(vm, "ApplyHistoricalAudit", previous);
            vm.SelectedScanModeIndex = 1;
        }

        long AuditElapsedMilliseconds()
        {
            var clock = Field<object>(vm, "_auditClock");
            return (long)clock.GetType().GetProperty("ElapsedMilliseconds", PrivateInstance).GetValue(clock);
        }

        static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static void SetAuditEvidenceSession(AIHubPageViewModel vm, AuditEvidenceSession session)
    {
        Field<AuditEvidenceSession>(vm, "_auditEvidenceSession").Dispose();
        typeof(AIHubPageViewModel).GetField("_auditEvidenceSession", PrivateInstance).SetValue(vm, session);
    }
    private static async Task CheckReadableOverview(AIHubPage page)
    {
        var vm = page.ViewModel;
        vm.AuditResultViewIndex = 0;
        var fixture = FluentAuditFixture();
        fixture.HealthScore = 9;
        fixture.EventsScanned = 85;
        fixture.Findings.Clear();
        var groups = new[]
        {
            (Category: "Reliability", High: 4, Medium: 0, Low: 0, Occurrences: 6, Title: "Critical Error: .NET Runtime (1026)", Chinese: ".NET Runtime 严重错误 (1026)"),
            (Category: "Stability", High: 1, Medium: 0, Low: 0, Occurrences: 1, Title: "Unexpected kernel shutdown", Chinese: "系统内核异常关闭"),
            (Category: "Configuration", High: 0, Medium: 4, Low: 0, Occurrences: 27, Title: "Warning: Microsoft-Windows-FilterManager (11)", Chinese: "Microsoft-Windows-FilterManager 配置警告 (11)"),
            (Category: "Network", High: 0, Medium: 3, Low: 1, Occurrences: 6, Title: "TCP and UDP temporary ports exhausted", Chinese: "TCP 和 UDP 临时端口耗尽"),
            (Category: "Application", High: 0, Medium: 3, Low: 0, Occurrences: 5, Title: "CoworkVMService repeatedly fails to set SCM recovery actions", Chinese: "CoworkVMService 反复无法设置 SCM 恢复操作"),
            (Category: "System", High: 0, Medium: 2, Low: 3, Occurrences: 14, Title: "Kernel-Power recorded an unexpected shutdown", Chinese: "Kernel-Power 记录到意外关机"),
        };
        foreach (var group in groups)
        {
            int count = group.High + group.Medium + group.Low;
            for (int index = 0; index < count; index++)
                fixture.Findings.Add(new AuditIssueEnhanced
                {
                    Key = group.Category + index, Category = group.Category, LogName = "System",
                    Title = group.Title, TitleZh = group.Chinese,
                    Severity = index < group.High ? "High" : index < group.High + group.Medium ? "Medium" : "Low",
                    Occurrences = index == 0 ? group.Occurrences - count + 1 : 1,
                    Recommendation = "Review the recorded event before changing system settings.", RecommendationZh = "调整系统设置前，先查看事件证据。",
                });
        }

        Invoke(vm, "ApplyHistoricalAudit", fixture);
        Require(vm.AuditOverviewHighCount == 5 && vm.AuditOverviewMediumCount == 12 && vm.AuditOverviewLowCount == 4 && vm.AuditCategories.Count == 6,
            "The screenshot-sized fixture must retain all 21 issues and the 5/12/4 risk distribution.");
        var overview = (Border)page.FindName("AuditPeriodOverview");
        var cards = (ItemsRepeater)page.FindName("AuditCategoryCards");
        foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        foreach (int width in new[] { 1280, 760, 480 })
        {
            page.RequestedTheme = theme;
            App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1100));
            ((ScrollViewer)page.FindName("AiHubContentScroll")).ChangeView(null, 0, null, true);
            await NextRendering();
            await NextRendering();
            var first = (Button)cards.GetOrCreateElement(0);
            var second = (Button)cards.GetOrCreateElement(1);
            await NextRendering();
            var firstBounds = first.TransformToVisual(cards).TransformBounds(new Windows.Foundation.Rect(0, 0, first.ActualWidth, first.ActualHeight));
            var secondBounds = second.TransformToVisual(cards).TransformBounds(new Windows.Foundation.Rect(0, 0, second.ActualWidth, second.ActualHeight));
            Require(first.ActualWidth > 200 && firstBounds.Right <= cards.ActualWidth + 1 && secondBounds.Right <= cards.ActualWidth + 1,
                "Category cards must fit the content width in " + theme + "/" + width);
            Require(overview.ActualWidth >= 620 ? Math.Abs(firstBounds.Top - secondBounds.Top) < 1 && secondBounds.Left > firstBounds.Left
                : Math.Abs(firstBounds.Left - secondBounds.Left) < 1 && secondBounds.Top > firstBounds.Top,
                "Categories must use two columns in wide layouts and one column in narrow layouts.");
            Require(Descendants(overview).OfType<Expander>().Count() == 0 && !Descendants(overview).OfType<TextBlock>().Any(text => text.Text == vm.AuditOverviewScore || text.Text == vm.AuditTotals),
                "The period overview must avoid nested expanders and duplicate score/count paragraphs.");
            var metrics = (Grid)page.FindName("AuditRiskMetrics");
            Require(Descendants(metrics).OfType<TextBlock>().All(text => !text.IsTextTrimmed && text.ActualWidth > 0),
                "Every risk count and text label must remain readable.");
            var dashboard = (Border)page.FindName("AuditDashboard");
            foreach (var risk in Descendants(metrics).OfType<Button>())
            {
                foreach (string state in new[] { "Normal", "PointerOver", "Pressed", "Normal" })
                {
                    VisualStateManager.GoToState(risk, state, false);
                    await NextRendering();
                    var surface = (Border)risk.Content;
                    var backdrop = CompositeBrush(dashboard.Background, theme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243));
                    var fill = CompositeBrush(surface.Background, backdrop);
                    Require(Descendants(surface).OfType<TextBlock>().All(text => Contrast(BrushColor(text.Foreground), fill) >= 4.5),
                        $"Risk card text must retain 4.5:1 contrast after theme switching at {theme}/{width}/{risk.Tag}/{state}.");
                }
            }

            var score = (FrameworkElement)page.FindName("AuditHealthSummary");
            var commands = (FrameworkElement)page.FindName("AuditCommandHeader");
            Require(IsDescendantOf(overview, dashboard) && IsDescendantOf(score, dashboard) && IsDescendantOf(commands, dashboard),
                "Scan actions, health score and period overview must share one dashboard surface.");
            Require(HeaderBounds(commands, dashboard).Bottom <= HeaderBounds(score, dashboard).Top + 2 &&
                HeaderBounds(score, dashboard).Bottom <= HeaderBounds(overview, dashboard).Top + 2 &&
                HeaderInside(HeaderBounds(commands, dashboard), dashboard) && HeaderInside(HeaderBounds(overview, dashboard), dashboard),
                "Dashboard sections must keep a single reading order and fit within the same surface.");
            CheckScoreActivityLayout(page, $"overview/{theme}/{width}");
            await Capture(page, "aihub-unified-dashboard-" + theme + "-" + width + ".png");
            overview.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
            await NextRendering();
            await NextRendering();
            await Capture(page, "aihub-readable-overview-" + theme + "-" + width + ".png");
            var results = (FrameworkElement)page.FindName("AuditResultsGrid");
            results.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
            await NextRendering();
            await NextRendering();
            var findings = (Border)page.FindName("AuditFindingsCard");
            var analysis = (Border)page.FindName("AuditSummaryCard");
            Require(findings.Visibility == Visibility.Collapsed && HeaderInside(HeaderBounds(analysis, results), results),
                "Overview must hide the findings list while keeping the AI summary readable.");
            Require(IsDescendantOf(findings, dashboard) && dashboard.Padding == new Thickness(0) && dashboard.BorderThickness == new Thickness(0) &&
                BrushColor(dashboard.Background).A == 0 && BrushColor(analysis.Background).A > 0,
                "The results workspace must use the native reading layer; AI analysis retains its own surface.");
            await Capture(page, "aihub-unified-results-" + theme + "-" + width + ".png");
        }

        var trigger = (Button)cards.GetOrCreateElement(0);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new ButtonAutomationPeer(trigger).GetPattern(PatternInterface.Invoke)).Invoke();
        await WaitUntil(() => vm.ShowAuditFindings);
        await NextRendering();
        Require(vm.FilteredIssues.Count == fixture.Findings.Count(issue => issue.Category == vm.AuditCategories[0].Category) &&
            !((SplitView)page.FindName("AiHubDetailsHost")).IsPaneOpen && overview.Visibility == Visibility.Collapsed,
            "A category card must open every issue in that category without also showing Overview or details.");
        Invoke(page, "OnAuditBackClick", page.FindName("AuditBackButton"), new RoutedEventArgs());
        await NextRendering();
        page.RequestedTheme = ElementTheme.Default;
        App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1000));
        Log("Six-category overview with 21 issues: risk metrics, two/one-column cards, light/dark layouts and category navigation PASS");
    }

    private static async Task CheckAuditWorkspace(AIHubPage page)
    {
        var vm = page.ViewModel;
        var fixture = FluentAuditFixture();
        // Deliberately separate category from log source; the old source predicate
        // incorrectly included an Application-category finding from System logs.
        fixture.Findings[0].Category = "Application";
        Invoke(vm, "ApplyHistoricalAudit", fixture);
        vm.AuditResultViewIndex = 0;
        vm.ResetFindingFilters();
        await NextRendering();
        var overview = (Border)page.FindName("AuditPeriodOverview");
        var findings = (Border)page.FindName("AuditFindingsCard");
        var cards = (ItemsRepeater)page.FindName("AuditCategoryCards");
        var categoryIndex = vm.AuditCategories.ToList().FindIndex(category => category.Category == "Application");
        var categoryButton = (Button)cards.GetOrCreateElement(categoryIndex);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new ButtonAutomationPeer(categoryButton).GetPattern(PatternInterface.Invoke)).Invoke();
        await NextRendering();
        Require(vm.ShowAuditFindings && overview.Visibility == Visibility.Collapsed && findings.Visibility == Visibility.Visible && vm.OverviewFindings.Count == 3,
            "Category navigation must replace Overview with all category matches, including low-risk issues.");
        vm.SetSourceFilter("System");
        Require(vm.OverviewFindings.Count == 1 && vm.OverviewFindings[0].EventId == "1000", "Category and source must intersect independently.");
        vm.SetSeverityFilter("High");
        Require(vm.ShowNoFilterMatches && vm.OverviewFindings.Count == 0 && vm.AuditCategories.Count == 2, "Empty intersections must preserve the overview snapshot.");
        vm.ResetFindingFilters();
        Require(vm.OverviewFindings.Count == 6 && !vm.HasFindingFilters, "Clear filters must remove category, source and risk restrictions.");
        vm.SetSourceFilter("Application");
        Require(vm.OverviewFindings.Count == 2 && vm.ApplicationLabel.EndsWith("(2)", StringComparison.Ordinal), "Source counts and results must use LogName only.");
        Invoke(page, "OnAuditBackClick", page.FindName("AuditBackButton"), new RoutedEventArgs());
        await NextRendering();
        await NextRendering();
        categoryButton = (Button)cards.GetOrCreateElement(categoryIndex);
        await WaitUntil(() => categoryButton.FocusState != FocusState.Unfocused);
        Require(vm.ShowAuditOverview && findings.Visibility == Visibility.Collapsed && categoryButton.FocusState != FocusState.Unfocused,
            "Back must restore the overview and focus to the originating category.");
        var risk = Descendants((Grid)page.FindName("AuditRiskMetrics")).OfType<Button>().Single(button => Equals(button.Tag, "High"));
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new ButtonAutomationPeer(risk).GetPattern(PatternInterface.Invoke)).Invoke();
        await NextRendering();
        Require(vm.IsFilterHigh && vm.IsSourceAll && vm.OverviewFindings.Count == 2 && vm.OverviewFindings.All(issue => issue.IsHigh), "Risk tiles must open a fresh risk filter.");
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        foreach (int width in new[] { 1280, 760, 480 })
        {
            page.RequestedTheme = theme;
            App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1000));
            findings.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
            await NextRendering();
            await NextRendering();
            var dashboard = (Border)page.FindName("AuditDashboard");
            Require(HeaderInside(HeaderBounds(findings, dashboard), dashboard) && overview.Visibility == Visibility.Collapsed,
                "The findings view must fit its workspace without simultaneously rendering Overview.");
            CheckScoreActivityLayout(page, $"findings/{theme}/{width}");
            await Capture(page, $"aihub-workspace-findings-{theme}-{width}.png");
        }

        page.RequestedTheme = ElementTheme.Default;
        App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 1000));
        vm.OpenAuditFindings();
        await NextRendering();
        Require(vm.OverviewFindings.Count == 6 && !vm.HasFindingFilters, "View all must show the complete result without the old top-three cap.");
        var host = (SplitView)page.FindName("AiHubDetailsHost");
        Invoke(page, "OpenFinding", fixture.Findings[4], page.FindName("AuditBackButton"));
        await WaitUntil(() => host.IsPaneOpen);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void PaneClosed(SplitView sender, object args) => closed.TrySetResult();
        host.PaneClosed += PaneClosed;
        try
        {
            vm.SetSourceFilter("System");
            // IsPaneOpen becomes false before the native close animation and its
            // callback finish. Let that callback release the old selection before
            // the next fixture opens a new finding in the same SplitView.
            await WaitUntil(() => !host.IsPaneOpen && closed.Task.IsCompleted);
            await NextRendering();
        }
        finally
        {
            host.PaneClosed -= PaneClosed;
        }
        Require(vm.OverviewFindings.Count == 4, "Filtering out the selected finding must close stale details.");
        vm.AuditResultViewIndex = 0;
        vm.ResetFindingFilters();
        EnsureNoWorker();
        Log("Overview/findings navigation, origin focus, uncapped list, independent category/source/risk filters, empty state and stale-detail dismissal PASS");
    }

    private static async Task CheckFluentOverviewAndDetails(AIHubPage page)
    {
        var vm = page.ViewModel;
        vm.ActiveTabIndex = 0;
        vm.ResetFindingFilters();
        vm.AuditResultViewIndex = 1;
        var fixture = FluentAuditFixture();
        Invoke(vm, "ApplyHistoricalAudit", fixture);
        await NextRendering();
        Require(vm.AuditCategories.Count == 2 && !string.IsNullOrWhiteSpace(vm.AuditTotals), "The complete audit must be summarized into its two problem categories.");
        string totals = vm.AuditTotals;
        var categoryRows = vm.AuditCategories.ToArray();
        vm.FindingSearchText = "no matching issue";
        await NextRendering();
        Require(vm.AuditTotals == totals && vm.AuditCategories.Count == 2, "Filtering the detail list must not change the complete audit summary.");
        Require(vm.AuditCategories.SequenceEqual(categoryRows), "Filtering details must preserve category rows and their expansion state.");
        vm.ResetFindingFilters();
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
            await WaitUntil(() => !vm.IsAuditing);
            Require(vm.SelectedScanLabel == (mode == 1 ? vm.FullScanLabel : vm.FastScanLabel) &&
                (range == originalScope ? vm.AllIssues.SequenceEqual(fixture.Findings) : vm.AllIssues.Count == 0) &&
                !vm.IsAuditing && !vm.IsAiAnalyzing,
                "Changing a display period must load matching evidence automatically without an AI request.");
        }
        vm.FullScanRangeIndex = -1;
        vm.SelectedScanModeIndex = 2;
        Require(vm.FullScanRangeIndex == 1 && vm.SelectedScanModeIndex == 0, "Invalid scan drafts must be rejected.");
        vm.FullScanRangeIndex = originalScope;
        await WaitUntil(() => !vm.IsAuditing);
        vm.SelectedScanModeIndex = originalMode;
        Require(vm.OverviewFindings.Count == 6 && vm.OverviewFindings.Take(3).Select(issue => issue.Key).SequenceEqual(new[] { "synthetic-finding-4", "synthetic-finding-1", "synthetic-finding-3" }),
            "The findings view must show every issue ordered by severity and frequency.");
        vm.SetSourceFilter("System");
        Require(vm.OverviewFindings.Count == 4, "Active filters must show all matching issues.");
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
        if (title.Text != fixture.Findings[1].DisplayTitle || !previous.IsEnabled)
        {
            Log($"DETAILS NEXT mismatch: title={title.Text}; expected={fixture.Findings[1].DisplayTitle}; selected={Field<AuditIssueEnhanced>(page, "_detailsFinding")?.Key}; " +
                $"paneFinding={Field<AuditIssueEnhanced>(pane, "_finding")?.Key}; previousEnabled={previous.IsEnabled}; open={host.IsPaneOpen}; " +
                $"orderedKeys={string.Join(",", vm.OverviewFindings.Select(issue => issue.Key))}");
        }
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
                Require(host.ActualWidth >= 640 || Math.Abs(host.OpenPaneLength - host.ActualWidth) < 1,
                    $"Narrow details must use the complete reading width at {theme}/{width}.");
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
        var legacyDns = new AuditIssueEnhanced
        {
            Key = "legacy-dns-remediation", EventId = "1014", Source = "Microsoft-Windows-DNS-Client", LogName = "System",
            Title = "Name resolution timed out", TitleZh = "域名解析超时", Severity = "Medium",
            Description = "An application could not resolve its configured hostname.", DescriptionZh = "应用无法解析配置的域名。",
            Recommendation = "Previous generic advice", RecommendationZh = "历史泛化建议",
        };
        Invoke(vm, "ApplyHistoricalAudit", new AuditResult { Timestamp = DateTime.UtcNow, Findings = new() { legacyDns } });
        await WaitForPaneLayout(true, "remediation-guide", () => Invoke(page, "OpenFinding", legacyDns, trigger));
        string checkText = ((TextBlock)pane.FindName("RecommendationText")).Text;
        Require(checkText.Contains("nslookup", StringComparison.Ordinal) && checkText.Contains("VPN", StringComparison.Ordinal) &&
            !checkText.Contains("Previous generic advice", StringComparison.Ordinal) && legacyDns.RemediationPlan == null,
            "Legacy DNS findings must show provider-specific checks and decision criteria without rewriting saved evidence.");
        foreach (string name in new[] { "ConditionalActionText", "VerificationText", "RecoveryText", "PlanSourceText" })
        {
            Require(!string.IsNullOrWhiteSpace(((TextBlock)pane.FindName(name)).Text),
                "Legacy findings must render separate action, verification, recovery and guide-source sections.");
        }
        Require(((Expander)pane.FindName("PreviousAdviceExpander")).Visibility == Visibility.Visible,
            "Previously saved unstructured advice must remain accessible separately from the diagnostic guide.");
        ((TextBlock)pane.FindName("CheckPlanLabel")).StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
        await NextRendering();
        await Capture(page, "aihub-remediation-checks.png");
        ((TextBlock)pane.FindName("ConditionalActionLabel")).StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
        await NextRendering();
        await Capture(page, "aihub-remediation-actions.png");
        Log("Legacy DNS remediation renders checks/criteria, conditional action, verification and recovery without mutating history PASS");
        Invoke(vm, "ApplyHistoricalAudit", fixture);
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

    private static async Task CheckAiFinalAssessment(AIHubPage page)
    {
        var vm = page.ViewModel;
        var original = Field<AuditResult>(vm, "_auditOverviewSnapshot");
        string originalScope = Field<string>(vm, "_knownAuditAnalysisScope");
        var timestamp = DateTime.UtcNow;
        SecurityEvent[] events = { new(41, "System", "Kernel-Power", 1, timestamp.AddMinutes(-5), "Synthetic reviewed shutdown") };
        var rules = AuditRuleEngine.Analyze(events);
        var snapshot = new AuditResult
        {
            Timestamp = timestamp, ScanStart = timestamp.AddDays(-2), ScanEnd = timestamp, EventsScanned = 1,
            Findings = rules.Select(issue => new AuditIssueEnhanced(issue) { EventRef = "item-000001" }).ToList(),
            HealthScore = HealthScoreCalculator.Calculate(rules), AnalysisOutcome = "ok", AiAnalysisScope = "previous-synthetic-scope",
            AiReportSummary = "Previous successful synthetic assessment", AiReportMeta = "previous metadata",
        };
        string fingerprint = (string)typeof(AiHubAuditAnalysisService).GetMethod("CreateEvidenceFingerprint", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { events });
        var final = new AiHubAuditAnalysisService.AuditAnalysisResult(Array.Empty<AuditIssue>(), false, null)
        {
            CompleteEvidenceCoverage = true, AnalyzedEvents = 1, Scope = "current-synthetic-scope", EvidenceFingerprint = fingerprint,
            UsedModel = "test-model", UsedRoute = "Main", MatchesRequestedModel = true,
        };
        try
        {
            Invoke(vm, "ApplyHistoricalAudit", snapshot);
            var oldFindings = vm.AllIssues.ToArray();
            SetPrivate(vm, "_retainingAiReport", true);
            SetPrivate(vm, "_persistCurrentAudit", false);
            Invoke(vm, "ApplyAiAnalysis", final with { CompleteEvidenceCoverage = false, IsPartial = true, Failure = "Synthetic partial failure" }, rules, events, snapshot, false);
            Require(vm.AllIssues.SequenceEqual(oldFindings) && vm.AiReportSummaryText == "Previous successful synthetic assessment" && snapshot.AnalysisOutcome == "ok",
                "A partial replacement keeps the last successful findings, report and saved scan outcome.");

            SetPrivate(vm, "_retainingAiReport", true);
            Invoke(vm, "ApplyAiAnalysis", final with { CompleteEvidenceCoverage = false }, rules, events, snapshot, false);
            Require(vm.AllIssues.SequenceEqual(oldFindings) && snapshot.AnalysisOutcome == "ok",
                "A result without proven input coverage cannot replace a previously complete report.");

            SetPrivate(vm, "_retainingAiReport", true);
            Invoke(vm, "ApplyAiAnalysis", final with { EvidenceFingerprint = "different-evidence" }, rules, events, snapshot, false);
            Require(vm.AllIssues.SequenceEqual(oldFindings) && snapshot.AnalysisOutcome == "ok",
                "A completed result for different evidence cannot replace the displayed successful assessment.");

            var lowered = new AuditIssue
            {
                Key = "reviewed-shutdown", EventRef = "item-000001", RelatedEventRefs = new() { "item-000001" },
                Title = "Reviewed shutdown", TitleZh = "已复核关机", Severity = "Low", Recommendation = "Observe normal shutdown",
            };
            SetPrivate(vm, "_retainingAiReport", true);
            Invoke(vm, "ApplyAiAnalysis", final with { Issues = new[] { lowered } }, rules, events, snapshot, false);
            Require(vm.AllIssues.Count == 1 && vm.AllIssues[0].Severity == "Low" && snapshot.Findings.Single().Severity == "Low" &&
                snapshot.Timestamp == timestamp && snapshot.AiAnalysisScope == "current-synthetic-scope" && vm.HighCount == 0,
                "Complete AI review lowers the current scan's rule severity and replaces its model-scope metadata without creating a new scan.");

            SetPrivate(vm, "_retainingAiReport", true);
            Invoke(vm, "ApplyAiAnalysis", final, rules, events, snapshot, false);
            Require(vm.AllIssues.Count == 0 && snapshot.Findings.Count == 0 && snapshot.Timestamp == timestamp &&
                vm.HealthScore == 100 && snapshot.HealthScore == 100 && vm.HighCount == 0 && vm.MediumCount == 0 && vm.LowCount == 0 &&
                vm.AiReportFindings.Count == 0 && snapshot.AnalysisOutcome == "ok" && vm.HasAiReport && !Field<bool>(vm, "_retainingAiReport"),
                "Complete empty AI assessment removes old current-scan findings and updates health, category counts, AI report and snapshot together.");
            string acceptedSummary = vm.AiReportSummaryText;
            SetPrivate(vm, "_retainingAiReport", true);
            Invoke(vm, "ApplyAiAnalysis", final with { CompleteEvidenceCoverage = false, IsPartial = true }, rules, events, snapshot, false);
            Require(vm.AllIssues.Count == 0 && snapshot.Findings.Count == 0 && vm.AiReportSummaryText == acceptedSummary,
                "A later partial retry cannot resurrect rules dismissed by the last complete assessment.");
            var fallback = final with
            {
                UsedModel = "gpt-6-luna", UsedRoute = "Fallback", MatchesRequestedModel = false,
                Scope = "current-synthetic-scope:actual:fallback-fixture",
            };
            Invoke(vm, "ApplyAiAnalysis", fallback, rules, events, snapshot, false);
            Require(snapshot.UsedModel == "gpt-6-luna" && vm.AiReportMetaText.Contains("gpt-6-luna · Fallback", StringComparison.Ordinal)
                && snapshot.AnalysisOutcome == "ok" && Field<bool>(vm, "_auditReportNeedsRefresh"),
                "A complete fallback report must show its actual model and keep the requested primary-model update available.");
            Invoke(vm, "ApplyHistoricalAudit", snapshot);
            Require(Field<bool>(vm, "_auditReportNeedsRefresh"), "Restoring fallback history cannot relabel it as a current primary-model report.");
            Invoke(vm, "ApplyAiAnalysis", final, rules, events, snapshot, false);
            Invoke(vm, "RefreshAuditReportIdentity", "another-synthetic-scope");
            Require(Field<bool>(vm, "_auditReportNeedsRefresh"), "A new model/policy scope still exposes updating after an empty successful report.");
            Invoke(vm, "RefreshAuditReportIdentity", "current-synthetic-scope");
            Require(!Field<bool>(vm, "_auditReportNeedsRefresh"), "The report returns to view mode for its accepted scope.");
            snapshot.AiAnalysisScope = string.Empty;
            Invoke(vm, "RefreshAuditReportIdentity", "current-synthetic-scope");
            Require(Field<bool>(vm, "_auditReportNeedsRefresh"), "A legacy report without model scope must request an update after readiness refresh.");
            snapshot.AiAnalysisScope = "current-synthetic-scope";
            Invoke(vm, "RefreshAuditReportIdentity", "current-synthetic-scope");
            await NextRendering();
            await Capture(page, "aihub-ai-final-assessment.png");
            Require(!Field<bool>(vm, "_persistCurrentAudit") && !vm.AiHub.IsEnabled,
                "Final-assessment fixtures neither persist user history nor start an AI route.");
        }
        finally
        {
            SetPrivate(vm, "_retainingAiReport", false);
            SetPrivate(vm, "_persistCurrentAudit", false);
            SetPrivate(vm, "_knownAuditAnalysisScope", originalScope);
            if (original != null) Invoke(vm, "ApplyHistoricalAudit", original);
            else Invoke(vm, "ClearAuditSnapshot");
        }
        Log("AI final assessment downgrade, empty replacement, partial preservation and scope identity PASS");

        static void SetPrivate(AIHubPageViewModel model, string field, object value) =>
            typeof(AIHubPageViewModel).GetField(field, PrivateInstance).SetValue(model, value);
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

            var completed = FluentAuditFixture();
            completed.AnalysisOutcome = "ok";
            completed.AiAnalysisScope = "synthetic-scope";
            completed.AiReportSummary = "A complete synthetic report that must survive retry failures.";
            completed.AiReportMeta = "synthetic metadata";
            completed.Findings[0].EventRef = "item-000001";
            Invoke(vm, "ApplyHistoricalAudit", completed);
            Invoke(vm, "RefreshAuditReportIdentity", "synthetic-scope");
            Require(Field<bool>(vm, "_auditReportNeedsRefresh") == false && vm.HasAiReport,
                "Identical completed report identity must remain available without an AI run.");
            string completedSummary = vm.AiReportSummaryText;
            string completedMeta = vm.AiReportMetaText;
            var completedFindings = vm.AllIssues.ToArray();
            foreach (bool cancelled in new[] { false, true })
            {
                typeof(AIHubPageViewModel).GetField("_retainingAiReport", PrivateInstance).SetValue(vm, true);
                typeof(AIHubPageViewModel).GetField("_persistCurrentAudit", PrivateInstance).SetValue(vm, false);
                Invoke(vm, "CompleteAiInterruption", completed, cancelled);
                Require(vm.HasAiReport && vm.AiReportSummaryText == completedSummary && vm.AiReportMetaText == completedMeta &&
                    vm.AllIssues.SequenceEqual(completedFindings) && completed.AnalysisOutcome == "ok" && vm.IsAiSummaryCaution,
                    "Failed or cancelled regeneration must preserve the last complete report and its snapshot outcome.");
            }
            Invoke(vm, "RefreshAuditReportIdentity", "changed-scope");
            Require(Field<bool>(vm, "_auditReportNeedsRefresh"), "A changed model or policy scope must mark the report for update.");
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
                    var editor = (FrameworkElement)page.FindName("TaskPolicyEditorCard");
                    Require(Math.Abs(editor.ActualWidth - content.ActualWidth) < 2,
                        $"Policy editor card must fill the content column at {theme}/{width}.");
                }
                await Capture(page, $"aihub-{theme}-{width}-tab{tab}.png");
                if (tab == 1) await CheckInventoryLayout(page, theme, width);
            }
        }
        finally { root.RequestedTheme = themeBefore; }
        Log("Independent tab scroll, compact module header, fixed service status/navigation, accessible module toggle, localized 96-DIP score, readable content viewport, shared page margins, responsive policy columns, cached page and 18 theme/width/tab renders PASS");
    }

    private static async Task CheckEnableCardAndHealthLayout(AIHubPage page, string context)
    {
        await NextRendering();
        page.UpdateLayout();
        var header = (FrameworkElement)page.FindName("AiHubPageHeader");
        var title = (TextBlock)page.FindName("AiHubPageTitle");
        var description = (TextBlock)page.FindName("AiHubDescription");
        var gate = (FrameworkElement)page.FindName("AiHubModuleGate");
        var enableCard = (FrameworkElement)page.FindName("AiHubEnableSettingsCard");
        var fixedHeader = (FrameworkElement)page.FindName("AiHubFixedHeader");
        var master = (FrameworkElement)page.FindName("AiHubMasterCardSurface");
        var tabs = (FrameworkElement)page.FindName("AiHubSections");
        var scroll = (ScrollViewer)page.FindName("AiHubContentScroll");
        var toggles = Descendants(page).OfType<ToggleSwitch>()
            .Where(toggle => AutomationProperties.GetAutomationId(toggle) == "Toggle_AIHub").ToArray();
        Require(toggles.Length == 1 && IsDescendantOf(toggles[0], enableCard) && IsDescendantOf(enableCard, gate) && IsDescendantOf(gate, fixedHeader) &&
            toggles[0].IsOn == page.ViewModel.IsEnabled && toggles[0].IsEnabled == !page.ViewModel.IsEnabledGpoConfigured &&
            !string.IsNullOrWhiteSpace(AutomationProperties.GetName(toggles[0])),
            context + ": the single labeled module switch must reflect the module and policy gates.");
        Require(!string.IsNullOrWhiteSpace(title.Text) && ReferenceEquals(title.Style, Application.Current.Resources["TitleTextBlockStyle"]) &&
            AutomationProperties.GetHeadingLevel(title) == AutomationHeadingLevel.Level1 && title.Visibility == Visibility.Visible &&
            (description is null || description.Visibility == Visibility.Collapsed),
            context + ": the compact header must retain the localized title and heading semantics while omitting the introductory description.");
        var headerBounds = HeaderBounds(header, page);
        var enableCardBounds = HeaderBounds(enableCard, page);
        var readinessBounds = HeaderBounds(master, page);
        var tabBounds = HeaderBounds(tabs, page);
        var scrollBounds = HeaderBounds(scroll, page);
        Log($"HEADER {context}: page={page.ActualWidth:F1}x{page.ActualHeight:F1}, header={headerBounds}, enableCard={enableCardBounds}, readiness={readinessBounds}, tabs={tabBounds}, content={scrollBounds}, viewport={scroll.ViewportWidth:F1}x{scroll.ViewportHeight:F1}");
        Require(HeaderInside(headerBounds, page) && HeaderInside(enableCardBounds, page) && HeaderInside(readinessBounds, page) && HeaderInside(tabBounds, page) &&
            HeaderInside(HeaderBounds(toggles[0], enableCard), enableCard) &&
            (description is null || description.Visibility == Visibility.Collapsed) &&
            headerBounds.Bottom <= enableCardBounds.Top + 2 &&
            enableCardBounds.Bottom <= readinessBounds.Top + 2 && readinessBounds.Bottom <= tabBounds.Top + 2 && tabBounds.Bottom <= scrollBounds.Top + 2,
            context + ": header, module switch card, service status and tabs must remain visible without overlap.");
        Require(!IsDescendantOf(header, scroll) && !IsDescendantOf(enableCard, scroll) && !IsDescendantOf(master, scroll) && !IsDescendantOf(tabs, scroll) &&
            scroll.ViewportHeight >= 120 && HeaderInside(scrollBounds, page),
            context + ": the fixed compact header must leave at least 120 DIPs of reading space, including in short windows.");
        Require(page.ActualWidth >= 640 || (readinessBounds.Height <= 36 && scrollBounds.Top <= 215),
            context + ": narrow headers must keep readiness on one line and leave most of the window for results.");
        Require(toggles[0].Focus(FocusState.Keyboard) && HeaderInside(HeaderBounds(toggles[0], page), page),
            context + ": keyboard focus must reach the module switch without a second scroll region.");
        var settings = Descendants(master).OfType<HyperlinkButton>().Single();
        Require(settings.Focus(FocusState.Programmatic), context + ": service settings must remain keyboard accessible.");

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
            CheckScoreActivityLayout(page, context);
        }
    }

    private static void CheckScoreActivityLayout(AIHubPage page, string context)
    {
        var vm = page.ViewModel;
        var health = (FrameworkElement)page.FindName("AuditHealthSummary");
        var combined = (FrameworkElement)page.FindName("AuditScoreActivityGrid");
        var score = (FrameworkElement)page.FindName("AuditOverviewGrid");
        var activity = (FrameworkElement)page.FindName("AuditActivityCard");
        var period = (FrameworkElement)page.FindName("AuditPeriodOverview");
        Require(IsDescendantOf(score, combined) && IsDescendantOf(activity, combined) && IsDescendantOf(combined, health) &&
            !IsDescendantOf(activity, period) && health.Visibility == Visibility.Visible && combined.Visibility == Visibility.Visible &&
            activity.Visibility == Visibility.Visible && activity.ActualWidth > 0 && activity.ActualHeight > 0,
            context + ": audit activity must remain with the health score in overview, findings and empty states.");
        var scoreBounds = HeaderBounds(score, combined);
        var activityBounds = HeaderBounds(activity, combined);
        Require(HeaderInside(HeaderBounds(combined, health), health) && HeaderInside(scoreBounds, combined) && HeaderInside(activityBounds, combined),
            context + ": score and activity must stay inside their shared health surface without overflow.");
        Require((scoreBounds.Right <= activityBounds.Left - 1 && Math.Abs(scoreBounds.Top - activityBounds.Top) <= 2) ||
                (scoreBounds.Bottom <= activityBounds.Top - 1 && Math.Abs(scoreBounds.Left - activityBounds.Left) <= 2),
            $"{context}: score and activity must use aligned, non-overlapping columns or rows (content={combined.ActualWidth:F1}, score={scoreBounds}, activity={activityBounds}).");

        foreach (var item in new[]
        {
            (Name: "AuditActivityScansCell", Label: vm.AuditsLabel, Value: vm.ActivityScansText),
            (Name: "AuditActivityFindingsCell", Label: vm.RecordedFindingsLabel, Value: vm.ActivityFindingsText),
            (Name: "AuditActivityDaysCell", Label: vm.ActiveDaysLabel, Value: vm.ActiveDaysText),
            (Name: "AuditActivityHealthCell", Label: vm.HealthAverageLabel, Value: vm.HealthAverageText),
            (Name: "AuditMetaScanCell", Label: vm.ScanLabel, Value: vm.ScanTypeText),
            (Name: "AuditMetaFinishedCell", Label: vm.FinishedLabel, Value: vm.FinishedText),
            (Name: "AuditMetaWindowCell", Label: vm.WindowLabel, Value: vm.WindowText),
            (Name: "AuditMetaEventsCell", Label: vm.EventsReadLabel, Value: vm.EventCountText),
        })
        {
            var cell = (FrameworkElement)page.FindName(item.Name);
            var texts = Descendants(cell).OfType<TextBlock>().ToArray();
            Require(IsDescendantOf(cell, activity) && cell.Visibility == Visibility.Visible && cell.ActualWidth > 0 &&
                HeaderInside(HeaderBounds(cell, activity), activity) && texts.Length == 2 &&
                texts[0].Text == item.Label && texts[1].Text == item.Value,
                $"{context}: {item.Name} must retain its localized label and current activity value inside the activity area.");
            foreach (var text in texts.Where(text => !string.IsNullOrEmpty(text.Text)))
            {
                Require(text.Visibility == Visibility.Visible && text.ActualWidth > 0 && text.ActualHeight > 0 &&
                    !text.IsTextTrimmed && HeaderInside(HeaderBounds(text, cell), cell),
                    $"{context}: {item.Name} text '{text.Text}' must remain visible and untrimmed within its cell.");
                if (text.TextWrapping == TextWrapping.NoWrap)
                {
                    var fullText = new TextBlock
                    {
                        Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize,
                        FontWeight = text.FontWeight, FontStyle = text.FontStyle, FontStretch = text.FontStretch,
                        CharacterSpacing = text.CharacterSpacing, FlowDirection = text.FlowDirection,
                    };
                    fullText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    Require(fullText.DesiredSize.Width <= text.ActualWidth + 2,
                        $"{context}: {item.Name} text '{text.Text}' needs {fullText.DesiredSize.Width:F1} DIPs but only {text.ActualWidth:F1} are visible.");
                }
            }
        }
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
                var headerGrid = shared.FindName("PageHeaderGrid") as Grid;
                var introduction = shared.FindName("PageIntro") as SettingsPageIntro;
                var title = shared.FindName("Header") as TextBlock;
                var description = shared.FindName("CompactDescriptionText") as TextBlock;
                var links = shared.FindName("CompactPrimaryLinksControl") as ItemsControl;
                var content = (FrameworkElement)shared.FindName("ModuleContentPresenter");
                var scroll = Descendants(shared).OfType<ScrollViewer>().Single(item => AutomationProperties.GetAutomationId(item) == "PageScrollViewer");
                scroll.ChangeView(null, 0, null, true);
                await NextRendering();
                await NextRendering();
                string context = module.Key + "/" + theme;
                Require(shared.CompactHeader && headerGrid != null && introduction != null &&
                    introduction.Visibility == Visibility.Collapsed,
                    context + ": the unified module page must use the compact Fluent header and collapse the legacy banner.");
                Require(title != null && !string.IsNullOrWhiteSpace(title.Text) &&
                    title.Text == shared.ModuleTitle && ReferenceEquals(title.Style, Application.Current.Resources["TitleTextBlockStyle"]) &&
                    AutomationProperties.GetHeadingLevel(title) == AutomationHeadingLevel.Level1,
                    context + ": the compact module header must retain its localized title and heading style.");
                Require(description != null && !string.IsNullOrWhiteSpace(shared.ModuleDescription) &&
                    description.Text == shared.ModuleDescription && description.TextWrapping == TextWrapping.Wrap &&
                    description.Foreground is SolidColorBrush { Color: { A: > 0 } } &&
                    description.ActualWidth > 0 && description.ActualHeight > 0,
                    context + ": the compact module header must show readable secondary description text.");
                Require(links != null && ReferenceEquals(links.ItemsSource, shared.PrimaryLinks) &&
                    links.Visibility == (shared.PrimaryLinks.Count > 0 ? Visibility.Visible : Visibility.Collapsed),
                    context + ": compact primary links must reflect this page's collection without an empty link strip.");
                var toggle = Descendants(page).OfType<ToggleSwitch>().Single(item => AutomationProperties.GetAutomationId(item) == "Toggle_" + module.Key);
                var enableCard = (FrameworkElement)page.FindName(module.Key + "EnableSettingsCard");
                var headerBounds = HeaderBounds(headerGrid, page);
                var enableCardBounds = HeaderBounds(enableCard, page);
                var toggleBounds = HeaderBounds(toggle, page);
                var contentBounds = HeaderBounds(content, page);
                Require(!toggle.IsOn && IsDescendantOf(toggle, enableCard) && IsDescendantOf(enableCard, content) &&
                    !IsDescendantOf(title, scroll) &&
                    HeaderInside(headerBounds, page) && HeaderInside(enableCardBounds, page) && HeaderInside(toggleBounds, page) &&
                    headerBounds.Bottom <= enableCardBounds.Top + 2 &&
                    Math.Abs(headerBounds.Left - contentBounds.Left) < 2 &&
                    Math.Abs(enableCardBounds.Left - contentBounds.Left) < 2,
                    context + ": the compact header must sit cleanly above the top enable SettingsCard in the shared column.");
                Log($"COMPACT HEADER {context}: header={headerBounds.Width:F1}x{headerBounds.Height:F1}, description={description.ActualWidth:F1}x{description.ActualHeight:F1}, toggle={toggleBounds.Left:F1},{toggleBounds.Top:F1}");
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
        Log("Awake and LightSwitch unified compact Fluent header, description, primary links, and header toggle renders in both themes PASS");
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
