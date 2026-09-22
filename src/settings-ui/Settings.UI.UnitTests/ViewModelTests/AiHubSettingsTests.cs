// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ViewModelTests
{
    [TestClass]
    public class AiHubSettingsTests
    {
        [TestMethod]
        public void AiHubViewModel_TabNavigationAndCommands_InitializedCorrectly()
        {
            var vm = new AiHubViewModel();

            Assert.AreEqual(0, vm.ActivePolicyIndex);
            Assert.AreEqual(0, vm.ActivePolicyTabIndex);
            Assert.IsNotNull(vm.SaveActivePolicyCommand);
            Assert.IsNotNull(vm.ResetActivePolicyCommand);
            Assert.IsNotNull(vm.SaveSecurityAuditPolicyCommand);
            Assert.IsNotNull(vm.SaveOptimizationPolicyCommand);
            Assert.IsNotNull(vm.ResetSecurityAuditPolicyCommand);
            Assert.IsNotNull(vm.ResetOptimizationPolicyCommand);

            vm.ActivePolicyIndex = 1;
            Assert.AreEqual(1, vm.ActivePolicyIndex);
            Assert.AreEqual(1, vm.ActivePolicyTabIndex);

            vm.ActivePolicyTabIndex = 2;
            Assert.AreEqual(2, vm.ActivePolicyIndex);
            Assert.AreEqual(2, vm.ActivePolicyTabIndex);
        }

        [TestMethod]
        public void AiHubViewModel_PolicyContent_PropertiesWork()
        {
            var vm = new AiHubViewModel();

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
        }

        [TestMethod]
        public void AiHubPage_Xaml_HostsReadinessIndicatorAndTaskPolicies()
        {
            // The AI service panel (kernel, endpoints, global policy) moved to General;
            // the module page keeps the shared-service status indicator and the per-chain
            // task policies.
            var content = ReadPageXaml("AIHubPage.xaml");

            // Module enable surface (the module page owns this automation id).
            Assert.IsTrue(content.Contains("x:Uid=\"AIHub_EnableSettingsCard\""), "AIHubPage.xaml should host the module enable card");
            Assert.IsTrue(content.Contains("AutomationProperties.AutomationId=\"Toggle_AIHub\""), "AIHubPage.xaml should own the module toggle automation id");

            // The shared AI service indicator: enabled/disabled plus readiness, colored by
            // level, with a re-check action. Removed from the panel in an earlier pass;
            // must stay removed.
            Assert.IsTrue(content.Contains("AiReadinessGlyph"), "AIHubPage.xaml should show the readiness glyph");
            Assert.IsTrue(content.Contains("AiReadinessBrush"), "AIHubPage.xaml should color the readiness glyph by level");
            Assert.IsTrue(content.Contains("AiReadinessText"), "AIHubPage.xaml should show the readiness text");
            Assert.IsTrue(content.Contains("AutomationProperties.AutomationId=\"AIHub_RecheckAiButton\""), "AIHubPage.xaml should offer a readiness re-check");
            Assert.IsFalse(content.Contains("x:Uid=\"AiHub_PipelineCard\""), "AIHubPage.xaml should not contain AiHub_PipelineCard");
            Assert.IsFalse(content.Contains("x:Uid=\"AiHub_PolicyFilePathCard\""), "AIHubPage.xaml should not contain AiHub_PolicyFilePathCard");

            // Task policies (per-chain AGENTS.md) are the third tab.
            Assert.IsTrue(content.Contains("x:Name=\"AiTaskPolicySegmented\""), "AIHubPage.xaml should contain AiTaskPolicySegmented");
            Assert.IsTrue(content.Contains("AIHub_Segment_SecurityAudit"), "AIHubPage.xaml should contain AIHub_Segment_SecurityAudit");
            Assert.IsTrue(content.Contains("AIHub_Segment_Optimization"), "AIHubPage.xaml should contain AIHub_Segment_Optimization");
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
