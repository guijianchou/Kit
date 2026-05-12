// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;
using System.IO;
using System.Linq;

using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Library.Helpers;
using Microsoft.PowerToys.Settings.UI.Library.Interfaces;
using Microsoft.PowerToys.Settings.UI.Library.Utilities;
using Microsoft.PowerToys.Settings.UI.UnitTests.BackwardsCompatibility;
using Microsoft.PowerToys.Settings.UI.UnitTests.Mocks;
using Microsoft.PowerToys.Settings.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

using JsonSerializer = System.Text.Json.JsonSerializer;

namespace ViewModelTests
{
    [TestClass]
    public class General
    {
        public const string GeneralSettingsFileName = "Test\\GeneralSettings";
        private static readonly string[] KitActiveEnabledModuleKeys = { "Awake", "LightSwitch", "Monitor", "PowerDisplay" };

        private Mock<SettingsUtils> mockGeneralSettingsUtils;

        [TestInitialize]
        public void SetUpStubSettingUtils()
        {
            mockGeneralSettingsUtils = ISettingsUtilsMocks.GetStubSettingsUtils<GeneralSettings>();
        }

        private sealed class TestGeneralViewModel : GeneralViewModel
        {
            public TestGeneralViewModel(
                Microsoft.PowerToys.Settings.UI.Library.Interfaces.ISettingsRepository<GeneralSettings> settingsRepository,
                string runAsAdminText,
                string runAsUserText,
                bool isElevated,
                bool isAdmin,
                Func<string, int> ipcMSGCallBackFunc,
                Func<string, int> ipcMSGRestartAsAdminMSGCallBackFunc,
                Func<string, int> ipcMSGCheckForUpdatesCallBackFunc,
                string configFileSubfolder = "")
                : base(settingsRepository, runAsAdminText, runAsUserText, isElevated, isAdmin, ipcMSGCallBackFunc, ipcMSGRestartAsAdminMSGCallBackFunc, ipcMSGCheckForUpdatesCallBackFunc, configFileSubfolder)
            {
            }

            protected override Microsoft.UI.Dispatching.DispatcherQueue GetDispatcherQueue()
            {
                return null;
            }
        }

        private TestGeneralViewModel CreateViewModel(
            ISettingsRepository<GeneralSettings> settingsRepository = null,
            Func<string, int> sendMockIPCConfigMSG = null,
            Func<string, int> sendCheckForUpdatesIPCMessage = null)
        {
            return new TestGeneralViewModel(
                settingsRepository: settingsRepository ?? SettingsRepository<GeneralSettings>.GetInstance(mockGeneralSettingsUtils.Object),
                runAsAdminText: "GeneralSettings_RunningAsAdminText",
                runAsUserText: "GeneralSettings_RunningAsUserText",
                isElevated: false,
                isAdmin: false,
                ipcMSGCallBackFunc: sendMockIPCConfigMSG ?? (_ => 0),
                ipcMSGRestartAsAdminMSGCallBackFunc: _ => 0,
                ipcMSGCheckForUpdatesCallBackFunc: sendCheckForUpdatesIPCMessage ?? (_ => 0),
                configFileSubfolder: GeneralSettingsFileName);
        }

        private static ISettingsRepository<GeneralSettings> CreateRepository(GeneralSettings settings)
        {
            var repository = new Mock<ISettingsRepository<GeneralSettings>>();
            repository.SetupProperty(x => x.SettingsConfig, settings);
            repository.Setup(x => x.ReloadSettings()).Returns(true);
            return repository.Object;
        }

        private static T GetViewModelProperty<T>(GeneralViewModel viewModel, string propertyName)
        {
            var property = typeof(GeneralViewModel).GetProperty(propertyName);
            Assert.IsNotNull(property, $"{propertyName} should be exposed by GeneralViewModel.");
            return (T)property.GetValue(viewModel);
        }

        private static void SetViewModelField<T>(GeneralViewModel viewModel, string fieldName, T value)
        {
            var field = typeof(GeneralViewModel).GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"{fieldName} should be present on GeneralViewModel.");
            field.SetValue(viewModel, value);
        }

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
        public void KitGeneralPageShouldExposeRetainedSectionsWithVersionUpdateGroup()
        {
            var xaml = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "GeneralPage.xaml"));

            StringAssert.Contains(xaml, "x:Uid=\"General_VersionAndUpdate\"");
            StringAssert.Contains(xaml, "x:Uid=\"Admin_Mode\"");
            StringAssert.Contains(xaml, "x:Uid=\"Appearance_Behavior\"");
            StringAssert.Contains(xaml, "x:Uid=\"General_SettingsBackupAndRestoreTitle\"");
            StringAssert.Contains(xaml, "x:Uid=\"General_Experimentation\"");
            StringAssert.Contains(xaml, "x:Uid=\"GeneralPage_EnableQuickAccess\"");
            StringAssert.Contains(xaml, "x:Uid=\"ShowSystemTrayIcon\"");
            StringAssert.Contains(xaml, "https://github.com/guijianchou/Kit/releases");

            Assert.IsFalse(xaml.Contains("General_DiagnosticsAndFeedback", StringComparison.Ordinal));
            Assert.IsFalse(xaml.Contains("GeneralPage_AutoDownloadAndInstallUpdates", StringComparison.Ordinal));
            Assert.IsFalse(xaml.Contains("GeneralPage_ShowNewUpdatesToast", StringComparison.Ordinal));
            Assert.IsFalse(xaml.Contains("GeneralPage_ReportBugPackage", StringComparison.Ordinal));
            Assert.IsFalse(xaml.Contains("github.com/microsoft/PowerToys/releases", StringComparison.Ordinal));
        }

        [TestMethod]
        public void KitGeneralShouldRemoveBottomAboutSection()
        {
            var xaml = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "GeneralPage.xaml"));

            Assert.IsFalse(xaml.Contains("x:Uid=\"General_About\"", StringComparison.Ordinal), "General should not keep a bottom About section because the version is already shown in the update section.");
            Assert.IsFalse(xaml.Contains("<Run x:Uid=\"General_Repository\" />", StringComparison.Ordinal), "General should not keep the old repository-only About card.");
        }

        [TestMethod]
        public void KitGeneralVersionSectionShouldShowReleaseNotesAndManualCheckOnly()
        {
            var xaml = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "GeneralPage.xaml"));

            var versionIndex = xaml.IndexOf("x:Uid=\"General_VersionAndUpdate\"", StringComparison.Ordinal);
            Assert.IsTrue(versionIndex >= 0, "Version and update section should be present.");

            var versionXaml = xaml.Substring(versionIndex);
            StringAssert.Contains(versionXaml, "x:Uid=\"ReleaseNotes\"");
            StringAssert.Contains(versionXaml, "https://github.com/guijianchou/Kit/releases");
            StringAssert.Contains(versionXaml, "x:Uid=\"GeneralPage_CheckForUpdates\"");
            StringAssert.Contains(versionXaml, "IsEnabled=\"{x:Bind ViewModel.IsCheckForUpdatesButtonEnabled, Mode=OneWay}\"");
            StringAssert.Contains(versionXaml, "x:Uid=\"GeneralPage_ViewRelease\"");
            StringAssert.Contains(versionXaml, "NavigateUri=\"{x:Bind ViewModel.PowerToysNewAvailableVersionUri, Mode=OneWay}\"");
            StringAssert.Contains(versionXaml, "Visibility=\"{x:Bind ViewModel.IsUpdateAvailable, Converter={StaticResource BoolToVisibilityConverter}, Mode=OneWay}\"");
            Assert.IsTrue(versionXaml.IndexOf("</tkcontrols:SettingsExpander>", StringComparison.Ordinal) < versionXaml.IndexOf("x:Uid=\"GeneralPage_ViewRelease\"", StringComparison.Ordinal), "Update result InfoBar should sit below the expander like PowerToys-main, not inside the expander content.");
            Assert.IsFalse(versionXaml.Contains("GeneralPage_AutoDownloadAndInstallUpdates", StringComparison.Ordinal), "Kit should not expose auto-download settings.");
            Assert.IsFalse(versionXaml.Contains("General_DownloadAndInstall", StringComparison.Ordinal), "Kit should not expose installer download actions.");
            Assert.IsFalse(versionXaml.Contains("General_InstallNow", StringComparison.Ordinal), "Kit should not expose installer install actions.");
        }

        [TestMethod]
        public void CheckForUpdatesCommandShouldSendKitReleaseCheckAction()
        {
            string sentMessage = null;
            var viewModel = CreateViewModel(sendCheckForUpdatesIPCMessage: msg =>
            {
                sentMessage = msg;
                return 0;
            });

            viewModel.CheckForUpdatesEventHandler.Execute(null);

            Assert.IsNotNull(sentMessage);
            StringAssert.Contains(sentMessage, "\"action_name\":\"check_for_updates\"");
        }

        [TestMethod]
        public void CheckForUpdatesCommandShouldShowImmediateVisibleStatus()
        {
            string sentMessage = null;
            var viewModel = CreateViewModel(sendCheckForUpdatesIPCMessage: msg =>
            {
                sentMessage = msg;
                return 0;
            });

            viewModel.CheckForUpdatesEventHandler.Execute(null);

            Assert.IsNotNull(sentMessage);
            Assert.IsTrue(GetViewModelProperty<bool>(viewModel, "UpdateCheckMessageVisible"));
            Assert.AreEqual("General_CheckingForUpdates/Text", GetViewModelProperty<string>(viewModel, "UpdateCheckMessage"));
            Assert.AreEqual("Informational", GetViewModelProperty<string>(viewModel, "UpdateCheckMessageSeverity"));
            Assert.IsTrue(GetViewModelProperty<bool>(viewModel, "IsCheckingForUpdates"));
            Assert.IsFalse(GetViewModelProperty<bool>(viewModel, "IsCheckForUpdatesButtonEnabled"));
        }

        [TestMethod]
        public void CheckForUpdatesCommandShouldIgnoreRepeatedClicksWhileRunning()
        {
            var sentCount = 0;
            var viewModel = CreateViewModel(sendCheckForUpdatesIPCMessage: msg =>
            {
                sentCount++;
                return 0;
            });

            viewModel.CheckForUpdatesEventHandler.Execute(null);
            viewModel.CheckForUpdatesEventHandler.Execute(null);

            Assert.AreEqual(1, sentCount);
            Assert.IsTrue(GetViewModelProperty<bool>(viewModel, "IsCheckingForUpdates"));
            Assert.IsFalse(GetViewModelProperty<bool>(viewModel, "IsCheckForUpdatesButtonEnabled"));
        }

        [TestMethod]
        public void PowerToysNewAvailableVersionUriShouldBeNullWhenLinkIsEmptyAndAbsoluteWhenSet()
        {
            var viewModel = CreateViewModel();

            Assert.IsNull(GetViewModelProperty<Uri>(viewModel, "PowerToysNewAvailableVersionUri"));
            Assert.IsFalse(viewModel.IsUpdateAvailable);

            var refreshMethod = typeof(GeneralViewModel).GetMethod("RefreshUpdatingState", new[] { typeof(UpdatingSettings) });
            refreshMethod.Invoke(viewModel, new object[]
            {
                new UpdatingSettings
                {
                    State = UpdatingSettings.UpdatingState.ReadyToDownload,
                    ReleasePageLink = "https://github.com/guijianchou/Kit/releases/tag/v9.9.9",
                    LastCheckedDate = DateTimeOffset.Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                },
            });

            var uri = GetViewModelProperty<Uri>(viewModel, "PowerToysNewAvailableVersionUri");
            Assert.IsNotNull(uri);
            Assert.IsTrue(uri.IsAbsoluteUri);
            Assert.AreEqual("https://github.com/guijianchou/Kit/releases/tag/v9.9.9", uri.AbsoluteUri);
            Assert.IsTrue(viewModel.IsUpdateAvailable);
        }

        [TestMethod]
        public void RefreshUpdatingStateShouldIgnoreCachedUpToDateStatusUntilManualCheck()
        {
            var viewModel = CreateViewModel();
            var refreshMethod = typeof(GeneralViewModel).GetMethod("RefreshUpdatingState", new[] { typeof(UpdatingSettings) });
            refreshMethod.Invoke(viewModel, new object[]
            {
                new UpdatingSettings
                {
                    State = UpdatingSettings.UpdatingState.UpToDate,
                    LastCheckedDate = DateTimeOffset.Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                },
            });

            Assert.IsFalse(GetViewModelProperty<bool>(viewModel, "UpdateCheckMessageVisible"));
        }

        [TestMethod]
        public void ManualRefreshUpdatingStateShouldShowFreshUpToDateStatus()
        {
            var viewModel = CreateViewModel();
            viewModel.CheckForUpdatesEventHandler.Execute(null);
            SetViewModelField<DateTime?>(viewModel, "_manualUpdateCheckBaselineLastChecked", DateTime.Now.AddMinutes(-1));

            var refreshMethod = typeof(GeneralViewModel).GetMethod("RefreshUpdatingState", new[] { typeof(UpdatingSettings) });
            refreshMethod.Invoke(viewModel, new object[]
            {
                new UpdatingSettings
                {
                    State = UpdatingSettings.UpdatingState.UpToDate,
                    LastCheckedDate = DateTimeOffset.Now.AddMinutes(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                },
            });

            Assert.IsTrue(GetViewModelProperty<bool>(viewModel, "UpdateCheckMessageVisible"));
            Assert.AreEqual("Success", GetViewModelProperty<string>(viewModel, "UpdateCheckMessageSeverity"));
            var message = GetViewModelProperty<string>(viewModel, "UpdateCheckMessage");
            StringAssert.Contains(message, "General_UpToDate/Title");
            StringAssert.Contains(message, "v" + Helper.GetProductVersion().TrimStart('v'));
            StringAssert.Contains(message, "General_VersionLastChecked/Text");
            Assert.IsFalse(GetViewModelProperty<bool>(viewModel, "IsCheckingForUpdates"));
            Assert.IsTrue(GetViewModelProperty<bool>(viewModel, "IsCheckForUpdatesButtonEnabled"));
        }

        [TestMethod]
        public void ManualRefreshUpdatingStateShouldNotAcceptStaleUpToDateStatus()
        {
            var viewModel = CreateViewModel();
            viewModel.CheckForUpdatesEventHandler.Execute(null);
            SetViewModelField<DateTime?>(viewModel, "_manualUpdateCheckBaselineLastChecked", DateTime.Now);

            var refreshMethod = typeof(GeneralViewModel).GetMethod("RefreshUpdatingState", new[] { typeof(UpdatingSettings) });
            refreshMethod.Invoke(viewModel, new object[]
            {
                new UpdatingSettings
                {
                    State = UpdatingSettings.UpdatingState.UpToDate,
                    LastCheckedDate = DateTimeOffset.Now.AddMinutes(-1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                },
            });

            Assert.IsTrue(GetViewModelProperty<bool>(viewModel, "IsCheckingForUpdates"));
            Assert.IsFalse(GetViewModelProperty<bool>(viewModel, "IsCheckForUpdatesButtonEnabled"));
            Assert.IsTrue(GetViewModelProperty<bool>(viewModel, "UpdateCheckMessageVisible"));
            Assert.AreEqual("General_CheckingForUpdates/Text", GetViewModelProperty<string>(viewModel, "UpdateCheckMessage"));
            Assert.AreEqual("Informational", GetViewModelProperty<string>(viewModel, "UpdateCheckMessageSeverity"));
        }

        [TestMethod]
        public void RefreshUpdatingStateShouldShowKitReleaseCheckResult()
        {
            var viewModel = CreateViewModel();
            var updateSettings = new UpdatingSettings
            {
                State = UpdatingSettings.UpdatingState.ReadyToDownload,
                ReleasePageLink = "https://github.com/guijianchou/Kit/releases/tag/v9.9.9",
                LastCheckedDate = DateTimeOffset.Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            };
            var refreshMethod = typeof(GeneralViewModel).GetMethod("RefreshUpdatingState", new[] { typeof(UpdatingSettings) });

            Assert.IsNotNull(refreshMethod, "GeneralViewModel should support refreshing from a loaded Kit update state.");

            refreshMethod.Invoke(viewModel, new object[] { updateSettings });

            Assert.IsTrue(GetViewModelProperty<bool>(viewModel, "UpdateCheckMessageVisible"));
            StringAssert.Contains(GetViewModelProperty<string>(viewModel, "UpdateCheckMessage"), "General_NewVersionAvailable/Title");
            StringAssert.Contains(GetViewModelProperty<string>(viewModel, "UpdateCheckMessage"), "v9.9.9");
            Assert.AreEqual("Success", GetViewModelProperty<string>(viewModel, "UpdateCheckMessageSeverity"));
            Assert.IsFalse(viewModel.IsDownloadAllowed);
            Assert.IsTrue(viewModel.IsUpdatePanelVisible);
            Assert.IsFalse(viewModel.AutoDownloadUpdates);
            Assert.IsFalse(viewModel.ShowNewUpdatesToastNotification);
            Assert.IsFalse(viewModel.ShowWhatsNewAfterUpdates);
        }

        [TestMethod]
        public void CheckForUpdatesIpcShouldReachRunnerKitReleaseCallback()
        {
            var shellPage = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "ShellPage.xaml.cs"));
            var settingsWindow = File.ReadAllText(FindSourceFile("src", "runner", "settings_window.cpp"));
            var updateUtils = File.ReadAllText(FindSourceFile("src", "runner", "UpdateUtils.cpp"));
            var commonUpdating = File.ReadAllText(FindSourceFile("src", "common", "updating", "updating.cpp"));
            var runnerProject = File.ReadAllText(FindSourceFile("src", "runner", "Kit.vcxproj"));
            var generalViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "GeneralViewModel.cs"));
            var updatingSettings = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "UpdatingSettings.cs"));

            StringAssert.Contains(shellPage, "CheckForUpdatesMsgCallback?.Invoke(msg);");
            StringAssert.Contains(settingsWindow, "action == L\"check_for_updates\"");
            StringAssert.Contains(settingsWindow, "isUpdateCheckThreadRunning.compare_exchange_strong");
            StringAssert.Contains(settingsWindow, "CheckForUpdatesCallback();");
            StringAssert.Contains(updateUtils, "https://api.github.com/repos/guijianchou/Kit/releases/latest");
            StringAssert.Contains(updateUtils, "https://github.com/guijianchou/Kit/releases");
            StringAssert.Contains(commonUpdating, "https://api.github.com/repos/guijianchou/Kit/releases/latest");
            StringAssert.Contains(commonUpdating, "https://api.github.com/repos/guijianchou/Kit/releases");
            StringAssert.Contains(updateUtils, "HttpCacheReadBehavior::NoCache");
            StringAssert.Contains(updateUtils, "HttpCacheWriteBehavior::NoCache");
            StringAssert.Contains(updateUtils, "Cache-Control");
            StringAssert.Contains(updateUtils, "response.IsSuccessStatusCode()");
            StringAssert.Contains(updateUtils, "#include <common/updating/updateState.h>");
            StringAssert.Contains(updateUtils, "UpdateState::read()");
            StringAssert.Contains(updateUtils, "UpdateState::store");
            StringAssert.Contains(updateUtils, "check_for_updates(UpdateCheckMode::Manual)");
            StringAssert.Contains(updateUtils, "check_for_updates(UpdateCheckMode::Periodic)");
            StringAssert.Contains(updateUtils, "mode == UpdateCheckMode::Periodic");
            StringAssert.Contains(updateUtils, "periodicCheckInterval = std::chrono::hours(24)");
            StringAssert.Contains(updateUtils, "failedRetryInterval = std::chrono::hours(2)");
            StringAssert.Contains(updateUtils, "retryAfterFailure = !check_for_updates(UpdateCheckMode::Periodic)");
            StringAssert.Contains(updateUtils, "std::this_thread::sleep_for(failedRetryInterval)");
            Assert.IsFalse(updateUtils.Contains("idlePollInterval", StringComparison.Ordinal), "Periodic checks should sleep until the next meaningful check instead of waking every hour.");
            StringAssert.Contains(runnerProject, @"..\common\updating\updateState.cpp");
            StringAssert.Contains(generalViewModel, "Helper.GetFileWatcher(string.Empty, UpdatingSettings.SettingsFile");
            StringAssert.Contains(generalViewModel, "UpdatingSettings.LoadSettings()");
            StringAssert.Contains(generalViewModel, "ManualUpdateCheckTimeoutMs");
            StringAssert.Contains(generalViewModel, "IsResultNewerThanBaseline");
            StringAssert.Contains(generalViewModel, "IsCheckForUpdatesButtonEnabled");
            StringAssert.Contains(updatingSettings, "FileShare.ReadWrite | FileShare.Delete");
            Assert.IsFalse(updateUtils.Contains("download_new_version_async", StringComparison.Ordinal), "Kit release checks must not download installers.");
            Assert.IsFalse(updateUtils.Contains("PowerToys.Update.exe", StringComparison.Ordinal), "Kit release checks must not launch the updater executable.");
            Assert.IsFalse(updateUtils.Contains("ShellExecuteEx", StringComparison.Ordinal), "Kit release checks must not launch external updater processes.");
            Assert.IsFalse(updateUtils.Contains("#include <common/updating/updating.h>", StringComparison.Ordinal), "Kit should reuse only the update-state file model, not the full updater library.");
            Assert.IsFalse(updateUtils.Contains("EnsureSuccessStatusCode", StringComparison.Ordinal), "Kit release checks should not throw first-chance WinRT exceptions for expected GitHub HTTP errors such as 403.");
            Assert.IsFalse(generalViewModel.Contains("QueueUpdateCheckResultRefresh", StringComparison.Ordinal), "Settings should rely on the existing UpdateState.json watcher instead of a manual polling loop.");
            Assert.IsFalse(generalViewModel.Contains("UpdateCheckMinimumDisplayMs", StringComparison.Ordinal), "Settings should not add artificial update-check timing.");
        }

        [TestMethod]
        public void KitGeneralPageShouldPlaceExperimentationBeforeBackupAndRestore()
        {
            var xaml = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "GeneralPage.xaml"));

            var experimentationIndex = xaml.IndexOf("x:Uid=\"General_Experimentation\"", StringComparison.Ordinal);
            var backupAndRestoreIndex = xaml.IndexOf("x:Uid=\"General_SettingsBackupAndRestoreTitle\"", StringComparison.Ordinal);

            Assert.IsTrue(experimentationIndex >= 0, "General Experimentation section should be present.");
            Assert.IsTrue(backupAndRestoreIndex >= 0, "General Back up & restore section should be present.");
            Assert.IsTrue(experimentationIndex < backupAndRestoreIndex, "Experimentation should appear before Back up & restore on the General page.");
        }

        [TestMethod]
        public void KitIntroTextShouldBeEnglish()
        {
            var resources = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Strings", "en-us", "Resources.resw"));
            var readme = File.ReadAllText(FindSourceFile("README.md"));

            StringAssert.Contains(resources, "Kit is a local PowerToys-derived workspace");
            StringAssert.Contains(readme, "Kit is a local, self-use Windows utility workspace derived from Microsoft PowerToys.");

            Assert.IsFalse(resources.Contains("应用源于", StringComparison.Ordinal));
            Assert.IsFalse(resources.Contains("修改自用", StringComparison.Ordinal));
            Assert.IsFalse(resources.Contains("差分比对", StringComparison.Ordinal));
            Assert.IsFalse(readme.Contains("应用源于", StringComparison.Ordinal));
            Assert.IsFalse(readme.Contains("修改自用", StringComparison.Ordinal));
            Assert.IsFalse(readme.Contains("差分比对", StringComparison.Ordinal));
        }

        [TestMethod]
        public void KitDashboardShouldUseEnglishPowerToysStyleHomeWithoutUpdateEntrypoints()
        {
            var dashboard = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "DashboardPage.xaml"));

            StringAssert.Contains(dashboard, "x:Uid=\"DashboardIntro\"");
            StringAssert.Contains(dashboard, "ms-appx:///Assets/Settings/Modules/PT.png");
            StringAssert.Contains(dashboard, "MaxWidth=\"160\"");
            StringAssert.Contains(dashboard, "QuickAccessList");
            StringAssert.Contains(dashboard, "ModuleList");
            StringAssert.Contains(dashboard, "x:Uid=\"QuickAccessTitle\"");
            StringAssert.Contains(dashboard, "x:Uid=\"ShortcutsOverview\"");
            StringAssert.Contains(dashboard, "x:Uid=\"UtilitiesHeader\"");

            Assert.IsFalse(dashboard.Contains("应用源于", StringComparison.Ordinal));
            Assert.IsFalse(dashboard.Contains("修改自用", StringComparison.Ordinal));
            Assert.IsFalse(dashboard.Contains("差分比对", StringComparison.Ordinal));
            Assert.IsFalse(dashboard.Contains("CheckUpdateControl", StringComparison.Ordinal));
            Assert.IsFalse(dashboard.Contains("LearnWhatsNew", StringComparison.Ordinal));
            Assert.IsFalse(dashboard.Contains("WhatsNewButton_Click", StringComparison.Ordinal));
        }

        [TestMethod]
        public void KitSettingsAndBackupPathsShouldUseKitBranding()
        {
            var settingPath = new SettingPath();
            var generalSettingsPath = settingPath.GetSettingsPath(string.Empty);
            var backupUtils = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "SettingsBackupAndRestoreUtils.cs"));
            var backupManifest = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "backup_restore_settings.json"));

            StringAssert.Contains(generalSettingsPath, $"{Path.DirectorySeparatorChar}Kit{Path.DirectorySeparatorChar}settings.json");
            Assert.IsFalse(generalSettingsPath.Contains($"{Path.DirectorySeparatorChar}PowerToys{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

            StringAssert.Contains(backupUtils, "Software\\\\Microsoft\\\\Kit");
            StringAssert.Contains(backupUtils, "\"Kit\\\\Backup\"");
            StringAssert.Contains(backupUtils, "\"Kit_settings_\"");
            Assert.IsFalse(backupUtils.Contains("Software\\\\Microsoft\\\\PowerToys", StringComparison.Ordinal));
            Assert.IsFalse(backupUtils.Contains("\"PowerToys\\\\Backup\"", StringComparison.Ordinal));

            StringAssert.Contains(backupManifest, "*Kit\\\\log_settings.json");
            StringAssert.Contains(backupManifest, "*Kit\\\\oobe_settings.json");
            Assert.IsFalse(backupManifest.Contains("*PowerToys\\\\log_settings.json", StringComparison.Ordinal));
            Assert.IsFalse(backupManifest.Contains("*PowerToys\\\\oobe_settings.json", StringComparison.Ordinal));
        }

        [TestMethod]
        public void KitDashboardShouldListCopiedModulesAndQuickAccessShouldKeepActionableModules()
        {
            var dashboardViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "DashboardViewModel.cs"));
            var quickAccessViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Controls", "QuickAccess", "QuickAccessViewModel.cs"));

            CollectionAssert.AreEqual(
                new[] { ModuleType.Awake, ModuleType.LightSwitch, ModuleType.Monitor, ModuleType.PowerDisplay },
                KitModuleCatalog.ActiveModules.ToArray());
            CollectionAssert.AreEqual(
                new[] { ModuleType.Awake, ModuleType.LightSwitch, ModuleType.Monitor, ModuleType.PowerDisplay },
                KitModuleCatalog.DashboardModules.ToArray());
            CollectionAssert.AreEqual(
                new[] { ModuleType.LightSwitch, ModuleType.Monitor, ModuleType.PowerDisplay },
                KitModuleCatalog.QuickAccessModules.ToArray());
            Assert.IsTrue(KitModuleCatalog.IsActiveModule(ModuleType.Monitor));
            Assert.IsTrue(KitModuleCatalog.IsActiveModule(ModuleType.PowerDisplay));
            Assert.IsFalse(KitModuleCatalog.IsActiveModule(ModuleType.ImageResizer));

            StringAssert.Contains(dashboardViewModel, "KitModuleCatalog.DashboardModules");
            StringAssert.Contains(dashboardViewModel, "ModuleType.LightSwitch");
            StringAssert.Contains(dashboardViewModel, "ModuleType.Awake");
            Assert.IsFalse(dashboardViewModel.Contains("foreach (ModuleType moduleType in Enum.GetValues<ModuleType>())", StringComparison.Ordinal));
            StringAssert.Contains(dashboardViewModel, "moduleTypes: KitModuleCatalog.DashboardModules");
            StringAssert.Contains(dashboardViewModel, "fallbackLauncher: OpenModuleSettingsFromQuickAccess");
            StringAssert.Contains(dashboardViewModel, "ModuleType.Awake => GetModuleItemsAwake()");
            StringAssert.Contains(dashboardViewModel, "ModuleType.PowerDisplay => GetModuleItemsPowerDisplay()");
            StringAssert.Contains(dashboardViewModel, "PowerDisplayLaunchClicked");
            StringAssert.Contains(dashboardViewModel, "new DashboardModuleActivationItem()");

            StringAssert.Contains(quickAccessViewModel, "KitModuleCatalog.QuickAccessModules");
            StringAssert.Contains(quickAccessViewModel, "ModuleType.LightSwitch");
            StringAssert.Contains(quickAccessViewModel, "ModuleType.PowerDisplay");
            StringAssert.Contains(quickAccessViewModel, "IEnumerable<ModuleType>? moduleTypes = null");
            StringAssert.Contains(quickAccessViewModel, "Func<ModuleType, bool>? fallbackLauncher = null");
            Assert.IsFalse(dashboardViewModel.Contains("private static readonly ModuleType[] KitDashboardModules", StringComparison.Ordinal));
            Assert.IsFalse(quickAccessViewModel.Contains("private static readonly ModuleType[] KitQuickAccessModules", StringComparison.Ordinal));
            Assert.IsFalse(quickAccessViewModel.Contains("AddFlyoutMenuItem(ModuleType.ColorPicker)", StringComparison.Ordinal));
            Assert.IsFalse(quickAccessViewModel.Contains("AddFlyoutMenuItem(ModuleType.CmdPal)", StringComparison.Ordinal));
        }

        [TestMethod]
        public void KitOutgoingGeneralSettingsShouldOnlySerializeActiveModuleEnabledStates()
        {
            var settings = new GeneralSettings();
            settings.Enabled.Awake = false;
            settings.Enabled.LightSwitch = true;
            settings.Enabled.Monitor = true;
            settings.Enabled.PowerDisplay = false;

            var outgoingJson = new OutGoingGeneralSettings(settings).ToString();

            using var document = System.Text.Json.JsonDocument.Parse(outgoingJson);
            var enabled = document.RootElement.GetProperty("general").GetProperty("enabled");
            var serializedModuleNames = enabled.EnumerateObject().Select(property => property.Name).ToArray();

            CollectionAssert.AreEquivalent(
                KitActiveEnabledModuleKeys,
                serializedModuleNames);
            Assert.IsFalse(enabled.TryGetProperty("FancyZones", out _));
            Assert.IsFalse(enabled.TryGetProperty("Image Resizer", out _));
            Assert.IsFalse(enabled.TryGetProperty("AlwaysOnTop", out _));
            Assert.AreEqual(false, enabled.GetProperty("Awake").GetBoolean());
            Assert.AreEqual(true, enabled.GetProperty("LightSwitch").GetBoolean());
            Assert.AreEqual(true, enabled.GetProperty("Monitor").GetBoolean());
            Assert.AreEqual(false, enabled.GetProperty("PowerDisplay").GetBoolean());
        }

        [TestMethod]
        public void KitRunnerShouldLoadCopiedAwakeModule()
        {
            var solution = File.ReadAllText(FindSourceFile("Kit.slnx"));
            var runnerMain = File.ReadAllText(FindSourceFile("src", "runner", "main.cpp"));
            var runnerSettingsHeader = File.ReadAllText(FindSourceFile("src", "runner", "settings_window.h"));
            var runnerSettingsSource = File.ReadAllText(FindSourceFile("src", "runner", "settings_window.cpp"));
            var shellXaml = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "ShellPage.xaml"));
            var shellCode = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "ShellPage.xaml.cs"));
            var appCode = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "App.xaml.cs"));
            var moduleGpoHelper = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Helpers", "ModuleGpoHelper.cs"));
            var settingsProject = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "PowerToys.Settings.csproj"));

            _ = FindSourceFile("src", "modules", "awake", "AwakeModuleInterface", "AwakeModuleInterface.vcxproj");
            _ = FindSourceFile("src", "modules", "awake", "Awake", "Awake.csproj");
            _ = FindSourceFile("src", "modules", "powerdisplay", "PowerDisplay", "PowerDisplay.csproj");
            _ = FindSourceFile("src", "modules", "powerdisplay", "PowerDisplay.Lib", "PowerDisplay.Lib.csproj");
            _ = FindSourceFile("src", "modules", "powerdisplay", "PowerDisplay.Models", "PowerDisplay.Models.csproj");
            _ = FindSourceFile("src", "modules", "powerdisplay", "PowerDisplayModuleInterface", "PowerDisplayModuleInterface.vcxproj");

            StringAssert.Contains(solution, "src/modules/awake/Awake/Awake.csproj");
            StringAssert.Contains(solution, "src/modules/awake/AwakeModuleInterface/AwakeModuleInterface.vcxproj");
            StringAssert.Contains(solution, "src/modules/powerdisplay/PowerDisplay/PowerDisplay.csproj");
            StringAssert.Contains(solution, "src/modules/powerdisplay/PowerDisplay.Lib/PowerDisplay.Lib.csproj");
            StringAssert.Contains(solution, "src/modules/powerdisplay/PowerDisplay.Models/PowerDisplay.Models.csproj");
            StringAssert.Contains(solution, "src/modules/powerdisplay/PowerDisplayModuleInterface/PowerDisplayModuleInterface.vcxproj");
            StringAssert.Contains(solution, "BuildDependency Project=\"src/modules/awake/Awake/Awake.csproj\"");
            StringAssert.Contains(solution, "BuildDependency Project=\"src/modules/awake/AwakeModuleInterface/AwakeModuleInterface.vcxproj\"");
            StringAssert.Contains(solution, "BuildDependency Project=\"src/modules/powerdisplay/PowerDisplay/PowerDisplay.csproj\"");
            StringAssert.Contains(solution, "BuildDependency Project=\"src/modules/powerdisplay/PowerDisplayModuleInterface/PowerDisplayModuleInterface.vcxproj\"");
            StringAssert.Contains(runnerMain, "KitKnownModules");
            StringAssert.Contains(runnerMain, "PowerToys.AwakeModuleInterface.dll");
            StringAssert.Contains(runnerMain, "PowerToys.LightSwitchModuleInterface.dll");
            StringAssert.Contains(runnerMain, "PowerToys.PowerDisplayModuleInterface.dll");
            Assert.IsFalse(runnerMain.Contains("directory_iterator", StringComparison.Ordinal));
            StringAssert.Contains(runnerSettingsHeader, "Awake,");
            StringAssert.Contains(runnerSettingsHeader, "Monitor,");
            StringAssert.Contains(runnerSettingsHeader, "PowerDisplay,");
            StringAssert.Contains(runnerSettingsSource, "return \"Awake\";");
            StringAssert.Contains(runnerSettingsSource, "value == \"Awake\"");
            StringAssert.Contains(runnerSettingsSource, "return \"Monitor\";");
            StringAssert.Contains(runnerSettingsSource, "value == \"Monitor\"");
            StringAssert.Contains(runnerSettingsSource, "return \"PowerDisplay\";");
            StringAssert.Contains(runnerSettingsSource, "value == \"PowerDisplay\"");
            StringAssert.Contains(shellXaml, "AwakeNavigationItem");
            StringAssert.Contains(shellXaml, "PowerDisplayNavigationItem");
            StringAssert.Contains(shellCode, "NavHelper.SetNavigateTo(AwakeNavigationItem, typeof(AwakePage));");
            StringAssert.Contains(shellCode, "NavHelper.SetNavigateTo(PowerDisplayNavigationItem, typeof(PowerDisplayPage));");
            StringAssert.Contains(appCode, "\"Awake\" => typeof(AwakePage)");
            StringAssert.Contains(appCode, "\"PowerDisplay\" => typeof(PowerDisplayPage)");
            StringAssert.Contains(moduleGpoHelper, "ModuleType.Awake => typeof(AwakePage)");
            StringAssert.Contains(moduleGpoHelper, "ModuleType.PowerDisplay => typeof(PowerDisplayPage)");
            StringAssert.Contains(settingsProject, @"..\..\modules\powerdisplay\PowerDisplay.Models\PowerDisplay.Models.csproj");
            Assert.IsFalse(settingsProject.Contains("Compile Remove=\"ViewModels\\Awake*.cs\"", StringComparison.Ordinal));
            Assert.IsFalse(settingsProject.Contains("Compile Remove=\"SettingsXAML\\Views\\Awake*.xaml.cs\"", StringComparison.Ordinal));
            Assert.IsFalse(settingsProject.Contains("Page Remove=\"SettingsXAML\\Views\\Awake*.xaml\"", StringComparison.Ordinal));
        }

        [TestMethod]
        public void KitUiTestsShouldUseKitDashboardModuleList()
        {
            var uiTests = File.ReadAllText(FindSourceFile("src", "settings-ui", "UITest-Settings", "SettingsTests.cs"));

            StringAssert.Contains(uiTests, "\"Awake\"");
            StringAssert.Contains(uiTests, "\"Light Switch\"");
            StringAssert.Contains(uiTests, "\"PowerToys.Awake\"");
            Assert.IsFalse(uiTests.Contains("\"Advanced Paste\"", StringComparison.Ordinal));
            Assert.IsFalse(uiTests.Contains("\"Color Picker\"", StringComparison.Ordinal));
            Assert.IsFalse(uiTests.Contains("\"PowerToys Run\"", StringComparison.Ordinal));
            Assert.IsFalse(uiTests.Contains("\"PowerToys.AdvancedPaste\"", StringComparison.Ordinal));
            Assert.IsFalse(uiTests.Contains("\"PowerToys.Run\"", StringComparison.Ordinal));
        }

        [TestMethod]
        public void KitDashboardQuickAccessEmptyStateShouldUseVisibleItemCount()
        {
            var dashboard = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "DashboardPage.xaml"));
            var dashboardViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "DashboardViewModel.cs"));
            var quickAccessViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Controls", "QuickAccess", "QuickAccessViewModel.cs"));

            StringAssert.Contains(quickAccessViewModel, "public int VisibleItemCount");
            StringAssert.Contains(dashboardViewModel, "public int VisibleQuickAccessItemsCount");
            StringAssert.Contains(dashboard, "ViewModel.VisibleQuickAccessItemsCount");
            Assert.IsFalse(dashboard.Contains("ViewModel.QuickAccessItems.Count", StringComparison.Ordinal));
        }

        [TestMethod]
        public void KitWindowTitleShouldUseKitWithoutDebugSubtitle()
        {
            var resources = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Strings", "en-us", "Resources.resw"));
            var shell = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "ShellPage.xaml.cs"));
            var titleIndex = resources.IndexOf("data name=\"SettingsWindow_Title\"", StringComparison.Ordinal);
            var adminTitleIndex = resources.IndexOf("data name=\"SettingsWindow_AdminTitle\"", StringComparison.Ordinal);

            Assert.IsTrue(titleIndex >= 0 && resources.IndexOf("<value>Kit</value>", titleIndex, StringComparison.Ordinal) > titleIndex);
            Assert.IsTrue(adminTitleIndex >= 0 && resources.IndexOf("<value>Administrator: Kit</value>", adminTitleIndex, StringComparison.Ordinal) > adminTitleIndex);
            Assert.IsFalse(shell.Contains("AppTitleBar.Subtitle = \"Debug\"", StringComparison.Ordinal));
        }

        [TestMethod]
        public void KitGeneralShouldNotKeepAboutAsLastSection()
        {
            var general = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "GeneralPage.xaml"));

            Assert.IsFalse(general.Contains("x:Uid=\"General_About\"", StringComparison.Ordinal), "The bottom About section should be removed.");
        }

        [TestMethod]
        public void KitGeneralShouldKeepVersionUpdateSectionAndRemoveAbout()
        {
            var generalXaml = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "GeneralPage.xaml"));
            var generalCodeBehind = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "GeneralPage.xaml.cs"));
            var generalViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "GeneralViewModel.cs"));

            var versionIndex = generalXaml.IndexOf("x:Uid=\"General_VersionAndUpdate\"", StringComparison.Ordinal);
            Assert.IsTrue(versionIndex >= 0, "General page should keep the PowerToys-style version and update group.");
            var versionXaml = generalXaml.Substring(versionIndex, generalXaml.IndexOf("x:Uid=\"Admin_Mode\"", StringComparison.Ordinal) - versionIndex);
            StringAssert.Contains(versionXaml, "Header=\"{x:Bind ViewModel.PowerToysVersion, Mode=OneWay}\"");
            StringAssert.Contains(versionXaml, "https://github.com/guijianchou/Kit/releases/");
            StringAssert.Contains(versionXaml, "GeneralPage_CheckForUpdates");
            Assert.IsFalse(versionXaml.Contains("GeneralPage_AutoDownloadAndInstallUpdates", StringComparison.Ordinal), "Kit should not expose automatic update downloads.");
            Assert.IsFalse(versionXaml.Contains("General_DownloadAndInstall", StringComparison.Ordinal), "Kit should not expose installer download actions.");
            Assert.IsFalse(versionXaml.Contains("General_InstallNow", StringComparison.Ordinal), "Kit should not expose installer install actions.");

            var aboutIndex = generalXaml.IndexOf("x:Uid=\"General_About\"", StringComparison.Ordinal);
            Assert.IsTrue(aboutIndex < 0, "General page should remove the bottom About group.");

            Assert.IsFalse(generalCodeBehind.Contains("InitializeReportBugLink", StringComparison.Ordinal), "General page should not prepare About bug-report links.");
            Assert.IsFalse(generalCodeBehind.Contains("BugReportToolClicked", StringComparison.Ordinal), "General page should not keep hidden About bug-report actions.");
            Assert.IsFalse(generalCodeBehind.Contains("ViewDiagnosticData_Click", StringComparison.Ordinal), "General page should not keep hidden diagnostics actions.");
            Assert.IsFalse(generalCodeBehind.Contains("OpenDiagnosticsAndFeedbackSettings_Click", StringComparison.Ordinal), "General page should not keep hidden diagnostics links.");
            Assert.IsFalse(generalViewModel.Contains("ReportBugLink", StringComparison.Ordinal), "General ViewModel should not keep About bug-report link state.");
            Assert.IsFalse(generalViewModel.Contains("github.com/microsoft/PowerToys/issues", StringComparison.Ordinal), "General ViewModel should not build upstream issue links for About.");
        }

        [TestMethod]
        public void KitAboutVersionShouldUse116ReleaseMetadata()
        {
            var versionProps = File.ReadAllText(FindSourceFile("src", "Version.props"));
            var versionProject = File.ReadAllText(FindSourceFile("src", "common", "version", "version.vcxproj"));
            var directoryBuildProps = File.ReadAllText(FindSourceFile("Directory.Build.props"));
            var helper = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "Utilities", "Helper.cs"));
            var generalViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "GeneralViewModel.cs"));
            var readme = File.ReadAllText(FindSourceFile("README.md"));
            var readmeZh = File.ReadAllText(FindSourceFile("README_zh.md"));
            var changelog = File.ReadAllText(FindSourceFile("changelog.md"));
            var developmentLog = File.ReadAllText(FindSourceFile("doc", "devdoc", "kit-development-experience.md"));

            StringAssert.Contains(versionProps, "<Version>1.1.6</Version>");
            Assert.IsFalse(versionProps.Contains("<DevEnvironment>beta1</DevEnvironment>", StringComparison.Ordinal));
            StringAssert.Contains(directoryBuildProps, "<_Parameter1>DevEnvironment</_Parameter1>");
            StringAssert.Contains(helper, "GetProductDisplayVersion");
            StringAssert.Contains(generalViewModel, "Helper.GetProductDisplayVersion()");
            StringAssert.Contains(versionProject, "#define VERSION_MAJOR $(Version.Split('.')[0])");
            StringAssert.Contains(versionProject, "#define VERSION_MINOR $(Version.Split('.')[1])");
            StringAssert.Contains(versionProject, "#define VERSION_REVISION $(Version.Split('.')[2])");
            StringAssert.Contains(readme, "Current Kit version: `1.1.6`.");
            StringAssert.Contains(readme, "## Changelog");
            StringAssert.Contains(readme, "See [changelog.md](changelog.md) for the full version history.");
            StringAssert.Contains(readmeZh, "当前 Kit 版本：`1.1.6`。");
            StringAssert.Contains(readmeZh, "[changelog.md](changelog.md)");
            StringAssert.Contains(changelog, "### 1.1.6");
            StringAssert.Contains(changelog, "PowerToys-main-style version/update section");
            StringAssert.Contains(developmentLog, "## 2026-05-11 General Update Layout Cleanup And 1.1.6 Release Notes");

            Assert.AreEqual("v1.1.6", Helper.GetProductDisplayVersion("v1.1.6", string.Empty));
            Assert.AreEqual("v1.1.6", Helper.GetProductDisplayVersion("v1.1.6", "Local"));
        }

        [TestMethod]
        public void KitUpdatePropertiesShouldRemainDisabled()
        {
            var settings = new GeneralSettings
            {
                AutoDownloadUpdates = true,
                ShowNewUpdatesToastNotification = true,
                ShowWhatsNewAfterUpdates = true,
            };
            var viewModel = CreateViewModel(CreateRepository(settings));

            Assert.IsFalse(viewModel.AutoDownloadUpdates);
            Assert.IsFalse(viewModel.ShowNewUpdatesToastNotification);
            Assert.IsFalse(viewModel.ShowWhatsNewAfterUpdates);
            Assert.IsFalse(viewModel.IsAutoDownloadUpdatesCardEnabled);
            Assert.IsFalse(viewModel.IsShowNewUpdatesToastNotificationCardEnabled);
            Assert.IsFalse(viewModel.IsShowWhatsNewAfterUpdatesCardEnabled);
            Assert.IsFalse(viewModel.IsDownloadAllowed);
            Assert.IsTrue(viewModel.IsUpdatePanelVisible);
            Assert.IsTrue(string.IsNullOrEmpty(viewModel.PowerToysNewAvailableVersion) || viewModel.PowerToysNewAvailableVersion.StartsWith('v'), "A cached update-state file may populate version metadata, but it must not enable updater UI.");
        }

        [TestMethod]
        public void KitDiagnosticPropertiesShouldRemainDisabled()
        {
            var viewModel = CreateViewModel();

            Assert.IsFalse(viewModel.EnableDataDiagnostics);
            Assert.IsFalse(viewModel.EnableViewDataDiagnostics);

            viewModel.EnableDataDiagnostics = true;
            viewModel.EnableViewDataDiagnostics = true;

            Assert.IsFalse(viewModel.EnableDataDiagnostics);
            Assert.IsFalse(viewModel.EnableViewDataDiagnostics);
            Assert.IsFalse(viewModel.ViewDiagnosticDataViewerChanged);
        }

        [TestMethod]
        [DataRow("v0.18.2")]
        [DataRow("v0.19.2")]
        [DataRow("v0.20.1")]
        [DataRow("v0.21.1")]
        [DataRow("v0.22.0")]
        public void OriginalFilesModificationTest(string version)
        {
            var settingPathMock = new Mock<SettingPath>();
            var fileMock = BackCompatTestProperties.GetGeneralSettingsIOProvider(version);

            var mockGeneralSettingsUtils = new SettingsUtils(fileMock.Object, settingPathMock.Object);
            GeneralSettings originalGeneralSettings = mockGeneralSettingsUtils.GetSettingsOrDefault<GeneralSettings>();

            var generalSettingsRepository = new BackCompatTestProperties.MockSettingsRepository<GeneralSettings>(mockGeneralSettingsUtils);

            // Initialise View Model with test Config files
            // Arrange
            Func<string, int> sendMockIPCConfigMSG = msg => 0;
            Func<string, int> sendRestartAdminIPCMessage = msg => 0;
            Func<string, int> sendCheckForUpdatesIPCMessage = msg => 0;
            var viewModel = new TestGeneralViewModel(
                settingsRepository: generalSettingsRepository,
                runAsAdminText: "GeneralSettings_RunningAsAdminText",
                runAsUserText: "GeneralSettings_RunningAsUserText",
                isElevated: false,
                isAdmin: false,
                ipcMSGCallBackFunc: sendMockIPCConfigMSG,
                ipcMSGRestartAsAdminMSGCallBackFunc: sendRestartAdminIPCMessage,
                ipcMSGCheckForUpdatesCallBackFunc: sendCheckForUpdatesIPCMessage,
                configFileSubfolder: string.Empty);

            // Verify that the old settings persisted
            Assert.AreEqual(originalGeneralSettings.AutoDownloadUpdates, viewModel.AutoDownloadUpdates);
            Assert.AreEqual(Helper.GetProductDisplayVersion(originalGeneralSettings.PowertoysVersion), viewModel.PowerToysVersion);
            Assert.AreEqual(originalGeneralSettings.RunElevated, viewModel.RunElevated);
            Assert.AreEqual(originalGeneralSettings.Startup, viewModel.Startup);

            // Verify that the stub file was used
            var expectedCallCount = 2;  // once via the view model, and once by the test (GetSettings<T>)
            BackCompatTestProperties.VerifyGeneralSettingsIOProviderWasRead(fileMock, expectedCallCount);
        }

        [TestMethod]
        public void IsElevatedShouldUpdateRunasAdminStatusAttrsWhenSuccessful()
        {
            // Arrange
            Func<string, int> sendMockIPCConfigMSG = msg => { return 0; };
            Func<string, int> sendRestartAdminIPCMessage = msg => { return 0; };
            Func<string, int> sendCheckForUpdatesIPCMessage = msg => { return 0; };
            GeneralViewModel viewModel = new TestGeneralViewModel(
                settingsRepository: SettingsRepository<GeneralSettings>.GetInstance(mockGeneralSettingsUtils.Object),
                "GeneralSettings_RunningAsAdminText",
                "GeneralSettings_RunningAsUserText",
                false,
                false,
                sendMockIPCConfigMSG,
                sendRestartAdminIPCMessage,
                sendCheckForUpdatesIPCMessage,
                GeneralSettingsFileName);

            Assert.AreEqual(viewModel.RunningAsUserDefaultText, viewModel.RunningAsText);
            Assert.IsFalse(viewModel.IsElevated);

            // Act
            viewModel.IsElevated = true;

            // Assert
            Assert.AreEqual(viewModel.RunningAsAdminDefaultText, viewModel.RunningAsText);
            Assert.IsTrue(viewModel.IsElevated);
        }

        [TestMethod]
        public void StartupShouldEnableRunOnStartUpWhenSuccessful()
        {
            // Assert
            bool sawExpectedIpcPayload = false;
            Func<string, int> sendMockIPCConfigMSG = msg =>
            {
                if (string.IsNullOrWhiteSpace(msg))
                {
                    return 0;
                }

                OutGoingGeneralSettings snd = JsonSerializer.Deserialize<OutGoingGeneralSettings>(msg);
                if (snd?.GeneralSettings is null)
                {
                    return 0;
                }

                Assert.IsTrue(snd.GeneralSettings.Startup);
                sawExpectedIpcPayload = true;
                return 0;
            };

            // Arrange
            Func<string, int> sendRestartAdminIPCMessage = msg => { return 0; };
            Func<string, int> sendCheckForUpdatesIPCMessage = msg => { return 0; };
            GeneralViewModel viewModel = new TestGeneralViewModel(
                settingsRepository: SettingsRepository<GeneralSettings>.GetInstance(mockGeneralSettingsUtils.Object),
                "GeneralSettings_RunningAsAdminText",
                "GeneralSettings_RunningAsUserText",
                false,
                false,
                sendMockIPCConfigMSG,
                sendRestartAdminIPCMessage,
                sendCheckForUpdatesIPCMessage,
                GeneralSettingsFileName);
            Assert.IsFalse(viewModel.Startup);

            // act
            viewModel.Startup = true;
            Assert.IsTrue(sawExpectedIpcPayload);
        }

        [TestMethod]
        public void RunElevatedShouldEnableAlwaysRunElevatedWhenSuccessful()
        {
            // Assert
            bool sawExpectedIpcPayload = false;
            Func<string, int> sendMockIPCConfigMSG = msg =>
            {
                if (string.IsNullOrWhiteSpace(msg))
                {
                    return 0;
                }

                OutGoingGeneralSettings snd = JsonSerializer.Deserialize<OutGoingGeneralSettings>(msg);
                if (snd?.GeneralSettings is null)
                {
                    return 0;
                }

                Assert.IsTrue(snd.GeneralSettings.RunElevated);
                sawExpectedIpcPayload = true;
                return 0;
            };

            Func<string, int> sendRestartAdminIPCMessage = msg => { return 0; };
            Func<string, int> sendCheckForUpdatesIPCMessage = msg => { return 0; };

            // Arrange
            GeneralViewModel viewModel = new TestGeneralViewModel(
                settingsRepository: SettingsRepository<GeneralSettings>.GetInstance(mockGeneralSettingsUtils.Object),
                "GeneralSettings_RunningAsAdminText",
                "GeneralSettings_RunningAsUserText",
                false,
                false,
                sendMockIPCConfigMSG,
                sendRestartAdminIPCMessage,
                sendCheckForUpdatesIPCMessage,
                GeneralSettingsFileName);

            Assert.IsFalse(viewModel.RunElevated);

            // act
            viewModel.RunElevated = true;
            Assert.IsTrue(sawExpectedIpcPayload);
        }

        [TestMethod]
        public void IsLightThemeRadioButtonCheckedShouldThemeToLightWhenSuccessful()
        {
            // Arrange
            GeneralViewModel viewModel = null;
            bool sawExpectedIpcPayload = false;

            // Assert
            Func<string, int> sendMockIPCConfigMSG = msg =>
            {
                if (string.IsNullOrWhiteSpace(msg))
                {
                    return 0;
                }

                OutGoingGeneralSettings snd = JsonSerializer.Deserialize<OutGoingGeneralSettings>(msg);
                if (snd?.GeneralSettings is null)
                {
                    return 0;
                }

                Assert.AreEqual("light", snd.GeneralSettings.Theme);
                sawExpectedIpcPayload = true;
                return 0;
            };

            Func<string, int> sendRestartAdminIPCMessage = msg => { return 0; };
            Func<string, int> sendCheckForUpdatesIPCMessage = msg => { return 0; };
            viewModel = new TestGeneralViewModel(
                settingsRepository: SettingsRepository<GeneralSettings>.GetInstance(mockGeneralSettingsUtils.Object),
                "GeneralSettings_RunningAsAdminText",
                "GeneralSettings_RunningAsUserText",
                false,
                false,
                sendMockIPCConfigMSG,
                sendRestartAdminIPCMessage,
                sendCheckForUpdatesIPCMessage,
                GeneralSettingsFileName);
            Assert.AreNotEqual(1, viewModel.ThemeIndex);

            // act
            viewModel.ThemeIndex = 1;
            Assert.IsTrue(sawExpectedIpcPayload);
        }

        [TestMethod]
        public void IsDarkThemeRadioButtonCheckedShouldThemeToDarkWhenSuccessful()
        {
            // Arrange
            bool sawExpectedIpcPayload = false;
            Func<string, int> sendMockIPCConfigMSG = msg =>
            {
                if (string.IsNullOrWhiteSpace(msg))
                {
                    return 0;
                }

                OutGoingGeneralSettings snd = JsonSerializer.Deserialize<OutGoingGeneralSettings>(msg);
                if (snd?.GeneralSettings is null)
                {
                    return 0;
                }

                Assert.AreEqual("dark", snd.GeneralSettings.Theme);
                sawExpectedIpcPayload = true;
                return 0;
            };

            Func<string, int> sendRestartAdminIPCMessage = msg => { return 0; };
            Func<string, int> sendCheckForUpdatesIPCMessage = msg => { return 0; };
            GeneralViewModel viewModel = new TestGeneralViewModel(
                settingsRepository: SettingsRepository<GeneralSettings>.GetInstance(mockGeneralSettingsUtils.Object),
                "GeneralSettings_RunningAsAdminText",
                "GeneralSettings_RunningAsUserText",
                false,
                false,
                sendMockIPCConfigMSG,
                sendRestartAdminIPCMessage,
                sendCheckForUpdatesIPCMessage,
                GeneralSettingsFileName);
            Assert.AreNotEqual(0, viewModel.ThemeIndex);

            // act
            viewModel.ThemeIndex = 0;
            Assert.IsTrue(sawExpectedIpcPayload);
        }

        [TestMethod]
        public void IsShowSysTrayIconEnabledByDefaultShouldDisableWhenSuccessful()
        {
            // Arrange
            bool sawExpectedIpcPayload = false;
            Func<string, int> sendMockIPCConfigMSG = msg =>
            {
                if (string.IsNullOrWhiteSpace(msg))
                {
                    return 0;
                }

                OutGoingGeneralSettings snd = JsonSerializer.Deserialize<OutGoingGeneralSettings>(msg);
                if (snd?.GeneralSettings is null)
                {
                    return 0;
                }

                Assert.IsFalse(snd.GeneralSettings.ShowSysTrayIcon);
                sawExpectedIpcPayload = true;
                return 0;
            };

            Func<string, int> sendRestartAdminIPCMessage = msg => { return 0; };
            Func<string, int> sendCheckForUpdatesIPCMessage = msg => { return 0; };
            GeneralViewModel viewModel = new TestGeneralViewModel(
                settingsRepository: SettingsRepository<GeneralSettings>.GetInstance(mockGeneralSettingsUtils.Object),
                "GeneralSettings_RunningAsAdminText",
                "GeneralSettings_RunningAsUserText",
                false,
                false,
                sendMockIPCConfigMSG,
                sendRestartAdminIPCMessage,
                sendCheckForUpdatesIPCMessage,
                GeneralSettingsFileName);
            Assert.IsTrue(viewModel.ShowSysTrayIcon);

            // Act
            viewModel.ShowSysTrayIcon = false;
            Assert.IsTrue(sawExpectedIpcPayload);
        }

        [TestMethod]
        public void AllModulesAreEnabledByDefault()
        {
            // arrange
            EnabledModules modules = new EnabledModules();

            // Assert
            Assert.IsTrue(modules.FancyZones);
            Assert.IsTrue(modules.ImageResizer);
            Assert.IsTrue(modules.PowerPreview);
            Assert.IsTrue(modules.ShortcutGuide);
            Assert.IsTrue(modules.PowerRename);
            Assert.IsFalse(modules.PowerLauncher);
            Assert.IsTrue(modules.ColorPicker);
        }
    }
}
