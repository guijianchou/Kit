// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using ManagedCommon;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ViewModelTests
{
    [TestClass]
    public class UDPtest
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
        public void UDPtestCatalogAndModuleHelperShouldBeConfiguredCorrectly()
        {
            CollectionAssert.Contains(KitModuleCatalog.ActiveModules.ToArray(), ModuleType.UDPtest);
            CollectionAssert.Contains(KitModuleCatalog.DashboardModules.ToArray(), ModuleType.UDPtest);
            CollectionAssert.DoesNotContain(KitModuleCatalog.QuickAccessModules.ToArray(), ModuleType.UDPtest);

            Assert.IsTrue(KitModuleCatalog.IsActiveModule(ModuleType.UDPtest));
            Assert.AreEqual("UDPtest/ModuleTitle", ModuleHelper.GetModuleLabelResourceName(ModuleType.UDPtest));
            Assert.AreEqual("UDPtest", ModuleHelper.GetModuleKey(ModuleType.UDPtest));
        }

        [TestMethod]
        public void UDPtestSettingsSerializationShouldRoundTrip()
        {
            var settings = new UDPtestSettings();
            Assert.IsNotNull(settings.Properties);
            Assert.AreEqual(1000, settings.Properties.ProbeIntervalMilliseconds.Value);

            settings.Properties.ProbeIntervalMilliseconds.Value = 2000;

            string json = settings.ToJsonString();
            Assert.IsTrue(json.Contains("2000", StringComparison.Ordinal));

            var deserialized = JsonSerializer.Deserialize<UDPtestSettings>(json, SettingsSerializationContext.Default.UDPtestSettings);
            Assert.IsNotNull(deserialized);
            Assert.AreEqual(2000, deserialized.Properties.ProbeIntervalMilliseconds.Value);
        }

        [TestMethod]
        public void UDPtestLayoutAndFoldedConfigRulesShouldBeEnforced()
        {
            var pageXaml = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "UDPtestPage.xaml"));
            var rowViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "UDPtestLineRowViewModel.cs"));
            var libPathHelper = File.ReadAllText(FindSourceFile("src", "modules", "UDPtest", "UDPtestLib", "Common", "UDPtestPathHelper.cs"));

            // 1. Verify TCP clean & non-foldable vs UDP folded config
            StringAssert.Contains(pageXaml, "TcpLineTemplate");
            StringAssert.Contains(pageXaml, "UdpLineTemplate");
            Assert.IsFalse(pageXaml.Contains("<Button x:Name=\"TcpExpandButton\""), "TCP lines must not have folded configuration expand button.");
            StringAssert.Contains(pageXaml, "UdpExpandButton");
            StringAssert.Contains(pageXaml, "ExpandGlyph");
            StringAssert.Contains(pageXaml, "Delete"); // Delete action in UDP template
            StringAssert.Contains(pageXaml, "Save");   // Save action in UDP template
            Assert.IsFalse(pageXaml.Contains("Restore Default", StringComparison.OrdinalIgnoreCase), "Expanded config must not have Restore Default button.");

            // 2. Verify validation and parser integration
            StringAssert.Contains(rowViewModel, "UdpTargetParser");
            StringAssert.Contains(rowViewModel, "CanSave");
            StringAssert.Contains(rowViewModel, "DeleteConfig");

            // 3. Verify data isolation paths
            StringAssert.Contains(libPathHelper, "Kit");
            StringAssert.Contains(libPathHelper, "UDPtest");
            Assert.IsFalse(libPathHelper.Contains("NetworkMonitor", StringComparison.Ordinal), "Path helper should isolate under Kit/UDPtest, not NetworkMonitor.");
        }

        [TestMethod]
        public void UDPtestModuleInterfaceAndRunnerShouldBeRegistered()
        {
            var runnerMain = File.ReadAllText(FindSourceFile("src", "runner", "main.cpp"));
            var runnerSettingsSource = File.ReadAllText(FindSourceFile("src", "runner", "settings_window.cpp"));
            var runnerSettingsHeader = File.ReadAllText(FindSourceFile("src", "runner", "settings_window.h"));
            var shellPageXaml = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "ShellPage.xaml"));
            var appXamlCs = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "App.xaml.cs"));

            StringAssert.Contains(runnerMain, "Kit.UDPtestModuleInterface.dll");
            StringAssert.Contains(runnerSettingsHeader, "UDPtest");
            StringAssert.Contains(runnerSettingsSource, "UDPtest");
            StringAssert.Contains(shellPageXaml, "UDPtestNavigationItem");
            StringAssert.Contains(appXamlCs, "\"UDPtest\" => typeof(UDPtestPage)");
        }

        [TestMethod]
        public void UDPtestGpoBehaviorShouldNotLockWhenUnavailableOrNotConfigured()
        {
            var viewModelSource = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "UDPtestViewModel.cs"));

            // Must only treat Enabled or Disabled as GPO configured, not Unavailable or NotConfigured
            StringAssert.Contains(viewModelSource, "_gpoConfiguration is GpoRuleConfigured.Enabled or GpoRuleConfigured.Disabled");
            Assert.IsFalse(viewModelSource.Contains("_gpoConfiguration != GpoRuleConfigured.NotConfigured", StringComparison.Ordinal), "Must not treat Unavailable as GPO configured.");
        }
    }
}
