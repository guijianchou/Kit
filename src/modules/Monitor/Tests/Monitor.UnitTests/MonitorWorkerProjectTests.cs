// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.PowerToys.Monitor.Tests;

[TestClass]
public sealed class MonitorWorkerProjectTests
{
    [TestMethod]
    public void WorkerProjectReferencesMonitorLibAndSupportsScanOnce()
    {
        string kitRoot = FindKitRoot();
        string workerProjectPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "PowerToys.Monitor.csproj");
        string programPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "Program.cs");

        Assert.IsTrue(File.Exists(workerProjectPath), "Monitor worker project should exist.");
        Assert.IsTrue(File.Exists(programPath), "Monitor worker Program.cs should exist.");

        string projectText = File.ReadAllText(workerProjectPath);
        string programText = File.ReadAllText(programPath);

        StringAssert.Contains(projectText, @"..\MonitorLib\MonitorLib.csproj");
        StringAssert.Contains(programText, "--scan-once");
    }

    [TestMethod]
    public void WorkerSupportsRunnerLifetimeExitEvent()
    {
        string kitRoot = FindKitRoot();
        string programPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "Program.cs");
        string commandLinePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "MonitorCommandLine.cs");
        string lifetimePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "MonitorLifetimeCancellation.cs");
        string eventsPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "MonitorWorkerEvents.cs");
        string moduleInterfacePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorModuleInterface", "dllmain.cpp");
        string sharedConstantsPath = Path.Combine(kitRoot, "src", "common", "interop", "shared_constants.h");

        string programText = File.ReadAllText(programPath);
        string commandLineText = File.ReadAllText(commandLinePath);
        string lifetimeText = File.ReadAllText(lifetimePath);
        string eventsText = File.ReadAllText(eventsPath);
        string moduleInterfaceText = File.ReadAllText(moduleInterfacePath);
        string sharedConstantsText = File.ReadAllText(sharedConstantsPath);

        StringAssert.Contains(commandLineText, "--pid");
        StringAssert.Contains(commandLineText, "ParentProcessId");
        StringAssert.Contains(eventsText, "MonitorExitEvent");
        StringAssert.Contains(eventsText, "KitMonitorExitEvent");
        StringAssert.Contains(eventsText, "MonitorBackgroundExitEvent");
        StringAssert.Contains(sharedConstantsText, "MONITOR_BACKGROUND_EXIT_EVENT");
        Assert.IsFalse(programText.Contains("PowerToysMonitorExitEvent", StringComparison.Ordinal));
        StringAssert.Contains(programText, "EventWaitHandle");
        StringAssert.Contains(programText, "EventResetMode.ManualReset");
        StringAssert.Contains(lifetimeText, "WaitHandle.WaitAny");
        StringAssert.Contains(moduleInterfaceText, "create_monitor_exit_event");
        StringAssert.Contains(moduleInterfaceText, "create_monitor_background_exit_event");
        StringAssert.Contains(moduleInterfaceText, "CreateEventW(&sa, TRUE, FALSE, CommonSharedConstants::MONITOR_EXIT_EVENT)");
        StringAssert.Contains(moduleInterfaceText, "CreateEventW(&sa, TRUE, FALSE, CommonSharedConstants::MONITOR_BACKGROUND_EXIT_EVENT)");
    }

    [TestMethod]
    public void ModuleInterfaceLaunchesWorkerHiddenWithoutRunningConsoleMessage()
    {
        string kitRoot = FindKitRoot();
        string programPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "Program.cs");
        string moduleInterfacePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorModuleInterface", "dllmain.cpp");

        string programText = File.ReadAllText(programPath);
        string moduleInterfaceText = File.ReadAllText(moduleInterfacePath);

        StringAssert.Contains(moduleInterfaceText, "CREATE_NO_WINDOW");
        StringAssert.Contains(moduleInterfaceText, "STARTF_USESHOWWINDOW");
        StringAssert.Contains(moduleInterfaceText, "SW_HIDE");
        Assert.IsFalse(programText.Contains("Monitor worker is running.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ModuleInterfaceFallsBackToDotnetDllWhenWorkerAppHostIsMissing()
    {
        string kitRoot = FindKitRoot();
        string moduleInterfacePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorModuleInterface", "dllmain.cpp");

        string moduleInterfaceText = File.ReadAllText(moduleInterfacePath);

        StringAssert.Contains(moduleInterfaceText, "PowerToys.Monitor.exe");
        StringAssert.Contains(moduleInterfaceText, "PowerToys.Monitor.dll");
        StringAssert.Contains(moduleInterfaceText, "dotnet.exe");
        StringAssert.Contains(moduleInterfaceText, "SearchPathW");
    }

    [TestMethod]
    public void ScanNowActionRunsOneShotCycleWithConfiguredActions()
    {
        string kitRoot = FindKitRoot();
        string monitorPagePath = Path.Combine(kitRoot, "src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "MonitorPage.xaml.cs");
        string moduleInterfacePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorModuleInterface", "dllmain.cpp");
        string commandLinePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "MonitorCommandLine.cs");
        string programPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "Program.cs");
        string scanCoordinatorPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "MonitorScanRunCoordinator.cs");
        string runtimePathsPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "MonitorRuntimePaths.cs");
        string progressTimeoutCancellationPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "MonitorProgressTimeoutCancellation.cs");
        string monitorPageCoordinatorPath = Path.Combine(kitRoot, "src", "settings-ui", "Settings.UI", "Services", "MonitorManualScanCoordinator.cs");

        string monitorPageText = File.ReadAllText(monitorPagePath);
        string moduleInterfaceText = File.ReadAllText(moduleInterfacePath);
        string commandLineText = File.ReadAllText(commandLinePath);
        string programText = File.ReadAllText(programPath);
        string scanCoordinatorText = File.ReadAllText(scanCoordinatorPath);
        string runtimePathsText = File.ReadAllText(runtimePathsPath);
        string progressTimeoutCancellationText = File.ReadAllText(progressTimeoutCancellationPath);
        string monitorPageCoordinatorText = File.ReadAllText(monitorPageCoordinatorPath);

        StringAssert.Contains(monitorPageText, "GetSerializedCustomAction(MonitorSettings.ModuleName, \"scanNow\"");
        Assert.IsFalse(monitorPageText.Contains("GetSerializedCustomAction(MonitorSettings.ModuleName, \"organizeDownloads\"", StringComparison.Ordinal));
        StringAssert.Contains(moduleInterfaceText, "\"--scan-once --use-configured-actions\"");
        StringAssert.Contains(commandLineText, "UseConfiguredActions");
        StringAssert.Contains(commandLineText, "--use-configured-actions");
        StringAssert.Contains(programText, "commandLine.UseConfiguredActions ? settings.AutoOrganize : commandLine.Organize");
        StringAssert.Contains(programText, "commandLine.UseConfiguredActions ? settings.AutoCleanInstallers : commandLine.CleanInstallers");
        StringAssert.Contains(monitorPageCoordinatorText, "CreateManualScanId");
        StringAssert.Contains(monitorPageText, "GetSerializedCustomAction(MonitorSettings.ModuleName, \"scanNow\", manualScanId)");
        StringAssert.Contains(moduleInterfaceText, "--scan-id");
        StringAssert.Contains(commandLineText, "ScanId");
        StringAssert.Contains(commandLineText, "--scan-id");
        StringAssert.Contains(programText, "commandLine.ScanId");
        Assert.IsFalse(programText.Contains("signalScanCompleted", StringComparison.Ordinal), "Manual scan progress should be driven by scan-progress.json and status DB, not a global completion event flag.");
        Assert.IsFalse(programText.Contains("SignalScanCompleted", StringComparison.Ordinal), "The worker should not signal the legacy global scan completion event.");
        Assert.IsFalse(programText.Contains("MonitorScanCompletedEvent", StringComparison.Ordinal), "The legacy Monitor scan completion event should be removed from the worker.");
        StringAssert.Contains(programText, "OneShotNoProgressTimeout");
        StringAssert.Contains(programText, "CancellationTokenSource");
        StringAssert.Contains(programText, "MonitorLifetimeCancellation");
        StringAssert.Contains(programText, "MonitorProgressTimeoutCancellation");
        StringAssert.Contains(progressTimeoutCancellationText, "Interlocked.Exchange");
        StringAssert.Contains(progressTimeoutCancellationText, "Interlocked.Read");
        Assert.IsFalse(progressTimeoutCancellationText.Contains("Join(TimeSpan", StringComparison.Ordinal), "Progress timeout watcher should exit before its cancellation source is disposed.");
        StringAssert.Contains(programText, "using EventWaitHandle exitEvent = new(false, EventResetMode.ManualReset, MonitorWorkerEvents.MonitorExitEvent);");
        StringAssert.Contains(programText, "using MonitorLifetimeCancellation lifetimeCancellation = new(commandLine.ParentProcessId, exitEvent);");
        StringAssert.Contains(programText, "CancellationTokenSource.CreateLinkedTokenSource");
        StringAssert.Contains(scanCoordinatorText, "ReportOneShotScanFailed");
        StringAssert.Contains(scanCoordinatorText, "MonitorScanProgressPhase.Failed");
        StringAssert.Contains(runtimePathsText, "ResolveProgressPath");
        StringAssert.Contains(programText, "catch (OperationCanceledException");
        Assert.IsFalse(programText.Contains("MonitorBackgroundExitEvent);\r\n                using LifetimeCancellation lifetimeCancellation = StartLifetimeCancellation(commandLine.ParentProcessId, exitEvent, backgroundExitEvent);", StringComparison.Ordinal), "Manual one-shot scans must not listen to background-only restart events.");
        Assert.IsFalse(programText.Contains("RunScanCycle(downloadsPath, csvPath, settings, organize, cleanInstallers, scanId, MonitorScanTrigger.Manual, statusDatabasePath, MonitorScanStatus.Failed, signalScanCompleted: true, oneShotCancellation.Token)", StringComparison.Ordinal), "Manual one-shot scans must combine timeout and runner lifetime cancellation.");
        Assert.IsFalse(programText.Contains("signalScanCompleted: true, CancellationToken.None", StringComparison.Ordinal), "Manual one-shot scans must use a finite cancellation token instead of waiting forever on the shared scan lock.");
        Assert.IsFalse(programText.Contains("new(OneShotScanTimeout)", StringComparison.Ordinal), "Manual one-shot scans should use no-progress timeout tracking instead of a fixed total-duration kill.");
        Assert.IsFalse(programText.Contains("OneShotScanTimeout", StringComparison.Ordinal), "Manual one-shot scans should not keep a fixed total-duration timeout constant.");
    }

    [TestMethod]
    public void MonitorDirectoryEnumerationDoesNotMaterializeWholeDirectories()
    {
        string kitRoot = FindKitRoot();
        string scannerPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorLib", "MonitorScanner.cs");
        string organizerPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorLib", "MonitorFileOrganizer.cs");
        string installerCleanerPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorLib", "MonitorInstallerCleaner.cs");

        string scannerText = File.ReadAllText(scannerPath);
        string organizerText = File.ReadAllText(organizerPath);
        string installerCleanerText = File.ReadAllText(installerCleanerPath);

        Assert.IsFalse(scannerText.Contains("EnumerateFiles().ToArray()", StringComparison.Ordinal), "MonitorScanner should stream file enumeration so one large directory cannot stall progress before yielding.");
        Assert.IsFalse(scannerText.Contains("EnumerateDirectories().ToArray()", StringComparison.Ordinal), "MonitorScanner should stream directory enumeration so one large directory cannot stall progress before yielding.");
        Assert.IsFalse(scannerText.Contains("List<FileInfo> files = new();", StringComparison.Ordinal), "MonitorScanner should process files as they are discovered instead of materializing the whole scan.");
        Assert.IsFalse(organizerText.Contains("EnumerateFiles().ToArray()", StringComparison.Ordinal), "MonitorFileOrganizer should stream files instead of materializing whole directories.");
        Assert.IsFalse(installerCleanerText.Contains("EnumerateFiles().ToArray()", StringComparison.Ordinal), "MonitorInstallerCleaner should stream files instead of materializing whole directories.");
    }

    [TestMethod]
    public void StatusSummaryQueriesAreRangeBounded()
    {
        string kitRoot = FindKitRoot();
        string statusStorePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorLib", "MonitorStatusStore.cs");
        string statusStoreText = File.ReadAllText(statusStorePath);

        Assert.IsFalse(statusStoreText.Contains("IReadOnlyList<RunRow> rows = ReadRows(connection);", StringComparison.Ordinal), "Status summaries should not read the whole scan_runs history before applying the selected range.");
        StringAssert.Contains(statusStoreText, "ReadRows(connection, range, now)");
        StringAssert.Contains(statusStoreText, "WHERE status <> $canceledStatus");
        StringAssert.Contains(statusStoreText, "AND started_at >= $startDate");
    }

    [TestMethod]
    public void ModuleInterfaceRejectsInvalidManualScanIdWithoutLaunchingUntrackedScan()
    {
        string kitRoot = FindKitRoot();
        string moduleInterfacePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorModuleInterface", "dllmain.cpp");
        string moduleInterfaceText = File.ReadAllText(moduleInterfacePath);

        StringAssert.Contains(moduleInterfaceText, "write_failed_scan_progress(scan_id, L\"Invalid scan id\")");
        int invalidIndex = moduleInterfaceText.IndexOf("write_failed_scan_progress(scan_id, L\"Invalid scan id\")", StringComparison.Ordinal);
        int launchIndex = moduleInterfaceText.IndexOf("if (!launch_process(args, MonitorProcessKind::OneShot))", StringComparison.Ordinal);
        Assert.IsTrue(invalidIndex >= 0, "Invalid manual scan ids should be reported to the requested scan id.");
        Assert.IsTrue(launchIndex > invalidIndex, "Manual scan id validation should complete before launch.");
        Assert.IsTrue(moduleInterfaceText.IndexOf("return;", invalidIndex, StringComparison.Ordinal) < launchIndex, "Invalid manual scan ids should return before launching an untracked scan.");
    }

    [TestMethod]
    public void ProgressTimeoutTracksOnlyPersistedProgress()
    {
        string kitRoot = FindKitRoot();
        string progressTimeoutCancellationPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "MonitorProgressTimeoutCancellation.cs");
        string progressTimeoutCancellationText = File.ReadAllText(progressTimeoutCancellationPath);

        int reportIndex = progressTimeoutCancellationText.IndexOf("_innerReporter.Report(snapshot, force);", StringComparison.Ordinal);
        int markIndex = progressTimeoutCancellationText.IndexOf("_owner.MarkProgress();", StringComparison.Ordinal);

        Assert.IsTrue(reportIndex >= 0, "The timeout reporter should forward progress to the inner reporter.");
        Assert.IsTrue(markIndex > reportIndex, "No-progress timeout should reset only after the progress snapshot is written successfully.");
        Assert.IsFalse(progressTimeoutCancellationText.Contains("Thread.Sleep", StringComparison.Ordinal), "The progress timeout watcher should be woken on Dispose instead of waiting for the next polling sleep.");
        Assert.IsFalse(progressTimeoutCancellationText.Contains("_watcherThread.Join();", StringComparison.Ordinal), "Dispose should not wait indefinitely on the progress timeout watcher.");
    }

    [TestMethod]
    public void ModuleInterfaceValidatesManualScanIdBeforeBuildingCommandLine()
    {
        string kitRoot = FindKitRoot();
        string moduleInterfacePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorModuleInterface", "dllmain.cpp");
        string moduleInterfaceText = File.ReadAllText(moduleInterfacePath);

        StringAssert.Contains(moduleInterfaceText, "is_valid_scan_id");
        StringAssert.Contains(moduleInterfaceText, "Invalid Monitor scan id rejected.");
        int validationIndex = moduleInterfaceText.IndexOf("if (is_valid_scan_id(scan_id))", StringComparison.Ordinal);
        int appendIndex = moduleInterfaceText.IndexOf("args += quote_argument(scan_id);", StringComparison.Ordinal);
        Assert.IsTrue(validationIndex >= 0, "Manual scan id validation should be present.");
        Assert.IsTrue(appendIndex > validationIndex, "Manual scan ids should be validated before being appended to the command line.");
    }

    [TestMethod]
    public void WorkerStatusWarningsIncludeWorkerResultWarnings()
    {
        string kitRoot = FindKitRoot();
        string programPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "Program.cs");
        string scanCoordinatorPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "MonitorScanRunCoordinator.cs");
        string workerResultPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorLib", "MonitorWorkerResult.cs");

        string scanCoordinatorText = File.ReadAllText(scanCoordinatorPath);
        string workerResultText = File.ReadAllText(workerResultPath);

        StringAssert.Contains(workerResultText, "WarningCount");
        StringAssert.Contains(scanCoordinatorText, "result.WarningCount");
    }

    [TestMethod]
    public void ModuleInterfaceIgnoresCustomActionsWhenDisabled()
    {
        string kitRoot = FindKitRoot();
        string moduleInterfacePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorModuleInterface", "dllmain.cpp");

        string moduleInterfaceText = File.ReadAllText(moduleInterfacePath);

        StringAssert.Contains(moduleInterfaceText, "if (!m_enabled)");
        StringAssert.Contains(moduleInterfaceText, "Monitor custom action ignored because the module is disabled.");
    }

    [TestMethod]
    public void ModuleDisableSignalsOneShotWorkersThroughSharedExitEvent()
    {
        string kitRoot = FindKitRoot();
        string moduleInterfacePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorModuleInterface", "dllmain.cpp");

        string moduleInterfaceText = File.ReadAllText(moduleInterfacePath);

        StringAssert.Contains(moduleInterfaceText, "signal_exit_event();");
        StringAssert.Contains(moduleInterfaceText, "stop_monitor_workers");
        StringAssert.Contains(moduleInterfaceText, "stop_monitor_workers();");
        StringAssert.Contains(moduleInterfaceText, "m_one_shot_processes");
        StringAssert.Contains(moduleInterfaceText, "wil::unique_handle");
        StringAssert.Contains(moduleInterfaceText, "one_shot_stop_deadline");
        StringAssert.Contains(moduleInterfaceText, "stop_one_shot_workers();");
        StringAssert.Contains(moduleInterfaceText, "launch_process(args, MonitorProcessKind::OneShot)");
        StringAssert.Contains(moduleInterfaceText, "write_failed_scan_progress(scan_id, L\"Scan failed to start\")");
        StringAssert.Contains(moduleInterfaceText, "if (!launch_process(args, MonitorProcessKind::OneShot))");
        StringAssert.Contains(moduleInterfaceText, "launch_process(L\"--scan-once --organize\", MonitorProcessKind::OneShot)");
        StringAssert.Contains(moduleInterfaceText, "launch_process(L\"--scan-once --clean-installers\", MonitorProcessKind::OneShot)");
        StringAssert.Contains(moduleInterfaceText, "launch_process(L\"\", MonitorProcessKind::Background)");
        Assert.IsFalse(moduleInterfaceText.Contains("std::vector<HANDLE> m_one_shot_processes", StringComparison.Ordinal), "One-shot process handles should be owned through RAII.");
        Assert.IsFalse(moduleInterfaceText.Contains("CloseHandle(m_process)", StringComparison.Ordinal), "Background process handle should be owned through RAII.");
        Assert.IsFalse(moduleInterfaceText.Contains("WaitForSingleObject(process, timeout_ms)", StringComparison.Ordinal), "Stopping several one-shot workers should use one shared deadline instead of waiting the full timeout for each process.");
        Assert.IsFalse(moduleInterfaceText.Contains("stop_background_worker();\r\n        Trace::EnableMonitor(false);", StringComparison.Ordinal), "Disable must signal one-shot workers as well as stop the tracked background worker.");
    }

    [TestMethod]
    public void WorkerRunsContinuousMonitoringUntilExitWhenStartedByRunner()
    {
        string kitRoot = FindKitRoot();
        string programPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "Program.cs");
        string scanCoordinatorPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "MonitorScanRunCoordinator.cs");
        string moduleInterfacePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorModuleInterface", "dllmain.cpp");

        string programText = File.ReadAllText(programPath);
        string scanCoordinatorText = File.ReadAllText(scanCoordinatorPath);
        string moduleInterfaceText = File.ReadAllText(moduleInterfacePath);

        StringAssert.Contains(programText, "RunContinuous");
        StringAssert.Contains(scanCoordinatorText, "RunScanCycle");
        StringAssert.Contains(scanCoordinatorText, "MonitorScanProgressFreshness.GetFreshProgressScanId(MonitorRuntimePaths.ResolveProgressPath(), now)");
        StringAssert.Contains(scanCoordinatorText, "MonitorStatusStore.RefreshStaleRunningScans(statusDatabasePath, now,");
        StringAssert.Contains(programText, "MonitorScanStatus.Canceled");
        Assert.IsFalse(programText.Contains("MonitorScanStatus.Warning, () => \"Scan interrupted\"", StringComparison.Ordinal), "Expected background worker restarts should be recorded as canceled rather than warning.");
        StringAssert.Contains(moduleInterfaceText, "sync_background_worker");
        StringAssert.Contains(moduleInterfaceText, "m_run_in_background");
        StringAssert.Contains(moduleInterfaceText, "if (m_run_in_background)");
        StringAssert.Contains(moduleInterfaceText, "if (!restart_running_worker && is_handle_running(m_process.get()))");
        StringAssert.Contains(moduleInterfaceText, "get_bool_value(L\"runInBackground\")");
        Assert.IsFalse(programText.Contains("Use --scan-once for a one-shot scan.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ContinuousWorkerContinuesAfterRecoverableScanFailure()
    {
        string kitRoot = FindKitRoot();
        string programPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "Program.cs");
        string scanCoordinatorPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "MonitorScanRunCoordinator.cs");

        string programText = File.ReadAllText(programPath);
        string scanCoordinatorText = File.ReadAllText(scanCoordinatorPath);

        StringAssert.Contains(programText, "catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SqliteException)");
        StringAssert.Contains(scanCoordinatorText, "TryCompleteStatusRun");
        StringAssert.Contains(programText, "Monitor background scan failed; waiting for next cycle.");
        Assert.IsFalse(programText.Contains("catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SqliteException)\r\n            {\r\n                return 1;", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MonitorSettingsWatcherAlwaysRestoresViewModelUpdateSuppression()
    {
        string kitRoot = FindKitRoot();
        string monitorPagePath = Path.Combine(kitRoot, "src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "MonitorPage.xaml.cs");

        string monitorPageText = File.ReadAllText(monitorPagePath);

        StringAssert.Contains(monitorPageText, "_suppressViewModelUpdates = true;");
        StringAssert.Contains(monitorPageText, "finally");
        StringAssert.Contains(monitorPageText, "_suppressViewModelUpdates = false;");
    }

    [TestMethod]
    public void WorkerSerializesScansAndSupportsCooperativeCancellation()
    {
        string kitRoot = FindKitRoot();
        string workerPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorLib", "MonitorWorker.cs");
        string programPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "Program.cs");
        string lifetimePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "MonitorLifetimeCancellation.cs");
        string hasherPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorLib", "MonitorHasher.cs");
        string moduleInterfacePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorModuleInterface", "dllmain.cpp");

        string workerText = File.ReadAllText(workerPath);
        string programText = File.ReadAllText(programPath);
        string lifetimeText = File.ReadAllText(lifetimePath);
        string hasherText = File.ReadAllText(hasherPath);
        string moduleInterfaceText = File.ReadAllText(moduleInterfacePath);

        StringAssert.Contains(workerText, "MonitorScanLock.Acquire");
        StringAssert.Contains(workerText, "CancellationToken");
        StringAssert.Contains(programText, "MonitorLifetimeCancellation");
        StringAssert.Contains(lifetimeText, "CancellationTokenSource");
        StringAssert.Contains(programText, "OperationCanceledException");
        StringAssert.Contains(hasherText, "cancellationToken.ThrowIfCancellationRequested");
        StringAssert.Contains(hasherText, "chunkSizeBytes");
        StringAssert.Contains(moduleInterfaceText, "background_stop_timeout_ms = 10000");
    }

    [TestMethod]
    public void ModuleInterfaceRestartsBackgroundWorkerAfterSettingsChanges()
    {
        string kitRoot = FindKitRoot();
        string moduleInterfacePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorModuleInterface", "dllmain.cpp");

        string moduleInterfaceText = File.ReadAllText(moduleInterfacePath);

        StringAssert.Contains(moduleInterfaceText, "sync_background_worker(const bool restart_running_worker)");
        StringAssert.Contains(moduleInterfaceText, "build_worker_settings_signature(values)");
        StringAssert.Contains(moduleInterfaceText, "std::to_wstring(resolved_value.size())");
        StringAssert.Contains(moduleInterfaceText, "signature += L':'");
        Assert.IsFalse(moduleInterfaceText.Contains("signature += value.value_or(L\"-\");", StringComparison.Ordinal), "String settings in the worker restart signature must be length-prefixed so paths containing delimiters cannot collide.");
        StringAssert.Contains(moduleInterfaceText, "const bool worker_settings_changed = new_signature != m_worker_settings_signature;");
        StringAssert.Contains(moduleInterfaceText, "sync_background_worker(worker_settings_changed)");
        Assert.IsFalse(moduleInterfaceText.Contains("sync_background_worker(true)", StringComparison.Ordinal), "Duplicate settings saves should not unconditionally restart an in-progress background worker.");
        StringAssert.Contains(moduleInterfaceText, "if (restart_running_worker)");
        StringAssert.Contains(moduleInterfaceText, "stop_background_worker(true);");
        StringAssert.Contains(moduleInterfaceText, "signal_background_exit_event();");
        Assert.IsFalse(moduleInterfaceText.Contains("if (request_exit)\r\n            {\r\n                signal_exit_event();", StringComparison.Ordinal), "Background restarts must not signal the all-workers exit event used by manual scans.");
    }

    [TestMethod]
    public void ContinuousWorkerListensForBackgroundRestartAndModuleDisableEvents()
    {
        string kitRoot = FindKitRoot();
        string programPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "Program.cs");
        string eventsPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "MonitorWorkerEvents.cs");
        string lifetimePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "MonitorLifetimeCancellation.cs");

        string programText = File.ReadAllText(programPath);
        string eventsText = File.ReadAllText(eventsPath);
        string lifetimeText = File.ReadAllText(lifetimePath);

        StringAssert.Contains(eventsText, "MonitorBackgroundExitEvent");
        StringAssert.Contains(programText, "using EventWaitHandle exitEvent = new(false, EventResetMode.ManualReset, MonitorWorkerEvents.MonitorExitEvent);");
        StringAssert.Contains(programText, "using EventWaitHandle backgroundExitEvent = new(false, EventResetMode.ManualReset, MonitorWorkerEvents.MonitorBackgroundExitEvent);");
        StringAssert.Contains(programText, "MonitorLifetimeCancellation lifetimeCancellation = new(commandLine.ParentProcessId, exitEvent, backgroundExitEvent);");
        StringAssert.Contains(programText, "lifetimeCancellation.Dispose();");
        StringAssert.Contains(programText, "MonitorLifetimeCancellation.WaitForNextCycleOrExit(commandLine.ParentProcessId, interval, exitEvent, backgroundExitEvent)");
        StringAssert.Contains(lifetimeText, "WaitHandle.WaitAny");
    }

    [TestMethod]
    public void ScannerGuardsDirectoryEnumerationFailures()
    {
        string kitRoot = FindKitRoot();
        string scannerPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorLib", "MonitorScanner.cs");

        string scannerText = File.ReadAllText(scannerPath);

        StringAssert.Contains(scannerText, "SafeEnumerateFiles");
        StringAssert.Contains(scannerText, "UnauthorizedAccessException");
        StringAssert.Contains(scannerText, "DirectoryNotFoundException");
    }

    [TestMethod]
    public void WorkerCommandLineRejectsInvalidNumericArgumentsAsUsageErrors()
    {
        string kitRoot = FindKitRoot();
        string commandLinePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "MonitorCommandLine.cs");

        string commandLineText = File.ReadAllText(commandLinePath);

        StringAssert.Contains(commandLineText, "TryParseInt32");
        StringAssert.Contains(commandLineText, "Invalid value for ");
        Assert.IsFalse(commandLineText.Contains("int.Parse(", StringComparison.Ordinal));
    }

    private static string FindKitRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Kit.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        Assert.Fail("Could not locate Kit root from test output directory.");
        return string.Empty;
    }
}
