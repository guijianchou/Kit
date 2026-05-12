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
            var runnerProject = File.ReadAllText(FindSourceFile("src", "runner", "Kit.vcxproj"));

            Assert.IsFalse(main.Contains("settings_telemetry::init();", StringComparison.Ordinal));
            Assert.IsFalse(runnerProject.Contains("settings_telemetry.cpp", StringComparison.Ordinal), "Runner should not compile the settings telemetry worker.");
            Assert.IsFalse(runnerProject.Contains("settings_telemetry.h", StringComparison.Ordinal), "Runner should not include the settings telemetry worker header.");
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
    }
}
