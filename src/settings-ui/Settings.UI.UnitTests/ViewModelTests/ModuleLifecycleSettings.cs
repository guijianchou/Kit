// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Text.Json;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.Library.Interfaces;
using Kit.Settings.UI.ViewModels;
using ManagedCommon;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace ViewModelTests
{
    [TestClass]
    public class ModuleLifecycleSettings
    {
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void UtilitiesAiHubToggleUpdatesTheModuleState(bool enabled)
        {
            var settings = new GeneralSettings();
            settings.Enabled.AiHub = !enabled;
            settings.Enabled.Awake = true;
            settings.Enabled.Localserver = false;

            // This is the state update invoked by the Utilities ToggleSwitch callback.
            ModuleHelper.SetIsModuleEnabled(settings, ModuleType.AIHub, enabled);

            Assert.AreEqual(enabled, settings.Enabled.AiHub);
            Assert.AreEqual(enabled, ModuleHelper.GetIsModuleEnabled(settings, ModuleType.AIHub));
            Assert.IsTrue(settings.Enabled.Awake);
            Assert.IsFalse(settings.Enabled.Localserver);
            Assert.AreEqual("AIHub", ModuleHelper.GetModuleKey(ModuleType.AIHub));
        }

        [TestMethod]
        public void PartialRunnerReplyPreservesOtherModulesWithoutSendingChanges()
        {
            var settings = new GeneralSettings();
            settings.Enabled.Awake = false;
            settings.Enabled.LightSwitch = false;
            settings.Enabled.Localserver = true;
            settings.Enabled.UDPtest = true;
            var notifications = 0;
            settings.AddEnabledModuleChangeNotification(() => notifications++);

            settings.Enabled.MergeFrom(JsonSerializer.Deserialize<EnabledModules>("{\"AIHub\":true}"));

            Assert.IsFalse(settings.Enabled.Awake);
            Assert.IsFalse(settings.Enabled.LightSwitch);
            Assert.IsTrue(settings.Enabled.Localserver);
            Assert.IsTrue(settings.Enabled.UDPtest);
            Assert.IsTrue(settings.Enabled.AiHub);
            Assert.AreEqual(0, notifications, "Reading a Runner reply must not generate a new enable request.");
        }

        [TestMethod]
        [DataRow("AIHub")]
        [DataRow("AiHub")]
        public void AiHubAliasesWriteTheRunnerModuleKey(string key)
        {
            var modules = JsonSerializer.Deserialize<EnabledModules>($"{{\"{key}\":true,\"OtherModule\":false}}");
            using var output = JsonDocument.Parse(modules.ToJsonString());

            Assert.IsTrue(output.RootElement.GetProperty("AIHub").GetBoolean());
            Assert.IsFalse(output.RootElement.TryGetProperty("AiHub", out _));
            Assert.IsFalse(output.RootElement.GetProperty("OtherModule").GetBoolean());
        }

        [TestMethod]
        public void LightSwitchRefreshReadsLatestRepositoryWithoutSendingSettings()
        {
            var repository = new Mock<ISettingsRepository<GeneralSettings>>();
            repository.SetupProperty(r => r.SettingsConfig, new GeneralSettings());
            var sent = new List<string>();
            using var viewModel = new LightSwitchViewModel(repository.Object, ipcMSGCallBackFunc: message =>
            {
                sent.Add(message);
                return 0;
            });
            var updated = new GeneralSettings();
            updated.Enabled.LightSwitch = false;
            repository.Object.SettingsConfig = updated;

            viewModel.RefreshEnabledState();

            Assert.IsFalse(viewModel.IsEnabled);
            Assert.AreEqual(0, sent.Count);
        }

        [TestMethod]
        public void LightSwitchToggleDoesNotResendOtherModulesFromAnOldSnapshot()
        {
            var original = new GeneralSettings();
            original.Enabled.LightSwitch = false;
            original.Enabled.Awake = false;
            var repository = new Mock<ISettingsRepository<GeneralSettings>>();
            repository.SetupProperty(r => r.SettingsConfig, original);
            string sent = null;
            using var viewModel = new LightSwitchViewModel(repository.Object, ipcMSGCallBackFunc: message =>
            {
                sent = message;
                return 0;
            });
            var updated = new GeneralSettings();
            updated.Enabled.LightSwitch = false;
            updated.Enabled.Awake = true;
            updated.Enabled.Localserver = true;
            repository.Object.SettingsConfig = updated;

            viewModel.IsEnabled = true;

            Assert.IsNotNull(sent);
            using var output = JsonDocument.Parse(sent);
            var enabled = output.RootElement.GetProperty("general").GetProperty("enabled");
            Assert.IsTrue(enabled.GetProperty("Awake").GetBoolean());
            Assert.IsTrue(enabled.GetProperty("Localserver").GetBoolean());
            Assert.IsTrue(enabled.GetProperty("LightSwitch").GetBoolean());
            Assert.IsTrue(updated.Enabled.LightSwitch);
        }
    }
}
