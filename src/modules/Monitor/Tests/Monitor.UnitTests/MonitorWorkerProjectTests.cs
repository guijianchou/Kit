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

        string programText = File.ReadAllText(programPath);
        string commandLineText = File.ReadAllText(commandLinePath);

        StringAssert.Contains(commandLineText, "--pid");
        StringAssert.Contains(commandLineText, "ParentProcessId");
        StringAssert.Contains(programText, "MonitorExitEvent");
        StringAssert.Contains(programText, "KitMonitorExitEvent");
        Assert.IsFalse(programText.Contains("PowerToysMonitorExitEvent", StringComparison.Ordinal));
        StringAssert.Contains(programText, "EventWaitHandle");
        StringAssert.Contains(programText, "WaitOne");
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

        string monitorPageText = File.ReadAllText(monitorPagePath);
        string moduleInterfaceText = File.ReadAllText(moduleInterfacePath);
        string commandLineText = File.ReadAllText(commandLinePath);
        string programText = File.ReadAllText(programPath);

        StringAssert.Contains(monitorPageText, "GetSerializedCustomAction(MonitorSettings.ModuleName, \"scanNow\"");
        Assert.IsFalse(monitorPageText.Contains("GetSerializedCustomAction(MonitorSettings.ModuleName, \"organizeDownloads\"", StringComparison.Ordinal));
        StringAssert.Contains(moduleInterfaceText, "\"--scan-once --use-configured-actions\"");
        StringAssert.Contains(commandLineText, "UseConfiguredActions");
        StringAssert.Contains(commandLineText, "--use-configured-actions");
        StringAssert.Contains(programText, "commandLine.UseConfiguredActions ? settings.AutoOrganize : commandLine.Organize");
        StringAssert.Contains(programText, "commandLine.UseConfiguredActions ? settings.AutoCleanInstallers : commandLine.CleanInstallers");
    }

    [TestMethod]
    public void WorkerRunsContinuousMonitoringUntilExitWhenStartedByRunner()
    {
        string kitRoot = FindKitRoot();
        string programPath = Path.Combine(kitRoot, "src", "modules", "Monitor", "Monitor", "Program.cs");
        string moduleInterfacePath = Path.Combine(kitRoot, "src", "modules", "Monitor", "MonitorModuleInterface", "dllmain.cpp");

        string programText = File.ReadAllText(programPath);
        string moduleInterfaceText = File.ReadAllText(moduleInterfacePath);

        StringAssert.Contains(programText, "RunContinuous");
        StringAssert.Contains(programText, "RunScanCycle");
        StringAssert.Contains(moduleInterfaceText, "sync_background_worker");
        StringAssert.Contains(moduleInterfaceText, "m_run_in_background");
        StringAssert.Contains(moduleInterfaceText, "if (m_run_in_background)");
        StringAssert.Contains(moduleInterfaceText, "get_bool_value(L\"runInBackground\")");
        Assert.IsFalse(programText.Contains("Use --scan-once for a one-shot scan.", StringComparison.Ordinal));
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
        StringAssert.Contains(moduleInterfaceText, "stop_background_worker();");
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
