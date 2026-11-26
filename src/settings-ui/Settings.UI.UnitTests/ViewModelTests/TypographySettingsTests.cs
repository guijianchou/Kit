// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Interfaces;
using Kit.Settings.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace ViewModelTests
{
    [TestClass]
    [DoNotParallelize]
    public class TypographySettingsTests
    {
        private static readonly string[] ExpectedTypographyApplySequence = ["persist", "apply", "persist", "apply", "persist", "apply", "persist", "apply"];

        [TestMethod]
        public void ExistingSettingsWithoutTypographyKeepSystemUiAtFourteen()
        {
            var settings = JsonSerializer.Deserialize("{\"startup\":true,\"theme\":\"dark\"}", SettingsSerializationContext.Default.GeneralSettings);

            Assert.AreEqual("system", settings.UiFontFamily);
            Assert.AreEqual(14, settings.UiFontSize);
            Assert.IsTrue(settings.Startup);
            Assert.AreEqual("dark", settings.Theme);
        }

        [DataTestMethod]
        [DataRow("system", 12)]
        [DataRow("system", 14)]
        [DataRow("Noto Sans SC", 16)]
        [DataRow("Noto Sans SC", 20)]
        public void TypographySurvivesSettingsSaveAndReload(string family, int size)
        {
            var fileSystem = new MockFileSystem();
            var storage = new SettingsUtils(fileSystem);
            var settings = new GeneralSettings { UiFontFamily = family, UiFontSize = size, ShowSysTrayIcon = false };
            storage.SaveSettings(settings.ToJsonString());

            var restored = new SettingsUtils(fileSystem).GetSettings<GeneralSettings>();

            Assert.AreEqual(family, restored.UiFontFamily);
            Assert.AreEqual(size, restored.UiFontSize);
            Assert.IsFalse(restored.ShowSysTrayIcon);
            using var document = JsonDocument.Parse(restored.ToJsonString());
            Assert.AreEqual(family, document.RootElement.GetProperty("ui_font_family").GetString());
            Assert.AreEqual(size, document.RootElement.GetProperty("ui_font_size").GetInt32());
        }

        [DataTestMethod]
        [DataRow("\"unknown font\"", "11")]
        [DataRow("null", "21")]
        [DataRow("false", "14.5")]
        [DataRow("123", "\"16\"")]
        [DataRow("[]", "null")]
        [DataRow("{}", "true")]
        [DataRow("\"\"", "[]")]
        [DataRow("\"SYSTEM\"", "{}")]
        [DataRow("\"Noto Sans SC, arbitrary fallback\"", "1e100")]
        public void MalformedTypographyResetsOnlyItsFields(string familyJson, string sizeJson)
        {
            string json = "{\"ui_font_family\":" + familyJson + ",\"ui_font_size\":" + sizeJson + ",\"startup\":true,\"theme\":\"dark\",\"show_tray_icon\":false}";
            var settings = JsonSerializer.Deserialize(json, SettingsSerializationContext.Default.GeneralSettings);

            Assert.AreEqual("system", settings.UiFontFamily);
            Assert.AreEqual(14, settings.UiFontSize);
            Assert.IsTrue(settings.Startup);
            Assert.AreEqual("dark", settings.Theme);
            Assert.IsFalse(settings.ShowSysTrayIcon, "Invalid typography must not reset unrelated preferences.");
        }

        [TestMethod]
        public void NumericWholePixelsAndDirectAssignmentsUseTheSameBounds()
        {
            var settings = JsonSerializer.Deserialize("{\"ui_font_family\":\"Noto Sans SC\",\"ui_font_size\":18.0}", SettingsSerializationContext.Default.GeneralSettings);
            Assert.AreEqual(18, settings.UiFontSize);
            settings.UiFontFamily = null;
            settings.UiFontSize = int.MaxValue;
            Assert.AreEqual("system", settings.UiFontFamily);
            Assert.AreEqual(14, settings.UiFontSize);
        }

        [TestMethod]
        public void FontChangesUseGeneralIpcAndSurviveAnUnrelatedSettingSave()
        {
            var fileSystem = new MockFileSystem();
            var storage = new SettingsUtils(fileSystem);
            var repository = Repository(new GeneralSettings());
            var messages = new List<OutGoingGeneralSettings>();
            using var viewModel = new TestGeneralViewModel(repository.Object, message =>
            {
                var outgoing = JsonSerializer.Deserialize(message, SettingsSerializationContext.Default.OutGoingGeneralSettings);
                messages.Add(outgoing);
                storage.SaveSettings(outgoing.GeneralSettings.ToJsonString());
                return 0;
            });

            viewModel.FontFamilyIndex = 1;
            viewModel.UiFontSize = 18;
            viewModel.ShowSysTrayIcon = false;

            Assert.AreEqual(3, messages.Count);
            Assert.AreEqual("Noto Sans SC", messages[0].GeneralSettings.UiFontFamily);
            Assert.AreEqual(14, messages[0].GeneralSettings.UiFontSize);
            Assert.AreEqual(18, messages[1].GeneralSettings.UiFontSize);
            Assert.AreEqual("Noto Sans SC", messages[2].GeneralSettings.UiFontFamily);
            Assert.AreEqual(18, messages[2].GeneralSettings.UiFontSize);
            var restored = new SettingsUtils(fileSystem).GetSettings<GeneralSettings>();
            Assert.AreEqual("Noto Sans SC", restored.UiFontFamily);
            Assert.AreEqual(18, restored.UiFontSize);
            Assert.IsFalse(restored.ShowSysTrayIcon);
        }

        [TestMethod]
        public void InvalidAndUnchangedControlValuesNeverSendSettings()
        {
            int sent = 0;
            var repository = Repository(new GeneralSettings { UiFontFamily = "Noto Sans SC", UiFontSize = 16 });
            using var viewModel = new TestGeneralViewModel(repository.Object, _ => ++sent);

            viewModel.FontFamilyIndex = -1;
            viewModel.FontFamilyIndex = 2;
            viewModel.FontFamilyIndex = 1;
            foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 11, 21, 14.5, 16 })
            {
                viewModel.UiFontSize = value;
            }

            Assert.AreEqual(0, sent);
            Assert.AreEqual(1, viewModel.FontFamilyIndex);
            Assert.AreEqual("Noto Sans SC", viewModel.UiFontFamily);
            Assert.AreEqual(16d, viewModel.UiFontSize);
            Assert.IsFalse(viewModel.TypographyChanged);
        }

        [TestMethod]
        public void ValidTypographyChangesApplyImmediatelyAfterPersisting()
        {
            var sequence = new List<string>();
            var repository = Repository(new GeneralSettings { UiFontFamily = "Noto Sans SC", UiFontSize = 18 });
            using var viewModel = new TestGeneralViewModel(
                repository.Object,
                _ =>
                {
                    sequence.Add("persist");
                    return 0;
                },
                applyAppearance: () => sequence.Add("apply"));

            viewModel.FontFamilyIndex = 0;
            viewModel.UiFontSize = 14;
            viewModel.FontFamilyIndex = 1;
            viewModel.UiFontSize = 18;

            CollectionAssert.AreEqual(ExpectedTypographyApplySequence, sequence);
            Assert.IsFalse(viewModel.AppearanceRestartRequired);
            Assert.IsFalse(viewModel.TypographyChanged);
        }

        [TestMethod]
        public void InvalidAndUnchangedTypographyNeverRequestsRuntimeApply()
        {
            int applied = 0;
            int sent = 0;
            var repository = Repository(new GeneralSettings { UiFontFamily = "Noto Sans SC", UiFontSize = 20 });
            using var viewModel = new TestGeneralViewModel(repository.Object, _ => ++sent, applyAppearance: () => ++applied);

            viewModel.FontFamilyIndex = -1;
            viewModel.FontFamilyIndex = 2;
            viewModel.FontFamilyIndex = 1;
            foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 11, 21, 14.5, 20 })
            {
                viewModel.UiFontSize = invalid;
            }

            Assert.AreEqual(0, applied);
            Assert.AreEqual(0, sent);
            Assert.AreEqual(20d, viewModel.UiFontSize);
            Assert.IsFalse(viewModel.AppearanceRestartRequired);
        }

        [DataTestMethod]
        [DataRow("system", 14)]
        [DataRow("Noto Sans SC", 20)]
        public void OpeningSettingsNeverRequestsARestart(string family, int size)
        {
            var repository = Repository(new GeneralSettings { UiFontFamily = family, UiFontSize = size });
            using var viewModel = new TestGeneralViewModel(repository.Object, _ => 0, "system", 14);

            Assert.AreEqual(family, viewModel.UiFontFamily);
            Assert.AreEqual((double)size, viewModel.UiFontSize);
            Assert.IsFalse(viewModel.TypographyChanged);
            Assert.IsFalse(viewModel.AppearanceRestartRequired);
        }

        [TestMethod]
        public void LanguageChangesApplyImmediatelyAfterPersistingWithoutRestart()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit", "language.json");
            byte[] original = File.Exists(path) ? File.ReadAllBytes(path) : null;
            try
            {
                var sequence = new List<string>();
                var repository = Repository(new GeneralSettings());
                using var viewModel = new TestGeneralViewModel(
                    repository.Object,
                    _ =>
                    {
                        sequence.Add("persist");
                        return 0;
                    },
                    applyLanguage: language => sequence.Add("apply:" + language));
                int first = (viewModel.LanguagesIndex + 1) % viewModel.Languages.Count;
                int second = (first + 1) % viewModel.Languages.Count;
                string firstTag = viewModel.Languages[first].Tag;
                string secondTag = viewModel.Languages[second].Tag;

                viewModel.LanguagesIndex = first;
                viewModel.LanguagesIndex = first;
                viewModel.LanguagesIndex = second;

                CollectionAssert.AreEqual(new[] { "persist", "apply:" + firstTag, "persist", "apply:" + secondTag }, sequence);
                Assert.IsFalse(viewModel.LanguageChanged);
                Assert.IsFalse(viewModel.AppearanceRestartRequired);
                Assert.AreEqual(secondTag, LanguageModel.LoadSetting());
            }
            finally
            {
                if (original != null)
                {
                    File.WriteAllBytes(path, original);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [TestMethod]
        public void RepositoryReloadRefreshesControlsWithoutEchoingIpc()
        {
            int sent = 0;
            int applied = 0;
            var repository = Repository(new GeneralSettings());
            using var viewModel = new TestGeneralViewModel(repository.Object, _ => ++sent, applyAppearance: () => ++applied);
            var updated = new GeneralSettings { UiFontFamily = "Noto Sans SC", UiFontSize = 20 };
            repository.Object.SettingsConfig = updated;
            repository.Raise(repo => repo.SettingsChanged += null, updated);

            Assert.AreEqual(1, viewModel.FontFamilyIndex);
            Assert.AreEqual(20d, viewModel.UiFontSize);
            Assert.IsFalse(viewModel.AppearanceRestartRequired);
            Assert.AreEqual(0, sent);
            Assert.AreEqual(1, applied, "An external font and size change refreshes the runtime once without echoing IPC.");
            viewModel.UiFontSize = 18;
            Assert.AreEqual(18, updated.UiFontSize, "Subsequent edits must update the replacement settings instance.");
            Assert.AreEqual(1, sent);
            Assert.AreEqual(2, applied);
        }

        private static Mock<ISettingsRepository<GeneralSettings>> Repository(GeneralSettings settings)
        {
            var repository = new Mock<ISettingsRepository<GeneralSettings>>();
            repository.SetupProperty(value => value.SettingsConfig, settings);
            return repository;
        }

        private sealed class TestGeneralViewModel : GeneralViewModel
        {
            public TestGeneralViewModel(ISettingsRepository<GeneralSettings> repository, Func<string, int> send, string appliedUiFontFamily = null, int? appliedUiFontSize = null, Action applyAppearance = null, Action<string> applyLanguage = null)
                : base(repository, "Administrator", "User", false, false, send, _ => 0, _ => 0, "Test\\TypographySettings", appliedUiFontFamily: appliedUiFontFamily, appliedUiFontSize: appliedUiFontSize, applyAppearance: applyAppearance ?? (() => { }), applyLanguage: applyLanguage ?? (_ => { }))
            {
            }

            protected override Microsoft.UI.Dispatching.DispatcherQueue GetDispatcherQueue() => null;
        }
    }
}
