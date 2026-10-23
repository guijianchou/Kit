// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.UnitTests.BackwardsCompatibility;
using Kit.Settings.UI.UnitTests.Mocks;
using Kit.Settings.UI.ViewModels;
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
            StringAssert.Contains(viewCodeBehind, "this.ViewModel.UpdateSunTimes(latitude, longitude);");
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

        [TestMethod]
        [DataRow("FixedHours")]
        [DataRow("Off")]
        [DataRow("FollowNightLight")]
        public void ModuleSettingsRefreshUpdatesTimesAndClearsSunMarkersWithoutSaving(string nextMode)
        {
            var messages = new List<string>();
            using var viewModel = CreateViewModel(new LightSwitchSettings(), message =>
            {
                messages.Add(message);
                return 0;
            });
            var city = new SearchLocation("Hong Kong", "China", 22.3, 114.2);
            viewModel.SearchLocations.Add(city);
            var notifications = new HashSet<string>();
            viewModel.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

            var sunSettings = new LightSwitchSettings();
            sunSettings.Properties.ScheduleMode.Value = "SunsetToSunrise";
            sunSettings.Properties.Latitude.Value = "22.3";
            sunSettings.Properties.Longitude.Value = "114.2";
            sunSettings.Properties.LightTime.Value = 367;
            sunSettings.Properties.DarkTime.Value = 1105;
            sunSettings.Properties.SunriseOffset.Value = 15;
            sunSettings.Properties.SunsetOffset.Value = -20;
            string savedSunSettings = sunSettings.ToJsonString();

            viewModel.ModuleSettings = sunSettings;

            Assert.AreEqual(savedSunSettings, sunSettings.ToJsonString());
            Assert.AreEqual(TimeSpan.FromMinutes(367), viewModel.LightTimePickerValue);
            Assert.AreEqual(TimeSpan.FromMinutes(1105), viewModel.DarkTimePickerValue);
            Assert.AreEqual(TimeSpan.FromMinutes(382), viewModel.LightTimeTimeSpan);
            Assert.AreEqual(TimeSpan.FromMinutes(1085), viewModel.DarkTimeTimeSpan);
            Assert.AreEqual<TimeSpan?>(TimeSpan.FromMinutes(367), viewModel.SunriseTimeSpan);
            Assert.AreEqual<TimeSpan?>(TimeSpan.FromMinutes(1105), viewModel.SunsetTimeSpan);
            Assert.AreEqual(-367, viewModel.SunriseOffsetMin);
            Assert.AreEqual(717, viewModel.SunriseOffsetMax);
            Assert.AreEqual(-722, viewModel.SunsetOffsetMin);
            Assert.AreEqual(334, viewModel.SunsetOffsetMax);
            Assert.AreSame(city, viewModel.SelectedCity);
            Assert.AreEqual(city.City, viewModel.SyncButtonInformation);
            AssertTimeBindingsNotified(notifications);
            Assert.IsTrue(notifications.Contains(nameof(viewModel.SyncButtonInformation)));

            notifications.Clear();
            var nextSettings = (LightSwitchSettings)sunSettings.Clone();
            nextSettings.Properties.ScheduleMode.Value = nextMode;
            nextSettings.Properties.LightTime.Value = 503;
            nextSettings.Properties.DarkTime.Value = 1259;
            string savedNextSettings = nextSettings.ToJsonString();

            viewModel.ModuleSettings = nextSettings;

            Assert.AreEqual(savedNextSettings, nextSettings.ToJsonString());
            Assert.AreEqual(TimeSpan.FromMinutes(503), viewModel.LightTimePickerValue);
            Assert.AreEqual(TimeSpan.FromMinutes(1259), viewModel.DarkTimePickerValue);
            Assert.AreEqual(TimeSpan.FromMinutes(503), viewModel.LightTimeTimeSpan);
            Assert.AreEqual(TimeSpan.FromMinutes(1259), viewModel.DarkTimeTimeSpan);
            Assert.IsNull(viewModel.SunriseTimeSpan);
            Assert.IsNull(viewModel.SunsetTimeSpan);
            AssertTimeBindingsNotified(notifications);
            Assert.AreEqual(0, messages.Count);
        }

        [TestMethod]
        public void InitialSunScheduleDisplayCalculatesTodayWithoutChangingSavedTimes()
        {
            var settings = new LightSwitchSettings();
            settings.Properties.ScheduleMode.Value = "SunsetToSunrise";
            settings.Properties.Latitude.Value = "22.3";
            settings.Properties.Longitude.Value = "114.2";
            settings.Properties.LightTime.Value = 503;
            settings.Properties.DarkTime.Value = 1259;
            settings.Properties.SunriseOffset.Value = 15;
            settings.Properties.SunsetOffset.Value = -20;
            string savedSettings = settings.ToJsonString();
            var messages = new List<string>();
            using var viewModel = CreateViewModel(settings, message =>
            {
                messages.Add(message);
                return 0;
            });
            viewModel.SearchLocations.Add(new SearchLocation("Hong Kong", "China", 22.3, 114.2));
            var today = DateTime.Now;
            var expected = SunCalc.CalculateSunriseSunset(22.3, 114.2, today.Year, today.Month, today.Day);
            int expectedLightMinutes = (expected.SunriseHour * 60) + expected.SunriseMinute;
            int expectedDarkMinutes = (expected.SunsetHour * 60) + expected.SunsetMinute;

            viewModel.InitializeScheduleMode();
            viewModel.ScheduleMode = viewModel.ScheduleMode;

            Assert.AreEqual(savedSettings, settings.ToJsonString());
            Assert.AreEqual<TimeSpan?>(TimeSpan.FromMinutes(expectedLightMinutes), viewModel.SunriseTimeSpan);
            Assert.AreEqual<TimeSpan?>(TimeSpan.FromMinutes(expectedDarkMinutes), viewModel.SunsetTimeSpan);
            Assert.AreEqual(TimeSpan.FromMinutes(expectedLightMinutes + 15), viewModel.LightTimeTimeSpan);
            Assert.AreEqual(TimeSpan.FromMinutes(expectedDarkMinutes - 20), viewModel.DarkTimeTimeSpan);
            Assert.AreEqual(-expectedLightMinutes, viewModel.SunriseOffsetMin);
            Assert.AreEqual(1439 - expectedDarkMinutes, viewModel.SunsetOffsetMax);
            Assert.AreEqual(TimeSpan.FromMinutes(503), viewModel.LightTimePickerValue);
            Assert.AreEqual(TimeSpan.FromMinutes(1259), viewModel.DarkTimePickerValue);
            Assert.AreEqual("Hong Kong", viewModel.SyncButtonInformation);
            Assert.AreEqual(0, messages.Count);

            var fixedSettings = (LightSwitchSettings)settings.Clone();
            fixedSettings.Properties.ScheduleMode.Value = "FixedHours";
            viewModel.ModuleSettings = fixedSettings;
            viewModel.InitializeScheduleMode();

            Assert.AreEqual(503, fixedSettings.Properties.LightTime.Value);
            Assert.AreEqual(1259, fixedSettings.Properties.DarkTime.Value);
            Assert.AreEqual(TimeSpan.FromMinutes(503), viewModel.LightTimeTimeSpan);
            Assert.AreEqual(TimeSpan.FromMinutes(1259), viewModel.DarkTimeTimeSpan);
            Assert.IsNull(viewModel.SunriseTimeSpan);
            Assert.IsNull(viewModel.SunsetTimeSpan);
            Assert.AreEqual(0, messages.Count);
        }

        [TestMethod]
        public void ModuleSettingsRefreshIgnoresTwoWayControlFeedback()
        {
            var messages = new List<string>();
            using var viewModel = CreateViewModel(new LightSwitchSettings(), message =>
            {
                messages.Add(message);
                return 0;
            });
            var settings = new LightSwitchSettings();
            settings.Properties.ScheduleMode.Value = "SunsetToSunrise";
            settings.Properties.LightTime.Value = 367;
            settings.Properties.DarkTime.Value = 1105;
            settings.Properties.SunriseOffset.Value = 15;
            string savedSettings = settings.ToJsonString();
            bool replayControlValues = true;
            viewModel.PropertyChanged += (_, e) =>
            {
                if (!replayControlValues)
                {
                    return;
                }

                switch (e.PropertyName)
                {
                    case nameof(viewModel.SunriseOffsetMax):
                        viewModel.SunriseOffset = 0;
                        break;
                    case nameof(viewModel.LightTimePickerValue):
                        viewModel.LightTimePickerValue = TimeSpan.FromHours(8);
                        break;
                    case nameof(viewModel.DarkTimePickerValue):
                        viewModel.DarkTimePickerValue = TimeSpan.FromHours(20);
                        break;
                    case nameof(viewModel.ScheduleMode):
                        viewModel.ScheduleMode = "FixedHours";
                        break;
                }
            };

            viewModel.ModuleSettings = settings;

            Assert.AreEqual(savedSettings, settings.ToJsonString());
            Assert.AreEqual(0, messages.Count);

            replayControlValues = false;
            viewModel.SunriseOffset = 16;
            Assert.AreEqual(16, settings.Properties.SunriseOffset.Value);
        }

        [TestMethod]
        public void ApplyingLocationPreservesValidOffsetsAndPublishesOneCompleteSnapshot()
        {
            var settings = CreateSunSettings(60, 180, 30, 0);
            var messages = new List<string>();
            using var viewModel = CreateViewModel(settings, message =>
            {
                messages.Add(message);
                return 0;
            });
            viewModel.RefreshModuleSettings();
            int notifications = 0;
            int pageSaves = 0;
            viewModel.PropertyChanged += (_, _) =>
            {
                ++notifications;
                if (!viewModel.IsRefreshingModuleSettings)
                {
                    ++pageSaves;
                }

                Assert.AreEqual("22.3", viewModel.Latitude);
                Assert.AreEqual("114.2", viewModel.Longitude);
                Assert.AreEqual(480, viewModel.LightTime);
                Assert.AreEqual(1080, viewModel.DarkTime);
                Assert.AreEqual<TimeSpan?>(TimeSpan.FromMinutes(480), viewModel.SunriseTimeSpan);
                Assert.AreEqual<TimeSpan?>(TimeSpan.FromMinutes(1080), viewModel.SunsetTimeSpan);
            };

            viewModel.ApplyLocation(22.3, 114.2, 480, 1080);

            Assert.IsTrue(notifications > 0);
            Assert.AreEqual(0, pageSaves);
            Assert.AreEqual(30, viewModel.SunriseOffset);
            Assert.AreEqual(0, viewModel.SunsetOffset);
            AssertCompleteSavedSettings(viewModel, messages);

            viewModel.ApplyLocation(22.3, 114.2, 480, 1080);
            Assert.AreEqual(1, messages.Count, "An unchanged location must not send another settings save.");
        }

        private static LightSwitchSettings CreateSunSettings(int lightMinutes, int darkMinutes, int sunriseOffset, int sunsetOffset)
        {
            var settings = new LightSwitchSettings();
            settings.Properties.ScheduleMode.Value = "SunsetToSunrise";
            settings.Properties.LightTime.Value = lightMinutes;
            settings.Properties.DarkTime.Value = darkMinutes;
            settings.Properties.SunriseOffset.Value = sunriseOffset;
            settings.Properties.SunsetOffset.Value = sunsetOffset;
            return settings;
        }

        private static void AssertCompleteSavedSettings(LightSwitchViewModel viewModel, List<string> messages)
        {
            Assert.AreEqual(1, messages.Count);
            using var document = JsonDocument.Parse(messages[0]);
            var saved = JsonSerializer.Deserialize<LightSwitchSettings>(document.RootElement.GetProperty("powertoys").GetProperty("LightSwitch"));
            Assert.IsNotNull(saved);
            Assert.AreEqual(viewModel.ModuleSettings.ToJsonString(), saved.ToJsonString());
        }

        private static LightSwitchViewModel CreateViewModel(
            LightSwitchSettings settings,
            System.Func<string, int> sendConfigMessage)
        {
            var generalSettingsRepository =
                new BackCompatTestProperties.MockSettingsRepository<GeneralSettings>(
                    ISettingsUtilsMocks.GetStubSettingsUtils<GeneralSettings>().Object);

            return new LightSwitchViewModel(
                generalSettingsRepository,
                settings,
                sendConfigMessage);
        }

        private static void AssertTimeBindingsNotified(HashSet<string> notifications)
        {
            string[] boundProperties =
            [
                nameof(LightSwitchViewModel.LightTimePickerValue),
                nameof(LightSwitchViewModel.DarkTimePickerValue),
                nameof(LightSwitchViewModel.LightTimeTimeSpan),
                nameof(LightSwitchViewModel.DarkTimeTimeSpan),
                nameof(LightSwitchViewModel.SunriseTimeSpan),
                nameof(LightSwitchViewModel.SunsetTimeSpan),
                nameof(LightSwitchViewModel.SunriseOffsetMin),
                nameof(LightSwitchViewModel.SunriseOffsetMax),
                nameof(LightSwitchViewModel.SunsetOffsetMin),
                nameof(LightSwitchViewModel.SunsetOffsetMax),
            ];
            foreach (string property in boundProperties)
            {
                Assert.IsTrue(notifications.Contains(property), $"Missing change notification for {property}.");
            }
        }
    }
}
