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

            StringAssert.Contains(viewModel, ": $\"{Latitude}°,{Longitude}°\";");
            Assert.IsFalse(viewModel.Contains('Ã'));
        }

        [TestMethod]
        public void LightSwitchPowerDisplayIntegrationShouldFollowOriginalModuleContract()
        {
            var viewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "LightSwitchViewModel.cs"));

            StringAssert.Contains(viewModel, "SettingsUtils.Default.GetSettingsFilePath(\"PowerDisplay\", \"profiles.json\")");
            StringAssert.Contains(viewModel, "JsonDocument.Parse");
            StringAssert.Contains(viewModel, "AvailableProfiles.Add(new PowerDisplayProfile");
            StringAssert.Contains(viewModel, "SettingsUtils.Default");
            StringAssert.Contains(viewModel, "GetSettingsOrDefault<GeneralSettings>(string.Empty)");
            StringAssert.Contains(viewModel, "generalSettings?.Enabled?.PowerDisplay ?? false");
            Assert.IsFalse(viewModel.Contains("private void CheckPowerDisplayEnabled()\r\n        {\r\n            IsPowerDisplayEnabled = false;\r\n        }", StringComparison.Ordinal));
        }
    }
}
