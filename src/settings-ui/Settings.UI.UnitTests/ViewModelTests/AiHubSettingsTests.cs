// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

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
        public void AiHubPage_Xaml_HostsPolicyAndServiceConfiguration()
        {
            // The AI service panel (kernel, endpoints, policy) belongs with the module it
            // configures. It used to live on General, which also meant the enable toggle
            // existed twice with the same automation id.
            var content = ReadPageXaml("AIHubPage.xaml");

            // Removed from the panel in an earlier pass; must stay removed.
            Assert.IsFalse(content.Contains("x:Uid=\"AiHub_PipelineCard\""), "AIHubPage.xaml should not contain AiHub_PipelineCard");
            Assert.IsFalse(content.Contains("x:Uid=\"AiHub_PolicyFilePathCard\""), "AIHubPage.xaml should not contain AiHub_PolicyFilePathCard");
            Assert.IsFalse(content.Contains("x:Name=\"AiPolicyRulesSection\""), "AIHubPage.xaml should not contain raw AiPolicyRulesSection");
            Assert.IsFalse(content.Contains("x:Uid=\"AiHub_SaveButton\""), "AIHubPage.xaml should not contain isolated AiHub_SaveButton card");

            // Policy editor surface.
            Assert.IsTrue(content.Contains("x:Name=\"AiPolicyRulesExpander\""), "AIHubPage.xaml should contain AiPolicyRulesExpander");
            Assert.IsTrue(content.Contains("x:Uid=\"AiHub_PolicyRulesExpander\""), "AIHubPage.xaml should contain AiHub_PolicyRulesExpander");
            Assert.IsTrue(content.Contains("x:Name=\"AiPolicySegmented\""), "AIHubPage.xaml should contain AiPolicySegmented");
            Assert.IsTrue(content.Contains("AiHub_Segment_GlobalPolicy"), "AIHubPage.xaml should contain AiHub_Segment_GlobalPolicy");
            Assert.IsTrue(content.Contains("AiHub_Segment_SecurityAudit"), "AIHubPage.xaml should contain AiHub_Segment_SecurityAudit");
            Assert.IsTrue(content.Contains("AiHub_Segment_Optimization"), "AIHubPage.xaml should contain AiHub_Segment_Optimization");
            Assert.IsTrue(content.Contains("x:Name=\"AiPolicyContentBox\""), "AIHubPage.xaml should contain AiPolicyContentBox");
            Assert.IsTrue(content.Contains("AiHub_SavePolicyButton"), "AIHubPage.xaml should contain AiHub_SavePolicyButton");
            Assert.IsTrue(content.Contains("AiHub_ResetPolicyButton"), "AIHubPage.xaml should contain AiHub_ResetPolicyButton");

            // The kernel/endpoint configuration came along with the policy editor.
            Assert.IsTrue(content.Contains("x:Uid=\"AiHub_KernelCard\""), "AIHubPage.xaml should host the kernel card");
            Assert.IsTrue(content.Contains("x:Uid=\"AiHub_MainEndpointExpander\""), "AIHubPage.xaml should host the main endpoint expander");
            Assert.IsTrue(content.Contains("x:Uid=\"AiHub_FallbackEndpointExpander\""), "AIHubPage.xaml should host the fallback endpoint expander");
        }

        [TestMethod]
        public void GeneralPage_NoLongerHostsTheAiServicePanel()
        {
            // Guards the migration: General must not silently regain a second AI Hub
            // toggle, which previously shared the Toggle_AiHub automation id with the
            // module page.
            var content = ReadPageXaml("GeneralPage.xaml");

            Assert.IsFalse(content.Contains("General_AiServices"), "GeneralPage.xaml should no longer host the AI services group");
            Assert.IsFalse(content.Contains("ViewModel.AiHub"), "GeneralPage.xaml should no longer bind the AI Hub view model");
            Assert.IsFalse(
                content.Contains("Toggle_AiHub"),
                "GeneralPage.xaml must not expose a second Toggle_AiHub: the module page owns that automation id.");
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
