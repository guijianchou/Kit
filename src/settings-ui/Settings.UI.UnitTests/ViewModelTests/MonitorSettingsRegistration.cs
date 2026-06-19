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
            Assert.AreEqual(43200, settings.Properties.ScanIntervalSeconds.Value);
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
            StringAssert.Contains(settingsJson, "\"scanIntervalSeconds\":{\"value\":43200}");

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
            var moduleHelper = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "Helpers", "ModuleHelper.cs"));
            var appCode = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "App.xaml.cs"));
            var moduleGpoHelper = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Helpers", "ModuleGpoHelper.cs"));
            var dashboardViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "DashboardViewModel.cs"));
            var quickAccessViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Controls", "QuickAccess", "QuickAccessViewModel.cs"));
            var resources = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Strings", "en-us", "Resources.resw"));

            _ = FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "MonitorViewModel.cs");
            _ = FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "MonitorPage.xaml");
            _ = FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "MonitorPage.xaml.cs");

            StringAssert.Contains(shellXaml, "MonitorNavigationItem");
            StringAssert.Contains(shellXaml, "/Assets/Settings/Icons/Monitor.png");
            StringAssert.Contains(moduleHelper, "ModuleType.Monitor => \"ms-appx:///Assets/Settings/Icons/Monitor.png\"");
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
            var statusPresentationService = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Services", "MonitorStatusPresentationService.cs"));
            var statusMetricViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "MonitorStatusMetricViewModel.cs"));
            var statusLegendItemViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "MonitorStatusLegendItemViewModel.cs"));
            var intervalOption = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "MonitorScanIntervalOption.cs"));
            var manualScanCoordinator = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Services", "MonitorManualScanCoordinator.cs"));
            var statusQueryService = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Services", "MonitorStatusQueryService.cs"));
            var progressSnapshotReader = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Services", "MonitorProgressSnapshotReader.cs"));
            var storagePaths = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Services", "MonitorSettingsStoragePaths.cs"));
            var resources = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Strings", "en-us", "Resources.resw"));
            var normalizedPageXaml = pageXaml.Replace("\r\n", "\n", StringComparison.Ordinal);

            StringAssert.Contains(pageXaml, "Monitor_ScanNowSettingsCard");
            StringAssert.Contains(pageXaml, "ModuleImageSource=\"ms-appx:///Assets/Settings/Modules/Monitor.png\"");
            StringAssert.Contains(pageXaml, "HeaderIcon=\"{ui:BitmapIcon Source=/Assets/Settings/Icons/Monitor.png}\"");
            StringAssert.Contains(pageXaml, "Link=\"https://github.com/guijianchou/Kit/tree/main/src/modules/Monitor\"");
            Assert.IsFalse(pageXaml.Contains("Link=\"https://github.com/microsoft/PowerToys\"", StringComparison.Ordinal));
            StringAssert.Contains(pageXaml, "Monitor_RunInBackgroundSettingsCard");
            StringAssert.Contains(pageXaml, "Monitor_ScanNowButton");
            StringAssert.Contains(pageXaml, "Monitor_ManualScanProgressBar");
            StringAssert.Contains(pageXaml, "ManualScanProgressText_Monitor");
            StringAssert.Contains(pageXaml, "ManualScanProgressDetail_Monitor");
            StringAssert.Contains(pageXaml, "ItemsSource=\"{x:Bind ViewModel.StatusMetrics, Mode=OneWay}\"");
            StringAssert.Contains(pageXaml, "ItemsSource=\"{x:Bind ViewModel.StatusLegendItems, Mode=OneWay}\"");
            StringAssert.Contains(pageXaml, "AutomationProperties.Name=\"{x:Bind ViewModel.StatusChartAccessibilityName, Mode=OneWay}\"");
            StringAssert.Contains(pageXaml, "AutomationProperties.AutomationId=\"MonitorStatusLegend\"");
            StringAssert.Contains(pageXaml, "AutomationProperties.Name=\"{Binding AccessibilityName}\"");
            Assert.IsFalse(pageXaml.Contains("Monitor_StatusRunsLabel", StringComparison.Ordinal), "Status metric labels should be supplied by the presentation service instead of page-local static cards.");
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

            StringAssert.Contains(pageXaml, "ScanIntervalSeconds_Monitor");
            StringAssert.Contains(pageXaml, "ItemsSource=\"{x:Bind ViewModel.ScanIntervalOptions, Mode=OneWay}\"");
            StringAssert.Contains(pageXaml, "SelectedValue=\"{x:Bind ViewModel.ScanIntervalSeconds, Mode=TwoWay}\"");
            StringAssert.Contains(pageXaml, "SelectedValuePath=\"Seconds\"");
            StringAssert.Contains(pageXaml, "DisplayMemberPath=\"Label\"");
            Assert.IsFalse(pageXaml.Contains("AutomationProperties.AutomationId=\"ScanIntervalSeconds_Monitor\"\r\n                            LargeChange=\"600\"", StringComparison.Ordinal));

            StringAssert.Contains(pageCode, "ScanNow_Click");
            StringAssert.Contains(pageCode, "_manualScanCoordinator");
            StringAssert.Contains(pageCode, "_statusQueryService");
            StringAssert.Contains(pageCode, "RestoreManualScanProgressIfRunning");
            StringAssert.Contains(storagePaths, "scan-progress.json");
            StringAssert.Contains(storagePaths, "monitor-status.db");
            StringAssert.Contains(statusQueryService, "MonitorScanTrigger.Manual");
            StringAssert.Contains(statusQueryService, "MonitorScanStatus.Running");
            StringAssert.Contains(statusQueryService, "MonitorStatusStore.TryGetLatestRun");
            StringAssert.Contains(progressSnapshotReader, "MonitorScanProgressFileReporter.Read");
            StringAssert.Contains(pageCode, "\"scanNow\"");
            StringAssert.Contains(pageCode, "manualScanId");
            StringAssert.Contains(manualScanCoordinator, "CreateManualScanId");
            StringAssert.Contains(manualScanCoordinator, "CompleteManualScanProgress");
            StringAssert.Contains(manualScanCoordinator, "ManualScanUiTimeout");
            StringAssert.Contains(manualScanCoordinator, "IsManualScanSnapshotExpired");
            StringAssert.Contains(manualScanCoordinator, "DateTimeOffset.UtcNow - snapshot.StartedAt >= ManualScanUiTimeout");
            StringAssert.Contains(manualScanCoordinator, "FailManualScanProgress");
            StringAssert.Contains(manualScanCoordinator, "manualScanTimedOut");
            StringAssert.Contains(manualScanCoordinator, "\"failed\"");
            StringAssert.Contains(manualScanCoordinator, "GetResourceString(\"Monitor_ManualScanStarting\"");
            StringAssert.Contains(manualScanCoordinator, "GetResourceString(\"Monitor_ManualScanWaitingForProgress\"");
            StringAssert.Contains(manualScanCoordinator, "GetResourceString(\"Monitor_ManualScanTimedOut\"");
            StringAssert.Contains(manualScanCoordinator, "GetResourceString(\"Monitor_ManualScanFailed\"");
            StringAssert.Contains(manualScanCoordinator, "GetResourceString(\"Monitor_ManualScanPhaseHashing\"");
            StringAssert.Contains(manualScanCoordinator, "FinishManualScanProgress");
            StringAssert.Contains(pageCode, "ViewModel.IsManualScanRunning = false;");
            StringAssert.Contains(pageCode, "ViewModel.IsManualScanRunning = true;");
            StringAssert.Contains(pageXaml, "IsEnabled=\"{x:Bind ViewModel.CanStartManualScan, Mode=OneWay}\"");
            Assert.IsFalse(pageCode.Contains("MonitorStatusStore.", StringComparison.Ordinal), "Monitor page should use MonitorStatusQueryService instead of querying the status database directly.");
            Assert.IsFalse(pageCode.Contains("JsonSerializer.Deserialize", StringComparison.Ordinal), "Monitor page should use MonitorProgressSnapshotReader instead of parsing progress JSON directly.");
            Assert.IsFalse(pageCode.Contains("WorkerProgressSnapshot", StringComparison.Ordinal), "Monitor page should use MonitorLib progress snapshots through the coordinator.");
            Assert.IsFalse(pageCode.Contains("ManualScanProgressValue + 1", StringComparison.Ordinal), "Monitor page should use worker progress instead of timer-only fake progress.");
            Assert.IsFalse(pageCode.Contains("MonitorScanCompletedEvent", StringComparison.Ordinal), "Monitor page should rely on scan-id progress and status, not the global completion event.");
            Assert.IsFalse(pageCode.Contains("IsScanCompletedSignaled", StringComparison.Ordinal), "Monitor page should not consume the worker completion event.");
            Assert.IsFalse(pageCode.Contains("completionSignaled", StringComparison.Ordinal), "Monitor page should not mix global completion events with per-scan progress.");
            Assert.IsFalse(pageCode.Contains("completionSignaled && progressSnapshot == null", StringComparison.Ordinal), "Monitor page must not complete a scan from the global completion event without matching scan progress.");
            Assert.IsFalse(pageCode.Contains("manualScanCompleted || completedWithoutProgressSnapshot", StringComparison.Ordinal), "Monitor page must only complete the current manual scan from its own scan id.");
            Assert.IsFalse(pageCode.Contains("\"organizeDownloads\"", StringComparison.Ordinal));
            StringAssert.Contains(pageCode, "BrowseDownloadsFolder_Click");
            StringAssert.Contains(pageCode, "ShellGetFolder.GetFolderDialog");

            var restoreMethodIndex = pageCode.IndexOf("private void RestoreManualScanProgressIfRunning()", StringComparison.Ordinal);
            var staleRefreshIndex = manualScanCoordinator.IndexOf("_statusQueryService.RefreshStaleRunningScans", StringComparison.Ordinal);
            var readProgressIndex = manualScanCoordinator.IndexOf("_progressSnapshotReader.ReadLatest", StringComparison.Ordinal);
            Assert.IsTrue(staleRefreshIndex >= 0, "Restore should refresh stale running scans before trusting persisted status.");
            Assert.IsTrue(readProgressIndex > staleRefreshIndex, "Restore should refresh stale status before reading the latest progress snapshot.");

            StringAssert.Contains(viewModel, "DownloadsPathDisplay");
            StringAssert.Contains(viewModel, "ScanIntervalOptions");
            StringAssert.Contains(intervalOption, "record MonitorScanIntervalOption");
            StringAssert.Contains(viewModel, "new MonitorScanIntervalOption(\"1h\", 3600)");
            StringAssert.Contains(viewModel, "new MonitorScanIntervalOption(\"2h\", 7200)");
            StringAssert.Contains(viewModel, "new MonitorScanIntervalOption(\"6h\", 21600)");
            StringAssert.Contains(viewModel, "new MonitorScanIntervalOption(\"12h\", 43200)");
            StringAssert.Contains(viewModel, "new MonitorScanIntervalOption(\"24h\", 86400)");
            StringAssert.Contains(viewModel, "RunInBackground");
            StringAssert.Contains(viewModel, "IsManualScanProgressVisible");
            StringAssert.Contains(viewModel, "IsManualScanProgressIndeterminate");
            StringAssert.Contains(viewModel, "ManualScanProgressValue");
            StringAssert.Contains(viewModel, "ManualScanProgressText");
            StringAssert.Contains(viewModel, "ManualScanProgressDetail");
            StringAssert.Contains(viewModel, "IsManualScanRunning");
            StringAssert.Contains(viewModel, "CanStartManualScan");
            StringAssert.Contains(viewModel, "MonitorStatusPresentationService");
            StringAssert.Contains(viewModel, "ApplyStatusPresentation");
            StringAssert.Contains(viewModel, "StatusMetrics");
            StringAssert.Contains(viewModel, "StatusLegendItems");
            StringAssert.Contains(viewModel, "StatusChartAccessibilityName");
            Assert.IsFalse(viewModel.Contains("RunCountText", StringComparison.Ordinal), "Status metric text should flow through StatusMetrics instead of duplicate bindable properties.");
            Assert.IsFalse(viewModel.Contains("SuccessRateText", StringComparison.Ordinal), "Status metric text should flow through StatusMetrics instead of duplicate bindable properties.");
            Assert.IsFalse(viewModel.Contains("IssueCountText", StringComparison.Ordinal), "Status metric text should flow through StatusMetrics instead of duplicate bindable properties.");
            Assert.IsFalse(viewModel.Contains("private static string FormatStatus(", StringComparison.Ordinal), "Status text formatting should live in MonitorStatusPresentationService.");
            Assert.IsFalse(viewModel.Contains("private static Brush GetBrush(", StringComparison.Ordinal), "Status color mapping should live in MonitorStatusPresentationService.");
            StringAssert.Contains(statusPresentationService, "CreateSummaryPresentation");
            StringAssert.Contains(statusPresentationService, "CreateUnavailablePresentation");
            StringAssert.Contains(statusPresentationService, "CreateChartAccessibilityName");
            StringAssert.Contains(statusPresentationService, "GetRunningRunCount");
            StringAssert.Contains(statusPresentationService, "Monitor_StatusRunsMetric");
            StringAssert.Contains(statusPresentationService, "Monitor_StatusLegendHealthy");
            StringAssert.Contains(statusPresentationService, "Monitor_StatusLegendWarningRunning");
            Assert.IsFalse(statusPresentationService.Contains("string RunCountText", StringComparison.Ordinal), "Presentation record should not duplicate metric values outside the metrics collection.");
            StringAssert.Contains(statusMetricViewModel, "public sealed class MonitorStatusMetricViewModel");
            StringAssert.Contains(statusLegendItemViewModel, "public sealed class MonitorStatusLegendItemViewModel");

            StringAssert.Contains(resources, "Monitor_ScanNowSettingsCard.Header");
            StringAssert.Contains(resources, "Monitor_RunInBackgroundSettingsCard.Header");
            StringAssert.Contains(resources, "Monitor_ScanNowButton.Content");
            StringAssert.Contains(resources, "Monitor_SelectDownloadsFolderButton.Content");
            StringAssert.Contains(resources, "Monitor_ManualScanStarting");
            StringAssert.Contains(resources, "Monitor_ManualScanWaitingForProgress");
            StringAssert.Contains(resources, "Monitor_ManualScanTimedOut");
            StringAssert.Contains(resources, "Monitor_ManualScanFailed");
            StringAssert.Contains(resources, "Monitor_ManualScanPhaseHashing");
            StringAssert.Contains(resources, "Monitor_ManualScanPhaseCategorizing");
            StringAssert.Contains(resources, "Monitor_ManualScanPhaseWriting");
            StringAssert.Contains(resources, "Monitor_ManualScanPhaseComplete");
            StringAssert.Contains(resources, "Monitor_ManualScanPhaseScanning");
            StringAssert.Contains(resources, "Monitor_ManualScanCompletedFiles");
            StringAssert.Contains(resources, "Monitor_ManualScanProgressFiles");
            StringAssert.Contains(resources, "Monitor_StatusRunsMetric");
            StringAssert.Contains(resources, "Monitor_StatusLegendHealthy");
            StringAssert.Contains(resources, "Monitor_StatusLegendWarningRunning");
            StringAssert.Contains(resources, "Monitor_StatusLegendNoData");
            StringAssert.Contains(resources, "Monitor_StatusChartSummary");
            StringAssert.Contains(resources, "Monitor_StatusChartUnavailable");
        }

        [TestMethod]
        public void MonitorManualScanProgressShouldSurfaceCompletedWarnings()
        {
            var manualScanCoordinator = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Services", "MonitorManualScanCoordinator.cs"));

            StringAssert.Contains(manualScanCoordinator, "string.Equals(snapshot.Phase, \"completed\", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(snapshot.Message)");
            StringAssert.Contains(manualScanCoordinator, "return snapshot.Message;");
        }

        [TestMethod]
        public void MonitorInfoShouldRefreshColorPresetCacheWhenVcpValueListContentChanges()
        {
            MonitorInfo monitor = new()
            {
                SupportsColorTemperature = true,
                ColorTemperatureVcp = 0x05,
                VcpCodesFormatted = new List<VcpCodeDisplayInfo>
                {
                    CreateColorPresetVcp("0x05", "6500 K"),
                },
            };

            Assert.AreEqual("6500 K", monitor.ColorPresetsForDisplay[0].DisplayName);

            monitor.VcpCodesFormatted = new List<VcpCodeDisplayInfo>
            {
                CreateColorPresetVcp("0x08", "9300 K"),
            };

            Assert.AreEqual(2, monitor.ColorPresetsForDisplay.Count);
            Assert.AreEqual("Custom (0x05)", monitor.ColorPresetsForDisplay[0].DisplayName);
            Assert.AreEqual("9300 K", monitor.ColorPresetsForDisplay[1].DisplayName);
        }

        private static VcpCodeDisplayInfo CreateColorPresetVcp(string value, string name)
        {
            return new VcpCodeDisplayInfo
            {
                Code = "0x14",
                Title = "Color Preset",
                Values = value,
                HasValues = true,
                ValueList = new List<VcpValueInfo>
                {
                    new()
                    {
                        Value = value,
                        Name = name,
                    },
                },
            };
        }

        [TestMethod]
        public void MonitorSettingsCardsShouldKeepBackgroundIntervalAndFolderOrder()
        {
            var pageXaml = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "MonitorPage.xaml"));

            var cleanIndex = pageXaml.IndexOf("x:Uid=\"Monitor_CleanInstallersSettingsCard\"", StringComparison.Ordinal);
            var downloadsPathIndex = pageXaml.IndexOf("x:Uid=\"Monitor_DownloadsPathSettingsCard\"", StringComparison.Ordinal);
            var runInBackgroundIndex = pageXaml.IndexOf("x:Uid=\"Monitor_RunInBackgroundSettingsCard\"", StringComparison.Ordinal);
            var scanIntervalIndex = pageXaml.IndexOf("x:Uid=\"Monitor_ScanIntervalSeconds\"", StringComparison.Ordinal);

            Assert.IsTrue(cleanIndex >= 0, "Clean installers card should exist.");
            Assert.IsTrue(downloadsPathIndex > cleanIndex, "Downloads folder should appear below Clean.");
            Assert.IsTrue(runInBackgroundIndex > downloadsPathIndex, "Run in background should appear below Downloads folder.");
            Assert.IsTrue(scanIntervalIndex > runInBackgroundIndex, "Scan interval should appear below Run in background.");
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
