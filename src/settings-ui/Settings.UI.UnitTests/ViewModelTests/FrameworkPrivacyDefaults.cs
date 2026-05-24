// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ViewModelTests
{
    [TestClass]
    public class FrameworkPrivacyDefaults
    {
        private static string FindSourceFile(params string[] relativePathParts)
        {
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var pathParts = new string[relativePathParts.Length + 1];
                pathParts[0] = directory.FullName;
                Array.Copy(relativePathParts, 0, pathParts, 1, relativePathParts.Length);

                var candidate = Path.Combine(pathParts);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            Assert.Fail($"Could not find source file: {Path.Combine(relativePathParts)}");
            return string.Empty;
        }

        [TestMethod]
        public void RunnerShouldNotStartSettingsTelemetryWorker()
        {
            var main = File.ReadAllText(FindSourceFile("src", "runner", "main.cpp"));
            var runnerProjectPath = FindSourceFile("src", "runner", "Kit.vcxproj");
            var runnerProject = File.ReadAllText(runnerProjectPath);
            var runnerProjectFilters = File.ReadAllText(FindSourceFile("src", "runner", "Kit.vcxproj.filters"));
            var runnerRoot = Path.GetDirectoryName(runnerProjectPath)!;

            Assert.IsFalse(main.Contains("settings_telemetry::init();", StringComparison.Ordinal));
            Assert.IsFalse(runnerProject.Contains("settings_telemetry.cpp", StringComparison.Ordinal), "Runner should not compile the settings telemetry worker.");
            Assert.IsFalse(runnerProject.Contains("settings_telemetry.h", StringComparison.Ordinal), "Runner should not include the settings telemetry worker header.");
            Assert.IsFalse(runnerProjectFilters.Contains("settings_telemetry.cpp", StringComparison.Ordinal), "Runner filters should not keep deleted settings telemetry worker source entries.");
            Assert.IsFalse(runnerProjectFilters.Contains("settings_telemetry.h", StringComparison.Ordinal), "Runner filters should not keep deleted settings telemetry worker header entries.");
            Assert.IsFalse(File.Exists(Path.Combine(runnerRoot, "settings_telemetry.cpp")), "Runner should delete the inactive settings telemetry worker source file.");
            Assert.IsFalse(File.Exists(Path.Combine(runnerRoot, "settings_telemetry.h")), "Runner should delete the inactive settings telemetry worker header file.");
        }

        [TestMethod]
        public void TelemetryCompatibilitySurfacesShouldStayNoOp()
        {
            var managedTelemetry = File.ReadAllText(FindSourceFile("src", "common", "ManagedTelemetry", "Telemetry", "PowerToysTelemetry.cs"));
            var nativeTraceBase = File.ReadAllText(FindSourceFile("src", "common", "Telemetry", "TraceBase.h"));
            var runnerTrace = File.ReadAllText(FindSourceFile("src", "runner", "trace.cpp"));

            StringAssert.Contains(managedTelemetry, "Retained as a no-op compatibility surface because telemetry is disabled in Kit.");
            Assert.IsFalse(managedTelemetry.Contains("base.WriteEvent", StringComparison.Ordinal), "Managed telemetry must not forward events to EventSource.");

            StringAssert.Contains(nativeTraceBase, "static bool IsDataDiagnosticsEnabled()");
            StringAssert.Contains(nativeTraceBase, "return false;");
            Assert.IsFalse(nativeTraceBase.Contains("TraceLoggingRegister(", StringComparison.Ordinal), "Native telemetry providers must not register from the shared base.");

            Assert.IsFalse(runnerTrace.Contains("TraceLoggingWrite(", StringComparison.Ordinal), "Runner trace methods should remain no-op.");
        }

        [TestMethod]
        public void ManagedTelemetryCompatibilitySurfaceShouldNotCarryTraceEventRuntime()
        {
            var centralPackages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));
            var managedTelemetryProject = File.ReadAllText(FindSourceFile("src", "common", "ManagedTelemetry", "Telemetry", "ManagedTelemetry.csproj"));
            var etwTrace = File.ReadAllText(FindSourceFile("src", "common", "ManagedTelemetry", "Telemetry", "EtwTrace.cs"));

            Assert.IsFalse(centralPackages.Contains("Microsoft.Diagnostics.Tracing.TraceEvent", StringComparison.Ordinal), "Kit should not keep a central package pin for the removed TraceEvent runtime.");
            Assert.IsFalse(managedTelemetryProject.Contains("Microsoft.Diagnostics.Tracing.TraceEvent", StringComparison.Ordinal), "Kit's no-op managed telemetry shim should not restore the TraceEvent package.");
            Assert.IsFalse(etwTrace.Contains("Microsoft.Diagnostics.Tracing.Session", StringComparison.Ordinal), "Kit's no-op ETWTrace shim should not reference TraceEvent sessions.");
            Assert.IsFalse(etwTrace.Contains("TraceEventSession", StringComparison.Ordinal), "Kit's no-op ETWTrace shim should not create TraceEvent sessions.");
            Assert.IsFalse(etwTrace.Contains("EnableEvents", StringComparison.Ordinal), "Kit's no-op ETWTrace shim should not enable managed EventSource listeners.");
            Assert.IsFalse(etwTrace.Contains("EnableProvider", StringComparison.Ordinal), "Kit's no-op ETWTrace shim should not enable ETW providers.");
            StringAssert.Contains(etwTrace, "public void Start()");
            StringAssert.Contains(etwTrace, "public void Stop()");
        }

        [TestMethod]
        public void KitManagedAppsShouldNotReferenceManagedTelemetry()
        {
            string[][] projectPaths =
            {
                new[] { "Kit.slnx" },
                new[] { "src", "common", "ManagedCommon", "ManagedCommon.csproj" },
                new[] { "src", "settings-ui", "Settings.UI.Library", "Settings.UI.Library.csproj" },
                new[] { "src", "settings-ui", "Settings.UI", "PowerToys.Settings.csproj" },
                new[] { "src", "settings-ui", "QuickAccess.UI", "PowerToys.QuickAccess.csproj" },
                new[] { "src", "settings-ui", "PowerToys.Settings.slnf" },
            };

            foreach (var pathParts in projectPaths)
            {
                var content = File.ReadAllText(FindSourceFile(pathParts));
                Assert.IsFalse(content.Contains("ManagedTelemetry", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not reference the managed telemetry project.");
            }

            string[][] sourcePaths =
            {
                new[] { "src", "common", "ManagedCommon", "RunnerHelper.cs" },
                new[] { "src", "settings-ui", "Settings.UI.Library", "EnabledModules.cs" },
                new[] { "src", "settings-ui", "Settings.UI.Library", "PowerPreviewProperties.cs" },
                new[] { "src", "settings-ui", "Settings.UI", "SettingsXAML", "App.xaml.cs" },
                new[] { "src", "settings-ui", "Settings.UI", "SettingsXAML", "MainWindow.xaml.cs" },
                new[] { "src", "settings-ui", "Settings.UI", "SettingsXAML", "Controls", "ShortcutControl", "ShortcutControl.xaml.cs" },
                new[] { "src", "settings-ui", "Settings.UI", "SettingsXAML", "Controls", "Dashboard", "ShortcutConflictControl.xaml.cs" },
                new[] { "src", "settings-ui", "QuickAccess.UI", "Services", "QuickAccessLauncher.cs" },
            };

            foreach (var pathParts in sourcePaths)
            {
                var content = File.ReadAllText(FindSourceFile(pathParts));
                Assert.IsFalse(content.Contains("PowerToysTelemetry", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not send managed telemetry.");
                Assert.IsFalse(content.Contains("Microsoft.PowerToys.Telemetry", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not import managed telemetry.");
            }
        }

        [TestMethod]
        public void RunnerHelperShouldCloseProcessHandlesAfterWaiting()
        {
            var runnerHelper = File.ReadAllText(FindSourceFile("src", "common", "ManagedCommon", "RunnerHelper.cs"));

            StringAssert.Contains(runnerHelper, "NativeMethods.CloseHandle(powerToysProcHandle)");
            StringAssert.Contains(runnerHelper, "NativeMethods.CloseHandle(hProcess)");
            StringAssert.Contains(runnerHelper, "hProcess = IntPtr.Zero;");
            StringAssert.Contains(runnerHelper, "NativeMethods.CloseHandle(runnerHandle)");
        }

        [TestMethod]
        public void KitMainSolutionShouldNotBuildAutoUpdaterExecutable()
        {
            var solution = File.ReadAllText(FindSourceFile("Kit.slnx"));

            Assert.IsFalse(solution.Contains("src/Update/PowerToys.Update.vcxproj", StringComparison.Ordinal), "Kit.slnx should not build the PowerToys updater executable.");
            Assert.IsFalse(solution.Contains("src/common/updating/updating.vcxproj", StringComparison.Ordinal), "Kit.slnx should not build the GitHub updater library.");
        }

        [TestMethod]
        public void RunnerShouldNotLinkOrLaunchAutoUpdater()
        {
            var runnerProject = File.ReadAllText(FindSourceFile("src", "runner", "Kit.vcxproj"));
            var runnerMain = File.ReadAllText(FindSourceFile("src", "runner", "main.cpp"));
            var updateUtils = File.ReadAllText(FindSourceFile("src", "runner", "UpdateUtils.cpp"));

            Assert.IsFalse(runnerProject.Contains(@"..\common\updating\updating.vcxproj", StringComparison.Ordinal), "Runner should not link the GitHub updater library.");
            StringAssert.Contains(runnerProject, "UpdateUtils.cpp", "Runner should compile Kit's check-only release notification worker.");
            Assert.IsFalse(runnerMain.Contains("uninstall_previous_msix_version_async", StringComparison.Ordinal), "Runner startup should not enter updater cleanup paths.");
            Assert.IsFalse(updateUtils.Contains("download_new_version_async", StringComparison.Ordinal), "UpdateUtils should not download installers.");
            Assert.IsFalse(updateUtils.Contains("PowerToys.Update.exe", StringComparison.Ordinal), "UpdateUtils should not launch the updater executable.");
        }

        [TestMethod]
        public void GeneralShouldExposeKitReleasesWithoutBottomAbout()
        {
            var generalPage = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "GeneralPage.xaml"));

            StringAssert.Contains(generalPage, "General_VersionAndUpdate");
            StringAssert.Contains(generalPage, "https://github.com/guijianchou/Kit/releases/");
            Assert.IsFalse(generalPage.Contains("General_About", StringComparison.Ordinal), "General should not keep a bottom About section.");
            Assert.IsFalse(generalPage.Contains("General_Repository", StringComparison.Ordinal), "General should not keep repository links in a removed About section.");
        }

        [TestMethod]
        public void RunnerShouldCheckKitReleasesOncePerDayWithoutAutoUpdating()
        {
            var runnerProject = File.ReadAllText(FindSourceFile("src", "runner", "Kit.vcxproj"));
            var runnerMain = File.ReadAllText(FindSourceFile("src", "runner", "main.cpp"));
            var settingsWindow = File.ReadAllText(FindSourceFile("src", "runner", "settings_window.cpp"));
            var updateUtils = File.ReadAllText(FindSourceFile("src", "runner", "UpdateUtils.cpp"));

            StringAssert.Contains(runnerProject, "UpdateUtils.cpp");
            StringAssert.Contains(runnerMain, "PeriodicUpdateWorker();");
            StringAssert.Contains(updateUtils, "https://api.github.com/repos/guijianchou/Kit/releases/latest");
            StringAssert.Contains(updateUtils, "html_url");
            StringAssert.Contains(updateUtils, "githubUpdateLastCheckedDate");
            StringAssert.Contains(updateUtils, "std::chrono::hours(24)");
            StringAssert.Contains(updateUtils, "std::chrono::hours(2)");
            StringAssert.Contains(updateUtils, "set_update_badge(true)");
            StringAssert.Contains(updateUtils, "notifications::show_toast_with_activations");
            StringAssert.Contains(updateUtils, "https://github.com/guijianchou/Kit/releases");
            StringAssert.Contains(updateUtils, "check_for_updates(UpdateCheckMode::Periodic)");
            StringAssert.Contains(updateUtils, "check_for_updates(UpdateCheckMode::Manual)");
            StringAssert.Contains(updateUtils, "elapsed < std::chrono::system_clock::duration::zero()");
            StringAssert.Contains(updateUtils, "retryAfterFailure = !check_for_updates(UpdateCheckMode::Periodic)");
            StringAssert.Contains(updateUtils, "std::this_thread::sleep_for(failedRetryInterval)");
            StringAssert.Contains(updateUtils, "UpdateState::store");
            StringAssert.Contains(updateUtils, "mode == UpdateCheckMode::Periodic");
            StringAssert.Contains(settingsWindow, "isUpdateCheckThreadRunning.compare_exchange_strong");
            Assert.IsFalse(updateUtils.Contains("idlePollInterval", StringComparison.Ordinal), "Runner should not wake every hour when the next update check time is known.");
            Assert.IsFalse(updateUtils.Contains("download_new_version_async", StringComparison.Ordinal), "Kit release check must not download installers.");
            Assert.IsFalse(updateUtils.Contains("LaunchPowerToysUpdate", StringComparison.Ordinal) && updateUtils.Contains("ShellExecuteEx", StringComparison.Ordinal), "Kit release check must not launch an updater.");
        }

        [TestMethod]
        public void TrayIconShouldExposeUpdateBadgeWithoutRestoringAutoUpdater()
        {
            var runnerProject = File.ReadAllText(FindSourceFile("src", "runner", "Kit.vcxproj"));
            var trayIcon = File.ReadAllText(FindSourceFile("src", "runner", "tray_icon.cpp"));
            var updateState = File.ReadAllText(FindSourceFile("src", "common", "updating", "updateState.cpp"));

            Assert.IsFalse(trayIcon.Contains("#include <common/updating/updateState.h>", StringComparison.Ordinal), "Tray startup should not depend on update state disk I/O.");
            Assert.IsFalse(trayIcon.Contains("UpdateState::read", StringComparison.Ordinal), "Tray startup should not read update state when the updater is disabled.");
            StringAssert.Contains(trayIcon, "update_available = false;");
            StringAssert.Contains(trayIcon, "LoadIcon(h_instance, MAKEINTRESOURCE(update_available ? APPICON_UPDATE : APPICON))");
            StringAssert.Contains(trayIcon, "InsertMenuW(h_sub_menu, 0, MF_BYPOSITION | MF_STRING, ID_UPDATE_MENU_COMMAND");
            StringAssert.Contains(trayIcon, "void set_tray_icon_update_available(bool available)");
            StringAssert.Contains(trayIcon, "update_available = available;");
            StringAssert.Contains(trayIcon, "LoadIcon(h_instance, MAKEINTRESOURCE(available ? APPICON_UPDATE : APPICON))");
            StringAssert.Contains(trayIcon, "Shell_NotifyIcon(NIM_MODIFY, &tray_icon_data);");
            StringAssert.Contains(runnerProject, @"..\common\updating\updateState.cpp", "Runner should reuse upstream's update-state file boundary so Settings can watch UpdateState.json.");
            StringAssert.Contains(updateState, @"Local\\KitRunnerUpdateStateMutex");
            Assert.IsFalse(runnerProject.Contains(@"..\common\updating\updating.vcxproj", StringComparison.Ordinal), "Runner should not restore the GitHub updater project reference just to show the badge.");
            Assert.IsFalse(updateState.Contains("PowerToysRunnerUpdateStateMutex", StringComparison.Ordinal), "Kit update-state mutex must not share the PowerToys runner mutex.");
        }

        [TestMethod]
        public void GeneralUpdateSettingsShouldDefaultOff()
        {
            var generalSettings = File.ReadAllText(FindSourceFile("src", "runner", "general_settings.cpp"));
            var generalSettingsModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "GeneralSettings.cs"));

            StringAssert.Contains(generalSettings, "static bool show_new_updates_toast_notification = false;");
            StringAssert.Contains(generalSettings, "static bool download_updates_automatically = false;");
            StringAssert.Contains(generalSettings, "static bool enable_experimentation = false;");
            StringAssert.Contains(generalSettings, "GetNamedBoolean(L\"show_new_updates_toast_notification\", false)");
            StringAssert.Contains(generalSettings, "GetNamedBoolean(L\"download_updates_automatically\", false)");
            StringAssert.Contains(generalSettings, "GetNamedBoolean(L\"enable_experimentation\", false)");
            StringAssert.Contains(generalSettingsModel, "EnableExperimentation = false;");
        }

        [TestMethod]
        public void LightSwitchTraceShouldNotWriteEtwEvents()
        {
            var lightSwitchTrace = File.ReadAllText(FindSourceFile("src", "modules", "LightSwitch", "LightSwitchModuleInterface", "trace.cpp"));

            Assert.IsFalse(lightSwitchTrace.Contains("TraceLoggingRegister", StringComparison.Ordinal));
            Assert.IsFalse(lightSwitchTrace.Contains("TraceLoggingWrite(", StringComparison.Ordinal));
        }

        [TestMethod]
        public void ActiveNativeModuleTracesShouldStayNoOp()
        {
            string[][] activeModuleTraceFiles =
            {
                new[] { "src", "modules", "awake", "AwakeModuleInterface", "trace.cpp" },
                new[] { "src", "modules", "powerdisplay", "PowerDisplayModuleInterface", "Trace.cpp" },
                new[] { "src", "modules", "lightswitch", "LightSwitchModuleInterface", "trace.cpp" },
            };

            foreach (var pathParts in activeModuleTraceFiles)
            {
                var traceSource = File.ReadAllText(FindSourceFile(pathParts));
                Assert.IsFalse(traceSource.Contains("TRACELOGGING_DEFINE_PROVIDER", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not define an ETW provider in Kit.");
                Assert.IsFalse(traceSource.Contains("TraceLoggingWriteWrapper", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not emit ETW events in Kit.");
                Assert.IsFalse(traceSource.Contains("TraceLoggingWrite(", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not emit ETW events in Kit.");
                Assert.IsFalse(traceSource.Contains("TraceLoggingOptionProjectTelemetry", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not carry PowerToys telemetry provider metadata in Kit.");
            }
        }
    }
}
