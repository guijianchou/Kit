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
        string monitorPageCoordinatorPath = Path.Combine(kitRoot, "src", "settings-ui", "Settings.UI", "Services", "MonitorManualScanCoordinator.cs");

        string monitorPageText = File.ReadAllText(monitorPagePath);
        string moduleInterfaceText = File.ReadAllText(moduleInterfacePath);
        string commandLineText = File.ReadAllText(commandLinePath);
        string programText = File.ReadAllText(programPath);
        string scanCoordinatorText = File.ReadAllText(scanCoordinatorPath);
        string runtimePathsText = File.ReadAllText(runtimePathsPath);
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
        StringAssert.Contains(programText, "OneShotScanTimeout");
        StringAssert.Contains(programText, "CancellationTokenSource");
        StringAssert.Contains(programText, "MonitorLifetimeCancellation");
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
        StringAssert.Contains(moduleInterfaceText, "sync_background_worker");
        StringAssert.Contains(moduleInterfaceText, "m_run_in_background");
        StringAssert.Contains(moduleInterfaceText, "if (m_run_in_background)");
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
        StringAssert.Contains(moduleInterfaceText, "sync_background_worker(true)");
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
        StringAssert.Contains(programText, "new MonitorLifetimeCancellation(commandLine.ParentProcessId, exitEvent, backgroundExitEvent)");
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
