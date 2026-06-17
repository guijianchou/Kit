// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ViewModelTests
{
    [TestClass]
    public class LightSwitch
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
        public void LightSwitchCoordinateFallbackShouldUseReadableDegreeSymbols()
        {
            var viewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "LightSwitchViewModel.cs"));
            var viewCodeBehind = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "LightSwitchPage.xaml.cs"));

            StringAssert.Contains(viewModel, ": $\"{Latitude}\u00B0,{Longitude}\u00B0\";");
            StringAssert.Contains(viewCodeBehind, "SyncButtonInformation = $\"{this.ViewModel.Latitude}\u00B0, {this.ViewModel.Longitude}\u00B0\";");
            Assert.IsFalse(viewModel.Contains("\u00C2\u00B0", StringComparison.Ordinal));
            Assert.IsFalse(viewCodeBehind.Contains("\u00C2\u00B0", StringComparison.Ordinal));
            Assert.IsFalse(viewModel.Contains('\u00C3'));
            Assert.IsFalse(viewCodeBehind.Contains('\u00C3'));
        }

        [TestMethod]
        public void LightSwitchShouldNotKeepPowerDisplayProfileBridgeAfterModuleRemoval()
        {
            var viewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "LightSwitchViewModel.cs"));
            var view = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "LightSwitchPage.xaml"));
            var viewCodeBehind = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "LightSwitchPage.xaml.cs"));
            var properties = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "LightSwitchProperties.cs"));

            Assert.IsFalse(viewModel.Contains("PowerDisplay", StringComparison.Ordinal), "LightSwitch view model should not read deleted PowerDisplay profiles or enabled state.");
            Assert.IsFalse(view.Contains("PowerDisplay", StringComparison.Ordinal), "LightSwitch page should not expose deleted PowerDisplay profile controls.");
            Assert.IsFalse(viewCodeBehind.Contains("PowerDisplay", StringComparison.Ordinal), "LightSwitch code-behind should not navigate to the deleted PowerDisplay settings page.");
            Assert.IsFalse(properties.Contains("PowerDisplay", StringComparison.Ordinal), "LightSwitch settings schema should not expose deleted PowerDisplay profile settings.");
            Assert.IsFalse(properties.Contains("ModeProfile", StringComparison.Ordinal), "LightSwitch settings schema should not keep monitor-profile fields after PowerDisplay removal.");
        }
    }
}
