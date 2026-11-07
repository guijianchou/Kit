// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;
using Kit.UITest;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LightSwitch.UITests
{
    internal sealed class TestHelper
    {
        [DllImport("Kit.LightSwitchModuleInterface.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void LightSwitch_SetSystemTheme(bool isLight);

        [DllImport("Kit.LightSwitchModuleInterface.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void LightSwitch_SetAppsTheme(bool isLight);

        /// <summary>
        /// Performs common test initialization: navigate to settings and enable the module
        /// </summary>
        /// <param name="testBase">The test base instance</param>
        /// <param name="testName">Name of the test for assertions</param>
        public static void InitializeTest(UITestBase testBase, string testName)
        {
            LaunchFromSetting(testBase);

            var toggleSwitch = SetLightSwitchToggle(testBase, enable: true);
            Assert.IsTrue(
                toggleSwitch.IsOn,
                $"Light Switch toggle switch should be ON for {testName}");
        }

        /// <summary>
        /// Navigate to the Light Switch settings page
        /// </summary>
        public static void LaunchFromSetting(UITestBase testBase)
        {
            var lightSwitch = testBase.Session.FindAll<NavigationViewItem>(By.AccessibilityId("LightSwitchNavItem"));

            if (lightSwitch.Count == 0)
            {
                testBase.Session.Find<NavigationViewItem>(By.AccessibilityId("SystemToolsNavItem"), 5000).Click(msPostAction: 500);
            }

            testBase.Session.Find<NavigationViewItem>(By.AccessibilityId("LightSwitchNavItem"), 5000).Click(msPostAction: 500);
        }

        /// <summary>
        /// Set the Light Switch enable toggle switch to the specified state
        /// </summary>
        public static ToggleSwitch SetLightSwitchToggle(UITestBase testBase, bool enable)
        {
            var toggleSwitch = testBase.Session.Find<ToggleSwitch>(By.AccessibilityId("Toggle_LightSwitch"), 5000);

            if (toggleSwitch.IsOn != enable)
            {
                toggleSwitch.Click(msPreAction: 1000, msPostAction: 2000);
            }

            if (toggleSwitch.IsOn != enable)
            {
                testBase.Session.SendKey(Key.Space, msPreAction: 0, msPostAction: 2000);
            }

            return toggleSwitch;
        }

        /// <summary>
        /// Performs common test cleanup: close LightSwitch task
        /// </summary>
        /// <param name="testBase">The test base instance</param>
        public static void CleanupTest(UITestBase testBase)
        {
            CloseLightSwitch(testBase);

            // Ensure we're attached to settings after cleanup
            try
            {
                testBase.Session.Attach(PowerToysModule.PowerToysSettings);
            }
            catch
            {
                // Ignore attachment errors - this is just cleanup
            }
        }

        /// <summary>
        /// Switch to white/light theme for both system and apps
        /// </summary>
        /// <param name="testBase">The test base instance</param>
        public static void CloseLightSwitch(UITestBase testBase)
        {
            // Kill LightSwitch process before setting themes
            KillLightSwitchProcess();

            // Set both themes to light (white)
            SetSystemTheme(true);
            SetAppsTheme(true);
        }

        /// <summary>
        /// Kill the LightSwitch service process if it's running
        /// </summary>
        private static void KillLightSwitchProcess()
        {
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName("Kit.LightSwitchService");
                foreach (var process in processes)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(2000);
                    }
                    catch
                    {
                        // Ignore errors killing individual processes
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                // Ignore errors enumerating processes
            }
        }

        /// <summary>
        /// Perform an update time test operation
        /// </summary>
        public static void PerformUpdateTimeTest(UITestBase testBase)
        {
            // Make sure in manual mode
            var modeCombobox = testBase.Session.Find<Element>(By.AccessibilityId("ModeSelection_LightSwitch"), 5000);
            Assert.IsNotNull(modeCombobox, "Mode combobox not found.");

            var neededTabs = 6;

            if (modeCombobox.Text != "Fixed hours")
            {
                modeCombobox.Click();
                var manualListItem = testBase.Session.Find<Element>(By.AccessibilityId("ManualCBItem_LightSwitch"), 5000);
                Assert.IsNotNull(manualListItem, "Fixed Hours combobox item not found.");
                manualListItem.Click();
                neededTabs = 1;
            }

            Assert.AreEqual("Fixed hours", modeCombobox.Text, "Mode combobox should be set to Fixed hours.");

            var timeline = testBase.Session.Find<Element>(By.AccessibilityId("Timeline_LightSwitch"), 5000);
            Assert.IsNotNull(timeline, "Timeline not found.");

            var helpText = timeline.GetAttribute("HelpText");
            string originalEndValue = GetHelpTextValue(helpText, "End");

            for (int i = 0; i < neededTabs; i++)
            {
                testBase.Session.SendKeys(Key.Tab);
            }

            testBase.Session.SendKeys(Key.Enter);
            testBase.Session.SendKeys(Key.Up);
            testBase.Session.SendKeys(Key.Enter);

            helpText = timeline.GetAttribute("HelpText");
            string updatedEndValue = GetHelpTextValue(helpText, "End");

            Assert.AreNotEqual(originalEndValue, updatedEndValue, "Timeline end time should have been updated.");

            helpText = timeline.GetAttribute("HelpText");
            string originalStartValue = GetHelpTextValue(helpText, "Start");

            testBase.Session.SendKeys(Key.Tab);
            testBase.Session.SendKeys(Key.Enter);
            testBase.Session.SendKeys(Key.Up);
            testBase.Session.SendKeys(Key.Enter);

            helpText = timeline.GetAttribute("HelpText");
            string updatedStartValue = GetHelpTextValue(helpText, "Start");

            Assert.AreNotEqual(originalStartValue, updatedStartValue, "Timeline start time should have been updated.");
        }

        /// <summary>
        /// Perform an update manual location test operation
        /// </summary>
        public static void PerformUserSelectedLocationTest(UITestBase testBase)
        {
            // Make sure in sun time mode
            var modeCombobox = testBase.Session.Find<Element>(By.AccessibilityId("ModeSelection_LightSwitch"), 5000);
            Assert.IsNotNull(modeCombobox, "Mode combobox not found.");

            if (modeCombobox.Text != "Sunset to sunrise")
            {
                modeCombobox.Click();
                var sunriseListItem = testBase.Session.Find<Element>(By.AccessibilityId("SunCBItem_LightSwitch"), 5000);
                Assert.IsNotNull(sunriseListItem, "Sunrise combobox item not found.");
                sunriseListItem.Click();
            }

            Assert.AreEqual("Sunset to sunrise", modeCombobox.Text, "Mode combobox should be set to Sunset to sunrise.");

            // Click the select location button
            var setLocationButton = testBase.Session.Find<Element>(By.AccessibilityId("SetLocationButton_LightSwitch"), 5000);
            Assert.IsNotNull(setLocationButton, "Set location button not found.");
            setLocationButton.Click(msPostAction: 1000);

            var latitudeBox = testBase.Session.Find<Element>(By.AccessibilityId("LatitudeBox_LightSwitch"), 5000);
            Assert.IsNotNull(latitudeBox, "Latitude text box not found.");
            latitudeBox.Click();

            testBase.Session.SendKeys(Key.Up);

            var longitudeBox = testBase.Session.Find<Element>(By.AccessibilityId("LongitudeBox_LightSwitch"), 5000);
            Assert.IsNotNull(longitudeBox, "Longitude text box not found.");
            longitudeBox.Click();

            testBase.Session.SendKeys(Key.Down);

            var sunrise = testBase.Session.Find<Element>(By.AccessibilityId("SunriseText_LightSwitch"), 5000);
            Assert.IsFalse(string.IsNullOrWhiteSpace(sunrise.Text));

            var sunset = testBase.Session.Find<Element>(By.AccessibilityId("SunsetText_LightSwitch"), 5000);
            Assert.IsFalse(string.IsNullOrWhiteSpace(sunset.Text));
        }

        /// <summary>
        /// Perform an update geolocation test operation
        /// </summary>
        public static void PerformGeolocationTest(UITestBase testBase)
        {
            // Make sure in sun time mode
            var modeCombobox = testBase.Session.Find<Element>(By.AccessibilityId("ModeSelection_LightSwitch"), 5000);
            Assert.IsNotNull(modeCombobox, "Mode combobox not found.");

            if (modeCombobox.Text != "Sunset to sunrise")
            {
                modeCombobox.Click();
                var sunriseListItem = testBase.Session.Find<Element>(By.AccessibilityId("SunCBItem_LightSwitch"), 5000);
                Assert.IsNotNull(sunriseListItem, "Sunrise combobox item not found.");
                sunriseListItem.Click();
            }

            Assert.AreEqual("Sunset to sunrise", modeCombobox.Text, "Mode combobox should be set to Sunset to sunrise.");

            // Click the select location button
            var setLocationButton = testBase.Session.Find<Element>(By.AccessibilityId("SetLocationButton_LightSwitch"), 5000);
            Assert.IsNotNull(setLocationButton, "Set location button not found.");
            setLocationButton.Click(msPostAction: 1000);

            var syncLocationButton = testBase.Session.Find<Element>(By.AccessibilityId("SyncLocationButton_LightSwitch"), 5000);
            Assert.IsNotNull(syncLocationButton, "Sync location button not found.");
            syncLocationButton.Click(msPostAction: 8000);

            var sunrise = testBase.Session.Find<Element>(By.AccessibilityId("SunriseText_LightSwitch"), 5000);
            Assert.IsFalse(string.IsNullOrWhiteSpace(sunrise.Text));

            var sunset = testBase.Session.Find<Element>(By.AccessibilityId("SunsetText_LightSwitch"), 5000);
            Assert.IsFalse(string.IsNullOrWhiteSpace(sunset.Text));
        }

        /// <summary>
        /// Perform an update time test operation
        /// </summary>
        public static void PerformOffsetTest(UITestBase testBase)
        {
            // Make sure in sun time mode
            var modeCombobox = testBase.Session.Find<Element>(By.AccessibilityId("ModeSelection_LightSwitch"), 5000);
            Assert.IsNotNull(modeCombobox, "Mode combobox not found.");

            if (modeCombobox.Text != "Sunset to sunrise")
            {
                modeCombobox.Click();
                var sunriseListItem = testBase.Session.Find<Element>(By.AccessibilityId("SunCBItem_LightSwitch"), 5000);
                Assert.IsNotNull(sunriseListItem, "Sunrise combobox item not found.");
                sunriseListItem.Click();
            }

            Assert.AreEqual("Sunset to sunrise", modeCombobox.Text, "Mode combobox should be set to Sunset to sunrise.");

            // Testing sunrise offset
            var sunriseOffset = testBase.Session.Find<Element>(By.AccessibilityId("SunriseOffset_LightSwitch"), 5000);
            Assert.IsNotNull(sunriseOffset, "Sunrise offset number box not found.");

            var timeline = testBase.Session.Find<Element>(By.AccessibilityId("Timeline_LightSwitch"), 5000);
            Assert.IsNotNull(timeline, "Timeline not found.");

            var helpText = timeline.GetAttribute("HelpText");
            string originalStartValue = GetHelpTextValue(helpText, "Start");

            sunriseOffset.Click();
            testBase.Session.SendKeys(Key.Up);

            helpText = timeline.GetAttribute("HelpText");
            string updatedStartValue = GetHelpTextValue(helpText, "Start");

            Assert.AreNotEqual(originalStartValue, updatedStartValue, "Timeline start time should have been updated.");

            // Testing sunset offset
            var sunsetOffset = testBase.Session.Find<Element>(By.AccessibilityId("SunsetOffset_LightSwitch"), 5000);
            Assert.IsNotNull(sunsetOffset, "Sunrise offset number box not found.");

            helpText = timeline.GetAttribute("HelpText");
            string originalEndValue = GetHelpTextValue(helpText, "End");

            sunsetOffset.Click();
            testBase.Session.SendKeys(Key.Up);

            helpText = timeline.GetAttribute("HelpText");
            string updatedEndValue = GetHelpTextValue(helpText, "End");

            Assert.AreNotEqual(originalEndValue, updatedEndValue, "Timeline end time should have been updated.");
        }

        /* Helpers */
        private static void SetSystemTheme(bool isLight)
        {
            LightSwitch_SetSystemTheme(isLight);
        }

        private static void SetAppsTheme(bool isLight)
        {
            LightSwitch_SetAppsTheme(isLight);
        }

        private static string GetHelpTextValue(string helpText, string key)
        {
            foreach (var part in helpText.Split(';'))
            {
                var kv = part.Split('=');
                if (kv.Length == 2 && kv[0] == key)
                {
                    return kv[1];
                }
            }

            return string.Empty;
        }
    }
}
