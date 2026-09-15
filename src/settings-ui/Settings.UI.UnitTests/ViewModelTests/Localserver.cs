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
    public class Localserver
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
        public void LocalserverCatalogAndModuleHelperShouldBeConfiguredCorrectly()
        {
            CollectionAssert.Contains(KitModuleCatalog.ActiveModules.ToArray(), ModuleType.Localserver);
            CollectionAssert.Contains(KitModuleCatalog.DashboardModules.ToArray(), ModuleType.Localserver);
            CollectionAssert.DoesNotContain(KitModuleCatalog.QuickAccessModules.ToArray(), ModuleType.Localserver);

            Assert.IsTrue(KitModuleCatalog.IsActiveModule(ModuleType.Localserver));
            Assert.AreEqual("Localserver/ModuleTitle", ModuleHelper.GetModuleLabelResourceName(ModuleType.Localserver));
            Assert.AreEqual("Localserver", ModuleHelper.GetModuleKey(ModuleType.Localserver));
        }

        [TestMethod]
        public void LocalserverSettingsSerializationShouldRoundTrip()
        {
            var settings = new LocalserverSettings();
            Assert.IsNotNull(settings.Properties);
            Assert.AreEqual(1000, settings.Properties.MaxLogLines.Value);
            Assert.AreEqual(5, settings.Properties.StopTimeoutSec.Value);

            settings.Properties.MaxLogLines.Value = 2500;
            settings.Properties.StopTimeoutSec.Value = 10;

            string json = settings.ToJsonString();
            Assert.IsTrue(json.Contains("2500", StringComparison.Ordinal));
            Assert.IsTrue(json.Contains("10", StringComparison.Ordinal));

            var deserialized = JsonSerializer.Deserialize<LocalserverSettings>(json, SettingsSerializationContext.Default.LocalserverSettings);
            Assert.IsNotNull(deserialized);
            Assert.AreEqual(2500, deserialized.Properties.MaxLogLines.Value);
            Assert.AreEqual(10, deserialized.Properties.StopTimeoutSec.Value);
        }

        [TestMethod]
        public void LocalserverSourceFilesShouldRespectStrictDeletionsAndLayoutRules()
        {
            var pageXaml = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "LocalserverPage.xaml"));
            var viewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "LocalserverViewModel.cs"));
            var libPathHelper = File.ReadAllText(FindSourceFile("src", "modules", "Localserver", "LocalserverLib", "Common", "LocalserverPathHelper.cs"));

            // 1. Strictly verify deletions
            Assert.IsFalse(pageXaml.Contains("LastError", StringComparison.OrdinalIgnoreCase), "Localserver UI should not have LastError element.");
            Assert.IsFalse(pageXaml.Contains("Last Error", StringComparison.OrdinalIgnoreCase), "Localserver UI should not have Last Error text.");
            Assert.IsFalse(pageXaml.Contains("ShowOnMainWindow", StringComparison.OrdinalIgnoreCase), "Localserver UI should not have ShowOnMainWindow (链路隐藏).");
            Assert.IsFalse(pageXaml.Contains("AutoStartOnBoot", StringComparison.OrdinalIgnoreCase), "Localserver UI should not have per-line AutoStartOnBoot toggle.");
            Assert.IsFalse(pageXaml.Contains("开机自启", StringComparison.Ordinal), "Localserver UI should not have 开机自启 text.");

            Assert.IsFalse(viewModel.Contains("public string LastError"), "Localserver ViewModel should not expose LastError property.");
            Assert.IsFalse(viewModel.Contains("ShowOnMainWindow", StringComparison.OrdinalIgnoreCase), "Localserver ViewModel should not have ShowOnMainWindow property.");
            Assert.IsFalse(viewModel.Contains("AutoStartOnBoot", StringComparison.OrdinalIgnoreCase), "Localserver ViewModel should not have AutoStartOnBoot property.");

            // 2. Verify Zone 3 runtime freezing & delete line placement
            StringAssert.Contains(pageXaml, "IsEnabled=\"{x:Bind IsEditable, Mode=OneWay}\"");
            StringAssert.Contains(pageXaml, "x:Uid=\"Localserver_DeleteLineCard\"");

            // 3. Verify data isolation paths
            StringAssert.Contains(libPathHelper, "Kit");
            StringAssert.Contains(libPathHelper, "Localserver");
            Assert.IsFalse(libPathHelper.Contains("LocalServerHub", StringComparison.Ordinal), "Path helper should isolate under Kit/Localserver, not LocalServerHub.");
        }

        [TestMethod]
        public void LocalserverModuleInterfaceAndRunnerShouldBeRegistered()
        {
            var runnerMain = File.ReadAllText(FindSourceFile("src", "runner", "main.cpp"));
            var runnerSettingsSource = File.ReadAllText(FindSourceFile("src", "runner", "settings_window.cpp"));
            var runnerSettingsHeader = File.ReadAllText(FindSourceFile("src", "runner", "settings_window.h"));
            var shellPageXaml = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "ShellPage.xaml"));
            var appXamlCs = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "App.xaml.cs"));

            StringAssert.Contains(runnerMain, "Kit.LocalserverModuleInterface.dll");
            StringAssert.Contains(runnerSettingsHeader, "Localserver");
            StringAssert.Contains(runnerSettingsSource, "Localserver");
            StringAssert.Contains(shellPageXaml, "LocalserverNavigationItem");
            StringAssert.Contains(appXamlCs, "\"Localserver\" => typeof(LocalserverPage)");
        }
    }
}
