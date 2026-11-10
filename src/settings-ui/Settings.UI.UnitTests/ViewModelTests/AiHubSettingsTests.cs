// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Threading.Tasks;
using Kit.AiHub.Models;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ViewModelTests
{
    [TestClass]
    public class AiHubSettingsTests
    {
        [TestMethod]
        public async Task AiHubViewModel_TabNavigationAndCommands_InitializedCorrectly()
        {
            using var vm = new AiHubViewModel();
            await vm.Initialization;

            Assert.AreEqual(0, vm.ActivePolicyIndex);
            Assert.AreEqual(0, vm.ActivePolicyTabIndex);
            Assert.IsNotNull(vm.SaveActivePolicyCommand);
            Assert.IsNotNull(vm.ResetActivePolicyCommand);
            Assert.IsNotNull(vm.SaveSecurityAuditPolicyCommand);
            Assert.IsNotNull(vm.SaveOptimizationPolicyCommand);
            Assert.IsNotNull(vm.ResetSecurityAuditPolicyCommand);
            Assert.IsNotNull(vm.ResetOptimizationPolicyCommand);
            Assert.IsNotNull(vm.DiscardActivePolicyCommand);

            vm.ActivePolicyIndex = 1;
            Assert.AreEqual(1, vm.ActivePolicyIndex);
            Assert.AreEqual(1, vm.ActivePolicyTabIndex);

            vm.ActivePolicyTabIndex = 2;
            Assert.AreEqual(2, vm.ActivePolicyIndex);
            Assert.AreEqual(2, vm.ActivePolicyTabIndex);

            vm.TaskPolicyIndex = 0;
            Assert.AreEqual(1, vm.ActivePolicyIndex);
            Assert.IsTrue(vm.IsSecurityAuditPolicyTab);
            vm.TaskPolicyIndex = 1;
            Assert.AreEqual(2, vm.ActivePolicyIndex);
            Assert.IsTrue(vm.IsOptimizationPolicyTab);
            vm.ActivePolicyIndex = -1;
            vm.ActivePolicyIndex = 3;
            vm.TaskPolicyIndex = 2;
            vm.TaskPolicyIndex = 3;
            Assert.AreEqual(2, vm.ActivePolicyIndex, "Removed or invalid selections must not address a different policy.");
        }

        [TestMethod]
        public async Task AiHubViewModel_PolicyContent_PropertiesWork()
        {
            using var vm = new AiHubViewModel();
            await vm.InitializeAsync();

            vm.SecurityPolicyContent = "test global policy";
            vm.SecurityAuditPolicyContent = "test security policy";
            vm.OptimizationPolicyContent = "test opt policy";

            vm.ActivePolicyIndex = 0;
            Assert.AreEqual("test global policy", vm.CurrentPolicyContent);

            vm.ActivePolicyIndex = 1;
            Assert.AreEqual("test security policy", vm.CurrentPolicyContent);

            vm.ActivePolicyIndex = 2;
            Assert.AreEqual("test opt policy", vm.CurrentPolicyContent);

            vm.CurrentPolicyContent = "updated opt policy";
            Assert.AreEqual("updated opt policy", vm.OptimizationPolicyContent);

            Assert.IsTrue(vm.IsCurrentPolicyDirty);
            Assert.IsTrue(vm.HasUnsavedPolicyChanges);
            await vm.InitializeAsync();
            Assert.AreEqual("updated opt policy", vm.CurrentPolicyContent, "Repeated initialization must preserve an unsaved draft.");
            vm.TaskPolicyIndex = 0;
            Assert.AreEqual("test security policy", vm.CurrentPolicyContent);
            Assert.AreEqual("updated opt policy", vm.OptimizationPolicyContent);
            Assert.AreEqual(vm.PoliciesLoaded && vm.CanToggle && !vm.IsBusy, vm.CanEditPolicies);
        }

        [TestMethod]
        public async Task AiHubEndpointContextWindowsRemainIndependentAndRejectFractionalTokens()
        {
            using var vm = new AiHubViewModel();
            await vm.Initialization;
            var main = vm.MainEndpoint!;
            var fallback = vm.FallbackEndpoint!;

            main.ContextWindowTokens = 512000;
            fallback.ContextWindowTokens = 128000;
            main.Mode = "native-api.v2";
            main.Effort = "xhigh";

            Assert.AreEqual(512000, main.Settings.ContextWindowTokens);
            Assert.AreEqual(128000, fallback.Settings.ContextWindowTokens);
            Assert.AreEqual("native-api.v2", main.Settings.Mode);
            Assert.AreEqual("xhigh", main.Settings.Effort);
            Assert.IsTrue(vm.HasUnsavedEndpointChanges);

            main.ContextWindowTokens = 512000.5;
            Assert.AreEqual(0, main.Settings.ContextWindowTokens, "A fractional token count must fail persistence validation, not silently truncate.");
            Assert.IsFalse(string.IsNullOrEmpty(main.ContextWindowError));
            Assert.AreEqual(128000, fallback.Settings.ContextWindowTokens);

            main.Settings.ContextWindowTokens = 64000;
            main.NotifyUpdated();
            Assert.AreEqual(64000d, main.ContextWindowTokens);
            Assert.AreEqual(string.Empty, main.ContextWindowError);
        }

        [TestMethod]
        [DataRow("invalid-url")]
        [DataRow("invalid-mode")]
        [DataRow("invalid-effort")]
        [DataRow("invalid-context")]
        public void NewlyConfiguredFallbackIsValidatedBeforeItCanBeSaved(string invalidField)
        {
            var config = AiHubConfig.CreateDefault();
            config.Targets[0].BaseUrl = "https://main.example.test";
            var fallback = config.Targets[1];
            fallback.BaseUrl = "https://fallback.example.test";
            switch (invalidField)
            {
                case "invalid-url":
                    fallback.BaseUrl = "not-an-endpoint";
                    break;
                case "invalid-mode":
                    fallback.Mode = "responses\nother";
                    break;
                case "invalid-effort":
                    fallback.Effort = "high other";
                    break;
                case "invalid-context":
                    fallback.ContextWindowTokens = 0;
                    break;
            }

            Assert.IsFalse(AiHubViewModel.TryPrepareEndpointSettings(config, out _));
            Assert.IsFalse(fallback.IsActive, "Validation must not activate the unsaved draft.");
        }

        [TestMethod]
        public void EndpointSaveSnapshotActivatesAndClearsFallbackWithoutChangingDrafts()
        {
            var config = AiHubConfig.CreateDefault();
            config.Targets[0].BaseUrl = "https://main.example.test";
            config.Targets[1].BaseUrl = "https://fallback.example.test";
            config.Targets[1].ContextWindowTokens = 128000;

            Assert.IsTrue(AiHubViewModel.TryPrepareEndpointSettings(config, out var enabled));
            Assert.IsTrue(enabled[1].IsActive);
            Assert.IsFalse(config.Targets[1].IsActive);
            Assert.AreEqual(128000, enabled[1].ContextWindowTokens);
            enabled[1].ContextWindowTokens = 64000;
            Assert.AreEqual(128000, config.Targets[1].ContextWindowTokens);

            config.Targets[1].IsActive = true;
            config.Targets[1].BaseUrl = string.Empty;
            Assert.IsTrue(AiHubViewModel.TryPrepareEndpointSettings(config, out var disabled));
            Assert.IsFalse(disabled[1].IsActive);
            Assert.IsTrue(config.Targets[1].IsActive);
        }

        [TestMethod]
        public void AiHubPage_Xaml_HostsReadinessIndicatorAndTaskPolicies()
        {
            // The AI service panel (kernel, endpoints, global policy) moved to General;
            // the module page keeps the shared-service status indicator and the per-chain
            // task policies.
            var content = ReadPageXaml("AIHubPage.xaml");

            // Module enable surface (the module page owns this automation id).
            Assert.IsTrue(content.Contains("x:Uid=\"AIHub_EnableToggleSwitch\""), "AIHubPage.xaml should host the module enable toggle");
            Assert.IsTrue(content.Contains("AutomationProperties.AutomationId=\"Toggle_AIHub\""), "AIHubPage.xaml should own the module toggle automation id");

            // The shared AI service indicator: enabled/disabled plus readiness, colored by
            // level, with a re-check action. Removed from the panel in an earlier pass;
            // must stay removed.
            Assert.IsTrue(content.Contains("AiReadinessGlyph"), "AIHubPage.xaml should show the readiness glyph");
            Assert.IsTrue(content.Contains("AiReadinessStyle(ViewModel.AiReadinessLevel)"), "AIHubPage.xaml should theme the readiness glyph by level");
            Assert.IsTrue(content.Contains("AiReadinessText"), "AIHubPage.xaml should show the readiness text");
            Assert.IsTrue(content.Contains("AutomationProperties.AutomationId=\"AIHub_RecheckAiButton\""), "AIHubPage.xaml should offer a readiness re-check");
            Assert.IsFalse(content.Contains("x:Uid=\"AiHub_PipelineCard\""), "AIHubPage.xaml should not contain AiHub_PipelineCard");
            Assert.IsFalse(content.Contains("x:Uid=\"AiHub_PolicyFilePathCard\""), "AIHubPage.xaml should not contain AiHub_PolicyFilePathCard");

            // Task policies (per-chain AGENTS.md) are the third tab.
            Assert.IsTrue(content.Contains("x:Name=\"AiTaskPolicySegmented\""), "AIHubPage.xaml should contain AiTaskPolicySegmented");
            Assert.IsTrue(content.Contains("AIHub_Segment_SecurityAudit"), "AIHubPage.xaml should contain AIHub_Segment_SecurityAudit");
            Assert.IsTrue(content.Contains("AIHub_Segment_Optimization"), "AIHubPage.xaml should contain AIHub_Segment_Optimization");
            Assert.IsFalse(content.Contains("AIHub_Segment_SelfTest"), "The self-test policy entry must be removed from AI Hub.");
            Assert.IsTrue(content.Contains("x:Name=\"AiTaskPolicyContentBox\""), "AIHubPage.xaml should contain AiTaskPolicyContentBox");
            Assert.IsTrue(content.Contains("x:Uid=\"AiHub_SavePolicyButton\""), "AIHubPage.xaml should contain the policy save button");
            Assert.IsTrue(content.Contains("x:Uid=\"AiHub_ResetPolicyButton\""), "AIHubPage.xaml should contain the policy reset button");

            // The kernel/endpoint configuration and the global policy moved to General;
            // they must not come back to the module page.
            Assert.IsFalse(content.Contains("x:Uid=\"AiHub_KernelCard\""), "AIHubPage.xaml should not host the kernel card");
            Assert.IsFalse(content.Contains("x:Uid=\"AiHub_MainEndpointExpander\""), "AIHubPage.xaml should not host the main endpoint expander");
            Assert.IsFalse(content.Contains("x:Uid=\"AiHub_FallbackEndpointExpander\""), "AIHubPage.xaml should not host the fallback endpoint expander");
            Assert.IsFalse(content.Contains("General_AiServicesGroup"), "AIHubPage.xaml should not host the General AI service group");
        }

        [TestMethod]
        public void GeneralPage_HostsTheAiServicePanelBeforeLogs()
        {
            // The shared AI service panel (kernel, endpoints, global policy) lives on
            // General as a first-class group, ordered between Appearance/behavior and
            // Diagnostics &amp; logs, and owns the service toggle automation id.
            var content = ReadPageXaml("GeneralPage.xaml");

            Assert.IsTrue(content.Contains("x:Uid=\"General_AiServicesGroup\""), "GeneralPage.xaml should host the AI service group");
            Assert.IsTrue(content.Contains("x:Uid=\"AiHub_KernelCard\""), "GeneralPage.xaml should host the kernel card");
            Assert.IsTrue(content.Contains("x:Uid=\"AiHub_MainEndpointExpander\""), "GeneralPage.xaml should host the main endpoint expander");
            Assert.IsTrue(content.Contains("x:Uid=\"AiHub_FallbackEndpointExpander\""), "GeneralPage.xaml should host the fallback endpoint expander");
            Assert.IsTrue(content.Contains("x:Name=\"AiPolicyRulesExpander\""), "GeneralPage.xaml should host the global policy editor");

            int aiServiceIndex = content.IndexOf("General_AiServicesGroup", StringComparison.Ordinal);
            int diagnosticsIndex = content.IndexOf("General_DiagnosticsGroup", StringComparison.Ordinal);
            Assert.IsTrue(aiServiceIndex >= 0 && diagnosticsIndex > aiServiceIndex, "The AI service group should appear before Diagnostics & logs");

            Assert.IsTrue(
                content.Contains("AutomationProperties.AutomationId=\"Toggle_AiHub\""),
                "GeneralPage.xaml should own the AI service toggle automation id");
            Assert.IsFalse(
                content.Contains("AutomationProperties.AutomationId=\"Toggle_AIHub\""),
                "GeneralPage.xaml must not duplicate the module page's toggle id");
        }

        private static string ReadPageXaml(string fileName)
        {
            var dir = new DirectoryInfo(System.AppDomain.CurrentDomain.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", fileName);
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }

                dir = dir.Parent;
            }

            Assert.Fail($"Could not find {fileName} from the test directory");
            return string.Empty;
        }
    }
}
