// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;

using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Library.Helpers;
using Microsoft.PowerToys.Settings.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ViewModelTests
{
    [TestClass]
    public class MonitorSettingsRegistration
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
        public void MonitorSettingsExposeModuleDefaultsAndClone()
        {
            var settings = new MonitorSettings();

            Assert.AreEqual("Monitor", MonitorSettings.ModuleName);
            Assert.AreEqual("Monitor", settings.Name);
            Assert.AreEqual("Monitor", settings.GetModuleName());
            Assert.AreEqual(ModuleType.Monitor, settings.GetModuleType());
            Assert.AreEqual("%USERPROFILE%\\Downloads", settings.Properties.DownloadsPath.Value);
            Assert.AreEqual("results.csv", settings.Properties.CsvFileName.Value);
            Assert.AreEqual(7200, settings.Properties.ScanIntervalSeconds.Value);
            Assert.AreEqual(500, settings.Properties.MaxFileSizeMegabytes.Value);
            Assert.AreEqual("SHA1", settings.Properties.HashAlgorithm.Value);
            Assert.IsTrue(settings.Properties.UseIncrementalHashing.Value);
            Assert.IsFalse(settings.Properties.RunInBackground.Value);
            Assert.IsTrue(settings.Properties.OrganizeDownloads.Value);
            Assert.IsFalse(settings.Properties.CleanInstallers.Value);

            var clone = (MonitorSettings)settings.Clone();

            Assert.AreNotSame(settings, clone);
            Assert.AreNotSame(settings.Properties, clone.Properties);
            Assert.AreEqual(settings.ToJsonString(), clone.ToJsonString());
        }

        [TestMethod]
        public void MonitorModuleRegistrationTogglesEnabledAndSerializes()
        {
            var generalSettings = new GeneralSettings();

            Assert.IsFalse(generalSettings.Enabled.Monitor);
            Assert.IsFalse(ModuleHelper.GetIsModuleEnabled(generalSettings, ModuleType.Monitor));

            ModuleHelper.SetIsModuleEnabled(generalSettings, ModuleType.Monitor, true);

            Assert.IsTrue(generalSettings.Enabled.Monitor);
            Assert.IsTrue(ModuleHelper.GetIsModuleEnabled(generalSettings, ModuleType.Monitor));
            Assert.AreEqual(MonitorSettings.ModuleName, ModuleHelper.GetModuleKey(ModuleType.Monitor));

            var settingsJson = JsonSerializer.Serialize(new MonitorSettings(), SettingsSerializationContext.Default.MonitorSettings);
            StringAssert.Contains(settingsJson, "\"name\":\"Monitor\"");
            StringAssert.Contains(settingsJson, "\"scanIntervalSeconds\":{\"value\":7200}");

            var ipcJson = new SndMonitorSettings(new MonitorSettings()).ToJsonString();
            StringAssert.Contains(ipcJson, "\"Monitor\"");
            StringAssert.Contains(ipcJson, "\"runInBackground\"");
            StringAssert.Contains(ipcJson, "\"cleanInstallers\"");
        }

        [TestMethod]
        public void MonitorSettingsUiShouldBeRegisteredInShellHomeAndQuickAccess()
        {
            var shellXaml = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "ShellPage.xaml"));
            var shellCode = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "ShellPage.xaml.cs"));
            var appCode = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "App.xaml.cs"));
            var moduleGpoHelper = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Helpers", "ModuleGpoHelper.cs"));
            var dashboardViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "DashboardViewModel.cs"));
            var quickAccessViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Controls", "QuickAccess", "QuickAccessViewModel.cs"));
            var resources = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Strings", "en-us", "Resources.resw"));

            _ = FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "MonitorViewModel.cs");
            _ = FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "MonitorPage.xaml");
            _ = FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "MonitorPage.xaml.cs");

            StringAssert.Contains(shellXaml, "MonitorNavigationItem");
            StringAssert.Contains(shellCode, "NavHelper.SetNavigateTo(MonitorNavigationItem, typeof(MonitorPage));");
            StringAssert.Contains(appCode, "\"Monitor\" => typeof(MonitorPage)");
            StringAssert.Contains(moduleGpoHelper, "ModuleType.Monitor => typeof(MonitorPage)");

            StringAssert.Contains(dashboardViewModel, "ModuleType.Monitor");
            StringAssert.Contains(dashboardViewModel, "ModuleType.Monitor => GetModuleItemsMonitor()");
            StringAssert.Contains(dashboardViewModel, "Monitor_RunInBackgroundSettingsCard");
            StringAssert.Contains(dashboardViewModel, "Monitor_ScanIntervalSeconds");

            StringAssert.Contains(quickAccessViewModel, "ModuleType.Monitor");
            StringAssert.Contains(quickAccessViewModel, "ModuleType.Monitor =>");

            StringAssert.Contains(resources, "Monitor.ModuleTitle");
            StringAssert.Contains(resources, "Monitor_EnableSettingsCard.Header");
            StringAssert.Contains(resources, "Monitor_RunInBackgroundSettingsCard.Header");
            StringAssert.Contains(resources, "Monitor_DownloadsPathSettingsCard.Header");
            StringAssert.Contains(resources, "Monitor_ScanIntervalSeconds.Header");
        }

        [TestMethod]
        public void HomeShortcutsShouldHideMonitorActivationItems()
        {
            var filterMethod = typeof(DashboardViewModel).GetMethod("GetShortcutItemsForDashboardModule", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(filterMethod, "Dashboard shortcut projection should be filtered without removing Monitor from the module list.");

            var monitorModule = CreateDashboardModule(ModuleType.Monitor, new DashboardModuleActivationItem() { Label = "Run in background", Activation = "Off" });
            var awakeModule = CreateDashboardModule(ModuleType.Awake, new DashboardModuleActivationItem() { Label = "Mode", Activation = "Indefinite" });
            var lightSwitchModule = CreateDashboardModule(ModuleType.LightSwitch, new DashboardModuleShortcutItem() { Label = "Toggle theme", Shortcut = new List<object>() });

            Assert.AreEqual(0, InvokeShortcutItemsFilter(filterMethod, monitorModule).Count, "Monitor status items should not appear in Home Shortcuts.");
            Assert.AreEqual(1, InvokeShortcutItemsFilter(filterMethod, awakeModule).Count, "Existing activation-based modules should remain in Home Shortcuts.");
            Assert.AreEqual(1, InvokeShortcutItemsFilter(filterMethod, lightSwitchModule).Count, "Real shortcut items should remain in Home Shortcuts.");
        }

        private static DashboardListItem CreateDashboardModule(ModuleType moduleType, params DashboardModuleItem[] items)
        {
            return new DashboardListItem()
            {
                Tag = moduleType,
                DashboardModuleItems = new ObservableCollection<DashboardModuleItem>(items),
            };
        }

        private static List<DashboardModuleItem> InvokeShortcutItemsFilter(MethodInfo filterMethod, DashboardListItem module)
        {
            return (List<DashboardModuleItem>)filterMethod.Invoke(null, new object[] { module });
        }

        [TestMethod]
        public void MonitorSettingsUiShouldExposeManualScanFolderPickerHashDropdownAndScanProgress()
        {
            var pageXaml = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "MonitorPage.xaml"));
            var pageCode = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "MonitorPage.xaml.cs"));
            var viewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "MonitorViewModel.cs"));
            var resources = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Strings", "en-us", "Resources.resw"));
            var normalizedPageXaml = pageXaml.Replace("\r\n", "\n", StringComparison.Ordinal);

            StringAssert.Contains(pageXaml, "Monitor_ScanNowSettingsCard");
            StringAssert.Contains(pageXaml, "Monitor_RunInBackgroundSettingsCard");
            StringAssert.Contains(pageXaml, "Monitor_ScanNowButton");
            StringAssert.Contains(pageXaml, "Monitor_ManualScanProgressBar");
            StringAssert.Contains(pageXaml, "ManualScanProgressText_Monitor");
            StringAssert.Contains(pageXaml, "ManualScanProgressDetail_Monitor");
            StringAssert.Contains(normalizedPageXaml, "x:Uid=\"Monitor_ScanNowSettingsCard\"\n                        HeaderIcon=\"{ui:FontIcon Glyph=&#xE8B7;}\"\n                        HorizontalContentAlignment=\"Stretch\"");
            StringAssert.Contains(pageXaml, "<Grid MinWidth=\"{StaticResource SettingActionControlMinWidth}\" ColumnSpacing=\"12\">");
            StringAssert.Contains(normalizedPageXaml, "Grid.Column=\"1\"\n                                x:Uid=\"Monitor_ScanNowButton\"");
            Assert.IsFalse(pageXaml.Contains("<StackPanel MinWidth=\"{StaticResource SettingActionControlMinWidth}\" Spacing=\"8\">", StringComparison.Ordinal));
            StringAssert.Contains(pageXaml, "Monitor_SelectDownloadsFolderButton");
            Assert.IsFalse(pageXaml.Contains("AutomationProperties.AutomationId=\"DownloadsPath_Monitor\"", StringComparison.Ordinal));

            StringAssert.Contains(pageXaml, "HashAlgorithmSelector_Monitor");
            StringAssert.Contains(pageXaml, "SelectedValue=\"{x:Bind ViewModel.HashAlgorithm, Mode=TwoWay}\"");
            StringAssert.Contains(pageXaml, "Tag=\"SHA1\"");
            StringAssert.Contains(pageXaml, "Tag=\"MD5\"");
            StringAssert.Contains(pageXaml, "Tag=\"SHA256\"");
            StringAssert.Contains(pageXaml, "Tag=\"SHA512\"");
            Assert.IsFalse(pageXaml.Contains("HashAlgorithmSha1_Monitor", StringComparison.Ordinal));

            StringAssert.Contains(pageCode, "ScanNow_Click");
            StringAssert.Contains(pageCode, "StartManualScanProgress");
            StringAssert.Contains(pageCode, "scan-progress.json");
            StringAssert.Contains(pageCode, "KitMonitorScanCompletedEvent");
            StringAssert.Contains(pageCode, "DeleteStaleManualScanProgress");
            StringAssert.Contains(pageCode, "File.Delete(progressPath)");
            StringAssert.Contains(pageCode, "ReadWorkerProgressSnapshot");
            StringAssert.Contains(pageCode, "\"scanNow\"");
            Assert.IsFalse(pageCode.Contains("ManualScanProgressValue + 1", StringComparison.Ordinal), "Monitor page should use worker progress instead of timer-only fake progress.");
            Assert.IsFalse(pageCode.Contains("\"organizeDownloads\"", StringComparison.Ordinal));
            StringAssert.Contains(pageCode, "BrowseDownloadsFolder_Click");
            StringAssert.Contains(pageCode, "ShellGetFolder.GetFolderDialog");

            StringAssert.Contains(viewModel, "DownloadsPathDisplay");
            StringAssert.Contains(viewModel, "RunInBackground");
            StringAssert.Contains(viewModel, "IsManualScanProgressVisible");
            StringAssert.Contains(viewModel, "IsManualScanProgressIndeterminate");
            StringAssert.Contains(viewModel, "ManualScanProgressValue");
            StringAssert.Contains(viewModel, "ManualScanProgressText");
            StringAssert.Contains(viewModel, "ManualScanProgressDetail");

            StringAssert.Contains(resources, "Monitor_ScanNowSettingsCard.Header");
            StringAssert.Contains(resources, "Monitor_RunInBackgroundSettingsCard.Header");
            StringAssert.Contains(resources, "Monitor_ScanNowButton.Content");
            StringAssert.Contains(resources, "Monitor_SelectDownloadsFolderButton.Content");
        }

        [TestMethod]
        public void MonitorRunInBackgroundShouldBeImmediatelyAfterManualScan()
        {
            var pageXaml = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "MonitorPage.xaml"));

            var scanNowIndex = pageXaml.IndexOf("x:Uid=\"Monitor_ScanNowSettingsCard\"", StringComparison.Ordinal);
            var runInBackgroundIndex = pageXaml.IndexOf("x:Uid=\"Monitor_RunInBackgroundSettingsCard\"", StringComparison.Ordinal);
            var downloadsPathIndex = pageXaml.IndexOf("x:Uid=\"Monitor_DownloadsPathSettingsCard\"", StringComparison.Ordinal);

            Assert.IsTrue(scanNowIndex >= 0, "Manual scan card should exist.");
            Assert.IsTrue(runInBackgroundIndex > scanNowIndex, "Run in background should appear below Manual scan.");
            Assert.IsTrue(downloadsPathIndex > runInBackgroundIndex, "Run in background should appear before folder/path settings.");
        }

        [TestMethod]
        public void MonitorModuleInterfaceShouldBeRegisteredWithRunnerAndSolution()
        {
            var runnerMain = File.ReadAllText(FindSourceFile("src", "runner", "main.cpp"));
            var solution = File.ReadAllText(FindSourceFile("Kit.slnx"));
            var moduleInterface = File.ReadAllText(FindSourceFile("src", "modules", "Monitor", "MonitorModuleInterface", "dllmain.cpp"));

            _ = FindSourceFile("src", "modules", "Monitor", "MonitorModuleInterface", "MonitorModuleInterface.vcxproj");
            _ = FindSourceFile("src", "modules", "Monitor", "MonitorModuleInterface", "dllmain.cpp");
            _ = FindSourceFile("src", "modules", "Monitor", "MonitorModuleInterface", "pch.cpp");
            _ = FindSourceFile("src", "modules", "Monitor", "MonitorModuleInterface", "pch.h");
            _ = FindSourceFile("src", "modules", "Monitor", "MonitorModuleInterface", "trace.cpp");
            _ = FindSourceFile("src", "modules", "Monitor", "MonitorModuleInterface", "trace.h");
            _ = FindSourceFile("src", "modules", "Monitor", "MonitorModuleInterface", "resource.h");
            _ = FindSourceFile("src", "modules", "Monitor", "MonitorModuleInterface", "MonitorModuleInterface.rc");

            StringAssert.Contains(runnerMain, "KitKnownModules");
            StringAssert.Contains(runnerMain, "PowerToys.MonitorModuleInterface.dll");
            Assert.IsFalse(runnerMain.Contains("directory_iterator", StringComparison.OrdinalIgnoreCase));

            StringAssert.Contains(solution, "src/modules/Monitor/MonitorLib/MonitorLib.csproj");
            StringAssert.Contains(solution, "src/modules/Monitor/Monitor/PowerToys.Monitor.csproj");
            StringAssert.Contains(solution, "src/modules/Monitor/MonitorModuleInterface/MonitorModuleInterface.vcxproj");
            StringAssert.Contains(solution, "BuildDependency Project=\"src/modules/Monitor/Monitor/PowerToys.Monitor.csproj\"");
            StringAssert.Contains(solution, "BuildDependency Project=\"src/modules/Monitor/MonitorModuleInterface/MonitorModuleInterface.vcxproj\"");

            StringAssert.Contains(moduleInterface, "m_enabled = true;");
            Assert.IsFalse(moduleInterface.Contains("m_enabled = launch_process", StringComparison.Ordinal));
        }
    }
}
