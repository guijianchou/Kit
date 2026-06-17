// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
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

        private static string FindSourceDirectory(params string[] relativePathParts)
        {
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var pathParts = new string[relativePathParts.Length + 1];
                pathParts[0] = directory.FullName;
                Array.Copy(relativePathParts, 0, pathParts, 1, relativePathParts.Length);

                var candidate = Path.Combine(pathParts);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            Assert.Fail($"Could not find source directory: {Path.Combine(relativePathParts)}");
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
        public void NativeTelemetryCompatibilitySurfacesShouldStayNoOp()
        {
            var nativeTraceBase = File.ReadAllText(FindSourceFile("src", "common", "Telemetry", "TraceBase.h"));
            var runnerTrace = File.ReadAllText(FindSourceFile("src", "runner", "trace.cpp"));

            StringAssert.Contains(nativeTraceBase, "static bool IsDataDiagnosticsEnabled()");
            StringAssert.Contains(nativeTraceBase, "return false;");
            Assert.IsFalse(nativeTraceBase.Contains("TraceLoggingRegister(", StringComparison.Ordinal), "Native telemetry providers must not register from the shared base.");

            Assert.IsFalse(runnerTrace.Contains("TraceLoggingWrite(", StringComparison.Ordinal), "Runner trace methods should remain no-op.");
        }

        [TestMethod]
        public void KitRunnerTraceShouldNotDependOnTelemetryInfrastructure()
        {
            var runnerTraceHeader = File.ReadAllText(FindSourceFile("src", "runner", "trace.h"));
            var runnerTraceSource = File.ReadAllText(FindSourceFile("src", "runner", "trace.cpp"));
            var runnerMain = File.ReadAllText(FindSourceFile("src", "runner", "main.cpp"));
            var runnerProject = File.ReadAllText(FindSourceFile("src", "runner", "Kit.vcxproj"));

            Assert.IsFalse(runnerTraceHeader.Contains("common/Telemetry", StringComparison.Ordinal), "Runner trace header should not include telemetry headers.");
            Assert.IsFalse(runnerTraceHeader.Contains("TraceBase", StringComparison.Ordinal), "Runner trace should not inherit telemetry compatibility base classes.");
            Assert.IsFalse(runnerTraceSource.Contains("TRACELOGGING_DEFINE_PROVIDER", StringComparison.Ordinal), "Runner trace should not define an ETW provider.");
            Assert.IsFalse(runnerTraceSource.Contains("ProjectTelemetry", StringComparison.Ordinal), "Runner trace should not include PowerToys telemetry provider metadata.");
            Assert.IsFalse(runnerMain.Contains("Shared::Trace::ETWTrace", StringComparison.Ordinal), "Runner startup should not create the inactive ETW trace object.");
            Assert.IsFalse(runnerMain.Contains("trace.UpdateState", StringComparison.Ordinal), "Runner startup should not update inactive ETW trace state.");
            Assert.IsFalse(runnerProject.Contains(@"common\Telemetry", StringComparison.Ordinal), "Runner project should not keep telemetry include paths.");
            Assert.IsFalse(runnerProject.Contains("EtwTrace.vcxproj", StringComparison.Ordinal), "Runner project should not reference inactive ETW trace targets.");
        }

        [TestMethod]
        public void ManagedTelemetryCompatibilitySourceShouldBeDeleted()
        {
            var solutionPath = FindSourceFile("Kit.slnx");
            var repoRoot = Path.GetDirectoryName(solutionPath)!;
            var managedTelemetryRoot = Path.Combine(repoRoot, "src", "common", "ManagedTelemetry");
            var telemetryBase = Path.Combine(repoRoot, "src", "common", "Telemetry", "TelemetryBase.cs");
            var centralPackages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));
            var solution = File.ReadAllText(solutionPath);

            Assert.IsFalse(centralPackages.Contains("Microsoft.Diagnostics.Tracing.TraceEvent", StringComparison.Ordinal), "Kit should not keep a central package pin for the removed TraceEvent runtime.");
            Assert.IsFalse(Directory.Exists(managedTelemetryRoot), "Kit should delete the inactive ManagedTelemetry source tree instead of keeping a no-op shim.");
            Assert.IsFalse(File.Exists(telemetryBase), "Kit should delete the managed TelemetryBase loose source once ManagedTelemetry is inactive.");
            Assert.IsFalse(solution.Contains("src/common/Telemetry/TelemetryBase.cs", StringComparison.Ordinal), "Kit.slnx should not keep a loose entry for deleted managed telemetry source.");
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
        public void ActiveManagedModulesShouldNotSendManagedTelemetry()
        {
            string[][] activeManagedModuleRoots =
            {
                new[] { "src", "modules", "awake", "Awake" },
            };

            foreach (var rootParts in activeManagedModuleRoots)
            {
                var moduleRoot = FindSourceDirectory(rootParts);
                var telemetryDirectory = Path.Combine(moduleRoot, "Telemetry");
                if (Directory.Exists(telemetryDirectory))
                {
                    Assert.IsFalse(Directory.EnumerateFiles(telemetryDirectory, "*.cs", SearchOption.AllDirectories).Any(), $"{Path.Combine(rootParts)} should not keep telemetry event source files.");
                }

                foreach (var sourceFile in Directory.EnumerateFiles(moduleRoot, "*.cs", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(moduleRoot, sourceFile);
                    if (relativePath.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
                    {
                        continue;
                    }

                    var content = File.ReadAllText(sourceFile);
                    Assert.IsFalse(content.Contains("Microsoft.PowerToys.Telemetry", StringComparison.Ordinal), $"{sourceFile} should not import managed telemetry.");
                    Assert.IsFalse(content.Contains("PowerToysTelemetry.Log.WriteEvent", StringComparison.Ordinal), $"{sourceFile} should not send managed telemetry.");
                }
            }
        }

        [TestMethod]
        public void AwakeReadmeShouldDocumentTelemetryFreeKitBehavior()
        {
            var readme = File.ReadAllText(FindSourceFile("src", "modules", "awake", "README.md"));

            StringAssert.Contains(readme, "Kit's Awake module does not emit telemetry events.");
            Assert.IsFalse(readme.Contains("Microsoft.PowerToys.Telemetry", StringComparison.Ordinal), "Awake README should not document the removed managed telemetry package.");
            Assert.IsFalse(readme.Contains("The module emits telemetry events", StringComparison.Ordinal), "Awake README should not claim Kit emits telemetry events.");
        }

        [TestMethod]
        public void InteropShouldNotKeepPowerDisplaySettingsTelemetryIpc()
        {
            var sharedConstants = File.ReadAllText(FindSourceFile("src", "common", "interop", "shared_constants.h"));
            var interopConstantsCpp = File.ReadAllText(FindSourceFile("src", "common", "interop", "Constants.cpp"));
            var interopConstantsHeader = File.ReadAllText(FindSourceFile("src", "common", "interop", "Constants.h"));
            var interopConstantsIdl = File.ReadAllText(FindSourceFile("src", "common", "interop", "Constants.idl"));

            Assert.IsFalse(sharedConstants.Contains("POWER_DISPLAY_SEND_SETTINGS_TELEMETRY_EVENT", StringComparison.Ordinal), "Interop constants should not expose PowerDisplay settings telemetry events.");
            Assert.IsFalse(interopConstantsCpp.Contains("PowerDisplaySendSettingsTelemetryEvent", StringComparison.Ordinal), "Interop C++ projection should not expose PowerDisplay settings telemetry events.");
            Assert.IsFalse(interopConstantsHeader.Contains("PowerDisplaySendSettingsTelemetryEvent", StringComparison.Ordinal), "Interop header should not expose PowerDisplay settings telemetry events.");
            Assert.IsFalse(interopConstantsIdl.Contains("PowerDisplaySendSettingsTelemetryEvent", StringComparison.Ordinal), "Interop IDL should not expose PowerDisplay settings telemetry events.");
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
            var installerCpp = File.ReadAllText(FindSourceFile("src", "common", "updating", "installer.cpp"));
            var installerHeader = File.ReadAllText(FindSourceFile("src", "common", "updating", "installer.h"));

            Assert.IsFalse(runnerProject.Contains(@"..\common\updating\updating.vcxproj", StringComparison.Ordinal), "Runner should not link the GitHub updater library.");
            StringAssert.Contains(runnerProject, "UpdateUtils.cpp", "Runner should compile Kit's check-only release notification worker.");
            Assert.IsFalse(runnerMain.Contains("uninstall_previous_msix_version_async", StringComparison.Ordinal), "Runner startup should not enter updater cleanup paths.");
            Assert.IsFalse(updateUtils.Contains("download_new_version_async", StringComparison.Ordinal), "UpdateUtils should not download installers.");
            Assert.IsFalse(updateUtils.Contains("PowerToys.Update.exe", StringComparison.Ordinal), "UpdateUtils should not launch the updater executable.");
            Assert.IsFalse(installerCpp.Contains("Microsoft.PowerToys", StringComparison.Ordinal), "Kit should not keep PowerToys MSIX package cleanup targets.");
            Assert.IsFalse(installerCpp.Contains("MSIX_PACKAGE_NAME", StringComparison.Ordinal), "Kit should not keep PowerToys MSIX package cleanup targets.");
            Assert.IsFalse(installerCpp.Contains("MSIX_PACKAGE_PUBLISHER", StringComparison.Ordinal), "Kit should not keep PowerToys MSIX package cleanup targets.");
            Assert.IsFalse(installerCpp.Contains("RemovePackageAsync", StringComparison.Ordinal), "Kit should not keep an unused package uninstall path.");
            Assert.IsFalse(installerCpp.Contains("uninstall_previous_msix_version_async", StringComparison.Ordinal), "Kit should not keep the unused PowerToys MSIX cleanup implementation.");
            Assert.IsFalse(installerHeader.Contains("uninstall_previous_msix_version_async", StringComparison.Ordinal), "Kit should not keep the unused PowerToys MSIX cleanup declaration.");
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
                new[] { "src", "modules", "lightswitch", "LightSwitchModuleInterface", "trace.cpp" },
                new[] { "src", "modules", "lightswitch", "LightSwitchService", "trace.cpp" },
                new[] { "src", "modules", "Monitor", "MonitorModuleInterface", "trace.cpp" },
            };

            foreach (var pathParts in activeModuleTraceFiles)
            {
                var traceSource = File.ReadAllText(FindSourceFile(pathParts));
                Assert.IsFalse(traceSource.Contains("TRACELOGGING_DEFINE_PROVIDER", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not define an ETW provider in Kit.");
                Assert.IsFalse(traceSource.Contains("TraceLoggingRegister", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not register an ETW provider in Kit.");
                Assert.IsFalse(traceSource.Contains("TraceLoggingUnregister", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not unregister an ETW provider in Kit.");
                Assert.IsFalse(traceSource.Contains("TraceLoggingWriteWrapper", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not emit ETW events in Kit.");
                Assert.IsFalse(traceSource.Contains("TraceLoggingWrite(", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not emit ETW events in Kit.");
                Assert.IsFalse(traceSource.Contains("TraceLoggingOptionProjectTelemetry", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not carry PowerToys telemetry provider metadata in Kit.");
                Assert.IsFalse(traceSource.Contains("ProjectTelemetryPrivacyDataTag", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not tag telemetry events in Kit.");
            }
        }

        [TestMethod]
        public void ModuleTemplateTraceShouldDefaultToNoOp()
        {
            var templateTrace = File.ReadAllText(FindSourceFile("tools", "project_template", "ModuleTemplate", "trace.cpp"));

            Assert.IsFalse(templateTrace.Contains("TRACELOGGING_DEFINE_PROVIDER", StringComparison.Ordinal), "New Kit module template should not define an ETW provider by default.");
            Assert.IsFalse(templateTrace.Contains("TraceLoggingRegister", StringComparison.Ordinal), "New Kit module template should not register an ETW provider by default.");
            Assert.IsFalse(templateTrace.Contains("TraceLoggingWriteWrapper", StringComparison.Ordinal), "New Kit module template should not emit ETW events by default.");
            Assert.IsFalse(templateTrace.Contains("TraceLoggingWrite(", StringComparison.Ordinal), "New Kit module template should not emit ETW events by default.");
            Assert.IsFalse(templateTrace.Contains("TraceLoggingOptionProjectTelemetry", StringComparison.Ordinal), "New Kit module template should not carry PowerToys telemetry provider metadata by default.");
            Assert.IsFalse(templateTrace.Contains("ProjectTelemetryPrivacyDataTag", StringComparison.Ordinal), "New Kit module template should not tag telemetry events by default.");
        }

        [TestMethod]
        public void ActiveNativeTraceProjectsShouldNotDependOnTelemetryBuildTargets()
        {
            string[][] projectPaths =
            {
                new[] { "src", "modules", "awake", "AwakeModuleInterface", "AwakeModuleInterface.vcxproj" },
                new[] { "src", "modules", "lightswitch", "LightSwitchModuleInterface", "LightSwitchModuleInterface.vcxproj" },
                new[] { "src", "modules", "lightswitch", "LightSwitchService", "LightSwitchService.vcxproj" },
                new[] { "src", "modules", "Monitor", "MonitorModuleInterface", "MonitorModuleInterface.vcxproj" },
                new[] { "tools", "project_template", "ModuleTemplate", "ModuleTemplate.vcxproj" },
                new[] { "tools", "project_template", "ModuleTemplate", "ModuleTemplateCompileTest.vcxproj" },
            };

            foreach (var pathParts in projectPaths)
            {
                var project = File.ReadAllText(FindSourceFile(pathParts));
                Assert.IsFalse(project.Contains(@"common\Telemetry", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not keep telemetry include paths.");
                Assert.IsFalse(project.Contains("EtwTrace.vcxproj", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not reference the inactive ETW trace project.");
            }
        }

        [TestMethod]
        public void ActiveNativeTraceHeadersShouldNotDependOnTelemetryBase()
        {
            string[][] headerPaths =
            {
                new[] { "src", "modules", "awake", "AwakeModuleInterface", "trace.h" },
                new[] { "src", "modules", "lightswitch", "LightSwitchModuleInterface", "trace.h" },
                new[] { "src", "modules", "lightswitch", "LightSwitchService", "trace.h" },
                new[] { "src", "modules", "Monitor", "MonitorModuleInterface", "trace.h" },
                new[] { "tools", "project_template", "ModuleTemplate", "trace.h" },
            };

            foreach (var pathParts in headerPaths)
            {
                var header = File.ReadAllText(FindSourceFile(pathParts));
                Assert.IsFalse(header.Contains("common/Telemetry", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not include telemetry headers.");
                Assert.IsFalse(header.Contains("TraceBase", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should not inherit telemetry compatibility base classes.");
            }
        }

        [TestMethod]
        public void KitInteropConstantsShouldNotExposeInactiveRuntimeIpc()
        {
            var interopConstantsIdl = File.ReadAllText(FindSourceFile("src", "common", "interop", "Constants.idl"));
            var interopConstantsHeader = File.ReadAllText(FindSourceFile("src", "common", "interop", "Constants.h"));
            var interopConstantsCpp = File.ReadAllText(FindSourceFile("src", "common", "interop", "Constants.cpp"));
            var sharedConstants = File.ReadAllText(FindSourceFile("src", "common", "interop", "shared_constants.h"));

            string[] inactiveProjectionMembers =
            {
                "PowerLauncherSharedEvent",
                "PowerLauncherCentralizedHookSharedEvent",
                "RunSendSettingsTelemetryEvent",
                "RunExitEvent",
                "FZEExitEvent",
                "FZEToggleEvent",
                "ColorPickerSendSettingsTelemetryEvent",
                "ShowColorPickerSharedEvent",
                "TerminateColorPickerSharedEvent",
                "AdvancedPasteShowUIMessage",
                "AdvancedPasteMarkdownMessage",
                "AdvancedPasteJsonMessage",
                "AdvancedPasteAdditionalActionMessage",
                "AdvancedPasteCustomActionMessage",
                "AdvancedPasteTerminateAppMessage",
                "AdvancedPasteShowUIEvent",
                "AlwaysOnTopPinEvent",
                "FindMyMouseTriggerEvent",
                "MouseHighlighterTriggerEvent",
                "MouseCrosshairsTriggerEvent",
                "CursorWrapTriggerEvent",
                "ZoomItZoomEvent",
                "ZoomItDrawEvent",
                "ZoomItBreakEvent",
                "ZoomItLiveZoomEvent",
                "ZoomItSnipEvent",
                "ZoomItRecordEvent",
                "ShowPowerOCRSharedEvent",
                "TerminatePowerOCRSharedEvent",
                "MouseJumpShowPreviewEvent",
                "TerminateMouseJumpSharedEvent",
                "ShowPeekEvent",
                "TerminatePeekEvent",
                "PowerAccentExitEvent",
                "ShortcutGuideTriggerEvent",
                "RegistryPreviewTriggerEvent",
                "MeasureToolTriggerEvent",
                "GcodePreviewResizeEvent",
                "BgcodePreviewResizeEvent",
                "QoiPreviewResizeEvent",
                "DevFilesPreviewResizeEvent",
                "MarkdownPreviewResizeEvent",
                "PdfPreviewResizeEvent",
                "SvgPreviewResizeEvent",
                "ShowHostsSharedEvent",
                "ShowHostsAdminSharedEvent",
                "TerminateHostsSharedEvent",
                "CropAndLockThumbnailEvent",
                "CropAndLockReparentEvent",
                "CropAndLockScreenshotEvent",
                "ShowEnvironmentVariablesSharedEvent",
                "ShowEnvironmentVariablesAdminSharedEvent",
                "WorkspacesLaunchEditorEvent",
                "WorkspacesHotkeyEvent",
                "ShowCmdPalEvent",
                "MWBToggleEasyMouseEvent",
                "MWBReconnectEvent",
                "OpenNewKeyboardManagerEvent",
                "KeyboardManagerEngineInstanceMutex",
            };

            foreach (var member in inactiveProjectionMembers)
            {
                Assert.IsFalse(interopConstantsIdl.Contains(member, StringComparison.Ordinal), $"Constants.idl should not expose inactive member {member}.");
                Assert.IsFalse(interopConstantsHeader.Contains(member, StringComparison.Ordinal), $"Constants.h should not expose inactive member {member}.");
                Assert.IsFalse(interopConstantsCpp.Contains(member, StringComparison.Ordinal), $"Constants.cpp should not expose inactive member {member}.");
            }

            string[] inactiveSharedSymbols =
            {
                "KEYBOARDMANAGER_INJECTED_FLAG",
                "POWER_LAUNCHER_SHARED_EVENT",
                "POWER_LAUNCHER_CENTRALIZED_HOOK_SHARED_EVENT",
                "RUN_SEND_SETTINGS_TELEMETRY_EVENT",
                "RUN_EXIT_EVENT",
                "FZE_EXIT_EVENT",
                "FANCY_ZONES_EDITOR_TOGGLE_EVENT",
                "COLOR_PICKER_SEND_SETTINGS_TELEMETRY_EVENT",
                "ADVANCED_PASTE_SHOW_UI_MESSAGE",
                "ADVANCED_PASTE_MARKDOWN_MESSAGE",
                "ADVANCED_PASTE_JSON_MESSAGE",
                "ADVANCED_PASTE_ADDITIONAL_ACTION_MESSAGE",
                "ADVANCED_PASTE_CUSTOM_ACTION_MESSAGE",
                "ADVANCED_PASTE_TERMINATE_APP_MESSAGE",
                "ADVANCED_PASTE_SHOW_UI_EVENT",
                "SHOW_COLOR_PICKER_SHARED_EVENT",
                "TERMINATE_COLOR_PICKER_SHARED_EVENT",
                "SHORTCUT_GUIDE_TRIGGER_EVENT",
                "SHORTCUT_GUIDE_EXIT_EVENT",
                "SHOW_HOSTS_EVENT",
                "SHOW_HOSTS_ADMIN_EVENT",
                "TERMINATE_HOSTS_EVENT",
                "FIND_MY_MOUSE_TRIGGER_EVENT",
                "MOUSE_HIGHLIGHTER_TRIGGER_EVENT",
                "MOUSE_CROSSHAIRS_TRIGGER_EVENT",
                "CURSOR_WRAP_TRIGGER_EVENT",
                "ALWAYS_ON_TOP_PIN_EVENT",
                "ALWAYS_ON_TOP_TERMINATE_EVENT",
                "ALWAYS_ON_TOP_INCREASE_OPACITY_EVENT",
                "ALWAYS_ON_TOP_DECREASE_OPACITY_EVENT",
                "POWERACCENT_EXIT_EVENT",
                "SHOW_POWEROCR_SHARED_EVENT",
                "TERMINATE_POWEROCR_SHARED_EVENT",
                "MOUSE_JUMP_SHOW_PREVIEW_EVENT",
                "TERMINATE_MOUSE_JUMP_SHARED_EVENT",
                "REGISTRY_PREVIEW_TRIGGER_EVENT",
                "MEASURE_TOOL_TRIGGER_EVENT",
                "GCODE_PREVIEW_RESIZE_EVENT",
                "BGCODE_PREVIEW_RESIZE_EVENT",
                "QOI_PREVIEW_RESIZE_EVENT",
                "DEV_FILES_PREVIEW_RESIZE_EVENT",
                "MARKDOWN_PREVIEW_RESIZE_EVENT",
                "PDF_PREVIEW_RESIZE_EVENT",
                "SVG_PREVIEW_RESIZE_EVENT",
                "SHOW_PEEK_SHARED_EVENT",
                "TERMINATE_PEEK_SHARED_EVENT",
                "TERMINATE_KBM_SHARED_EVENT",
                "CROP_AND_LOCK_REPARENT_EVENT",
                "CROP_AND_LOCK_THUMBNAIL_EVENT",
                "CROP_AND_LOCK_SCREENSHOT_EVENT",
                "CROP_AND_LOCK_EXIT_EVENT",
                "SHOW_ENVIRONMENT_VARIABLES_EVENT",
                "SHOW_ENVIRONMENT_VARIABLES_ADMIN_EVENT",
                "ZOOMIT_REFRESH_SETTINGS_EVENT",
                "GRABANDMOVE_REFRESH_SETTINGS_EVENT",
                "GRABANDMOVE_EXIT_EVENT",
                "ZOOMIT_EXIT_EVENT",
                "ZOOMIT_ZOOM_EVENT",
                "ZOOMIT_DRAW_EVENT",
                "ZOOMIT_BREAK_EVENT",
                "ZOOMIT_LIVEZOOM_EVENT",
                "ZOOMIT_SNIP_EVENT",
                "ZOOMIT_SNIPOCR_EVENT",
                "ZOOMIT_RECORD_EVENT",
                "OPEN_NEW_KEYBOARD_MANAGER_EVENT",
                "KEYBOARD_MANAGER_ENGINE_INSTANCE_MUTEX",
                "CMDPAL_SHOW_EVENT",
                "CMDPAL_EXIT_EVENT",
                "MWB_TOGGLE_EASY_MOUSE_EVENT",
                "MWB_RECONNECT_EVENT",
                "WORKSPACES_LAUNCH_EDITOR_EVENT",
                "WORKSPACES_HOTKEY_EVENT",
            };

            foreach (var symbol in inactiveSharedSymbols)
            {
                Assert.IsFalse(sharedConstants.Contains(symbol, StringComparison.Ordinal), $"shared_constants.h should not keep inactive IPC symbol {symbol}.");
            }
        }

        [TestMethod]
        public void SettingsTerminationIpcShouldUseKitNamedProjection()
        {
            var interopConstantsIdl = File.ReadAllText(FindSourceFile("src", "common", "interop", "Constants.idl"));
            var interopConstantsHeader = File.ReadAllText(FindSourceFile("src", "common", "interop", "Constants.h"));
            var interopConstantsCpp = File.ReadAllText(FindSourceFile("src", "common", "interop", "Constants.cpp"));
            var settingsApp = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "App.xaml.cs"));

            StringAssert.Contains(interopConstantsIdl, "KitRunnerTerminateSettingsEvent");
            StringAssert.Contains(interopConstantsHeader, "KitRunnerTerminateSettingsEvent");
            StringAssert.Contains(interopConstantsCpp, "Constants::KitRunnerTerminateSettingsEvent");
            StringAssert.Contains(settingsApp, "Constants.KitRunnerTerminateSettingsEvent()");

            Assert.IsFalse(interopConstantsIdl.Contains("PowerToysRunnerTerminateSettingsEvent", StringComparison.Ordinal), "Interop IDL should not expose Kit's active Settings termination event through a PowerToys-named method.");
            Assert.IsFalse(interopConstantsHeader.Contains("PowerToysRunnerTerminateSettingsEvent", StringComparison.Ordinal), "Interop header should not expose Kit's active Settings termination event through a PowerToys-named method.");
            Assert.IsFalse(interopConstantsCpp.Contains("PowerToysRunnerTerminateSettingsEvent", StringComparison.Ordinal), "Interop C++ projection should not expose Kit's active Settings termination event through a PowerToys-named method.");
            Assert.IsFalse(settingsApp.Contains("PowerToysRunnerTerminateSettingsEvent", StringComparison.Ordinal), "Settings should consume the Kit-named Settings termination event projection.");
        }
    }
}
