using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Contract;
using Kit.AIHubLib.Models;
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
        CheckBackupUnderDataDirectory();
        CheckAuditOutputContract();
        // The wrapper restores these files. Keep save checks independent of the user's endpoints.
        new Kit.AiHub.Storage.AiHubSettingsStore().Update(config =>
        {
            config.IsEnabled = true;
            config.SelectedKernel = "codex";
            config.Targets = Kit.AiHub.Models.AiHubConfig.CreateDefault().Targets;
            foreach (var target in config.Targets)
            {
                target.BaseUrl = "https://example.invalid/v1";
            }
        });
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

        int generalMessages = 0;
        ShellPage.SetDefaultSndMessageCallback(message =>
        {
            using var json = JsonDocument.Parse(message);
            if (json.RootElement.TryGetProperty("general", out _) && ++generalMessages <= 8)
            {
                // Model the Runner's reply, bounding a regression so the test can report it.
                App.GetSettingsWindow().DispatcherQueue.TryEnqueue(() => ShellPage.ShellHandler.SignalGeneralDataUpdate());
            }
        });
        NavigationService.Navigate(typeof(GeneralPage));
        await Task.Delay(200);
        var generalPage = (GeneralPage)NavigationService.Frame.Content;
        var refreshedProperties = new List<string>();
        generalPage.ViewModel.PropertyChanged += (_, args) => refreshedProperties.Add(args.PropertyName);
        generalPage.RefreshEnabledState();
        generalPage.ViewModel.NotifyAllBackupAndRestoreProperties();
        await Task.Delay(1200);
        if (generalMessages != 0) throw new InvalidOperationException("General/backup status refresh echoed settings to the Runner: " + generalMessages);
        if (!refreshedProperties.Contains(nameof(GeneralViewModel.EnableDataDiagnostics)) ||
            !refreshedProperties.Contains(nameof(GeneralViewModel.LastSettingsBackupDate)) ||
            !refreshedProperties.Contains(nameof(GeneralViewModel.CurrentSettingMatchText)))
            throw new InvalidOperationException("General/backup status refresh did not notify the UI.");
        generalPage.ViewModel.EnableWarningsElevatedApps = !generalPage.ViewModel.EnableWarningsElevatedApps;
        await Task.Delay(1200);
        if (generalMessages != 1) throw new InvalidOperationException("One settings edit should produce one command after the Runner reply, got " + generalMessages);
        File.AppendAllText(Report, "General/backup status: UI notifications preserved, no IPC echoes, one command per settings edit.\n");
        var aiService = generalPage.ViewModel.AiServices;
        if (!aiService.CanToggle) throw new InvalidOperationException("The shared AI service toggle is disabled.");
        File.AppendAllText(Report, "General page AI service toggle: available.\n");
        await CheckAiServiceControls((GeneralPage)NavigationService.Frame.Content);
        if (generalMessages != 1) throw new InvalidOperationException("AI service toggles restarted the general-settings feedback loop.");
        await CheckAiHubPage(repository);
    }

    private static void CheckBackupUnderDataDirectory()
    {
        string parent = Path.Combine(Path.GetTempPath(), "Kit.Backup.Tests");
        string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(root, "Backup");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(root, "settings.json"), "{\"fixture\":true}");
        // A nested backup must neither be read nor included, even if it contains invalid JSON.
        File.WriteAllText(Path.Combine(backup, "settings.json"), "not json");
        try
        {
            var service = SettingsBackupAndRestoreUtils.Instance;
            var preview = service.BackupSettings(root, backup, dryRun: true);
            if (!preview.Success || Directory.GetFiles(backup, "*.ptb").Length != 0)
            {
                throw new InvalidOperationException("Backup preview must succeed without writing an archive.");
            }

            var result = service.BackupSettings(root, backup, dryRun: false);
            if (!result.Success)
            {
                throw new InvalidOperationException("Backup under the app data directory failed: " + result.Message);
            }

            string archive = Directory.GetFiles(backup, "*.ptb").Single();
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count != 2 || zip.GetEntry("settings.json") == null || zip.GetEntry("manifest.json") == null)
                {
                    throw new InvalidOperationException("The backup must contain settings and manifest only.");
                }
            }

            var unchanged = service.BackupSettings(root, backup, dryRun: true);
            if (unchanged.Success || !unchanged.LastBackupExists || unchanged.Message != "General_SettingsBackupAndRestore_NothingToBackup")
            {
                throw new InvalidOperationException("Backup comparison must use the selected backup directory.");
            }

            if (service.BackupSettings(root, root, dryRun: true).Success ||
                service.BackupSettings(root, Path.Combine(root, "Settings"), dryRun: true).Success)
            {
                throw new InvalidOperationException("Only the dedicated Backup subtree is allowed inside app data.");
            }

            File.AppendAllText(Report, "Backup: in-data archive, recursion exclusion, dry run and previous-backup comparison PASS\n");
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved) != Path.GetFullPath(parent) || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
            {
                throw new InvalidOperationException("Unexpected backup fixture path.");
            }

            Directory.Delete(resolved, recursive: true);
        }
    }

    private static void CheckAuditOutputContract()
    {
        var validate = typeof(AiHubAuditAnalysisService).GetMethod("ValidateOutput", BindingFlags.Static | BindingFlags.NonPublic);
        var readResult = typeof(AiHubAuditAnalysisService).GetMethod("ReadAnalysisResult", BindingFlags.Static | BindingFlags.NonPublic);
        var ids = new HashSet<string>(StringComparer.Ordinal) { "item-000001" };
        var issue = new AuditIssue
        {
            Key = "fixture", EventRef = "item-000001", EventId = "1000", Severity = "Medium", Occurrences = 1,
            RelatedEventRefs = new List<string> { "item-000001" },
            Title = "Fixture title", TitleZh = "测试标题", Description = "Fixture evidence", DescriptionZh = "测试证据",
            RootCause = "Unknown cause", RootCauseZh = "原因未知", Recommendation = "Review evidence", RecommendationZh = "检查证据",
        };
        var output = new AuditIssueContainer { Issues = new List<AuditIssue> { issue } };
        string validJson = JsonSerializer.Serialize(output, AIHubLibJsonContext.Default.AuditIssueContainer);
        if (!(bool)validate.Invoke(null, new object[] { output, ids })) throw new InvalidOperationException("Valid audit evidence was rejected.");
        foreach (Action<AuditIssue> invalidate in new Action<AuditIssue>[]
        {
            item => item.EventRef = "event-3",
            item => item.RelatedEventRefs = new List<string> { "unknown-reference" },
            item => item.RelatedEventRefs.Clear(),
            item => item.Severity = "medium",
            item => item.Occurrences = 0,
            item => item.Key = new string('x', 49),
            item => item.TitleZh = string.Empty,
            item => item.Recommendation = new string('x', 8193),
        })
        {
            var invalid = JsonSerializer.Deserialize(validJson, AIHubLibJsonContext.Default.AuditIssueContainer);
            invalidate(invalid.Issues[0]);
            if ((bool)validate.Invoke(null, new object[] { invalid, ids })) throw new InvalidOperationException("Invalid audit evidence passed validation.");
        }

        var partial = new AiTaskResult<AuditIssueContainer>
        {
            IsSuccess = false, ErrorCode = AiErrorCode.PolicyViolation, Payload = output, CompletedBatches = 1, TotalBatches = 7,
        };
        var retained = (AiHubAuditAnalysisService.AuditAnalysisResult)readResult.Invoke(null, new object[] { partial, "smoke" });
        if (retained.Issues.Count != 1 || !retained.IsPartial || !retained.Failure.Contains("1/7"))
            throw new InvalidOperationException("Partial audit results or their incomplete status were lost.");
        var failed = new AiTaskResult<AuditIssueContainer> { ErrorCode = AiErrorCode.EndpointFailed, TotalBatches = 7 };
        var rejected = (AiHubAuditAnalysisService.AuditAnalysisResult)readResult.Invoke(null, new object[] { failed, "smoke" });
        if (rejected.Issues != null || rejected.IsPartial || rejected.Failure == null)
            throw new InvalidOperationException("Failed audit result was presented as usable.");
        var successful = new AiTaskResult<AuditIssueContainer> { IsSuccess = true, Payload = output, CompletedBatches = 7, TotalBatches = 7 };
        var completed = (AiHubAuditAnalysisService.AuditAnalysisResult)readResult.Invoke(null, new object[] { successful, "smoke" });
        if (completed.IsPartial || completed.Failure != null || !retained.IsPartial || !retained.Failure.Contains("1/7") || rejected.Failure == null)
            throw new InvalidOperationException("Successful audit retained stale failure state.");
        File.AppendAllText(Report, "Audit output: real reference/bilingual/severity/size validation, partial retention and failure reset passed.\n");
    }

    private static async Task CheckAuditWorkflowStates(AIHubPage page)
    {
        var vm = page.ViewModel;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(AIHubPageViewModel);
        var model = (ScanProgressModel)type.GetField("_auditProgress", instance).GetValue(vm);
        var optimizationModel = (ScanProgressModel)type.GetField("_scanProgress", instance).GetValue(vm);
        int previousTab = vm.ActiveTabIndex;
        var previousAudit = type.GetField("_auditCts", instance).GetValue(vm);
        var previousOptimization = type.GetField("_optimizationCts", instance).GetValue(vm);
        var progressFields = new[] { "_auditDetailedPercent", "_auditWasDeepAnalysis", "_auditIncomplete" }
            .ToDictionary(name => name, name => type.GetField(name, instance).GetValue(vm));
        using var auditCancellation = new CancellationTokenSource();
        using var optimizationCancellation = new CancellationTokenSource();
        using var nextOptimizationCancellation = new CancellationTokenSource();
        try
        {
            vm.ActiveTabIndex = 0;
            type.GetField("_auditCts", instance).SetValue(vm, auditCancellation);
            type.GetField("_optimizationCts", instance).SetValue(vm, optimizationCancellation);
            type.GetProperty(nameof(vm.IsAuditing)).SetValue(vm, true);
            type.GetProperty(nameof(vm.AuditPhase)).SetValue(vm, ScanPhase.Scanning);
            model.Reset();
            type.GetField("_auditWasDeepAnalysis", instance).SetValue(vm, false);
            type.GetField("_auditIncomplete", instance).SetValue(vm, false);
            type.GetMethod("BeginStage", instance).Invoke(vm, new object[] { "Collect event logs", true });
            type.GetMethod("ApplyAuditCollectionProgress", instance).Invoke(vm, new object[]
            {
                new Kit.AIHubLib.Services.EventCollectionProgress("Application", 2, 4, 12, 32, true, false),
            });
            await Task.Delay(100);
            var bar = (ProgressBar)page.FindName("AuditWorkflowProgress");
            var status = (TextBlock)page.FindName("AuditWorkflowStatus");
            if (bar.IsIndeterminate || Math.Abs(bar.Value - 15) > 0.01 || status.Text != vm.AuditStatusText || !status.Text.Contains("2/4"))
                throw new InvalidOperationException("Collected channel counts are not bound to the shared audit progress bar.");
            model.CompleteStage("Collect event logs");
            type.GetMethod("BeginStage", instance).Invoke(vm, new object[] { "Analyze findings", true });
            type.GetMethod("ApplyAuditAiProgress", instance).Invoke(vm, new object[] { new AiTaskProgress("Prepare", "Preparing analysis", 0, 0) });

            var captured = new CapturedProgress();
            var progressType = typeof(AiHubAuditAnalysisService).GetNestedType("AuditProgress", BindingFlags.NonPublic);
            var progress = (IProgress<AiTaskProgress>)Activator.CreateInstance(progressType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: new object[] { "smoke", Stopwatch.StartNew(), captured }, culture: null);
            progress.Report(new AiTaskProgress("Route", "batch=1/7, AI native event: request=request-fixture, event=retry, source=stderr, code=EndpointFailed, httpStatus=0, reason=StreamDisconnected", 1, 7));
            if (captured.Value.Stage != "Route" || captured.Value.CompletedBatches != 1 || captured.Value.TotalBatches != 7)
                throw new InvalidOperationException("Audit progress translation lost its typed phase or batch counts.");
            if (!captured.Message.Contains("1/7") || captured.Message.Contains("request-fixture") || captured.Message.Contains("StreamDisconnected"))
                throw new InvalidOperationException("Audit retry progress omitted batch counts or exposed diagnostic details.");
            if (!captured.Message.Contains("Retrying connection") && !captured.Message.Contains("连接重试中"))
                throw new InvalidOperationException("Batch-prefixed native retry diagnostics did not reach the audit status.");
            progress.Report(new AiTaskProgress("Failover", "batch=1/7, request=request-fixture, reason=RouteTimeout", 1, 7));
            if ((!captured.Message.Contains("Main did not complete; using fallback") && !captured.Message.Contains("主链未完成，正在使用备用链路"))
                || !captured.Message.Contains("1/7") || captured.Message.Contains("request-fixture") || captured.Message.Contains("RouteTimeout")
                || captured.Message.Contains("Retrying connection") || captured.Message.Contains("连接重试中"))
                throw new InvalidOperationException("Audit failover status omitted the fallback message or retained retry/diagnostic details.");
            type.GetProperty(nameof(vm.AuditStatusText)).SetValue(vm, captured.Message);
            await Task.Delay(100);
            if (!bar.IsIndeterminate || vm.SidebarWorkflowPercentText == "50%" || status.Text != captured.Message)
                throw new InvalidOperationException("AI audit progress is still fixed at 50% or its batch status is not bound.");
            var applyAiProgress = type.GetMethod("ApplyAuditAiProgress", instance);
            applyAiProgress.Invoke(vm, new object[] { captured.Value });
            await Task.Delay(100);
            if (bar.IsIndeterminate || Math.Abs(bar.Value - (30 + (65.0 / 7))) > 0.01 || status.Text != captured.Message)
                throw new InvalidOperationException("Known AI batch counts did not replace indeterminate progress.");
            applyAiProgress.Invoke(vm, new object[] { new AiTaskProgress("Validate", "Validated 3/7", 3, 7) });
            double validatedPercent = vm.SidebarWorkflowPercent;
            applyAiProgress.Invoke(vm, new object[] { new AiTaskProgress("Queue", "Queued batch", 1, 7) });
            if (vm.SidebarWorkflowPercent != validatedPercent || validatedPercent <= 30 || validatedPercent >= 100)
                throw new InvalidOperationException("Out-of-order batch progress regressed or prematurely completed the audit.");

            type.GetProperty(nameof(vm.IsAuditing)).SetValue(vm, false);
            type.GetField("_auditWasDeepAnalysis", instance).SetValue(vm, true);
            type.GetProperty(nameof(vm.IsAiAnalyzing)).SetValue(vm, true);
            model.Reset();
            model.CompleteStage("Collect event logs");
            type.GetMethod("BeginStage", instance).Invoke(vm, new object[] { "Analyze findings", true });
            await Task.Delay(100);
            if (!bar.IsIndeterminate || !vm.SidebarWorkflowVisible)
                throw new InvalidOperationException("Deep analysis with unknown batch count did not use the shared indeterminate bar.");
            applyAiProgress.Invoke(vm, new object[] { new AiTaskProgress("Validate", "Deep analysis 2/4", 2, 4) });
            type.GetField("_auditIncomplete", instance).SetValue(vm, true);
            type.GetProperty(nameof(vm.AuditPhase)).SetValue(vm, ScanPhase.SelectingTargets);
            type.GetProperty(nameof(vm.IsAiAnalyzing)).SetValue(vm, false);
            await Task.Delay(100);
            if (bar.IsIndeterminate || Math.Abs(bar.Value - 47.5) > 0.01 ||
                (vm.SidebarWorkflowTitle != "Partially completed" && vm.SidebarWorkflowTitle != "部分完成"))
                throw new InvalidOperationException("Partial deep analysis was displayed as fully complete.");
            type.GetField("_auditIncomplete", instance).SetValue(vm, false);
            type.GetMethod("EndStage", instance).Invoke(vm, new object[] { "Analyze findings", true });
            type.GetMethod("EndStage", instance).Invoke(vm, new object[] { "Finish", true });
            await Task.Delay(100);
            if (bar.Value != 100 || bar.IsIndeterminate ||
                (vm.SidebarWorkflowTitle != "AI analysis complete" && vm.SidebarWorkflowTitle != "AI 分析完成"))
                throw new InvalidOperationException("Successful deep analysis did not complete the shared bar.");
            type.GetMethod("BeginStage", instance).Invoke(vm, new object[] { "Analyze findings", true });
            type.GetField("_auditIncomplete", instance).SetValue(vm, true);
            type.GetProperty(nameof(vm.AuditPhase)).SetValue(vm, ScanPhase.Failed);
            await Task.Delay(100);
            if (bar.Value >= 100 || bar.IsIndeterminate || vm.SidebarWorkflowGlyph == "\uE73E")
                throw new InvalidOperationException("Failed deep analysis retained success progress or its success glyph.");

            type.GetField("_auditWasDeepAnalysis", instance).SetValue(vm, false);
            type.GetField("_auditIncomplete", instance).SetValue(vm, false);
            type.GetProperty(nameof(vm.IsAuditing)).SetValue(vm, true);
            type.GetProperty(nameof(vm.AuditPhase)).SetValue(vm, ScanPhase.Scanning);
            model.Reset();
            model.CompleteStage("Collect event logs");
            type.GetMethod("BeginStage", instance).Invoke(vm, new object[] { "Analyze findings", true });
            if (!vm.ScanAllCommand.CanExecute(null) || !vm.ScanDownloadsCommand.CanExecute(null) || !vm.ScanTemporaryFilesCommand.CanExecute(null))
                throw new InvalidOperationException("A running audit still disables Optimization scans.");
            type.GetProperty(nameof(vm.SelectedCandidatesCount)).SetValue(vm, 1);
            if (!vm.CanExecuteOptimization)
                throw new InvalidOperationException("A running audit still disables confirmed Optimization execution.");
            type.GetProperty(nameof(vm.SelectedCandidatesCount)).SetValue(vm, 0);

            type.GetProperty(nameof(vm.IsOptimizing)).SetValue(vm, true);
            type.GetProperty(nameof(vm.IsAuditing)).SetValue(vm, false);
            type.GetProperty(nameof(vm.IsAiAnalyzing)).SetValue(vm, false);
            if (!vm.FastScanCommand.CanExecute(null) || !vm.FullScanCommand.CanExecute(null))
                throw new InvalidOperationException("A running Optimization still disables audit commands.");
            type.GetProperty(nameof(vm.IsAuditing)).SetValue(vm, true);
            type.GetMethod("ResetScanProgress", instance).Invoke(vm, null);
            type.GetMethod("BeginStage", instance).Invoke(vm, new object[] { "Scan Downloads", false });
            type.GetProperty(nameof(vm.ScanPhase)).SetValue(vm, ScanPhase.Scanning);
            if (model.CurrentStage != "Analyze findings" || vm.AuditPhase != ScanPhase.Scanning || !vm.IsAuditProgressIndeterminate)
                throw new InvalidOperationException("Optimization startup overwrote audit progress.");

            vm.CancelOptimizationCommand.Execute(null);
            if (!optimizationCancellation.IsCancellationRequested || auditCancellation.IsCancellationRequested)
                throw new InvalidOperationException("Optimization cancellation reached the audit lifetime.");
            type.GetMethod("StopOptimization", instance).Invoke(vm, new object[] { true });
            if (!vm.IsAuditing || vm.AuditPhase != ScanPhase.Scanning || model.CurrentStage != "Analyze findings")
                throw new InvalidOperationException("Stopping Optimization changed the audit state.");

            type.GetField("_optimizationCts", instance).SetValue(vm, nextOptimizationCancellation);
            type.GetMethod("BeginStage", instance).Invoke(vm, new object[] { "Scan Downloads", false });
            type.GetProperty(nameof(vm.ScanPhase)).SetValue(vm, ScanPhase.Scanning);
            vm.CancelAuditCommand.Execute(null);
            if (!auditCancellation.IsCancellationRequested || nextOptimizationCancellation.IsCancellationRequested)
                throw new InvalidOperationException("Audit cancellation reached the Optimization lifetime.");
            type.GetMethod("StopAudit", instance).Invoke(vm, new object[] { true });
            await Task.Delay(100);
            if (vm.IsAuditing || bar.IsIndeterminate || vm.SidebarWorkflowPercent != 0 || vm.AuditPhase != ScanPhase.Idle)
                throw new InvalidOperationException("Cancelled audit retained a scanning state.");
            if (!vm.IsOptimizing || !vm.IsScanning || optimizationModel.CurrentStage != "Scan Downloads")
                throw new InvalidOperationException("Stopping the audit changed Optimization progress.");
            type.GetProperty(nameof(vm.IsAuditing)).SetValue(vm, true);
            type.GetProperty(nameof(vm.AuditPhase)).SetValue(vm, ScanPhase.Scanning);
            type.GetMethod("StopAudit", instance).Invoke(vm, new object[] { false });
            if (vm.IsAuditing || vm.AuditPhase != ScanPhase.Failed || vm.StatusSeverity != InfoBarSeverity.Error || !vm.IsScanning || vm.SidebarWorkflowPercent >= 100)
                throw new InvalidOperationException("Audit failure changed the independent Optimization phase.");

            foreach (string outcome in new[] { "failed", "partial", "ok" })
            {
                type.GetMethod("FinishAuditStatus", instance).Invoke(vm, new object[] { 80, 8, outcome, Array.Empty<string>() });
                var expected = outcome == "ok" ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
                if (vm.StatusSeverity != expected || vm.StatusMessage != vm.AuditStatusText)
                    throw new InvalidOperationException("Audit completion overwrote an AI failure or partial warning.");
            }

            type.GetMethod("ShowStatus", instance).Invoke(vm, new object[] { "audit result", InfoBarSeverity.Warning, false });
            type.GetMethod("ShowStatus", instance).Invoke(vm, new object[] { "optimization result", InfoBarSeverity.Success, true });
            if (vm.StatusMessage != "audit result" || vm.StatusSeverity != InfoBarSeverity.Warning)
                throw new InvalidOperationException("Background Optimization overwrote the audit banner.");
            vm.ActiveTabIndex = 1;
            if (vm.StatusMessage != "optimization result" || vm.StatusSeverity != InfoBarSeverity.Success || !vm.IsStatusOpen)
                throw new InvalidOperationException("Optimization banner was not retained when switching tabs.");
            vm.IsStatusOpen = false;
            vm.ActiveTabIndex = 0;
            if (vm.StatusMessage != "audit result" || !vm.IsStatusOpen)
                throw new InvalidOperationException("Closing the Optimization banner closed the audit banner.");
            File.AppendAllText(Report, "Audit UI: bound channel/batch progress, shared deep-analysis unknown/partial/success/failure states, retry/failover status, concurrent commands, and independent cancellation/progress/banners passed.\n");
        }
        finally
        {
            type.GetField("_auditCts", instance).SetValue(vm, previousAudit);
            type.GetField("_optimizationCts", instance).SetValue(vm, previousOptimization);
            type.GetProperty(nameof(vm.IsAuditing)).SetValue(vm, false);
            type.GetProperty(nameof(vm.IsAiAnalyzing)).SetValue(vm, false);
            type.GetProperty(nameof(vm.IsOptimizing)).SetValue(vm, false);
            type.GetProperty(nameof(vm.SelectedCandidatesCount)).SetValue(vm, 0);
            type.GetProperty(nameof(vm.AuditPhase)).SetValue(vm, ScanPhase.Idle);
            type.GetProperty(nameof(vm.ScanPhase)).SetValue(vm, ScanPhase.Idle);
            type.GetProperty(nameof(vm.AuditStatusText)).SetValue(vm, string.Empty);
            type.GetProperty(nameof(vm.OptimizationWorkflowStatus)).SetValue(vm, string.Empty);
            model.Reset();
            type.GetMethod("ResetScanProgress", instance).Invoke(vm, null);
            vm.ActiveTabIndex = 1;
            vm.IsStatusOpen = false;
            vm.ActiveTabIndex = 0;
            vm.IsStatusOpen = false;
            vm.ActiveTabIndex = previousTab;
            foreach (var field in progressFields) type.GetField(field.Key, instance).SetValue(vm, field.Value);
            type.GetMethod("PublishScanProgress", instance).Invoke(vm, null);
        }
    }

    private static async Task CheckSelectedAuditRange(AIHubPage page)
    {
        var vm = page.ViewModel;
        var type = typeof(AIHubPageViewModel);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var fields = new[] { "_auditCts", "_selectedScopeIndex", "_hasAuditResult", "_lastCompletedAuditUtc", "_auditDetailedPercent", "_auditWasDeepAnalysis", "_auditIncomplete" }
            .ToDictionary(name => name, name => type.GetField(name, instance).GetValue(vm));
        var properties = new[]
        {
            "HealthScoreGrade", "HighCount", "MediumCount", "LowCount", "ActivityFindingsText", "EventCountText", "FinishedText", "WindowText",
            "ScanTypeText", "HealthVerdict", "HealthSummaryText", "AuditTimestampText", "AiReportSummaryText", "AiReportMetaText", "AiStatusText",
            "AiAnalysisRanText", "AuditStatusText", "HasAiReport", "IsAuditing", "IsAiAnalyzing", "AuditPhase",
        }.ToDictionary(name => name, name => type.GetProperty(name).GetValue(vm));
        var rawEvents = (List<SecurityEvent>)type.GetField("_lastAuditEvents", instance).GetValue(vm);
        var previousEvents = rawEvents.ToArray();
        var previousIssues = vm.AllIssues.ToArray();
        var previousReport = vm.AiReportFindings.ToArray();
        var schedule = (DispatcherQueueTimer)type.GetField("_scheduleTimer", instance).GetValue(vm);
        bool scheduled = schedule.IsRunning;
        var history = type.GetField("_auditHistoryStorage", instance).GetValue(vm);
        string historyPath = (string)history.GetType().GetField("_storagePath", instance).GetValue(history);
        byte[] historyBefore = File.Exists(historyPath) ? File.ReadAllBytes(historyPath) : null;
        var pending = new CancellationTokenSource();
        try
        {
            schedule.Stop();
            type.GetField("_auditCts", instance).SetValue(vm, pending);
            type.GetField("_selectedScopeIndex", instance).SetValue(vm, 0);
            type.GetProperty(nameof(vm.IsAiAnalyzing)).SetValue(vm, true);
            foreach (int range in new[] { 1, 2 })
            {
                vm.AllIssues.Clear();
                vm.AllIssues.Add(new AuditIssueEnhanced { Title = "Previous range finding", Severity = "High", EventId = "1000", LogName = "Application" });
                rawEvents.Clear();
                rawEvents.Add(new SecurityEvent(1000, "Application", "Range fixture", 2, DateTime.UtcNow, "Previous range evidence"));
                vm.AiReportFindings.Clear();
                vm.AiReportFindings.Add(new AiReportFinding("Previous report", "High", "Previous evidence", "Review", "1000"));
                type.GetField("_hasAuditResult", instance).SetValue(vm, true);
                type.GetProperty(nameof(vm.HasAiReport)).SetValue(vm, true);
                CancellationToken oldToken = pending.Token;
                vm.FullScanRangeIndex = range;
                pending = (CancellationTokenSource)type.GetField("_auditCts", instance).GetValue(vm);
                if (!oldToken.IsCancellationRequested || pending.Token == oldToken || !vm.IsAuditing || vm.IsAiAnalyzing ||
                    vm.AllIssues.Count != 0 || rawEvents.Count != 0 || vm.AiReportFindings.Count != 0 || vm.HasAiReport || vm.HasAuditData ||
                    vm.WindowText != vm.ScanRangeDescription || vm.HighCount != 0 || vm.MediumCount != 0 || vm.LowCount != 0)
                    throw new InvalidOperationException("Changing the top range did not cancel and clear the previous audit before collecting the new range.");
            }

            // Cancel before yielding the UI thread: this only exercises local collection,
            // and no queued completion may save history or start an AI request.
            vm.CancelAuditCommand.Execute(null);
            for (int attempt = 0; attempt < 50 && vm.IsAuditing; attempt++) await Task.Delay(100);
            if (vm.IsAuditing || vm.IsAiAnalyzing || vm.HasAuditData || rawEvents.Count != 0 || vm.AiReportFindings.Count != 0 ||
                !Equals(type.GetField("_lastCompletedAuditUtc", instance).GetValue(vm), fields["_lastCompletedAuditUtc"]) ||
                vm.WindowText != vm.ScanRangeDescription || vm.AuditPhase != ScanPhase.Idle)
                throw new InvalidOperationException("Cancelled range collection published stale results or advanced the audit history cursor.");
            byte[] historyAfter = File.Exists(historyPath) ? File.ReadAllBytes(historyPath) : null;
            if ((historyBefore == null) != (historyAfter == null) || (historyBefore != null && !historyBefore.SequenceEqual(historyAfter)))
                throw new InvalidOperationException("Changing the top range wrote an audit history entry.");
            File.AppendAllText(Report, "Audit range: rapid 1-week/1-month changes cancel old lifetimes, clear findings/evidence/AI report, update the window, and leave history unchanged after cancellation.\n");
        }
        finally
        {
            pending.Cancel();
            pending.Dispose();
            foreach (var field in fields) type.GetField(field.Key, instance).SetValue(vm, field.Value);
            vm.AllIssues.Clear();
            foreach (var issue in previousIssues) vm.AllIssues.Add(issue);
            rawEvents.Clear();
            rawEvents.AddRange(previousEvents);
            vm.AiReportFindings.Clear();
            foreach (var finding in previousReport) vm.AiReportFindings.Add(finding);
            foreach (var property in properties) type.GetProperty(property.Key).SetValue(vm, property.Value);
            ((ScanProgressModel)type.GetField("_auditProgress", instance).GetValue(vm)).Reset();
            type.GetMethod("RebuildSections", instance).Invoke(vm, new object[] { false });
            type.GetMethod("PublishScanProgress", instance).Invoke(vm, null);
            vm.IsStatusOpen = false;
            if (scheduled) schedule.Start();
        }
    }

    private sealed class CapturedProgress : IProgress<AiTaskProgress>
    {
        public AiTaskProgress Value { get; private set; } = new(string.Empty, string.Empty, 0, 0);

        public string Message => Value.StatusMessage;

        public void Report(AiTaskProgress value) => Value = value;
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
        await CheckAuditWorkflowStates(page);
        await CheckSelectedAuditRange(page);
        await CheckAuditUxStates(page);
        await CheckOptimizationCleanup(page);
        if (vm.AiHub.ActivePolicyIndex != 1) throw new InvalidOperationException("Task policy editor initially selects the global policy.");

        await CheckAiHubThemes(page);

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
        File.AppendAllText(Report, "AI Hub: native-style plugin writeback, policy selection/save feedback, disabled re-check, service navigation, and themed layout renders passed.\n");
    }

    private static async Task CheckOptimizationCleanup(AIHubPage page)
    {
        var vm = page.ViewModel;
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Kit.Optimization.UI.Tests"));
        string fixture = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string old = Path.Combine(fixture, "old.tmp");
        string locked = Path.Combine(fixture, "locked.tmp");
        var refresh = typeof(AIHubPageViewModel).GetMethod("RecalculateCandidateMetrics", BindingFlags.Instance | BindingFlags.NonPublic);
        var observed = (List<TempFileInfo>)typeof(AIHubPageViewModel).GetField("_observedOptimizationItems", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(vm);
        var organizerField = typeof(AIHubPageViewModel).GetField("_downloadOrganizerService", BindingFlags.Instance | BindingFlags.NonPublic);
        var originalOrganizer = organizerField.GetValue(vm);
        try
        {
            vm.ActiveTabIndex = 1;
            organizerField.SetValue(vm, Activator.CreateInstance(typeof(Kit.AIHubLib.Services.DownloadOrganizerService), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { fixture }, null));
            foreach (string path in new[] { old, locked })
            {
                File.WriteAllText(path, "fixture");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
                var file = new FileInfo(path);
                vm.OptimizationCandidates.Add(new TempFileInfo { FilePath = path, FileName = file.Name, CacheRoot = fixture, Category = "Temporary files", Action = "delete", SizeInBytes = file.Length, LastModified = file.LastWriteTimeUtc, ReviewState = "approved" });
            }
            string reportPath = Path.Combine(fixture, "report.pdf");
            File.WriteAllText(reportPath, "fixture");
            File.SetLastWriteTimeUtc(reportPath, DateTime.UtcNow.AddDays(-10));
            var reportFile = new FileInfo(reportPath);
            var download = new TempFileInfo { FilePath = reportPath, FileName = "report.pdf", Category = "Documents", Action = "move", TargetRelativePath = "Documents", SizeInBytes = reportFile.Length, LastModified = reportFile.LastWriteTimeUtc, ReviewState = "approved", ReasonEn = "Document will be moved into Documents.", ReasonZh = "文档将移入 Documents 分类目录。" };
            vm.OptimizationCandidates.Add(download);
            observed.AddRange(vm.OptimizationCandidates);
            observed.Add(new TempFileInfo { FilePath = Path.Combine(fixture, "unreviewed.tmp"), FileName = "unreviewed.tmp", CacheRoot = fixture, Category = "NuGet packages", Action = "delete", ReviewState = "failed", SizeInBytes = 2000000000, ReasonEn = "Review timed out. This file cannot be cleaned." });
            foreach (var entry in new[] { ("C++ precompiled headers", "Example/x64/Debug", 18000000000L), ("VS Code CachedData", "Code/CachedData", 200000000L), ("Edge cache", "Edge/Default/Cache", 100000000L) })
            {
                string scope = Path.Combine(fixture, entry.Item2.Replace('/', Path.DirectorySeparatorChar));
                observed.Add(new TempFileInfo { FilePath = Path.Combine(scope, "cache"), FileName = "cache", CacheRoot = scope, Category = entry.Item1, DevelopmentRepository = entry.Item1.StartsWith("C++", StringComparison.Ordinal) ? Path.Combine(fixture, "Example") : string.Empty, Action = "delete", ReviewState = "skipped", SizeInBytes = entry.Item3 });
            }
            refresh.Invoke(vm, null);
            await Task.Delay(100);
            if (vm.OptimizationCategories.Count != 6 || vm.DownloadCategories.Count != 1 || vm.CacheCategories.Count != 5 || !vm.CleanAllCommand.CanExecute(null)) throw new InvalidOperationException("Optimization category binding or clean-all availability failed.");
            if (!vm.CacheGroups.Select(group => group.Family).SequenceEqual(new[] { "development", "packages", "editors", "browsers", "system" })) throw new InvalidOperationException("The five broad cache groups are missing or unordered.");
            if (vm.CacheGroups.Sum(group => group.ApprovedCount) != 2 || vm.CacheGroups.Any(group => group.ApprovedCount == 0 && group.IsEnabled) || vm.CacheCategories.Any(group => group.ApprovedCount == 0 && group.IsEnabled)) throw new InvalidOperationException("AI failure enabled cleanup or group totals are incorrect.");
            if (!Descendants(page).OfType<Button>().Any(button => AutomationProperties.GetAutomationId(button) == "AIHub_ScanDownloads" && button.IsEnabled)) throw new InvalidOperationException("Independent Downloads scan is missing.");
            vm.UserDirectorySummaries.Add(@"C:\Users\Fixture\.dsh · 20 MB · State retained; no verified cache path.");
            typeof(AIHubPageViewModel).GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string) }, null).Invoke(vm, new object[] { nameof(vm.HasUserDirectorySummaries) });
            await Task.Delay(100);
            var inventory = Descendants(page).OfType<Expander>().Single(item => item.Header as string == vm.UserDirectoryLabel);
            if (inventory.Visibility != Visibility.Visible) throw new InvalidOperationException("User-directory inventory was hidden.");
            inventory.IsExpanded = true;
            await Task.Delay(300);
            if (!Descendants(inventory).OfType<TextBlock>().Any(item => item.Text.Contains(".dsh"))) throw new InvalidOperationException("Protected user directory is missing from the inventory.");
            await RenderPage(page, "aihub-user-directory-inventory.png");
            inventory.IsExpanded = false;
            await RenderPage(page, "aihub-optimization-categories.png");
            var details = Descendants(page).OfType<Expander>().First(expander => expander.DataContext is OptimizationCategoryViewModel);
            details.IsExpanded = true;
            await Task.Delay(500);
            if (!Descendants(details).OfType<TextBlock>().Any(text => text.Text.Contains(" → Documents"))) throw new InvalidOperationException("Downloads target is missing from expanded details.");
            await RenderPage(page, "aihub-optimization-details.png");
            details.IsExpanded = false;
            var development = Descendants(page).OfType<Expander>().Single(expander => expander.DataContext is OptimizationGroupViewModel group && group.Family == "development");
            development.IsExpanded = true;
            await Task.Delay(150);
            var projectGroup = vm.CacheGroups.Single(group => group.Family == "development").Projects.Single();
            if (projectGroup.Name != "Example" || projectGroup.Categories.Count != 1 || projectGroup.IsEnabled) throw new InvalidOperationException("Project aggregation or approval scope is incorrect.");
            Descendants(development).OfType<Expander>().Single(item => item.DataContext is OptimizationGroupViewModel group && group.Family == "project").IsExpanded = true;
            await Task.Delay(200);
            if (!Descendants(development).OfType<TextBlock>().Any(text => text.Text.Contains("Example\\x64\\Debug"))) throw new InvalidOperationException("Development output location is missing from its broad group.");
            await RenderPage(page, "aihub-optimization-development.png");
            var window = App.GetSettingsWindow().AppWindow;
            var originalSize = window.Size;
            var presenter = window.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
            presenter?.Restore();
            try
            {
                foreach (int width in new[] { 1050, 850 })
                {
                    window.Resize(new Windows.Graphics.SizeInt32(width, 1100));
                    await Task.Delay(250);
                    await RenderPage(page, $"aihub-optimization-development-{width}.png");
                }
            }
            finally
            {
                window.Resize(originalSize);
            }
            var stress = Enumerable.Range(0, 267).Select(index => new TempFileInfo
            {
                FilePath = Path.Combine(fixture, "Example", $"Module{index}", "Debug", "cache.pch"),
                FileName = "cache.pch",
                CacheRoot = Path.Combine(fixture, "Example", $"Module{index}", "Debug"),
                DevelopmentRepository = Path.Combine(fixture, "Example"),
                Category = "C++ precompiled headers",
                Action = "delete",
                ReviewState = "failed",
                SizeInBytes = 100000,
            }).ToList();
            observed.AddRange(stress);
            refresh.Invoke(vm, null);
            await Task.Delay(150);
            if (Descendants(page).OfType<Expander>().Count(item => item.DataContext is OptimizationCategoryViewModel) > 2)
                throw new InvalidOperationException("Collapsed cache groups eagerly created their child views.");
            var largeGroup = Descendants(page).OfType<Expander>().Single(item => item.DataContext is OptimizationGroupViewModel group && group.Family == "development");
            largeGroup.IsExpanded = true;
            await Task.Delay(150);
            var projectRow = Descendants(largeGroup).OfType<Expander>().Single(item => item.DataContext is OptimizationGroupViewModel group && group.Family == "project");
            if (((OptimizationGroupViewModel)projectRow.DataContext).Categories.Count != 268) throw new InvalidOperationException("Development locations were not consolidated by project.");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            projectRow.IsExpanded = true;
            await Task.Delay(150);
            int realized = Descendants(largeGroup).OfType<Expander>().Count(item => item.DataContext is OptimizationCategoryViewModel);
            if (realized == 0 || realized > 30 || timer.ElapsedMilliseconds > 5000)
                throw new InvalidOperationException($"Cache virtualization failed: {realized} rows, {timer.ElapsedMilliseconds} ms.");
            if (Descendants(largeGroup).OfType<ListView>().Count() != 2)
                throw new InvalidOperationException("Collapsed child rows eagerly created file lists.");
            File.AppendAllText(Report, $"Optimization virtualization: 268 locations, {realized} realized rows, {timer.ElapsedMilliseconds} ms including 150 ms settle.\n");
            await RenderPage(page, "aihub-optimization-many-projects.png");
            observed.RemoveAll(stress.Contains);
            refresh.Invoke(vm, null);
            await Task.Delay(100);
            var editors = Descendants(page).OfType<Expander>().Single(item => item.DataContext is OptimizationGroupViewModel group && group.Family == "editors");
            editors.IsExpanded = true;
            await Task.Delay(500);
            if (!Descendants(editors).OfType<Expander>().All(item => item.IsEnabled)) throw new InvalidOperationException("Unreviewed cache details cannot be inspected.");
            await RenderPage(page, "aihub-optimization-editors.png");
            using (var held = File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                vm.CleanAllCommand.Execute(null);
                if (!vm.IsExecutingOptimization || vm.CleanAllCommand.CanExecute(null)) throw new InvalidOperationException("Cleanup progress or command exclusion failed.");
                for (int attempt = 0; attempt < 100 && vm.IsOptimizing; attempt++) await Task.Delay(50);
                if (vm.IsOptimizing || vm.OptimizationProgress != 100 || File.Exists(old) || !File.Exists(locked)) throw new InvalidOperationException("Cleanup did not delete only the available snapshot.");
            }
            if (!vm.OptimizationCandidates.Contains(download) || vm.OptimizationCandidates.Count != 2) throw new InvalidOperationException("Clean all touched Downloads or discarded a failed candidate.");
            vm.CacheGroups.Single(group => group.Family == "system").ExecuteCommand.Execute(null);
            for (int attempt = 0; attempt < 100 && vm.IsOptimizing; attempt++) await Task.Delay(50);
            if (vm.IsOptimizing || File.Exists(locked)) throw new InvalidOperationException("Per-category cleanup retry failed.");
            vm.OrganizeDownloadsCommand.Execute(null);
            for (int attempt = 0; attempt < 100 && vm.IsOptimizing; attempt++) await Task.Delay(50);
            if (vm.IsOptimizing || File.Exists(reportPath) || !File.Exists(Path.Combine(fixture, "Documents", "report.pdf"))) throw new InvalidOperationException("Downloads organization failed.");
            if (vm.CacheCategories.Count != 4 || vm.CacheGroups.Any(group => group.IsEnabled) || vm.CleanAllCommand.CanExecute(null)) throw new InvalidOperationException("Unapproved results disappeared or enabled a write.");
            File.AppendAllText(Report, "Optimization: five broad groups, Debug output location, direct cleanup, clean-all excludes Downloads, progress, failed-item retention and group retry passed.\n");
        }
        finally
        {
            vm.UserDirectorySummaries.Clear();
            vm.OptimizationCandidates.Clear();
            observed.Clear();
            organizerField.SetValue(vm, originalOrganizer);
            refresh.Invoke(vm, null);
            vm.ActiveTabIndex = 0;
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(fixture)), parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid cleanup fixture boundary.");
            foreach (string path in new[] { old, locked, Path.Combine(fixture, "report.pdf"), Path.Combine(fixture, "Documents", "report.pdf") }) if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(Path.Combine(fixture, "Documents"))) Directory.Delete(Path.Combine(fixture, "Documents"), recursive: false);
            Directory.Delete(fixture, recursive: false);
        }
    }

    private static async Task CheckAiHubThemes(AIHubPage page)
    {
        var root = (FrameworkElement)App.GetSettingsWindow().Content;
        var previousTheme = root.RequestedTheme;
        var presenter = App.GetSettingsWindow().AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        presenter?.Restore();
        try
        {
            Windows.UI.Color[] firstDarkPalette = null;
            foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                page.ViewModel.ActiveTabIndex = 0;
                await Task.Delay(100);
                var palette = await CheckAiHubPalette(page, theme);
                if (theme == ElementTheme.Dark && firstDarkPalette != null)
                {
                    if (!palette.SequenceEqual(firstDarkPalette))
                        throw new InvalidOperationException("Cached AI Hub colors did not return to the original dark palette.");
                    NavigationService.Navigate(typeof(DashboardPage));
                    NavigationService.Navigate(typeof(AIHubPage));
                    await Task.Delay(100);
                    if (!ReferenceEquals(page, NavigationService.Frame.Content) || page.ActualTheme != theme)
                        throw new InvalidOperationException("Cached AI Hub lost its inherited theme on navigation.");
                    break;
                }
                if (theme == ElementTheme.Dark) firstDarkPalette = palette;
                foreach (int width in new[] { 1350, 1050, 850 })
                {
                    App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1000));
                    for (int tab = 0; tab < 3; tab++)
                    {
                        page.ViewModel.ActiveTabIndex = tab;
                        await Task.Delay(150);
                        foreach (var viewer in Descendants(page).OfType<ScrollViewer>()) viewer.ChangeView(null, 0, null, true);
                        await Task.Delay(100);
                        var summary = (FrameworkElement)page.FindName("AuditFindingsSummary");
                        var overview = (Grid)page.FindName("AuditOverviewGrid");
                        if (tab == 0 && Grid.GetRow(summary) != (overview.ActualWidth < 640 ? 1 : 0))
                            throw new InvalidOperationException("Audit summary did not adapt to its content width.");
                        await RenderPage(page, $"aihub-{theme}-tab-{tab}-{width}.png");
                    }
                }
            }
            File.AppendAllText(Report, "AI Hub theme: Dark/Light/Dark, cached navigation, severity contrast, all readiness/health states, detail badges, and 18 layout renders passed.\n");
        }
        finally
        {
            root.RequestedTheme = previousTheme;
        }
    }

    private static async Task<Windows.UI.Color[]> CheckAiHubPalette(AIHubPage page, ElementTheme theme)
    {
        if (page.RequestedTheme != ElementTheme.Default || page.ActualTheme != theme)
            throw new InvalidOperationException("AI Hub does not inherit the window theme.");
        var vm = page.ViewModel;
        var score = (TextBlock)page.FindName("AuditScoreText");
        var summary = (FrameworkElement)page.FindName("AuditFindingsSummary");
        var surface = VisualTreeHelper.GetParent((DependencyObject)page.FindName("AuditOverviewGrid"));
        while (surface is not Border) surface = VisualTreeHelper.GetParent(surface);
        byte value = theme == ElementTheme.Dark ? (byte)32 : (byte)243;
        var backdrop = Windows.UI.Color.FromArgb(255, value, value, value);
        var background = Composite(((SolidColorBrush)((Border)surface).Background).Color, backdrop);
        AssertContrast(((SolidColorBrush)score.Foreground).Color, background, 4.5, "health score");
        var palette = new List<Windows.UI.Color> { background };
        foreach (string label in new[] { vm.HighLabel, vm.MediumLabel, vm.LowLabel })
        {
            var text = Descendants(summary).OfType<TextBlock>().First(item => item.Text == label);
            var color = ((SolidColorBrush)text.Foreground).Color;
            AssertContrast(color, background, 4.5, "severity label");
            palette.Add(color);
        }

        var readiness = (FontIcon)page.FindName("AiReadinessIcon");
        var indicator = (Microsoft.UI.Xaml.Shapes.Ellipse)page.FindName("AuditHealthIndicator");
        var originalLevel = vm.AiReadinessLevel;
        int originalScore = vm.HealthScore;
        var readinessColors = new Dictionary<AiReadinessLevel, Windows.UI.Color>();
        try
        {
            foreach (var level in Enum.GetValues<AiReadinessLevel>())
            {
                typeof(AIHubPageViewModel).GetProperty(nameof(vm.AiReadinessLevel)).SetValue(vm, level);
                await Task.Delay(20);
                var color = ((SolidColorBrush)readiness.Foreground).Color;
                AssertContrast(color, background, 3, "readiness " + level);
                readinessColors[level] = color;
                palette.Add(color);
            }
            foreach (var entry in new[] { (0, AiReadinessLevel.NotConfigured), (59, AiReadinessLevel.NotConfigured), (60, AiReadinessLevel.Degraded), (79, AiReadinessLevel.Degraded), (80, AiReadinessLevel.Ready), (100, AiReadinessLevel.Ready) })
            {
                typeof(AIHubPageViewModel).GetProperty(nameof(vm.HealthScore)).SetValue(vm, entry.Item1);
                await Task.Delay(20);
                if (((SolidColorBrush)indicator.Fill).Color != readinessColors[entry.Item2])
                    throw new InvalidOperationException("Health indicator does not match score " + entry.Item1);
            }
        }
        finally
        {
            typeof(AIHubPageViewModel).GetProperty(nameof(vm.HealthScore)).SetValue(vm, originalScore);
            typeof(AIHubPageViewModel).GetProperty(nameof(vm.AiReadinessLevel)).SetValue(vm, originalLevel);
        }

        var dialog = new FindingDetailsDialog { XamlRoot = page.XamlRoot, RequestedTheme = page.ActualTheme };
        var shown = dialog.ShowAsync();
        try
        {
            await Task.Delay(100);
            foreach (string severity in new[] { "High", "Medium", "Low" })
            {
                dialog.SetFinding(new AuditIssueEnhanced { Title = "Synthetic finding", Severity = severity, LogName = "Application", EventId = "1000" });
                dialog.UpdateLayout();
                var badge = (Border)dialog.FindName("SeverityBadge");
                var text = (TextBlock)dialog.FindName("SeverityTextBlock");
                AssertContrast(((SolidColorBrush)text.Foreground).Color, Composite(((SolidColorBrush)badge.Background).Color, backdrop), 4.5, "detail badge " + severity);
                if (severity == "Medium") await RenderPage(dialog, $"aihub-{theme}-detail.png");
            }
        }
        finally
        {
            dialog.Hide();
            await shown;
        }
        File.AppendAllText(Report, $"AI Hub {theme}: theme inheritance, text contrast >= 4.5, status contrast >= 3, and score thresholds passed.\n");
        return palette.ToArray();
    }

    private static Windows.UI.Color Composite(Windows.UI.Color foreground, Windows.UI.Color background)
    {
        double alpha = foreground.A / 255.0;
        return Windows.UI.Color.FromArgb(255,
            (byte)((foreground.R * alpha) + (background.R * (1 - alpha))),
            (byte)((foreground.G * alpha) + (background.G * (1 - alpha))),
            (byte)((foreground.B * alpha) + (background.B * (1 - alpha))));
    }

    private static void AssertContrast(Windows.UI.Color foreground, Windows.UI.Color background, double minimum, string label)
    {
        static double Linear(byte value) => value <= 10 ? value / 3294.6 : Math.Pow(((value / 255.0) + 0.055) / 1.055, 2.4);
        static double Luminance(Windows.UI.Color color) => (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));
        double first = Luminance(Composite(foreground, background));
        double second = Luminance(background);
        double contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
        if (contrast < minimum) throw new InvalidOperationException($"{label} contrast {contrast:F2} is below {minimum}: {foreground} on {background}");
    }

    private static async Task CheckAuditUxStates(AIHubPage page)
    {
        var vm = page.ViewModel;
        var field = typeof(AIHubPageViewModel).GetField("_hasAuditResult", BindingFlags.Instance | BindingFlags.NonPublic);
        var originalIssues = vm.AllIssues.ToArray();
        bool originalResult = (bool)field.GetValue(vm);
        bool originalReport = vm.HasAiReport;
        int originalTab = vm.ActiveTabIndex;
        try
        {
            vm.ActiveTabIndex = 0;
            vm.AllIssues.Clear();
            field.SetValue(vm, false);
            vm.ResetFindingFilters();
            await Task.Delay(50);
            if (!vm.ShowNoAudit || vm.ShowEmptyIssues || vm.HealthScoreText != "—" ||
                ((TextBlock)page.FindName("AuditScoreText")).Text != "—")
                throw new InvalidOperationException("An unscanned audit claims a health score.");

            field.SetValue(vm, true);
            vm.ResetFindingFilters();
            if (vm.ShowNoAudit || !vm.HasAuditData || !vm.ShowEmptyIssues || vm.HealthScoreText == "—")
                throw new InvalidOperationException("A completed audit with no findings is shown as unscanned.");

            var important = new AuditIssueEnhanced { Title = "Synthetic security finding", Severity = "High", LogName = "Security", EventId = "4625" };
            vm.AllIssues.Add(important);
            vm.AllIssues.Add(new AuditIssueEnhanced { Title = "Synthetic application finding", Severity = "Low", LogName = "Application" });
            vm.SetSourceFilter("Security");
            if (vm.FilteredIssues.Count != 1 || vm.FilteredIssues[0] != important)
                throw new InvalidOperationException("Security channel filter is incorrect.");
            vm.SetSeverityFilter("Low");
            vm.FindingSearchText = "no-such-finding";
            await Task.Delay(50);
            if (!vm.ShowNoFilterMatches || !vm.HasFindingFilters || vm.AuditPriorityFinding != important)
                throw new InvalidOperationException("Filtering changed the audit overview or hid the empty-filter state.");
            var reset = Descendants(page).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "AIHub_ResetFindingFiltersButton");
            if (reset.Visibility != Visibility.Visible) throw new InvalidOperationException("Filter reset action is hidden.");
            new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(reset).Invoke();
            await Task.Delay(50);
            if (vm.HasFindingFilters || vm.FilteredIssues.Count != 2 || vm.FindingSearchText.Length != 0 || vm.ShowNoFilterMatches)
                throw new InvalidOperationException("Clear filters did not restore all findings.");

            typeof(AIHubPageViewModel).GetProperty(nameof(vm.HasAiReport)).SetValue(vm, true);
            foreach (int tab in new[] { 0, 1, 2 })
            {
                vm.ActiveTabIndex = tab;
                await Task.Delay(50);
                bool visible = true;
                for (DependencyObject element = (DependencyObject)page.FindName("AuditAiReport"); element != null; element = VisualTreeHelper.GetParent(element))
                    if (element is UIElement ui && ui.Visibility == Visibility.Collapsed) visible = false;
                if (visible != (tab == 0)) throw new InvalidOperationException("AI report appeared outside the audit tab.");
            }
            File.AppendAllText(Report, "AI Hub UX: unscanned/completed-empty states, Security filter, one-click reset, stable overview, and report tab isolation passed.\n");
        }
        finally
        {
            vm.AllIssues.Clear();
            foreach (var issue in originalIssues) vm.AllIssues.Add(issue);
            field.SetValue(vm, originalResult);
            typeof(AIHubPageViewModel).GetProperty(nameof(vm.HasAiReport)).SetValue(vm, originalReport);
            vm.ResetFindingFilters();
            vm.ActiveTabIndex = originalTab;
        }
    }

    private static async Task RenderPage(FrameworkElement page, string name)
    {
        page.UpdateLayout();
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(page);
        var buffer = await bitmap.GetPixelsAsync();
        byte[] pixels = new byte[buffer.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        // RenderTargetBitmap omits the window's Mica backdrop. Composite onto its solid theme fallback.
        byte backdrop = page.ActualTheme == ElementTheme.Dark ? (byte)32 : (byte)243;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            double transparent = (255 - pixels[offset + 3]) / 255.0;
            for (int channel = 0; channel < 3; channel++)
                pixels[offset + channel] = (byte)Math.Min(255, pixels[offset + channel] + (backdrop * transparent));
            pixels[offset + 3] = 255;
        }
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
            if (service.MainEndpoint.TestCommand.CanExecute(null) || service.FallbackEndpoint.TestCommand.CanExecute(null)) throw new InvalidOperationException("AI endpoint tests remain enabled while the service is off.");
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
                    .Single(box => box.PlaceholderText == Kit.AiHub.Models.AiTargetSettings.DefaultLunaModel);
                var effortBox = entry.Expander.Items.OfType<SettingsCard>().Select(card => card.Content).OfType<ComboBox>()
                    .Single(box => box.Items.OfType<ComboBoxItem>().Any(item => (string)item.Tag == "max"));
                if (!effortBox.Items.OfType<ComboBoxItem>().Select(item => (string)item.Tag).SequenceEqual(new[] { "low", "high", "max" }))
                    throw new InvalidOperationException("Reasoning effort must offer only low/high/max.");
                string savedModel = entry.Endpoint.Model;
                string savedEffort = entry.Endpoint.Effort;
                string selectedEffort = cycle == 0 ? "max" : "high";
                modelBox.Text = cycle == 0 ? "gpt-6-luna" : "gpt-6.1-sol";
                effortBox.SelectedValue = selectedEffort;
                if (entry.Endpoint.Model != modelBox.Text || !service.SaveEndpointsCommand.CanExecute(null)) throw new InvalidOperationException("Endpoint edit did not enable saving.");
                service.SaveEndpointsCommand.Execute(null);
                var saved = new Kit.AiHub.Storage.AiHubSettingsStore().Load();
                var savedEndpoint = saved.Targets.Single(target => target.Name == entry.Endpoint.Settings.Name);
                if (savedEndpoint.Model != modelBox.Text || savedEndpoint.Effort != selectedEffort) throw new InvalidOperationException("Endpoint model/effort was not saved unchanged.");
                modelBox.Text = savedModel;
                effortBox.SelectedValue = savedEffort;
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
            if (!service.IsBusy || service.MainEndpoint.TestCommand.CanExecute(null) || service.FallbackEndpoint.TestCommand.CanExecute(null)) throw new InvalidOperationException("AI endpoint tests remain enabled while an operation is running.");
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
