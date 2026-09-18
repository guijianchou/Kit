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
        public void GeneralPage_Xaml_PolicyRulesStructure_Valid()
        {
            var baseDir = System.AppDomain.CurrentDomain.BaseDirectory;
            var dir = new DirectoryInfo(baseDir);
            string xamlPath = null;

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "GeneralPage.xaml");
                if (File.Exists(candidate))
                {
                    xamlPath = candidate;
                    break;
                }

                dir = dir.Parent;
            }

            Assert.IsNotNull(xamlPath, "Could not find GeneralPage.xaml from test directory");

            var content = File.ReadAllText(xamlPath);

            // Verify AI Hub Pipeline card and policy path card are removed
            Assert.IsFalse(content.Contains("x:Uid=\"AiHub_PipelineCard\""), "GeneralPage.xaml should not contain AiHub_PipelineCard");
            Assert.IsFalse(content.Contains("x:Uid=\"AiHub_PolicyFilePathCard\""), "GeneralPage.xaml should not contain AiHub_PolicyFilePathCard");

            // Verify raw header and bottom standalone Save card are removed
            Assert.IsFalse(content.Contains("x:Name=\"AiPolicyRulesSection\""), "GeneralPage.xaml should not contain raw AiPolicyRulesSection");
            Assert.IsFalse(content.Contains("x:Uid=\"AiHub_SaveButton\""), "GeneralPage.xaml should not contain isolated AiHub_SaveButton card");

            // Verify unified Scheme A Policy Rules SettingsExpander is present
            Assert.IsTrue(content.Contains("x:Name=\"AiPolicyRulesExpander\""), "GeneralPage.xaml should contain AiPolicyRulesExpander");
            Assert.IsTrue(content.Contains("x:Uid=\"AiHub_PolicyRulesExpander\""), "GeneralPage.xaml should contain AiHub_PolicyRulesExpander");

            // Verify Segmented control and all 3 segment items are present
            Assert.IsTrue(content.Contains("x:Name=\"AiPolicySegmented\""), "GeneralPage.xaml should contain AiPolicySegmented");
            Assert.IsTrue(content.Contains("AiHub_Segment_GlobalPolicy"), "GeneralPage.xaml should contain AiHub_Segment_GlobalPolicy");
            Assert.IsTrue(content.Contains("AiHub_Segment_SecurityAudit"), "GeneralPage.xaml should contain AiHub_Segment_SecurityAudit");
            Assert.IsTrue(content.Contains("AiHub_Segment_Optimization"), "GeneralPage.xaml should contain AiHub_Segment_Optimization");

            // Verify editor and local action buttons (Save & Reset) are present
            Assert.IsTrue(content.Contains("x:Name=\"AiPolicyContentBox\""), "GeneralPage.xaml should contain AiPolicyContentBox");
            Assert.IsTrue(content.Contains("AiHub_SavePolicyButton"), "GeneralPage.xaml should contain AiHub_SavePolicyButton");
            Assert.IsTrue(content.Contains("AiHub_ResetPolicyButton"), "GeneralPage.xaml should contain AiHub_ResetPolicyButton");
        }
    }
}
