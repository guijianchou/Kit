// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO.Abstractions.TestingHelpers;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace ViewModelTests
{
    [TestClass]
    public class NavigationPaneStateTests
    {
        [TestMethod]
        public void MissingPreferenceKeepsExpandedDefaultWithoutWritingOnStartup()
        {
            var fileSystem = new MockFileSystem();
            var settings = new SettingsUtils(fileSystem);

            var state = new NavigationPaneState(settings);

            Assert.IsTrue(state.IsPaneOpen);
            Assert.IsFalse(settings.SettingsExists(fileName: SettingsUiPreferences.FileName));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void UserPreferenceSurvivesNewStateAndSettingsInstances(bool isPaneOpen)
        {
            var fileSystem = new MockFileSystem();
            var settings = new SettingsUtils(fileSystem);
            settings.SaveSettings("{\"startup\":true}");

            new NavigationPaneState(settings).SetUserPreference(isPaneOpen);
            var restored = new NavigationPaneState(new SettingsUtils(fileSystem));

            Assert.AreEqual(isPaneOpen, restored.IsPaneOpen);
            Assert.AreEqual(isPaneOpen, restored.ForDisplayMode(NavigationViewDisplayMode.Expanded));
            Assert.AreEqual("{\"startup\":true}", fileSystem.File.ReadAllText(settings.GetSettingsFilePath()));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void AutomaticCompactAndMinimalLayoutsDoNotOverwriteUserPreference(bool isPaneOpen)
        {
            var fileSystem = new MockFileSystem();
            var settings = new SettingsUtils(fileSystem);
            var state = new NavigationPaneState(settings);
            state.SetUserPreference(isPaneOpen);
            string before = fileSystem.File.ReadAllText(settings.GetSettingsFilePath(fileName: SettingsUiPreferences.FileName));

            Assert.IsFalse(state.ForDisplayMode(NavigationViewDisplayMode.Compact));
            Assert.IsFalse(state.ForDisplayMode(NavigationViewDisplayMode.Minimal));
            Assert.AreEqual(isPaneOpen, state.ForDisplayMode(NavigationViewDisplayMode.Expanded));

            Assert.AreEqual(before, fileSystem.File.ReadAllText(settings.GetSettingsFilePath(fileName: SettingsUiPreferences.FileName)));
            Assert.AreEqual(isPaneOpen, new NavigationPaneState(new SettingsUtils(fileSystem)).IsPaneOpen);
        }

        [DataTestMethod]
        [DataRow("{")]
        [DataRow("{\"navigation_pane_open\":\"false\"}")]
        [DataRow("null")]
        public void InvalidPreferenceDoesNotPreventStartupOrRewriteTheInput(string content)
        {
            var fileSystem = new MockFileSystem();
            var settings = new SettingsUtils(fileSystem);
            settings.SaveSettings(content, fileName: SettingsUiPreferences.FileName);

            Assert.IsTrue(new NavigationPaneState(settings).IsPaneOpen);
            Assert.AreEqual(content, fileSystem.File.ReadAllText(settings.GetSettingsFilePath(fileName: SettingsUiPreferences.FileName)));
        }

        [TestMethod]
        public void MissingPropertyUsesExpandedDefault()
        {
            var settings = new SettingsUtils(new MockFileSystem());
            settings.SaveSettings("{}", fileName: SettingsUiPreferences.FileName);

            Assert.IsTrue(new NavigationPaneState(settings).IsPaneOpen);
        }

        [TestMethod]
        public void ReadFailureKeepsShellUsable()
        {
            var settings = new Mock<SettingsUtils>(new MockFileSystem(), null);
            settings.Setup(value => value.GetSettings<SettingsUiPreferences>(string.Empty, SettingsUiPreferences.FileName)).Throws<UnauthorizedAccessException>();

            Assert.IsTrue(new NavigationPaneState(settings.Object).IsPaneOpen);
        }

        [TestMethod]
        public void WriteFailureKeepsTheCurrentSessionPreference()
        {
            var settings = new Mock<SettingsUtils>(new MockFileSystem(), null);
            settings.Setup(value => value.GetSettings<SettingsUiPreferences>(string.Empty, SettingsUiPreferences.FileName)).Returns(new SettingsUiPreferences());
            settings.Setup(value => value.SaveSettings(It.IsAny<string>(), string.Empty, SettingsUiPreferences.FileName)).Throws<UnauthorizedAccessException>();
            var state = new NavigationPaneState(settings.Object);

            state.SetUserPreference(false);

            Assert.IsFalse(state.IsPaneOpen);
            Assert.IsFalse(state.ForDisplayMode(NavigationViewDisplayMode.Expanded));
        }
    }
}
