// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ViewModelTests
{
    [TestClass]
    public class BuildCompatibility
    {
        private static string FindSourceFile(params string[] relativePathParts)
        {
            if (TryFindSourceFile(out var sourceFile, relativePathParts))
            {
                return sourceFile;
            }

            Assert.Fail($"Could not find source file: {Path.Combine(relativePathParts)}");
            return string.Empty;
        }

        private static bool TryFindSourceFile(out string sourceFile, params string[] relativePathParts)
        {
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(new[] { directory.FullName }.Concat(relativePathParts).ToArray());
                if (File.Exists(candidate))
                {
                    sourceFile = candidate;
                    return true;
                }

                directory = directory.Parent;
            }

            sourceFile = string.Empty;
            return false;
        }

        private static bool TryFindSourceOrPowerToysReferenceFile(out string sourceFile, params string[] relativePathParts)
        {
            if (TryFindSourceFile(out sourceFile, relativePathParts))
            {
                return true;
            }

            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(new[] { directory.FullName, "PowerToys-main" }.Concat(relativePathParts).ToArray());
                if (File.Exists(candidate))
                {
                    sourceFile = candidate;
                    return true;
                }

                directory = directory.Parent;
            }

            return false;
        }

        [TestMethod]
        public void CsWinRTProjectionShouldRecoverFromStaleResponseFiles()
        {
            var csWinRTProps = File.ReadAllText(FindSourceFile("src", "Common.Dotnet.CsWinRT.props"));

            StringAssert.Contains(csWinRTProps, "KitInvalidateStaleCsWinRTProjection");
            StringAssert.Contains(csWinRTProps, "cswinrt.rsp");
            StringAssert.Contains(csWinRTProps, @"**\*.cs");
        }

        [TestMethod]
        public void CsWinRTProjectionShouldNotRunDuringDesignTimeBuilds()
        {
            var csWinRTProps = File.ReadAllText(FindSourceFile("src", "Common.Dotnet.CsWinRT.props"));

            StringAssert.Contains(csWinRTProps, "DesignTimeBuild");
            StringAssert.Contains(csWinRTProps, "<CsWinRTGenerateProjection Condition=\"'$(DesignTimeBuild)' == 'true'\">false</CsWinRTGenerateProjection>");
        }

        [TestMethod]
        public void NativeWinMDProjectsShouldPublishToRepoOutput()
        {
            var interopProject = File.ReadAllText(FindSourceFile("src", "common", "interop", "PowerToys.Interop.vcxproj"));
            var gpoWrapperProject = File.ReadAllText(FindSourceFile("src", "common", "GPOWrapper", "GPOWrapper.vcxproj"));

            StringAssert.Contains(interopProject, "CopyInteropWinMDToRepoOutput");
            StringAssert.Contains(interopProject, @"$(RepoRoot)$(Platform)\$(Configuration)\");
            StringAssert.Contains(gpoWrapperProject, "CopyGPOWrapperWinMDToRepoOutput");
            StringAssert.Contains(gpoWrapperProject, @"$(RepoRoot)$(Platform)\$(Configuration)\");
        }

        [TestMethod]
        public void RunnerShouldHaveExplicitWilIncludeFallback()
        {
            var runnerProject = File.ReadAllText(FindSourceFile("src", "runner", "Kit.vcxproj"));

            StringAssert.Contains(runnerProject, @"$(PkgMicrosoft_Windows_ImplementationLibrary)\include");
        }

        [TestMethod]
        public void KitBrandingShouldUseKitProductMetadata()
        {
            var directoryBuildProps = File.ReadAllText(FindSourceFile("Directory.Build.props"));

            StringAssert.Contains(directoryBuildProps, "<AssemblyProduct>Kit</AssemblyProduct>");
            StringAssert.Contains(directoryBuildProps, "<Product>Kit</Product>");
            StringAssert.Contains(directoryBuildProps, "<PackageTags>Kit</PackageTags>");
        }

        [TestMethod]
        public void ReleaseBuildShouldKeepSlimPublishDefaults()
        {
            var directoryBuildProps = File.ReadAllText(FindSourceFile("Directory.Build.props"));
            var directoryBuildTargets = File.ReadAllText(FindSourceFile("Directory.Build.targets"));
            var cppBuildProps = File.ReadAllText(FindSourceFile("Cpp.Build.props"));
            var commonUiProject = File.ReadAllText(FindSourceFile("src", "common", "Common.UI", "Common.UI.csproj"));

            StringAssert.Contains(directoryBuildProps, "<SatelliteResourceLanguages>en-US</SatelliteResourceLanguages>");
            StringAssert.Contains(directoryBuildProps, "<DebugType>none</DebugType>");
            StringAssert.Contains(directoryBuildProps, "<DebugSymbols>false</DebugSymbols>");
            StringAssert.Contains(directoryBuildTargets, "<DebugType>none</DebugType>");
            StringAssert.Contains(directoryBuildTargets, "<DebugSymbols>false</DebugSymbols>");
            StringAssert.Contains(directoryBuildTargets, "KitRemoveReleasePdbsFromCsprojOutput");
            StringAssert.Contains(directoryBuildTargets, @"$(OutDir)**\*.pdb");
            StringAssert.Contains(directoryBuildTargets, "KitRemoveNonEnglishSatelliteDirsFromCsprojOutput");
            StringAssert.Contains(directoryBuildTargets, "KitNonEnglishSatelliteLanguage Include=");
            StringAssert.Contains(directoryBuildTargets, "af-ZA;am-ET;ar-SA");
            StringAssert.Contains(directoryBuildTargets, "en-GB");
            StringAssert.Contains(directoryBuildTargets, "zh-CN;zh-TW");
            StringAssert.Contains(directoryBuildTargets, @"@(KitNonEnglishSatelliteLanguage->'$(OutDir)%(Identity)')");
            StringAssert.Contains(directoryBuildTargets, "KitRemoveStaticLibArtifactsFromRuntimeOutput");
            StringAssert.Contains(directoryBuildTargets, @"$(OutDir)**\*.lib;$(OutDir)**\*.exp;$(OutDir)**\*.lib.lastcodeanalysissucceeded");
            StringAssert.Contains(directoryBuildTargets, "KitRemoveInactiveModelProviderArtifactsFromRuntimeOutput");
            StringAssert.Contains(directoryBuildTargets, @"$(OutDir)**\Assets\Settings\Icons\Models\*.svg;$(OutDir)**\*Foundry*");
            StringAssert.Contains(directoryBuildTargets, "KitRemoveInactiveManagedTelemetryArtifactsFromOutput");
            StringAssert.Contains(directoryBuildTargets, @"$(OutDir)**\PowerToys.ManagedTelemetry.*");
            StringAssert.Contains(directoryBuildTargets, @"$(OutDir)**\Dia2Lib.dll");
            StringAssert.Contains(directoryBuildTargets, @"$(OutDir)**\TraceReloggerLib.dll");
            StringAssert.Contains(directoryBuildTargets, @"$(OutDir)**\KernelTraceControl.dll");
            StringAssert.Contains(directoryBuildTargets, @"$(OutDir)**\msdia140.dll");
            StringAssert.Contains(cppBuildProps, "<DebugInformationFormat>None</DebugInformationFormat>");
            StringAssert.Contains(cppBuildProps, "<GenerateDebugInformation>false</GenerateDebugInformation>");
            StringAssert.Contains(commonUiProject, "<UseWPF>false</UseWPF>");
            StringAssert.Contains(commonUiProject, "<UseWindowsForms>false</UseWindowsForms>");
        }

        [TestMethod]
        public void KitBuildToolsShouldSupportExplicitOutputCleanupAndArtifactChecks()
        {
            var cleanStaleVersionsScript = File.ReadAllText(FindSourceFile("tools", "build", "clean-stale-versions.ps1"));
            var verifyRuntimeArtifactsScript = File.ReadAllText(FindSourceFile("tools", "build", "verify-runtime-artifacts.ps1"));

            StringAssert.Contains(cleanStaleVersionsScript, "Version.props");
            StringAssert.Contains(cleanStaleVersionsScript, "-WhatIf");
            StringAssert.Contains(cleanStaleVersionsScript, "Remove-Item");
            StringAssert.Contains(cleanStaleVersionsScript, "1.0.3");
            StringAssert.Contains(cleanStaleVersionsScript, "Debug");
            StringAssert.Contains(cleanStaleVersionsScript, "Release");

            StringAssert.Contains(verifyRuntimeArtifactsScript, "*.lib");
            StringAssert.Contains(verifyRuntimeArtifactsScript, "*.pdb");
            StringAssert.Contains(verifyRuntimeArtifactsScript, "*Foundry*");
            StringAssert.Contains(verifyRuntimeArtifactsScript, "PowerToys.ManagedTelemetry.*");
            StringAssert.Contains(verifyRuntimeArtifactsScript, "Dia2Lib.dll");
            StringAssert.Contains(verifyRuntimeArtifactsScript, "TraceReloggerLib.dll");
            StringAssert.Contains(verifyRuntimeArtifactsScript, "KernelTraceControl.dll");
            StringAssert.Contains(verifyRuntimeArtifactsScript, "msdia140.dll");
            StringAssert.Contains(verifyRuntimeArtifactsScript, "$OutputRoot");
            StringAssert.Contains(verifyRuntimeArtifactsScript, "Join-Path $platformRoot 'Release'");
            StringAssert.Contains(verifyRuntimeArtifactsScript, "Non-English locale directory");
            StringAssert.Contains(verifyRuntimeArtifactsScript, "exit 1");
        }

        [TestMethod]
        public void KitSettingsShouldDeleteInactiveModuleSourceFilesInsteadOfExcludingThem()
        {
            var settingsProject = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "PowerToys.Settings.csproj"));
            var settingsUiRoot = Path.GetDirectoryName(FindSourceFile("src", "settings-ui", "Settings.UI", "PowerToys.Settings.csproj"));

            Assert.IsFalse(settingsProject.Contains(@"<Compile Remove=""", StringComparison.Ordinal), "Inactive Settings sources should be deleted rather than hidden behind Compile Remove rules.");
            Assert.IsFalse(settingsProject.Contains(@"<Page Remove=""SettingsXAML\Views\", StringComparison.Ordinal), "Inactive Settings XAML pages should be deleted rather than hidden behind Page Remove rules.");
            Assert.IsFalse(settingsProject.Contains(@"<Page Remove=""SettingsXAML\OOBE\", StringComparison.Ordinal), "Inactive OOBE XAML pages should be deleted rather than hidden behind Page Remove rules.");
            Assert.IsFalse(settingsProject.Contains(@"<Page Remove=""SettingsXAML\OobeWindow.xaml", StringComparison.Ordinal), "Inactive OOBE window should be deleted rather than hidden behind Page Remove rules.");
            Assert.IsFalse(settingsProject.Contains(@"<Page Remove=""SettingsXAML\ScoobeWindow.xaml", StringComparison.Ordinal), "Inactive SCOOBE window should be deleted rather than hidden behind Page Remove rules.");
            Assert.IsFalse(settingsProject.Contains(@"<Page Remove=""SettingsXAML\Panels\", StringComparison.Ordinal), "Inactive Settings panels should be deleted rather than hidden behind Page Remove rules.");
            Assert.IsFalse(settingsProject.Contains(@"<Page Remove=""SettingsXAML\Controls\ModelPicker\", StringComparison.Ordinal), "Inactive model picker pages should be deleted rather than hidden behind Page Remove rules.");

            string[] inactiveViewModelPrefixes =
            {
                "AdvancedPaste",
                "AlwaysOnTop",
                "Cmd",
                "ColorPicker",
                "CropAndLock",
                "EnvironmentVariables",
                "FancyZones",
                "FileLocksmith",
                "GrabAndMove",
                "Hosts",
                "ImageResizer",
                "KeyboardManager",
                "MeasureTool",
                "Mouse",
                "NewPlus",
                "Peek",
                "PowerAccent",
                "PowerLauncher",
                "PowerOcr",
                "PowerPreview",
                "PowerRename",
                "RegistryPreview",
                "ShortcutGuide",
                "Workspaces",
                "ZoomIt",
            };

            foreach (var prefix in inactiveViewModelPrefixes)
            {
                AssertNoFiles(settingsUiRoot!, Path.Combine("ViewModels", $"{prefix}*.cs"));
                AssertNoFiles(settingsUiRoot!, Path.Combine("SettingsXAML", "Views", $"{prefix}*.xaml"));
                AssertNoFiles(settingsUiRoot!, Path.Combine("SettingsXAML", "Views", $"{prefix}*.xaml.cs"));
            }

            string[] inactiveSourceFiles =
            {
                Path.Combine("Converters", "MouseJumpPreviewTypeConverter.cs"),
                Path.Combine("SettingsXAML", "Controls", "ModelPicker", "FoundryLocalModelPicker.xaml"),
                Path.Combine("SettingsXAML", "Controls", "ModelPicker", "FoundryLocalModelPicker.xaml.cs"),
                Path.Combine("SettingsXAML", "OobeWindow.xaml"),
                Path.Combine("SettingsXAML", "OobeWindow.xaml.cs"),
                Path.Combine("SettingsXAML", "ScoobeWindow.xaml"),
                Path.Combine("SettingsXAML", "ScoobeWindow.xaml.cs"),
                Path.Combine("SettingsXAML", "Panels", "MouseJumpPanel.xaml"),
                Path.Combine("SettingsXAML", "Panels", "MouseJumpPanel.xaml.cs"),
            };

            foreach (var relativePath in inactiveSourceFiles)
            {
                Assert.IsFalse(File.Exists(Path.Combine(settingsUiRoot!, relativePath)), $"Inactive source file should be deleted: {relativePath}");
            }

            AssertNoFiles(settingsUiRoot!, Path.Combine("SettingsXAML", "OOBE", "Views", "*.xaml"));
            AssertNoFiles(settingsUiRoot!, Path.Combine("SettingsXAML", "OOBE", "Views", "*.xaml.cs"));
            AssertNoFiles(settingsUiRoot!, Path.Combine("SettingsXAML", "OOBE", "Views", "*.cs"));
        }

        [TestMethod]
        public void KitSettingsShouldDeleteInactiveControlsConvertersAndOobeViewModels()
        {
            var settingsProjectPath = FindSourceFile("src", "settings-ui", "Settings.UI", "PowerToys.Settings.csproj");
            var settingsProject = File.ReadAllText(settingsProjectPath);
            var settingsUiRoot = Path.GetDirectoryName(settingsProjectPath);

            string[] inactiveSourceFiles =
            {
                Path.Combine("Converters", "ImageResizerDoubleToAutoConverter.cs"),
                Path.Combine("Converters", "ImageResizerFitToIntConverter.cs"),
                Path.Combine("Converters", "ImageResizerFitToStringConverter.cs"),
                Path.Combine("Converters", "ImageResizerNumberBoxValueConverter.cs"),
                Path.Combine("Converters", "ImageResizerSizeToAccessibleTextConverter.cs"),
                Path.Combine("Converters", "ImageResizerUnitToIntConverter.cs"),
                Path.Combine("Converters", "ImageResizerUnitToStringConverter.cs"),
                Path.Combine("Converters", "ImageResizerZeroToEmptyStringNumberFormatter.cs"),
                Path.Combine("Converters", "ZoomItInitialZoomConverter.cs"),
                Path.Combine("Converters", "ZoomItOpacitySliderConverter.cs"),
                Path.Combine("Converters", "ZoomItTypeSpeedSliderConverter.cs"),
                Path.Combine("OOBE", "Enums", "PowerToysModules.cs"),
                Path.Combine("OOBE", "ViewModel", "OobePowerToysModule.cs"),
                Path.Combine("OOBE", "ViewModel", "OobeShellViewModel.cs"),
                Path.Combine("SettingsXAML", "Controls", "ColorFormatEditor.xaml"),
                Path.Combine("SettingsXAML", "Controls", "ColorFormatEditor.xaml.cs"),
                Path.Combine("SettingsXAML", "Controls", "ColorPickerButton.xaml"),
                Path.Combine("SettingsXAML", "Controls", "ColorPickerButton.xaml.cs"),
                Path.Combine("SettingsXAML", "Controls", "FancyZonesPreviewControl.xaml"),
                Path.Combine("SettingsXAML", "Controls", "FancyZonesPreviewControl.xaml.cs"),
                Path.Combine("SettingsXAML", "Controls", "ImageResizerDimensionsNumberBox.cs"),
                Path.Combine("SettingsXAML", "Controls", "OOBEPageControl.xaml"),
                Path.Combine("SettingsXAML", "Controls", "OOBEPageControl.xaml.cs"),
                Path.Combine("SettingsXAML", "Controls", "PowerAccentShortcutControl.xaml"),
                Path.Combine("SettingsXAML", "Controls", "PowerAccentShortcutControl.xaml.cs"),
            };

            foreach (var relativePath in inactiveSourceFiles)
            {
                Assert.IsFalse(File.Exists(Path.Combine(settingsUiRoot!, relativePath)), $"Inactive Settings UI source should be deleted: {relativePath}");
            }

            StringAssert.Contains(settingsProject, "KitInactiveSettingsXamlOutputs");
            StringAssert.Contains(settingsProject, @"$(OutDir)SettingsXAML\Controls\ColorFormatEditor.xbf");
            StringAssert.Contains(settingsProject, @"$(OutDir)SettingsXAML\Controls\OOBEPageControl.xbf");
        }

        [TestMethod]
        public void KitSettingsResourcesShouldDeleteInactiveModuleAndOobeStrings()
        {
            var resources = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Strings", "en-us", "Resources.resw"));

            string[] inactiveResourceNamePrefixes =
            {
                "AdvancedPaste",
                "Alternate_OOBE",
                "CmdPal",
                "ColorPicker",
                "FancyZones",
                "FileLocksmith",
                "GPO_AdvancedPasteAi",
                "ImageResizer",
                "Launch_ShortcutGuide",
                "LearnMore_AdvancedPaste",
                "LearnMore_CmdPal",
                "LearnMore_ColorPicker",
                "LearnMore_FancyZones",
                "LearnMore_FileLocksmith",
                "LearnMore_ImageResizer",
                "LearnMore_MouseWithoutBorders",
                "LearnMore_PowerRename",
                "LearnMore_ShortcutGuide",
                "MouseWithoutBorders",
                "OOBE_",
                "Oobe",
                "OobeWindow",
                "PowerLauncher",
                "PowerRename",
                "Run_CheckOutCmdPal",
                "Run_NavigateCmdPalSettings",
                "Shell_AdvancedPaste",
                "Shell_CmdPal",
                "Shell_ColorPicker",
                "Shell_FancyZones",
                "Shell_FileLocksmith",
                "Shell_ImageResizer",
                "Shell_MouseWithoutBorders",
                "Shell_PowerLauncher",
                "Shell_PowerRename",
                "Shell_ShortcutGuide",
                "ShortcutGuide",
                "Scoobe",
                "ScoobeWindow",
            };

            foreach (var prefix in inactiveResourceNamePrefixes)
            {
                Assert.IsFalse(resources.Contains($"name=\"{prefix}", StringComparison.Ordinal), $"Inactive Settings resource prefix should be deleted: {prefix}");
            }
        }

        [TestMethod]
        public void KitSettingsTestsShouldDeleteInactiveModuleTestsInsteadOfProjectExcludingThem()
        {
            var settingsTestsProjectPath = FindSourceFile("src", "settings-ui", "Settings.UI.UnitTests", "Settings.UI.UnitTests.csproj");
            var settingsTestsProject = File.ReadAllText(settingsTestsProjectPath);
            var settingsTestsRoot = Path.GetDirectoryName(settingsTestsProjectPath);

            string[] inactiveTestFiles =
            {
                Path.Combine("ViewModelTests", "ColorPicker.cs"),
                Path.Combine("ViewModelTests", "FancyZones.cs"),
                Path.Combine("ViewModelTests", "ImageResizer.cs"),
                Path.Combine("ViewModelTests", "KeyboardManager.cs"),
                Path.Combine("ViewModelTests", "PowerLauncherViewModelTest.cs"),
                Path.Combine("ViewModelTests", "PowerPreview.cs"),
                Path.Combine("ViewModelTests", "PowerRename.cs"),
                Path.Combine("ViewModelTests", "ShortcutGuide.cs"),
            };

            Assert.IsFalse(settingsTestsProject.Contains(@"<Compile Remove=""ViewModelTests\", StringComparison.Ordinal), "Inactive module tests should be deleted rather than hidden behind Compile Remove rules.");

            foreach (var inactiveTestFile in inactiveTestFiles)
            {
                Assert.IsFalse(File.Exists(Path.Combine(settingsTestsRoot!, inactiveTestFile)), $"Inactive module test file should be deleted: {inactiveTestFile}");
            }
        }

        [TestMethod]
        public void KitSolutionShouldNotDirectlyBuildInactiveCommonAndDscProjects()
        {
            var solutionPath = FindSourceFile("Kit.slnx");
            var solution = File.ReadAllText(solutionPath);
            var app = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "App.xaml.cs"));
            var repoRoot = Path.GetDirectoryName(solutionPath);

            string[] inactiveProjects =
            {
                "src/common/CalculatorEngineCommon/CalculatorEngineCommon.vcxproj",
                "src/common/FilePreviewCommon/FilePreviewCommon.csproj",
                "src/common/GPOWrapperProjection/GPOWrapperProjection.csproj",
                "src/common/PowerToys.ModuleContracts/PowerToys.ModuleContracts.csproj",
                "src/common/UITestAutomation/UITestAutomation.csproj",
                "src/dsc/",
                "src/modules/awake/Awake.ModuleServices/Awake.ModuleServices.csproj",
            };

            foreach (var project in inactiveProjects)
            {
                Assert.IsFalse(solution.Contains(project, StringComparison.Ordinal), $"Kit.slnx should not directly build inactive project {project}.");
            }

            StringAssert.Contains(solution, "src/common/Common.Search/Common.Search.csproj");
            AssertNoFiles(repoRoot!, Path.Combine("src", "common", "GPOWrapperProjection", "*"));
            Assert.IsFalse(Directory.Exists(Path.Combine(repoRoot!, "src", "dsc")), "Kit should delete the inactive DSC source tree after removing DSC projects from the solution.");
            Assert.IsFalse(File.Exists(Path.Combine(repoRoot!, "tools", "build", "generate-dsc-manifests.ps1")), "Kit should delete the inactive DSC manifest generation script after removing DSC projects from the solution.");
            Assert.IsFalse(File.Exists(Path.Combine(repoRoot!, "src", "settings-ui", "Settings.UI.Library", "Utilities", "SetAdditionalSettingsCommandLineCommand.cs")), "Kit should delete the DSC-only additional settings command once DSC generation is removed.");
            Assert.IsFalse(app.Contains("setAdditional", StringComparison.Ordinal), "Settings should not keep the DSC-only setAdditional command-line entry point.");
            Assert.IsFalse(app.Contains("SetAdditionalSettingsCommandLineCommand", StringComparison.Ordinal), "Settings should not reference the deleted DSC-only additional settings command.");
        }

        [TestMethod]
        public void KitQuickAccessFlyoutShouldOpenSettingsForModulesWithoutDirectActions()
        {
            var launcherViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "ViewModels", "LauncherViewModel.cs"));
            var coordinatorInterface = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "Services", "IQuickAccessCoordinator.cs"));
            var coordinator = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "Services", "QuickAccessCoordinator.cs"));
            var settingsDeepLink = File.ReadAllText(FindSourceFile("src", "common", "Common.UI", "SettingsDeepLink.cs"));

            StringAssert.Contains(launcherViewModel, "fallbackLauncher: OpenModuleSettings");
            StringAssert.Contains(launcherViewModel, "private bool OpenModuleSettings(ModuleType moduleType)");
            StringAssert.Contains(coordinatorInterface, "void OpenModuleSettings(ModuleType moduleType);");
            StringAssert.Contains(coordinator, "ModuleType.Monitor => SettingsDeepLink.SettingsWindow.Monitor");
            StringAssert.Contains(coordinator, "ModuleType.PowerDisplay => SettingsDeepLink.SettingsWindow.PowerDisplay");
            StringAssert.Contains(settingsDeepLink, "Monitor,");
            StringAssert.Contains(settingsDeepLink, "PowerDisplay,");
            StringAssert.Contains(settingsDeepLink, "return \"Monitor\";");
            StringAssert.Contains(settingsDeepLink, "return \"PowerDisplay\";");
        }

        [TestMethod]
        public void KitSettingsDeepLinksShouldOnlyExposeActiveWindows()
        {
            var settingsDeepLink = File.ReadAllText(FindSourceFile("src", "common", "Common.UI", "SettingsDeepLink.cs"));

            string[] activeWindows =
            {
                "Dashboard",
                "Overview",
                "Awake",
                "LightSwitch",
                "Monitor",
                "PowerDisplay",
            };

            foreach (var activeWindow in activeWindows)
            {
                Assert.IsTrue(
                    settingsDeepLink.Contains($"{activeWindow},", StringComparison.Ordinal) || settingsDeepLink.Contains($"{activeWindow} =", StringComparison.Ordinal),
                    $"Settings deep links should expose active {activeWindow} window.");
                StringAssert.Contains(settingsDeepLink, $"return \"{activeWindow}\";");
            }

            string[] inactiveWindows =
            {
                "AdvancedPaste",
                "AlwaysOnTop",
                "ColorPicker",
                "FancyZones",
                "ImageResizer",
                "KBM",
                "MouseUtils",
                "MouseWithoutBorders",
                "PowerLauncher",
                "PowerRename",
                "Workspaces",
                "ZoomIt",
            };

            foreach (var inactiveWindow in inactiveWindows)
            {
                Assert.IsFalse(settingsDeepLink.Contains($"{inactiveWindow},", StringComparison.Ordinal), $"Settings deep links should not expose inactive {inactiveWindow} windows.");
                Assert.IsFalse(settingsDeepLink.Contains($"return \"{inactiveWindow}\";", StringComparison.Ordinal), $"Settings deep links should not route inactive {inactiveWindow} windows.");
            }
        }

        [TestMethod]
        public void KitSettingsShouldRegisterPowerDisplaySerializationAndModels()
        {
            var settingsProject = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "PowerToys.Settings.csproj"));
            var settingsLibraryProject = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "Settings.UI.Library.csproj"));
            var serializationContext = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "SettingsSerializationContext.cs"));

            StringAssert.Contains(settingsProject, @"..\..\modules\powerdisplay\PowerDisplay.Models\PowerDisplay.Models.csproj");
            StringAssert.Contains(settingsLibraryProject, @"..\..\modules\powerdisplay\PowerDisplay.Models\PowerDisplay.Models.csproj");
            Assert.IsFalse(settingsLibraryProject.Contains(@"<Compile Remove=""MonitorInfo.cs""", StringComparison.Ordinal));
            Assert.IsFalse(settingsLibraryProject.Contains(@"<Compile Remove=""PowerDisplayActionMessage.cs""", StringComparison.Ordinal));
            Assert.IsFalse(settingsLibraryProject.Contains(@"<Compile Remove=""PowerDisplayProperties.cs""", StringComparison.Ordinal));
            Assert.IsFalse(settingsLibraryProject.Contains(@"<Compile Remove=""PowerDisplaySettings.cs""", StringComparison.Ordinal));

            StringAssert.Contains(serializationContext, "using PowerDisplay.Models;");
            StringAssert.Contains(serializationContext, "[JsonSerializable(typeof(PowerDisplaySettings))]");
            StringAssert.Contains(serializationContext, "[JsonSerializable(typeof(PowerDisplayProperties))]");
            StringAssert.Contains(serializationContext, "[JsonSerializable(typeof(MonitorInfo))]");
            StringAssert.Contains(serializationContext, "[JsonSerializable(typeof(PowerDisplayActionMessage))]");
            StringAssert.Contains(serializationContext, "[JsonSerializable(typeof(PowerDisplayActionMessage.ActionData))]");
            StringAssert.Contains(serializationContext, "[JsonSerializable(typeof(PowerDisplayActionMessage.PowerDisplayAction))]");
            StringAssert.Contains(serializationContext, "[JsonSerializable(typeof(List<MonitorInfo>))]");
        }

        [TestMethod]
        public void KitSettingsLibraryShouldNotHideInactiveModuleModelsBehindProjectExclusions()
        {
            var settingsLibraryProjectPath = FindSourceFile("src", "settings-ui", "Settings.UI.Library", "Settings.UI.Library.csproj");
            var settingsLibraryProject = File.ReadAllText(settingsLibraryProjectPath);
            var settingsLibraryRoot = Path.GetDirectoryName(settingsLibraryProjectPath);

            Assert.IsFalse(settingsLibraryProject.Contains(@"<Compile Remove=""", StringComparison.Ordinal), "Inactive Settings library sources should be deleted rather than hidden behind Compile Remove rules.");

            string[] inactiveSourceFiles =
            {
                "MouseJumpProperties.cs",
                "MouseJumpSettings.cs",
                "MouseJumpThumbnailSize.cs",
                "SndMouseJumpSettings.cs",
            };

            foreach (var fileName in inactiveSourceFiles)
            {
                Assert.IsFalse(File.Exists(Path.Combine(settingsLibraryRoot!, fileName)), $"Inactive Settings library source file should be deleted: {fileName}");
            }
        }

        [TestMethod]
        public void KitSettingsLibraryShouldNotKeepInactiveIpcWrappers()
        {
            var settingsLibraryProjectPath = FindSourceFile("src", "settings-ui", "Settings.UI.Library", "Settings.UI.Library.csproj");
            var settingsLibraryRoot = Path.GetDirectoryName(settingsLibraryProjectPath);
            var serializationContext = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "SettingsSerializationContext.cs"));

            string[] activeIpcTypes =
            {
                "SndAwakeSettings",
                "SndLightSwitchSettings",
                "SndMonitorSettings",
            };

            foreach (var activeIpcType in activeIpcTypes)
            {
                StringAssert.Contains(serializationContext, $"[JsonSerializable(typeof({activeIpcType}))]");
                StringAssert.Contains(serializationContext, $"[JsonSerializable(typeof(SndModuleSettings<{activeIpcType}>))]");
            }

            string[] inactiveIpcFiles =
            {
                "FindMyMouseSettingsIPCMessage.cs",
                "MouseHighlighterSettingsIPCMessage.cs",
                "MousePointerCrosshairsSettingsIPCMessage.cs",
                "PowerRenameSettingsIPCMessage.cs",
                "ShortcutGuideSettingsIPCMessage.cs",
                "SndCursorWrapSettings.cs",
                "SndFindMyMouseSettings.cs",
                "SndGrabAndMoveSettings.cs",
                "SndImageResizerSettings.cs",
                "SndKeyboardManagerSettings.cs",
                "SndMouseHighlighterSettings.cs",
                "SndMousePointerCrosshairsSettings.cs",
                "SndPowerAccentSettings.cs",
                "SndPowerOcrSettings.cs",
                "SndPowerPreviewSettings.cs",
                "SndPowerRenameSettings.cs",
                "SndRegistryPreviewSettings.cs",
                "SndShortcutGuideSettings.cs",
            };

            foreach (var fileName in inactiveIpcFiles)
            {
                Assert.IsFalse(File.Exists(Path.Combine(settingsLibraryRoot!, fileName)), $"Inactive IPC wrapper source file should be deleted: {fileName}");
                var typeName = Path.GetFileNameWithoutExtension(fileName);
                Assert.IsFalse(serializationContext.Contains(typeName, StringComparison.Ordinal), $"Inactive IPC wrapper should not be registered in SettingsSerializationContext: {typeName}");
            }
        }

        [TestMethod]
        public void KitQuickAccessShouldOnlyReferenceActiveModuleSettings()
        {
            var quickAccessViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Controls", "QuickAccess", "QuickAccessViewModel.cs"));
            var quickAccessLauncher = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Controls", "QuickAccess", "QuickAccessLauncher.cs"));
            var sourceGenerationContext = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SerializationContext", "SourceGenerationContextContext.cs"));

            string[] inactiveTypeNames =
            {
                "AdvancedPasteSettings",
                "AlwaysOnTopSettings",
                "ColorPickerSettings",
                "FancyZonesSettings",
                "KeyboardManagerSettings",
                "MeasureToolSettings",
                "PowerLauncherSettings",
                "PowerOcrSettings",
                "ShortcutGuideSettings",
                "WorkspacesSettings",
            };

            foreach (var inactiveTypeName in inactiveTypeNames)
            {
                Assert.IsFalse(quickAccessViewModel.Contains(inactiveTypeName, StringComparison.Ordinal), $"Quick Access view model should not read inactive settings type {inactiveTypeName}.");
                Assert.IsFalse(sourceGenerationContext.Contains(inactiveTypeName, StringComparison.Ordinal), $"Settings UI source-generation context should not register inactive settings type {inactiveTypeName}.");
            }

            string[] inactiveLauncherCases =
            {
                "ModuleType.ColorPicker",
                "ModuleType.EnvironmentVariables",
                "ModuleType.FancyZones",
                "ModuleType.Hosts",
                "ModuleType.PowerLauncher",
                "ModuleType.PowerOCR",
                "ModuleType.RegistryPreview",
                "ModuleType.MeasureTool",
                "ModuleType.ShortcutGuide",
                "ModuleType.CmdPal",
                "ModuleType.Workspaces",
                "ModuleType.KeyboardManager",
            };

            foreach (var inactiveLauncherCase in inactiveLauncherCases)
            {
                Assert.IsFalse(quickAccessLauncher.Contains(inactiveLauncherCase, StringComparison.Ordinal), $"Quick Access launcher should not retain inactive launch case {inactiveLauncherCase}.");
            }

            StringAssert.Contains(quickAccessViewModel, "ModuleType.Awake");
            StringAssert.Contains(quickAccessViewModel, "ModuleType.LightSwitch");
            StringAssert.Contains(quickAccessViewModel, "ModuleType.Monitor");
            StringAssert.Contains(quickAccessViewModel, "ModuleType.PowerDisplay");
            StringAssert.Contains(quickAccessLauncher, "ModuleType.LightSwitch");
            StringAssert.Contains(quickAccessLauncher, "ModuleType.PowerDisplay");
        }

        [TestMethod]
        public void KitShortcutConflictWindowShouldNotSpecialCaseInactiveModuleSettings()
        {
            var shortcutConflictViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "ShortcutConflictViewModel.cs"));

            string[] inactiveSettingsTypeNames =
            {
                "AdvancedPasteSettings",
                "MouseWithoutBordersSettings",
                "PeekSettings",
                "PowerLauncherSettings",
            };

            foreach (var inactiveSettingsTypeName in inactiveSettingsTypeNames)
            {
                Assert.IsFalse(shortcutConflictViewModel.Contains(inactiveSettingsTypeName, StringComparison.Ordinal), $"Shortcut conflict UI should not special-case inactive settings type {inactiveSettingsTypeName}.");
            }

            Assert.IsFalse(shortcutConflictViewModel.Contains("HotkeyChanged", StringComparison.Ordinal), "Shortcut conflict UI should not keep the inactive PowerToys Run HotkeyChanged workaround.");
            Assert.IsFalse(shortcutConflictViewModel.Contains("AdvancedPaste custom actions", StringComparison.Ordinal), "Shortcut conflict UI should not keep inactive AdvancedPaste custom-action label branches.");
        }

        [TestMethod]
        public void KitSettingsFactoryShouldResolveOnlyActiveHotkeySettings()
        {
            var settingsFactory = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "SettingsFactory.cs"));

            StringAssert.Contains(settingsFactory, "GeneralSettingsModuleKey");
            StringAssert.Contains(settingsFactory, "LightSwitchSettings.ModuleName");
            StringAssert.Contains(settingsFactory, "PowerDisplaySettings.ModuleName");

            string[] inactiveSettingsTypeNames =
            {
                "AdvancedPasteSettings",
                "AlwaysOnTopSettings",
                "ColorPickerSettings",
                "MouseWithoutBordersSettings",
                "PeekSettings",
                "PowerLauncherSettings",
                "ShortcutGuideSettings",
                "WorkspacesSettings",
            };

            foreach (var inactiveSettingsTypeName in inactiveSettingsTypeNames)
            {
                Assert.IsFalse(settingsFactory.Contains(inactiveSettingsTypeName, StringComparison.Ordinal), $"SettingsFactory should not resolve inactive hotkey settings type {inactiveSettingsTypeName}.");
            }

            Assert.IsFalse(settingsFactory.Contains("Assembly.GetAssembly", StringComparison.Ordinal), "SettingsFactory should not scan the Settings.UI.Library assembly for every historical IHotkeyConfig.");
            Assert.IsFalse(settingsFactory.Contains(".GetTypes()", StringComparison.Ordinal), "SettingsFactory should not discover inactive settings types with Assembly.GetTypes().");
            Assert.IsFalse(settingsFactory.Contains("MakeGenericType", StringComparison.Ordinal), "SettingsFactory should not use reflection to instantiate repositories for inactive module settings.");
            Assert.IsFalse(settingsFactory.Contains("GetFreshSettings", StringComparison.Ordinal), "SettingsFactory should not expose unused fresh-settings loading paths.");
            Assert.IsFalse(settingsFactory.Contains("GetAvailableModuleNames", StringComparison.Ordinal), "SettingsFactory should not expose inactive module enumeration APIs.");
            Assert.IsFalse(settingsFactory.Contains("GetAllHotkeySettings", StringComparison.Ordinal), "SettingsFactory should not expose broad hotkey enumeration APIs.");
            Assert.IsFalse(settingsFactory.Contains("GetRepository<", StringComparison.Ordinal), "SettingsFactory should not expose generic repository access outside its shortcut-conflict responsibility.");
        }

        [TestMethod]
        public void KitPageViewModelBaseShouldNotCarryInactiveMouseUtilsConflictBranches()
        {
            var pageViewModelBase = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "PageViewModelBase.cs"));

            Assert.IsFalse(pageViewModelBase.Contains("ProcessMouseUtilsConflictGroup", StringComparison.Ordinal), "Active Settings page base should not carry a deleted MouseUtils page conflict helper.");
            Assert.IsFalse(pageViewModelBase.Contains("\"MouseUtils\"", StringComparison.Ordinal), "Active Settings page base should not branch on the deleted MouseUtils page name.");
            Assert.IsFalse(pageViewModelBase.Contains("FindMyMouseSettings", StringComparison.Ordinal), "Active Settings page base should not reference inactive MouseUtils settings models.");
            Assert.IsFalse(pageViewModelBase.Contains("MouseHighlighterSettings", StringComparison.Ordinal), "Active Settings page base should not reference inactive MouseUtils settings models.");
            Assert.IsFalse(pageViewModelBase.Contains("MousePointerCrosshairsSettings", StringComparison.Ordinal), "Active Settings page base should not reference inactive MouseUtils settings models.");
        }

        [TestMethod]
        public void KitQuickAccessAllAppsShouldOnlyListActiveModules()
        {
            var allAppsViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "ViewModels", "AllAppsViewModel.cs"));

            StringAssert.Contains(allAppsViewModel, "KitModuleCatalog.ActiveModules");
            Assert.IsFalse(allAppsViewModel.Contains("Enum.GetValues<ModuleType>()", StringComparison.Ordinal), "Quick Access All apps should not expose every upstream PowerToys module in Kit.");
            Assert.IsFalse(allAppsViewModel.Contains("moduleType == ModuleType.GeneralSettings", StringComparison.Ordinal), "Filtering only GeneralSettings is too broad for Kit's trimmed module surface.");
        }

        [TestMethod]
        public void KitQuickAccessFlyoutDefaultsAndGpoShouldStayInActiveModuleSurface()
        {
            var flyoutMenuItem = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "ViewModels", "FlyoutMenuItem.cs"));
            var quickAccessGpoHelper = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "Helpers", "ModuleGpoHelper.cs"));

            StringAssert.Contains(flyoutMenuItem, "ModuleType.Awake");
            Assert.IsFalse(flyoutMenuItem.Contains("ModuleType.PowerLauncher", StringComparison.Ordinal), "Quick Access flyout item fallback should not use inactive Power Launcher.");
            StringAssert.Contains(quickAccessGpoHelper, "ModuleType.Awake => GPOWrapper.GetConfiguredAwakeEnabledValue()");
            StringAssert.Contains(quickAccessGpoHelper, "ModuleType.LightSwitch => GPOWrapper.GetConfiguredLightSwitchEnabledValue()");
            StringAssert.Contains(quickAccessGpoHelper, "ModuleType.PowerDisplay => GPOWrapper.GetConfiguredPowerDisplayEnabledValue()");

            string[] inactiveModules =
            {
                "AdvancedPaste",
                "AlwaysOnTop",
                "CmdPal",
                "ColorPicker",
                "CropAndLock",
                "EnvironmentVariables",
                "FancyZones",
                "FileLocksmith",
                "ImageResizer",
                "KeyboardManager",
                "PowerLauncher",
                "PowerRename",
                "Workspaces",
                "ZoomIt",
            };

            foreach (var inactiveModule in inactiveModules)
            {
                Assert.IsFalse(quickAccessGpoHelper.Contains($"ModuleType.{inactiveModule} =>", StringComparison.Ordinal), $"Quick Access GPO helper should not keep inactive {inactiveModule} branches.");
            }
        }

        [TestMethod]
        public void KitSettingsGpoHelperShouldStayInActiveModuleSurface()
        {
            var settingsGpoHelper = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Helpers", "ModuleGpoHelper.cs"));
            var gpoConfigurationStart = settingsGpoHelper.IndexOf("public static GpoRuleConfigured GetModuleGpoConfiguration", StringComparison.Ordinal);
            var pageTypeStart = settingsGpoHelper.IndexOf("public static System.Type GetModulePageType", StringComparison.Ordinal);

            Assert.AreNotEqual(-1, gpoConfigurationStart, "Settings GPO helper should expose GetModuleGpoConfiguration.");
            Assert.AreNotEqual(-1, pageTypeStart, "Settings GPO helper should expose GetModulePageType.");

            var gpoConfiguration = settingsGpoHelper.Substring(gpoConfigurationStart, pageTypeStart - gpoConfigurationStart);

            AssertHasGpoBranch(gpoConfiguration, "Awake");
            AssertHasGpoBranch(gpoConfiguration, "LightSwitch");
            AssertHasGpoBranch(gpoConfiguration, "PowerDisplay");
            Assert.IsFalse(HasGpoBranch(gpoConfiguration, "Monitor"), "Settings GPO helper should not expose a Monitor GPO branch until a Monitor GPO rule exists.");

            string[] inactiveModules =
            {
                "AdvancedPaste",
                "AlwaysOnTop",
                "CmdPal",
                "ColorPicker",
                "CropAndLock",
                "CursorWrap",
                "EnvironmentVariables",
                "FancyZones",
                "FileLocksmith",
                "FindMyMouse",
                "Hosts",
                "ImageResizer",
                "KeyboardManager",
                "MouseHighlighter",
                "MouseJump",
                "MousePointerCrosshairs",
                "MouseWithoutBorders",
                "NewPlus",
                "Peek",
                "PowerAccent",
                "PowerLauncher",
                "PowerOCR",
                "PowerRename",
                "RegistryPreview",
                "MeasureTool",
                "ShortcutGuide",
                "Workspaces",
                "ZoomIt",
                "GrabAndMove",
            };

            foreach (var inactiveModule in inactiveModules)
            {
                Assert.IsFalse(HasGpoBranch(gpoConfiguration, inactiveModule), $"Settings GPO helper should not keep inactive {inactiveModule} branches.");
            }
        }

        [TestMethod]
        public void KitGpoPolicySurfaceShouldStayInActiveModuleSurface()
        {
            var gpoIdl = File.ReadAllText(FindSourceFile("src", "common", "GPOWrapper", "GPOWrapper.idl"));
            var gpoHeader = File.ReadAllText(FindSourceFile("src", "common", "GPOWrapper", "GPOWrapper.h"));
            var gpoImplementation = File.ReadAllText(FindSourceFile("src", "common", "GPOWrapper", "GPOWrapper.cpp"));
            var gpoUtilities = File.ReadAllText(FindSourceFile("src", "common", "utils", "gpo.h"));
            var gpoTests = File.ReadAllText(FindSourceFile("src", "common", "UnitTests-CommonUtils", "Gpo.Tests.cpp"));

            string[] retainedPolicies =
            {
                "GetConfiguredAwakeEnabledValue",
                "GetConfiguredLightSwitchEnabledValue",
                "GetConfiguredPowerDisplayEnabledValue",
                "GetDisableAutomaticUpdateDownloadValue",
                "GetDisableNewUpdateToastValue",
                "GetDisableShowWhatsNewAfterUpdatesValue",
                "GetAllowExperimentationValue",
                "GetAllowDataDiagnosticsValue",
                "GetConfiguredRunAtStartupValue",
            };

            foreach (var retainedPolicy in retainedPolicies)
            {
                StringAssert.Contains(gpoIdl, retainedPolicy);
                StringAssert.Contains(gpoHeader, retainedPolicy);
                StringAssert.Contains(gpoImplementation, retainedPolicy);
            }

            string[] inactivePolicyTokens =
            {
                "AlwaysOnTop",
                "AdvancedPaste",
                "Bgcode",
                "CmdNotFound",
                "CmdPal",
                "ColorPicker",
                "CropAndLock",
                "CursorWrap",
                "EnvironmentVariables",
                "FancyZones",
                "FileLocksmith",
                "FindMyMouse",
                "Gcode",
                "GrabAndMove",
                "HostsFileEditor",
                "ImageResizer",
                "KeyboardManager",
                "MarkdownPreview",
                "MonacoPreview",
                "MouseHighlighter",
                "MouseJump",
                "MousePointerCrosshairs",
                "MouseWithoutBorders",
                "Mwb",
                "NewPlus",
                "Pdf",
                "Peek",
                "PowerLauncher",
                "PowerRename",
                "Qoi",
                "QuickAccent",
                "RegistryPreview",
                "RunPlugin",
                "ScreenRuler",
                "ShortcutGuide",
                "Stl",
                "Svg",
                "TextExtractor",
                "Workspaces",
                "ZoomIt",
            };

            foreach (var inactivePolicyToken in inactivePolicyTokens)
            {
                Assert.IsFalse(gpoIdl.Contains(inactivePolicyToken, StringComparison.Ordinal), $"GPO IDL should not expose inactive policy token {inactivePolicyToken}.");
                Assert.IsFalse(gpoHeader.Contains(inactivePolicyToken, StringComparison.Ordinal), $"GPO header should not expose inactive policy token {inactivePolicyToken}.");
                Assert.IsFalse(gpoImplementation.Contains(inactivePolicyToken, StringComparison.Ordinal), $"GPO implementation should not expose inactive policy token {inactivePolicyToken}.");
                Assert.IsFalse(gpoUtilities.Contains(inactivePolicyToken, StringComparison.Ordinal), $"gpo.h should not expose inactive policy token {inactivePolicyToken}.");
                Assert.IsFalse(gpoTests.Contains(inactivePolicyToken, StringComparison.Ordinal), $"Common GPO tests should not keep inactive policy token {inactivePolicyToken}.");
            }

            Assert.IsFalse(gpoUtilities.Contains("PerUserInstallationDisabled", StringComparison.Ordinal), "Kit runtime GPO utilities should not keep installer-only policy readers.");
            Assert.IsFalse(gpoUtilities.Contains("SuspendNewUpdateAvailableToast", StringComparison.Ordinal), "Kit runtime GPO utilities should not keep inactive update-toast policy readers.");
            Assert.IsFalse(gpoUtilities.Contains("ConfigureGlobalUtilityEnabledState", StringComparison.Ordinal), "Kit runtime GPO utilities should not keep the upstream all-utilities policy reader.");
        }

        [TestMethod]
        public void KitShouldNotExposeUpstreamBugReportTool()
        {
            var repoRoot = Path.GetDirectoryName(FindSourceFile(".gitignore"));
            var runnerProject = File.ReadAllText(FindSourceFile("src", "runner", "Kit.vcxproj"));
            var runnerFilters = File.ReadAllText(FindSourceFile("src", "runner", "Kit.vcxproj.filters"));
            var runnerResource = File.ReadAllText(FindSourceFile("src", "runner", "resource.base.h"));
            var runnerMenu = File.ReadAllText(FindSourceFile("src", "runner", "runner.base.rc"));
            var trayIcon = File.ReadAllText(FindSourceFile("src", "runner", "tray_icon.cpp"));
            var settingsWindow = File.ReadAllText(FindSourceFile("src", "runner", "settings_window.cpp"));
            var launchPage = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "QuickAccessXAML", "Flyout", "LaunchPage.xaml"));
            var launchPageCodeBehind = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "QuickAccessXAML", "Flyout", "LaunchPage.xaml.cs"));
            var coordinatorInterface = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "Services", "IQuickAccessCoordinator.cs"));
            var coordinator = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "Services", "QuickAccessCoordinator.cs"));

            Assert.IsFalse(Directory.Exists(Path.Combine(repoRoot!, "tools", "BugReportTool")), "Kit should delete the upstream bug report tool source because it collects inactive PowerToys module state.");
            Assert.IsFalse(File.Exists(Path.Combine(repoRoot!, "src", "runner", "bug_report.cpp")), "Kit runner should delete the upstream bug report launcher implementation.");
            Assert.IsFalse(File.Exists(Path.Combine(repoRoot!, "src", "runner", "bug_report.h")), "Kit runner should delete the upstream bug report launcher header.");

            foreach (var source in new[]
            {
                runnerProject,
                runnerFilters,
                runnerResource,
                runnerMenu,
                trayIcon,
                settingsWindow,
                launchPage,
                launchPageCodeBehind,
                coordinatorInterface,
                coordinator,
            })
            {
                Assert.IsFalse(source.Contains("BugReport", StringComparison.Ordinal), "Kit active runtime UI should not expose the upstream bug report tool.");
                Assert.IsFalse(source.Contains("bug_report", StringComparison.Ordinal), "Kit active runtime IPC should not keep bug report status plumbing.");
                Assert.IsFalse(source.Contains("bugreport", StringComparison.Ordinal), "Kit active runtime IPC should not keep bug report launch messages.");
                Assert.IsFalse(source.Contains("REPORT_BUG", StringComparison.Ordinal), "Kit tray resources should not keep bug report commands.");
                Assert.IsFalse(source.Contains("PowerToys.BugReportTool", StringComparison.Ordinal), "Kit should not launch the upstream PowerToys bug report executable.");
            }
        }

        [TestMethod]
        public void KitShouldDeleteInactiveCmdPalDevelopmentSurfaces()
        {
            var repoRoot = Path.GetDirectoryName(FindSourceFile(".gitignore"));
            var solution = File.ReadAllText(FindSourceFile("Kit.slnx"));

            Assert.IsFalse(File.Exists(Path.Combine(repoRoot!, "src", "CmdPalVersion.props")), "Kit should delete the orphaned CmdPal version props file until Command Palette is an active module.");
            Assert.IsFalse(Directory.Exists(Path.Combine(repoRoot!, "tools", "module_loader")), "Kit should delete the inactive upstream standalone module loader tool instead of carrying non-shipping PowerToys module development surfaces.");
            Assert.IsFalse(solution.Contains("ModuleLoader", StringComparison.Ordinal), "Kit solution should not build the inactive standalone module loader.");
        }

        [TestMethod]
        public void KitGpoPolicyAssetsShouldStayInActiveModuleSurface()
        {
            var admx = File.ReadAllText(FindSourceFile("src", "gpo", "assets", "PowerToys.admx"));
            var adml = File.ReadAllText(FindSourceFile("src", "gpo", "assets", "en-US", "PowerToys.adml"));

            foreach (var activePolicy in new[]
            {
                "ConfigureEnabledUtilityAwake",
                "ConfigureEnabledUtilityLightSwitch",
                "ConfigureEnabledUtilityPowerDisplay",
                "DisableAutomaticUpdateDownload",
                "DisableNewUpdateToast",
                "DoNotShowWhatsNewAfterUpdates",
                "AllowExperimentation",
                "AllowDiagnosticData",
                "ConfigureRunAtStartup",
            })
            {
                StringAssert.Contains(admx, activePolicy);
                StringAssert.Contains(adml, activePolicy);
            }

            StringAssert.Contains(admx, "SUPPORTED_KIT_1_2_2");
            StringAssert.Contains(adml, "SUPPORTED_KIT_1_2_2");

            foreach (var inactivePolicyToken in new[]
            {
                "ConfigureAllUtilityGlobalEnabledState",
                "ConfigureEnabledUtilityAdvancedPaste",
                "ConfigureEnabledUtilityAlwaysOnTop",
                "ConfigureEnabledUtilityCmdNotFound",
                "ConfigureEnabledUtilityCmdPal",
                "ConfigureEnabledUtilityColorPicker",
                "ConfigureEnabledUtilityCropAndLock",
                "ConfigureEnabledUtilityEnvironmentVariables",
                "ConfigureEnabledUtilityFancyZones",
                "ConfigureEnabledUtilityFile",
                "ConfigureEnabledUtilityFindMyMouse",
                "ConfigureEnabledUtilityHostsFileEditor",
                "ConfigureEnabledUtilityImageResizer",
                "ConfigureEnabledUtilityKeyboardManager",
                "ConfigureEnabledUtilityMouse",
                "ConfigureEnabledUtilityNewPlus",
                "ConfigureEnabledUtilityPeek",
                "ConfigureEnabledUtilityPowerLauncher",
                "ConfigureEnabledUtilityPowerRename",
                "ConfigureEnabledUtilityQuickAccent",
                "ConfigureEnabledUtilityRegistryPreview",
                "ConfigureEnabledUtilityScreenRuler",
                "ConfigureEnabledUtilityShortcutGuide",
                "ConfigureEnabledUtilityTextExtractor",
                "ConfigureEnabledUtilityVideoConferenceMute",
                "ConfigureEnabledUtilityWorkspaces",
                "ConfigureEnabledUtilityZoomIt",
                "DisablePerUserInstallation",
                "SuspendNewUpdateToast",
                "PowerToysRun",
                "AdvancedPaste",
                "MouseWithoutBorders",
            })
            {
                Assert.IsFalse(admx.Contains(inactivePolicyToken, StringComparison.Ordinal), $"Kit ADMX should not expose inactive policy token {inactivePolicyToken}.");
                Assert.IsFalse(adml.Contains(inactivePolicyToken, StringComparison.Ordinal), $"Kit ADML should not localize inactive policy token {inactivePolicyToken}.");
            }
        }

        [TestMethod]
        public void KitGlobalSuppressionsShouldNotCarryInactiveModuleTargets()
        {
            var globalSuppressions = File.ReadAllText(FindSourceFile("src", "codeAnalysis", "GlobalSuppressions.cs"));

            StringAssert.Contains(globalSuppressions, "SuppressMessage");

            foreach (var inactiveTarget in new[]
            {
                "MouseWithoutBorders",
                "AdvancedPaste",
                "EnvironmentVariablesUILib",
                "HostsUILib",
                "Peek.",
                "Microsoft.PowerToys.Run.Plugin",
                "RegistryPreviewUILib",
            })
            {
                Assert.IsFalse(globalSuppressions.Contains(inactiveTarget, StringComparison.Ordinal), $"Global code-analysis suppressions should not carry inactive module target {inactiveTarget}.");
            }
        }

        [TestMethod]
        public void KitSettingsShouldDeleteInactiveModuleAssetsInsteadOfProjectExcludingThem()
        {
            var settingsProjectPath = FindSourceFile("src", "settings-ui", "Settings.UI", "PowerToys.Settings.csproj");
            var settingsProject = File.ReadAllText(settingsProjectPath);
            var settingsRoot = Path.GetDirectoryName(settingsProjectPath);
            var moduleAssetsRoot = Path.Combine(settingsRoot!, "Assets", "Settings", "Modules");
            var modelIconsRoot = Path.Combine(settingsRoot!, "Assets", "Settings", "Icons", "Models");
            var imagesRoot = Path.Combine(settingsRoot!, "Images");

            string[] activeModuleAssets =
            {
                "Awake.png",
                "LightSwitch.png",
                "PowerDisplay.png",
                "PT.png",
            };

            foreach (var activeAsset in activeModuleAssets)
            {
                Assert.IsTrue(File.Exists(Path.Combine(moduleAssetsRoot, activeAsset)), $"Active Settings module asset should remain: {activeAsset}");
            }

            string[] inactiveModuleAssets =
            {
                "AdvancedPaste.png",
                "AlwaysOnTop.png",
                "CmdNotFound.png",
                "CmdPal.png",
                "ColorPicker.png",
                "CropAndLock.png",
                "EnvironmentVariables.png",
                "FancyZones.png",
                "FileExplorerPreview.png",
                "FileLocksmith.png",
                "GrabAndMove.png",
                "HostsFileEditor.png",
                "ImageResizer.png",
                "KBM.png",
                "MouseUtils.png",
                "MouseWithoutBorders.png",
                "NewPlus.png",
                "Peek.png",
                "PowerLauncher.png",
                "PowerRename.png",
                "QuickAccent.png",
                "RegistryPreview.png",
                "Run.png",
                "ScreenRuler.png",
                "ShortcutGuide.png",
                "TextExtractor.png",
                "Wallpaper.png",
                "Workspaces.png",
                "ZoomIt.png",
            };

            foreach (var inactiveAsset in inactiveModuleAssets)
            {
                Assert.IsFalse(File.Exists(Path.Combine(moduleAssetsRoot, inactiveAsset)), $"Inactive Settings module asset should be deleted: {inactiveAsset}");
            }

            string[] inactiveModelIcons =
            {
                "Azure.svg",
                "AzureAI.svg",
                "FoundryLocal.svg",
                "Gemini.svg",
                "Mistral.svg",
                "Ollama.svg",
                "Onnx.svg",
                "OpenAI.dark.svg",
                "OpenAI.light.svg",
                "WindowsML.svg",
            };

            foreach (var inactiveModelIcon in inactiveModelIcons)
            {
                Assert.IsFalse(File.Exists(Path.Combine(modelIconsRoot, inactiveModelIcon)), $"Inactive AI model icon should be deleted: {inactiveModelIcon}");
            }

            AssertNoFiles(moduleAssetsRoot, Path.Combine("OOBE", "*"));
            Assert.IsFalse(File.Exists(Path.Combine(imagesRoot, "MouseJump-Desktop.png")), "Inactive Mouse Jump preview image should be deleted.");
            StringAssert.Contains(settingsProject, "KitRemoveInactiveSettingsAssetsFromOutput");
            StringAssert.Contains(settingsProject, "KitInactiveSettingsModuleAssets");
            StringAssert.Contains(settingsProject, @"$(OutDir)Assets\Settings\Modules\*.png");
            StringAssert.Contains(settingsProject, @"$(OutDir)Assets\Settings\Modules\OOBE\**\*");
            StringAssert.Contains(settingsProject, @"$(OutDir)Assets\Settings\Icons\Models\*.svg");
            Assert.IsFalse(settingsProject.Contains(@"<Content Remove=""Assets\Settings\Modules\OOBE", StringComparison.Ordinal), "Deleted OOBE assets should not stay hidden behind project exclusions.");
            Assert.IsFalse(settingsProject.Contains(@"<None Remove=""Assets\Settings\Modules\OOBE", StringComparison.Ordinal), "Deleted OOBE assets should not stay hidden behind project exclusions.");
            Assert.IsFalse(settingsProject.Contains(@"<None Remove=""Assets\Settings\Icons\Models\", StringComparison.Ordinal), "Deleted AI model icons should not stay hidden behind project exclusions.");
            Assert.IsFalse(settingsProject.Contains(@"<Content Include=""Assets\Settings\Icons\Models\", StringComparison.Ordinal), "Deleted AI model icons should not be re-added as content.");
            Assert.IsFalse(settingsProject.Contains(@"Assets\Settings\Modules\APDialog", StringComparison.Ordinal), "Deleted Advanced Paste dialog assets should not stay hidden behind project exclusions.");
            Assert.IsFalse(settingsProject.Contains(@"<None Remove=""Assets\Settings\Modules\LightSwitch.png", StringComparison.Ordinal), "Active LightSwitch asset should not be removed from the project by exclusion.");
            Assert.IsFalse(settingsProject.Contains(@"<EmbeddedResource Include=""Images\MouseJump-Desktop.png", StringComparison.Ordinal), "Deleted Mouse Jump preview should not stay embedded.");
            Assert.IsFalse(settingsProject.Contains(@"SettingsXAML\OOBE\Views\OobeWorkspaces.xaml", StringComparison.Ordinal), "Deleted OOBE pages should not keep XAML metadata.");
            Assert.IsFalse(settingsProject.Contains(@"SettingsXAML\Panels\MouseJumpPanel.xaml", StringComparison.Ordinal), "Deleted Mouse Jump panel should not keep XAML metadata.");
            Assert.IsFalse(settingsProject.Contains(@"SettingsXAML\Views\WorkspacesPage.xaml", StringComparison.Ordinal), "Deleted Workspaces page should not keep XAML metadata.");
        }

        [TestMethod]
        public void KitSettingsShouldDeleteLegacySiblingAssetTree()
        {
            var settingsUiRoot = Path.GetDirectoryName(FindSourceFile("src", "settings-ui", "PowerToys.Settings.slnf"));
            var settingsProject = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "PowerToys.Settings.csproj"));

            Assert.IsFalse(Directory.Exists(Path.Combine(settingsUiRoot!, "Assets")), "Kit should delete the legacy sibling Settings asset tree instead of keeping an unused full upstream copy beside Settings.UI.");
            Assert.IsFalse(settingsProject.Contains(@"..\Assets\", StringComparison.Ordinal), "Settings.UI should not depend on the deleted sibling asset tree.");
        }

        [TestMethod]
        public void KitSettingsShouldDeleteInactiveAuxiliaryPayloadsInsteadOfShippingThem()
        {
            var settingsProjectPath = FindSourceFile("src", "settings-ui", "Settings.UI", "PowerToys.Settings.csproj");
            var settingsProject = File.ReadAllText(settingsProjectPath);
            var settingsRoot = Path.GetDirectoryName(settingsProjectPath);

            AssertNoFiles(settingsRoot!, Path.Combine("Assets", "Settings", "CmdPal", "*"));

            string[] inactiveScriptPayloads =
            {
                Path.Combine("Assets", "Settings", "Scripts", "CheckCmdNotFoundRequirements.ps1"),
                Path.Combine("Assets", "Settings", "Scripts", "DisableModule.ps1"),
                Path.Combine("Assets", "Settings", "Scripts", "EnableModule.ps1"),
                Path.Combine("Assets", "Settings", "Scripts", "InstallPowerShell7.ps1"),
                Path.Combine("Assets", "Settings", "Scripts", "InstallWinGetClientModule.ps1"),
                Path.Combine("Assets", "Settings", "Scripts", "UpgradeModule.ps1"),
            };

            foreach (var inactiveScriptPayload in inactiveScriptPayloads)
            {
                Assert.IsFalse(File.Exists(Path.Combine(settingsRoot!, inactiveScriptPayload)), $"Inactive script payload should be deleted: {inactiveScriptPayload}");
            }

            Assert.IsFalse(settingsProject.Contains(@"<None Update=""Assets\Settings\Scripts\", StringComparison.Ordinal), "Inactive Settings scripts should not be copied to output.");
            Assert.IsFalse(settingsProject.Contains(@"<Content Include=""Assets\Settings\CmdPal", StringComparison.Ordinal), "Inactive CmdPal assets should not be copied to output.");
            StringAssert.Contains(settingsProject, @"$(OutDir)Assets\Settings\CmdPal\**\*");
            StringAssert.Contains(settingsProject, @"$(OutDir)Assets\Settings\Scripts\*.ps1");
            StringAssert.Contains(settingsProject, @"$(OutDir)Assets\Settings\CmdPal;$(OutDir)Assets\Settings\Scripts");
        }

        [TestMethod]
        public void KitSettingsShouldDeleteInactiveIconAssetsInsteadOfShippingThem()
        {
            var settingsProjectPath = FindSourceFile("src", "settings-ui", "Settings.UI", "PowerToys.Settings.csproj");
            var settingsProject = File.ReadAllText(settingsProjectPath);
            var quickAccessProject = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "PowerToys.QuickAccess.csproj"));
            var settingsRoot = Path.GetDirectoryName(settingsProjectPath);
            var iconAssetsRoot = Path.Combine(settingsRoot!, "Assets", "Settings", "Icons");

            string[] activeIconAssets =
            {
                "Awake.png",
                "LightSwitch.png",
                "PowerDisplay.png",
                "PowerToys.png",
            };

            foreach (var activeIcon in activeIconAssets)
            {
                Assert.IsTrue(File.Exists(Path.Combine(iconAssetsRoot, activeIcon)), $"Active Settings icon asset should remain: {activeIcon}");
                StringAssert.Contains(quickAccessProject, $@"..\Settings.UI\Assets\Settings\Icons\{activeIcon}");
            }

            string[] inactiveIconAssets =
            {
                "Advanced.png",
                "AdvancedPaste.png",
                "AlwaysOnTop.png",
                "CmdPal.png",
                "ColorPicker.png",
                "CommandNotFound.png",
                "CropAndLock.png",
                "CursorWrap.png",
                "EnvironmentVariables.png",
                "FancyZones.png",
                "FileExplorerPreview.png",
                "FileLocksmith.png",
                "FileManagement.png",
                "FindMyMouse.png",
                "GrabAndMove.png",
                "Hosts.png",
                "ImageResizer.png",
                "InputOutput.png",
                "KeyboardManager.png",
                "MouseCrosshairs.png",
                "MouseHighlighter.png",
                "MouseJump.png",
                "MouseUtils.png",
                "MouseWithoutBorders.png",
                "NewPlus.png",
                "Peek.png",
                "PowerRename.png",
                "PowerToysRun.png",
                "QuickAccent.png",
                "RegistryPreview.png",
                "ScreenRuler.png",
                "SemanticKernel.png",
                "ShortcutGuide.png",
                "SystemTools.png",
                "TextExtractor.png",
                "WindowingAndLayouts.png",
                "Workspaces.png",
                "ZoomIt.png",
            };

            foreach (var inactiveIcon in inactiveIconAssets)
            {
                Assert.IsFalse(File.Exists(Path.Combine(iconAssetsRoot, inactiveIcon)), $"Inactive Settings icon asset should be deleted: {inactiveIcon}");
            }

            StringAssert.Contains(settingsProject, "KitInactiveSettingsIconAssets");
            StringAssert.Contains(settingsProject, @"$(OutDir)Assets\Settings\Icons\*.png");
            Assert.IsFalse(quickAccessProject.Contains(@"..\Settings.UI\Assets\Settings\Icons\**\*", StringComparison.Ordinal), "Quick Access should not copy the full inactive Settings icon tree.");
        }

        [TestMethod]
        public void KitSettingsShouldDeleteAdvancedPasteLanguageModelProvider()
        {
            var solutionPath = FindSourceFile("Kit.slnx");
            var repoRoot = Path.GetDirectoryName(solutionPath)!;
            var solution = File.ReadAllText(solutionPath);
            var settingsFilter = File.ReadAllText(FindSourceFile("src", "settings-ui", "PowerToys.Settings.slnf"));
            var settingsProjectPath = FindSourceFile("src", "settings-ui", "Settings.UI", "PowerToys.Settings.csproj");
            var settingsProject = File.ReadAllText(settingsProjectPath);
            var settingsUiRoot = Path.GetDirectoryName(settingsProjectPath);
            var settingsLibraryProjectPath = FindSourceFile("src", "settings-ui", "Settings.UI.Library", "Settings.UI.Library.csproj");
            var settingsLibraryRoot = Path.GetDirectoryName(settingsLibraryProjectPath);
            var pasteAiProviderDefinition = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "PasteAIProviderDefinition.cs"));
            var pasteAiConfiguration = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "PasteAIConfiguration.cs"));
            var resources = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Strings", "en-us", "Resources.resw"));
            var centralPackages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));
            var notice = File.ReadAllText(FindSourceFile("NOTICE.md"));

            Assert.IsFalse(solution.Contains("LanguageModelProvider", StringComparison.Ordinal), "Kit.slnx should not build the AdvancedPaste-only LanguageModelProvider project.");
            Assert.IsFalse(settingsFilter.Contains("LanguageModelProvider", StringComparison.Ordinal), "Settings solution filter should not build LanguageModelProvider.");
            StringAssert.Contains(settingsFilter, @"""path"": ""..\\..\\Kit.slnx""");
            StringAssert.Contains(settingsFilter, @"src\\modules\\powerdisplay\\PowerDisplay.Models\\PowerDisplay.Models.csproj");
            Assert.IsFalse(settingsFilter.Contains("MouseJump.Common", StringComparison.Ordinal), "Settings solution filter should not reference inactive MouseJump projects.");
            Assert.IsFalse(settingsFilter.Contains("ZoomItSettingsInterop", StringComparison.Ordinal), "Settings solution filter should not reference inactive ZoomIt projects.");
            Assert.IsFalse(settingsProject.Contains(@"common\LanguageModelProvider\LanguageModelProvider.csproj", StringComparison.Ordinal), "Settings should not reference LanguageModelProvider.");
            Assert.IsFalse(settingsProject.Contains("FoundryLocalModelPicker", StringComparison.Ordinal), "Removed model picker should not stay hidden behind project exclusions.");
            Assert.IsFalse(settingsProject.Contains(@"Content Include=""Assets\Settings\Icons\Models\*.svg""", StringComparison.Ordinal), "Removed AI model provider icons should not be re-added as Content.");
            Assert.IsFalse(File.Exists(Path.Combine(settingsUiRoot!, "SettingsXAML", "Controls", "ModelPicker", "FoundryLocalModelPicker.xaml")));
            Assert.IsFalse(File.Exists(Path.Combine(settingsUiRoot!, "SettingsXAML", "Controls", "ModelPicker", "FoundryLocalModelPicker.xaml.cs")));
            Assert.IsFalse(Directory.Exists(Path.Combine(repoRoot, "src", "common", "LanguageModelProvider")), "Kit should delete the inactive AdvancedPaste-only LanguageModelProvider source tree instead of only removing it from build graphs.");
            Assert.IsFalse(File.Exists(Path.Combine(settingsUiRoot!, "Converters", "ServiceTypeToIconConverter.cs")), "Settings UI should delete the AdvancedPaste AI provider icon converter after deleting the AdvancedPaste page and model icons.");
            Assert.IsFalse(File.Exists(Path.Combine(settingsLibraryRoot!, "AdvancedPasteMigrationHelper.cs")), "Settings library should delete AdvancedPaste AI migration helpers that only served the removed UI/module.");
            Assert.IsFalse(File.Exists(Path.Combine(settingsLibraryRoot!, "AIServiceTypeMetadata.cs")), "Settings library should delete UI-only AdvancedPaste AI provider metadata.");
            Assert.IsFalse(File.Exists(Path.Combine(settingsLibraryRoot!, "AIServiceTypeRegistry.cs")), "Settings library should delete UI-only AdvancedPaste AI provider registry metadata.");
            Assert.IsFalse(File.Exists(Path.Combine(settingsLibraryRoot!, "PasteAIProviderDefaults.cs")), "Settings library should delete AdvancedPaste AI provider default model helpers after removing the provider UI.");
            Assert.IsFalse(File.Exists(Path.Combine(settingsLibraryRoot!, "AIServiceType.cs")), "Settings library should not keep an AdvancedPaste AI enum after the provider UI helpers are deleted.");
            Assert.IsFalse(File.Exists(Path.Combine(settingsLibraryRoot!, "AIServiceTypeExtensions.cs")), "Settings library should not keep AdvancedPaste AI enum normalization helpers after the provider UI helpers are deleted.");
            StringAssert.Contains(pasteAiProviderDefinition, @"JsonPropertyName(""service-type"")");
            Assert.IsFalse(pasteAiProviderDefinition.Contains("ServiceTypeKind", StringComparison.Ordinal), "Paste AI compatibility DTOs should keep persisted strings without carrying non-serialized AI enum helper properties.");
            StringAssert.Contains(pasteAiConfiguration, @"JsonPropertyName(""active-provider-id"")");
            Assert.IsFalse(pasteAiConfiguration.Contains("ActiveServiceTypeKind", StringComparison.Ordinal), "Paste AI compatibility DTOs should not carry non-serialized AI enum helper properties.");
            Assert.IsFalse(resources.Contains("FoundryLocal_RestartRequiredNote", StringComparison.Ordinal), "Settings resources should not keep Foundry Local UI strings after deleting the model picker.");

            string[] inactiveAiPackagePins =
            {
                @"PackageVersion Include=""Microsoft.Extensions.AI""",
                @"PackageVersion Include=""Microsoft.Extensions.AI.OpenAI""",
                @"PackageVersion Include=""Microsoft.AI.Foundry.Local""",
                @"PackageVersion Include=""Microsoft.SemanticKernel""",
                @"PackageVersion Include=""Microsoft.SemanticKernel.Connectors.OpenAI""",
                @"PackageVersion Include=""Microsoft.SemanticKernel.Connectors.AzureAIInference""",
                @"PackageVersion Include=""Microsoft.SemanticKernel.Connectors.Google""",
                @"PackageVersion Include=""Microsoft.SemanticKernel.Connectors.MistralAI""",
                @"PackageVersion Include=""Microsoft.SemanticKernel.Connectors.Ollama""",
                @"PackageVersion Include=""OpenAI""",
            };

            foreach (var packagePin in inactiveAiPackagePins)
            {
                Assert.IsFalse(centralPackages.Contains(packagePin, StringComparison.Ordinal), $"Central packages should not keep inactive AdvancedPaste AI package pin: {packagePin}");
            }

            Assert.IsFalse(notice.Contains("- OpenAI", StringComparison.Ordinal), "Third-party notices should not list the removed OpenAI package dependency.");
        }

        [TestMethod]
        public void KitLocalPackageCachesShouldStayIgnored()
        {
            var gitIgnore = File.ReadAllText(FindSourceFile(".gitignore"));

            StringAssert.Contains(gitIgnore, ".nuget-cache/");
            StringAssert.Contains(gitIgnore, ".nuget-packages/");
            StringAssert.Contains(gitIgnore, ".nuget-appdata/");
        }

        [TestMethod]
        public void KitSettingsPackageReferencesShouldNotDocumentInactiveModuleHacks()
        {
            var settingsProject = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "PowerToys.Settings.csproj"));

            var packageReferenceBlockStart = settingsProject.IndexOf("<ItemGroup>", StringComparison.Ordinal);
            var packageReferenceBlockEnd = settingsProject.IndexOf("<Manifest Include=\"$(ApplicationManifest)\" />", StringComparison.Ordinal);
            Assert.IsTrue(packageReferenceBlockStart >= 0, "Settings project should keep package references in the first item group.");
            Assert.IsTrue(packageReferenceBlockEnd > packageReferenceBlockStart, "Settings project package references should appear before the application manifest entry.");

            var packageReferenceBlock = settingsProject[packageReferenceBlockStart..packageReferenceBlockEnd];
            string[] inactiveModuleCommentTokens =
            {
                "CmdPal",
                "MWB",
                "Mouse Without Borders",
                "Advanced Paste",
                "AdvancedPaste",
            };

            foreach (var inactiveModuleCommentToken in inactiveModuleCommentTokens)
            {
                Assert.IsFalse(packageReferenceBlock.Contains(inactiveModuleCommentToken, StringComparison.Ordinal), $"Settings package-reference comments should not explain active dependency pins through inactive module '{inactiveModuleCommentToken}'.");
            }
        }

        [TestMethod]
        public void KitCentralPackagesShouldNotKeepRegistryPreviewOnlySkiaSharpPin()
        {
            var centralPackages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));

            Assert.IsFalse(centralPackages.Contains(@"PackageVersion Include=""SkiaSharp.Views.WinUI""", StringComparison.Ordinal), "Kit should not keep the Registry Preview-only SkiaSharp.Views.WinUI central package pin.");
            Assert.IsFalse(centralPackages.Contains("Registry Preview", StringComparison.Ordinal), "Central package comments should not explain package pins through inactive Registry Preview behavior.");
            Assert.IsFalse(centralPackages.Contains("HexBox", StringComparison.Ordinal), "Central package comments should not keep inactive Registry Preview HexBox details.");

            foreach (var projectFile in Directory.EnumerateFiles(Path.GetDirectoryName(FindSourceFile("Kit.slnx"))!, "*.*proj", SearchOption.AllDirectories))
            {
                if (projectFile.Contains($"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var project = File.ReadAllText(projectFile);
                Assert.IsFalse(project.Contains(@"PackageReference Include=""SkiaSharp.Views.WinUI""", StringComparison.Ordinal), $"Kit project should not reference the Registry Preview-only SkiaSharp.Views.WinUI package: {projectFile}");
            }
        }

        [TestMethod]
        public void KitCentralPackagesShouldNotKeepCommandPaletteOnlyExtensionPin()
        {
            var centralPackages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));

            Assert.IsFalse(centralPackages.Contains(@"PackageVersion Include=""Microsoft.CommandPalette.Extensions""", StringComparison.Ordinal), "Kit should not keep the unused Command Palette extension central package pin.");

            foreach (var projectFile in Directory.EnumerateFiles(Path.GetDirectoryName(FindSourceFile("Kit.slnx"))!, "*.*proj", SearchOption.AllDirectories))
            {
                if (projectFile.Contains($"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var project = File.ReadAllText(projectFile);
                Assert.IsFalse(project.Contains(@"PackageReference Include=""Microsoft.CommandPalette.Extensions""", StringComparison.Ordinal), $"Kit project should not reference the inactive Command Palette extension package: {projectFile}");
            }
        }

        [TestMethod]
        public void KitCentralPackagesShouldNotKeepCommandPaletteAdaptiveCardsPins()
        {
            var centralPackages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));
            var notice = File.ReadAllText(FindSourceFile("NOTICE.md"));
            string[] commandPaletteAdaptiveCardPackages =
            {
                "AdaptiveCards.ObjectModel.WinUI3",
                "AdaptiveCards.Rendering.WinUI3",
                "AdaptiveCards.Templating",
                "Microsoft.Bot.AdaptiveExpressions.Core",
            };

            foreach (var packageName in commandPaletteAdaptiveCardPackages)
            {
                Assert.IsFalse(centralPackages.Contains($@"PackageVersion Include=""{packageName}""", StringComparison.Ordinal), $"Kit should not keep the inactive Command Palette Adaptive Cards central package pin: {packageName}");
                Assert.IsFalse(notice.Contains($"- {packageName}", StringComparison.Ordinal), $"Third-party notices should not list the removed Command Palette Adaptive Cards package dependency: {packageName}");
            }

            foreach (var projectFile in Directory.EnumerateFiles(Path.GetDirectoryName(FindSourceFile("Kit.slnx"))!, "*.*proj", SearchOption.AllDirectories))
            {
                if (projectFile.Contains($"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var project = File.ReadAllText(projectFile);
                foreach (var packageName in commandPaletteAdaptiveCardPackages)
                {
                    Assert.IsFalse(project.Contains($@"PackageReference Include=""{packageName}""", StringComparison.Ordinal), $"Kit project should not reference the inactive Command Palette Adaptive Cards package {packageName}: {projectFile}");
                }
            }
        }

        [TestMethod]
        public void KitCentralPackagesShouldNotKeepCommandPaletteWinGetInteropPin()
        {
            var centralPackages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));

            Assert.IsFalse(centralPackages.Contains(@"PackageVersion Include=""Microsoft.WindowsPackageManager.ComInterop""", StringComparison.Ordinal), "Kit should not keep the inactive Command Palette WinGet interop central package pin.");

            foreach (var projectFile in Directory.EnumerateFiles(Path.GetDirectoryName(FindSourceFile("Kit.slnx"))!, "*.*proj", SearchOption.AllDirectories))
            {
                if (projectFile.Contains($"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var project = File.ReadAllText(projectFile);
                Assert.IsFalse(project.Contains(@"PackageReference Include=""Microsoft.WindowsPackageManager.ComInterop""", StringComparison.Ordinal), $"Kit project should not reference the inactive Command Palette WinGet interop package: {projectFile}");
                Assert.IsFalse(project.Contains("PkgMicrosoft_WindowsPackageManager_ComInterop", StringComparison.Ordinal), $"Kit project should not keep generated path-property usage for the inactive Command Palette WinGet interop package: {projectFile}");
            }
        }

        [TestMethod]
        public void KitCentralPackagesShouldNotKeepAdvancedPasteMarkdownConversionPins()
        {
            var centralPackages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));
            var notice = File.ReadAllText(FindSourceFile("NOTICE.md"));
            string[] advancedPasteMarkdownPackages =
            {
                "HtmlAgilityPack",
                "ReverseMarkdown",
            };

            foreach (var packageName in advancedPasteMarkdownPackages)
            {
                Assert.IsFalse(centralPackages.Contains($@"PackageVersion Include=""{packageName}""", StringComparison.Ordinal), $"Kit should not keep the inactive AdvancedPaste Markdown conversion central package pin: {packageName}");
                Assert.IsFalse(notice.Contains($"- {packageName}", StringComparison.Ordinal), $"Third-party notices should not list the removed AdvancedPaste Markdown conversion package dependency: {packageName}");
            }

            foreach (var projectFile in Directory.EnumerateFiles(Path.GetDirectoryName(FindSourceFile("Kit.slnx"))!, "*.*proj", SearchOption.AllDirectories))
            {
                if (projectFile.Contains($"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var project = File.ReadAllText(projectFile);
                foreach (var packageName in advancedPasteMarkdownPackages)
                {
                    Assert.IsFalse(project.Contains($@"PackageReference Include=""{packageName}""", StringComparison.Ordinal), $"Kit project should not reference the inactive AdvancedPaste Markdown conversion package {packageName}: {projectFile}");
                }
            }
        }

        [TestMethod]
        public void KitCentralPackagesShouldNotKeepPowerToysRunPackagePins()
        {
            var centralPackages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));
            var notice = File.ReadAllText(FindSourceFile("NOTICE.md"));
            string[] powerToysRunPackages =
            {
                "hyjiacan.pinyin4net",
                "Mages",
                "UnitsNet",
            };

            foreach (var packageName in powerToysRunPackages)
            {
                Assert.IsFalse(centralPackages.Contains($@"PackageVersion Include=""{packageName}""", StringComparison.Ordinal), $"Kit should not keep the inactive PowerToys Run central package pin: {packageName}");
                Assert.IsFalse(notice.Contains($"- {packageName}", StringComparison.Ordinal), $"Third-party notices should not list the removed PowerToys Run package dependency: {packageName}");
            }

            Assert.IsFalse(notice.Contains("## Utility: PowerToys Run built-in extensions", StringComparison.Ordinal), "Third-party notices should not keep the removed PowerToys Run extension notice section.");
            Assert.IsFalse(notice.Contains("#### Mages", StringComparison.Ordinal), "Third-party notices should not keep the removed PowerToys Run Mages license section.");

            foreach (var projectFile in Directory.EnumerateFiles(Path.GetDirectoryName(FindSourceFile("Kit.slnx"))!, "*.*proj", SearchOption.AllDirectories))
            {
                if (projectFile.Contains($"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var project = File.ReadAllText(projectFile);
                foreach (var packageName in powerToysRunPackages)
                {
                    Assert.IsFalse(project.Contains($@"PackageReference Include=""{packageName}""", StringComparison.Ordinal), $"Kit project should not reference the inactive PowerToys Run package {packageName}: {projectFile}");
                }
            }
        }

        [TestMethod]
        public void KitCentralPackagesShouldNotKeepPreviewPaneAndPowerAccentPins()
        {
            var centralPackages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));
            var notice = File.ReadAllText(FindSourceFile("NOTICE.md"));
            string[] inactiveModulePackages =
            {
                "HelixToolkit",
                "HelixToolkit.Core.Wpf",
                "UnicodeInformation",
            };

            foreach (var packageName in inactiveModulePackages)
            {
                Assert.IsFalse(centralPackages.Contains($@"PackageVersion Include=""{packageName}""", StringComparison.Ordinal), $"Kit should not keep the inactive PreviewPane or PowerAccent central package pin: {packageName}");
                Assert.IsFalse(notice.Contains($"- {packageName}", StringComparison.Ordinal), $"Third-party notices should not list the removed PreviewPane or PowerAccent package dependency: {packageName}");
            }

            foreach (var projectFile in Directory.EnumerateFiles(Path.GetDirectoryName(FindSourceFile("Kit.slnx"))!, "*.*proj", SearchOption.AllDirectories))
            {
                if (projectFile.Contains($"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var project = File.ReadAllText(projectFile);
                foreach (var packageName in inactiveModulePackages)
                {
                    Assert.IsFalse(project.Contains($@"PackageReference Include=""{packageName}""", StringComparison.Ordinal), $"Kit project should not reference the inactive PreviewPane or PowerAccent package {packageName}: {projectFile}");
                }
            }
        }

        [TestMethod]
        public void KitDotNetBuildLayerShouldFollowPowerToysNet10Versions()
        {
            var dotnetProps = File.ReadAllText(FindSourceFile("src", "Common.Dotnet.CsWinRT.props"));

            StringAssert.Contains(dotnetProps, "<CoreTargetFramework>net10.0</CoreTargetFramework>");
            Assert.IsFalse(dotnetProps.Contains("<CoreTargetFramework>net9.0</CoreTargetFramework>", StringComparison.Ordinal), "Shared .NET build props should not leave default CsWinRT projects on net9.");

            string[] net10ProjectPaths =
            {
                Path.Combine("src", "common", "Common.UI.Controls", "Common.UI.Controls.csproj"),
                Path.Combine("src", "common", "UITestAutomation", "UITestAutomation.csproj"),
                Path.Combine("src", "settings-ui", "QuickAccess.UI", "PowerToys.QuickAccess.csproj"),
                Path.Combine("src", "settings-ui", "Settings.UI.Controls", "Settings.UI.Controls.csproj"),
            };

            foreach (var relativePath in net10ProjectPaths)
            {
                var project = File.ReadAllText(FindSourceFile(relativePath.Split(Path.DirectorySeparatorChar)));

                StringAssert.Contains(project, "<TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>");
                Assert.IsFalse(project.Contains("net9.0-windows10.0.26100.0", StringComparison.Ordinal), $"{relativePath} should not remain on net9.");
            }

            var packages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));
            string[] expectedNet10PackagePins =
            {
                @"<PackageVersion Include=""Microsoft.Bcl.AsyncInterfaces"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""Microsoft.CodeAnalysis.NetAnalyzers"" Version=""10.0.102"" />",
                @"<PackageVersion Include=""Microsoft.Data.Sqlite"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""Microsoft.Extensions.Caching.Abstractions"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""Microsoft.Extensions.Caching.Memory"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""Microsoft.Extensions.DependencyInjection"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""Microsoft.Extensions.Hosting"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""Microsoft.Extensions.Hosting.WindowsServices"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""Microsoft.Extensions.Logging"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""Microsoft.Extensions.Logging.Abstractions"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""Microsoft.Win32.SystemEvents"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""Microsoft.Windows.Compatibility"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""System.CodeDom"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""System.ComponentModel.Composition"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""System.Configuration.ConfigurationManager"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""System.Data.OleDb"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""System.Data.SqlClient"" Version=""4.9.1"" />",
                @"<PackageVersion Include=""System.Diagnostics.EventLog"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""System.Diagnostics.PerformanceCounter"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""System.Drawing.Common"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""System.Management"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""System.Runtime.Caching"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""System.ServiceProcess.ServiceController"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""System.Text.Encoding.CodePages"" Version=""10.0.7"" />",
                @"<PackageVersion Include=""System.Text.Json"" Version=""10.0.7"" />",
            };

            foreach (var packagePin in expectedNet10PackagePins)
            {
                StringAssert.Contains(packages, packagePin);
            }

            Assert.IsFalse(packages.Contains(@"<PackageVersion Include=""System.Collections.Immutable""", StringComparison.Ordinal), "PowerToys-main no longer pins System.Collections.Immutable centrally.");

            var settingsProject = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "PowerToys.Settings.csproj"));
            var settingsUnitTestsProject = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.UnitTests", "Settings.UI.UnitTests.csproj"));
            var xamlIndexBuilderProject = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.XamlIndexBuilder", "Settings.UI.XamlIndexBuilder.csproj"));
            var uiTestAutomationProject = File.ReadAllText(FindSourceFile("src", "common", "UITestAutomation", "UITestAutomation.csproj"));
            StringAssert.Contains(settingsProject, @"Targets=""Restore;Build""");
            Assert.IsFalse(settingsProject.Contains(@"<PackageReference Include=""System.Net.Http""", StringComparison.Ordinal));
            Assert.IsFalse(settingsProject.Contains(@"<PackageReference Include=""System.Private.Uri""", StringComparison.Ordinal));
            Assert.IsFalse(settingsProject.Contains(@"<PackageReference Include=""System.Text.RegularExpressions""", StringComparison.Ordinal));
            Assert.IsFalse(settingsProject.Contains(@"<PackageReference Include=""System.Text.Json""", StringComparison.Ordinal));
            Assert.IsFalse(settingsUnitTestsProject.Contains(@"<PackageReference Include=""System.Net.Http""", StringComparison.Ordinal));
            Assert.IsFalse(settingsUnitTestsProject.Contains(@"<PackageReference Include=""System.Private.Uri""", StringComparison.Ordinal));
            Assert.IsFalse(settingsUnitTestsProject.Contains(@"<PackageReference Include=""System.Text.RegularExpressions""", StringComparison.Ordinal));
            Assert.IsFalse(xamlIndexBuilderProject.Contains(@"<PackageReference Include=""System.Text.Json""", StringComparison.Ordinal));
            Assert.IsFalse(uiTestAutomationProject.Contains(@"<PackageReference Include=""System.Text.RegularExpressions""", StringComparison.Ordinal));

            if (TryFindSourceOrPowerToysReferenceFile(out var buildTemplatePath, ".pipelines", "v2", "templates", "job-build-project.yml"))
            {
                var buildTemplate = File.ReadAllText(buildTemplatePath);
                StringAssert.Contains(buildTemplate, "TargetFramework=net10.0-windows10.0.26100.0");
                Assert.IsFalse(buildTemplate.Contains("net9.0-windows10.0.26100.0", StringComparison.Ordinal));
            }

            if (TryFindSourceOrPowerToysReferenceFile(out var publishScriptPath, "installer", "PowerToysSetupVNext", "publish.cmd"))
            {
                var publishScript = File.ReadAllText(publishScriptPath);
                StringAssert.Contains(publishScript, "TargetFramework=net10.0-windows10.0.26100.0");
                Assert.IsFalse(publishScript.Contains("net9.0-windows10.0.26100.0", StringComparison.Ordinal));
            }

            if (TryFindSourceOrPowerToysReferenceFile(out var devDocPluginChecklistPath, "doc", "devdoc", "modules", "launcher", "new-plugin-checklist.md"))
            {
                var devDocPluginChecklist = File.ReadAllText(devDocPluginChecklistPath);
                StringAssert.Contains(devDocPluginChecklist, "net10.0-windows10.0.22621.0");
                StringAssert.Contains(devDocPluginChecklist, ".NET 10");
                Assert.IsFalse(devDocPluginChecklist.Contains(".NET 9", StringComparison.Ordinal));
                Assert.IsFalse(devDocPluginChecklist.Contains("net9.0-windows10.0.22621.0", StringComparison.Ordinal));
            }

            if (TryFindSourceOrPowerToysReferenceFile(out var devDocsPluginChecklistPath, "doc", "devdocs", "modules", "launcher", "new-plugin-checklist.md"))
            {
                var devDocsPluginChecklist = File.ReadAllText(devDocsPluginChecklistPath);
                StringAssert.Contains(devDocsPluginChecklist, "net10.0-windows10.0.22621.0");
                StringAssert.Contains(devDocsPluginChecklist, ".NET 10");
                Assert.IsFalse(devDocsPluginChecklist.Contains(".NET 9", StringComparison.Ordinal));
                Assert.IsFalse(devDocsPluginChecklist.Contains("net9.0-windows10.0.22621.0", StringComparison.Ordinal));
            }
        }

        [TestMethod]
        public void RunnerShouldAcceptKitProtocolWhileKeepingPowerToysProtocolCompatibility()
        {
            var runnerMain = File.ReadAllText(FindSourceFile("src", "runner", "main.cpp"));

            StringAssert.Contains(runnerMain, "KIT_URI_PROTOCOL_SCHEME");
            StringAssert.Contains(runnerMain, "L\"kit://\"");
            StringAssert.Contains(runnerMain, "PT_URI_PROTOCOL_SCHEME");
            StringAssert.Contains(runnerMain, "L\"powertoys://\"");
        }

        [TestMethod]
        public void KitShouldKeepPowerToysModuleDllCompatibilityAndKitStorageRoot()
        {
            var sharedConstants = File.ReadAllText(FindSourceFile("src", "common", "interop", "shared_constants.h"));
            var runnerMain = File.ReadAllText(FindSourceFile("src", "runner", "main.cpp"));

            StringAssert.Contains(sharedConstants, "const wchar_t APPDATA_PATH[] = L\"Kit\"");
            StringAssert.Contains(runnerMain, "L\"PowerToys.AwakeModuleInterface.dll\"");
            StringAssert.Contains(runnerMain, "L\"PowerToys.LightSwitchModuleInterface.dll\"");
            StringAssert.Contains(runnerMain, "L\"PowerToys.MonitorModuleInterface.dll\"");
            StringAssert.Contains(runnerMain, "L\"PowerToys.PowerDisplayModuleInterface.dll\"");
        }

        [TestMethod]
        public void KitRunnerShouldNotKeepInactiveImageResizerAiDetectionPath()
        {
            var runnerMain = File.ReadAllText(FindSourceFile("src", "runner", "main.cpp"));
            var generalSettings = File.ReadAllText(FindSourceFile("src", "runner", "general_settings.cpp"));
            var runnerProject = File.ReadAllText(FindSourceFile("src", "runner", "Kit.vcxproj"));
            var runnerProjectFilters = File.ReadAllText(FindSourceFile("src", "runner", "Kit.vcxproj.filters"));

            StringAssert.Contains(runnerMain, "KitKnownModules");
            StringAssert.Contains(runnerMain, "is_known_module_registered");
            Assert.IsFalse(runnerMain.Contains("DetectAiCapabilitiesAsync", StringComparison.Ordinal), "Kit runner should not keep inactive Image Resizer AI detection code.");
            Assert.IsFalse(runnerMain.Contains("is_image_resizer_registered_for_kit", StringComparison.Ordinal), "Kit runner should not probe inactive Image Resizer registration.");
            Assert.IsFalse(runnerMain.Contains("package::IsWin11OrGreater()", StringComparison.Ordinal), "Kit startup should not run an OS check solely for inactive Image Resizer AI detection.");
            Assert.IsFalse(generalSettings.Contains("DetectAiCapabilitiesAsync", StringComparison.Ordinal), "General settings updates should not trigger inactive Image Resizer AI detection.");
            Assert.IsFalse(generalSettings.Contains("Image Resizer", StringComparison.Ordinal), "General settings module updates should stay scoped to loaded Kit modules.");
            Assert.IsFalse(runnerProject.Contains("ai_detection.h", StringComparison.Ordinal), "Runner project should not keep inactive Image Resizer AI detection headers.");
            Assert.IsFalse(runnerProjectFilters.Contains("ai_detection.h", StringComparison.Ordinal), "Runner project filters should not keep inactive Image Resizer AI detection headers.");
        }

        [TestMethod]
        public void KitRunnerShouldNotKeepInactiveShortcutGuideWinKeyTrackingPath()
        {
            var powertoyModule = File.ReadAllText(FindSourceFile("src", "runner", "powertoy_module.cpp"));
            var centralizedKeyboardHook = File.ReadAllText(FindSourceFile("src", "runner", "centralized_kb_hook.cpp"));
            var centralizedKeyboardHookHeader = File.ReadAllText(FindSourceFile("src", "runner", "centralized_kb_hook.h"));
            var moduleInterface = File.ReadAllText(FindSourceFile("src", "modules", "interface", "powertoy_module_interface.h"));

            string[] inactiveWinKeyTrackingTokens =
            {
                "keep_track_of_pressed_win_key",
                "milliseconds_win_key_must_be_pressed",
                "AddPressedKeyAction",
                "PressedKeyDescriptor",
                "PressedKeyTimerProc",
                "pressedKeyDescriptors",
                "Shortcut Guide",
            };

            foreach (var inactiveToken in inactiveWinKeyTrackingTokens)
            {
                Assert.IsFalse(powertoyModule.Contains(inactiveToken, StringComparison.Ordinal), $"Runner module wrapper should not keep inactive Shortcut Guide Win-key tracking token '{inactiveToken}'.");
                Assert.IsFalse(centralizedKeyboardHook.Contains(inactiveToken, StringComparison.Ordinal), $"Centralized keyboard hook should not keep inactive Shortcut Guide Win-key tracking token '{inactiveToken}'.");
                Assert.IsFalse(centralizedKeyboardHookHeader.Contains(inactiveToken, StringComparison.Ordinal), $"Centralized keyboard hook header should not expose inactive Shortcut Guide Win-key tracking token '{inactiveToken}'.");
                Assert.IsFalse(moduleInterface.Contains(inactiveToken, StringComparison.Ordinal), $"Module interface should not expose inactive Shortcut Guide Win-key tracking token '{inactiveToken}'.");
            }
        }

        [TestMethod]
        public void KitRunnerShouldNotKeepNoOpKeyboardHookWindowRegistration()
        {
            var trayIcon = File.ReadAllText(FindSourceFile("src", "runner", "tray_icon.cpp"));
            var centralizedKeyboardHook = File.ReadAllText(FindSourceFile("src", "runner", "centralized_kb_hook.cpp"));
            var centralizedKeyboardHookHeader = File.ReadAllText(FindSourceFile("src", "runner", "centralized_kb_hook.h"));

            StringAssert.Contains(trayIcon, "CentralizedHotkeys::RegisterWindow(hwnd)");
            Assert.IsFalse(trayIcon.Contains("CentralizedKeyboardHook::RegisterWindow", StringComparison.Ordinal), "Tray initialization should not call a no-op keyboard hook window registration after pressed-key timers are deleted.");
            Assert.IsFalse(centralizedKeyboardHook.Contains("RegisterWindow(HWND", StringComparison.Ordinal), "Keyboard hook implementation should not keep a no-op window-registration method.");
            Assert.IsFalse(centralizedKeyboardHookHeader.Contains("RegisterWindow(HWND", StringComparison.Ordinal), "Keyboard hook header should not expose a no-op window-registration method.");
        }

        [TestMethod]
        public void KitStartupShouldAvoidInactiveOobeScoobeAndUpdaterDiskReads()
        {
            var runnerMain = File.ReadAllText(FindSourceFile("src", "runner", "main.cpp"));
            var settingsWindow = File.ReadAllText(FindSourceFile("src", "runner", "settings_window.cpp"));
            var settingsWindowHeader = File.ReadAllText(FindSourceFile("src", "runner", "settings_window.h"));
            var trayIcon = File.ReadAllText(FindSourceFile("src", "runner", "tray_icon.cpp"));
            var settingsApp = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "App.xaml.cs"));
            var textBlockStyles = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Styles", "TextBlock.xaml"));
            var settingsHelpers = File.ReadAllText(FindSourceFile("src", "common", "SettingsAPI", "settings_helpers.cpp"));
            var settingsHelpersHeader = File.ReadAllText(FindSourceFile("src", "common", "SettingsAPI", "settings_helpers.h"));

            Assert.IsFalse(runnerMain.Contains("get_oobe_opened_state", StringComparison.Ordinal), "Kit startup should not read disabled OOBE state.");
            Assert.IsFalse(runnerMain.Contains("get_last_version_run", StringComparison.Ordinal), "Kit startup should not read disabled SCOOBE/update version state.");
            Assert.IsFalse(runnerMain.Contains("save_last_version_run", StringComparison.Ordinal), "Kit startup should not write last-version state when SCOOBE is disabled.");
            Assert.IsFalse(runnerMain.Contains("save_oobe_opened_state", StringComparison.Ordinal), "Kit startup should not write disabled OOBE state.");
            Assert.IsFalse(runnerMain.Contains("openOobe", StringComparison.Ordinal), "Kit runner should not keep disabled OOBE launch plumbing.");
            Assert.IsFalse(runnerMain.Contains("openScoobe", StringComparison.Ordinal), "Kit runner should not keep disabled SCOOBE launch plumbing.");
            Assert.IsFalse(settingsWindow.Contains("show_oobe_window", StringComparison.Ordinal), "Kit Settings launcher should not pass disabled OOBE arguments.");
            Assert.IsFalse(settingsWindow.Contains("show_scoobe_window", StringComparison.Ordinal), "Kit Settings launcher should not pass disabled SCOOBE arguments.");
            Assert.IsFalse(settingsWindow.Contains("open_oobe_window", StringComparison.Ordinal), "Kit Settings launcher should not expose disabled OOBE windows.");
            Assert.IsFalse(settingsWindow.Contains("open_scoobe_window", StringComparison.Ordinal), "Kit Settings launcher should not expose disabled SCOOBE windows.");
            Assert.IsFalse(settingsWindowHeader.Contains("open_oobe_window", StringComparison.Ordinal), "Kit Settings launcher header should not expose disabled OOBE windows.");
            Assert.IsFalse(settingsWindowHeader.Contains("open_scoobe_window", StringComparison.Ordinal), "Kit Settings launcher header should not expose disabled SCOOBE windows.");
            Assert.IsFalse(trayIcon.Contains("UpdateState::read", StringComparison.Ordinal), "Kit tray startup should not read updater state when updater UI is disabled.");
            Assert.IsFalse(settingsApp.Contains("OobeShellViewModel { get; } = new()", StringComparison.Ordinal), "Kit Settings should not eagerly construct OOBE state when OOBE windows are stubbed.");
            Assert.IsFalse(settingsApp.Contains("ShowOobe", StringComparison.Ordinal), "Kit Settings should not keep disabled OOBE launch flags.");
            Assert.IsFalse(settingsApp.Contains("ShowScoobe", StringComparison.Ordinal), "Kit Settings should not keep disabled SCOOBE launch flags.");
            Assert.IsFalse(settingsApp.Contains("OpenOobe", StringComparison.Ordinal), "Kit Settings should not keep disabled OOBE launch methods.");
            Assert.IsFalse(settingsApp.Contains("OpenScoobe", StringComparison.Ordinal), "Kit Settings should not keep disabled SCOOBE launch methods.");
            Assert.IsFalse(textBlockStyles.Contains("OobeSubtitleStyle", StringComparison.Ordinal), "Kit Settings should not keep styles used only by deleted OOBE pages.");

            string[] staleSettingsStateHelpers =
            {
                "get_oobe_opened_state",
                "save_oobe_opened_state",
                "get_last_version_run",
                "save_last_version_run",
                "oobe_settings.json",
                "last_version_run.json",
                "opened_at_first_launch",
            };

            foreach (var staleHelper in staleSettingsStateHelpers)
            {
                Assert.IsFalse(settingsHelpers.Contains(staleHelper, StringComparison.Ordinal), $"SettingsAPI should not keep disabled OOBE/SCOOBE state helper: {staleHelper}");
                Assert.IsFalse(settingsHelpersHeader.Contains(staleHelper, StringComparison.Ordinal), $"SettingsAPI header should not expose disabled OOBE/SCOOBE state helper: {staleHelper}");
            }
        }

        [TestMethod]
        public void KitSettingsFirstFrameShouldDeferGeneralPageMaintenanceWork()
        {
            var generalViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "GeneralViewModel.cs"));
            var generalPage = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "GeneralPage.xaml.cs"));
            var shellPage = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "ShellPage.xaml.cs"));

            Assert.IsFalse(generalPage.Contains("doRefreshBackupRestoreStatus(100);", StringComparison.Ordinal), "Backup dry-run should not be scheduled from the GeneralPage constructor before the first frame.");
            StringAssert.Contains(generalViewModel, "RunDeferredStartupMaintenance");
            StringAssert.Contains(generalViewModel, "Task.Run(DeleteOldDiagnosticData)");
            StringAssert.Contains(shellPage, "await Task.Delay(1000)");
            StringAssert.Contains(shellPage, "SearchIndexService.BuildIndex()");
        }

        [TestMethod]
        public void KitXamlIndexBuilderShouldNotCarryInactiveModuleFallbacks()
        {
            var xamlIndexBuilderProgram = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.XamlIndexBuilder", "Program.cs"));
            var moduleIconResolver = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.XamlIndexBuilder", "ModuleIconResolver.cs"));

            Assert.IsFalse(xamlIndexBuilderProgram.Contains("PanelPageMapping", StringComparison.Ordinal), "Kit has no active Settings panels, so search indexing should not carry inactive panel-to-page fallback mappings.");
            Assert.IsFalse(xamlIndexBuilderProgram.Contains("MouseJumpPanel", StringComparison.Ordinal), "Kit search indexing should not explicitly include deleted Mouse Jump panels.");
            Assert.IsFalse(moduleIconResolver.Contains("FileNameOverrides", StringComparison.Ordinal), "Kit search indexing should derive active page icons from XAML instead of carrying inactive upstream page overrides.");

            string[] inactiveSearchIndexFallbacks =
            {
                "FancyZones",
                "FileLocksmith",
                "CmdNotFound",
                "CommandNotFound",
                "PowerLauncher",
                "PowerToysRun",
            };

            foreach (var inactiveFallback in inactiveSearchIndexFallbacks)
            {
                Assert.IsFalse(xamlIndexBuilderProgram.Contains(inactiveFallback, StringComparison.Ordinal), $"XamlIndexBuilder Program.cs should not reference inactive module fallback '{inactiveFallback}'.");
                Assert.IsFalse(moduleIconResolver.Contains(inactiveFallback, StringComparison.Ordinal), $"ModuleIconResolver.cs should not reference inactive module fallback '{inactiveFallback}'.");
            }
        }

        [TestMethod]
        public void KitRunnerShouldReuseStartupGeneralSettingsForInitialModuleEnablement()
        {
            var runnerMain = File.ReadAllText(FindSourceFile("src", "runner", "main.cpp"));
            var generalSettings = File.ReadAllText(FindSourceFile("src", "runner", "general_settings.cpp"));
            var generalSettingsHeader = File.ReadAllText(FindSourceFile("src", "runner", "general_settings.h"));

            StringAssert.Contains(runnerMain, "const json::JsonObject& startupGeneralSettings");
            StringAssert.Contains(runnerMain, "start_enabled_powertoys(startupGeneralSettings)");
            StringAssert.Contains(generalSettingsHeader, "void start_enabled_powertoys(const json::JsonObject& general_settings);");
            StringAssert.Contains(generalSettings, "void start_enabled_powertoys(const json::JsonObject& general_settings)");
            Assert.IsFalse(generalSettings.Contains("general_settings = load_general_settings();", StringComparison.Ordinal), "Initial module enablement should reuse the already-loaded settings object instead of re-reading settings.json.");
        }

        [TestMethod]
        public void KitRunnerExecutableShouldBePrimaryNameWithPowerToysFallbacks()
        {
            var runnerProject = File.ReadAllText(FindSourceFile("src", "runner", "Kit.vcxproj"));
            var runnerResource = File.ReadAllText(FindSourceFile("src", "runner", "resource.base.h"));
            var runnerHelper = File.ReadAllText(FindSourceFile("src", "common", "ManagedCommon", "RunnerHelper.cs"));
            var pathResolver = File.ReadAllText(FindSourceFile("src", "common", "ManagedCommon", "PowerToysPathResolver.cs"));

            StringAssert.Contains(runnerProject, "<TargetName>Kit</TargetName>");
            StringAssert.Contains(runnerResource, "#define ORIGINAL_FILENAME \"Kit.exe\"");
            StringAssert.Contains(runnerHelper, "\"Kit.exe\"");
            StringAssert.Contains(runnerHelper, "\"PowerToys.exe\"");
            StringAssert.Contains(pathResolver, "KitRegistryKey");
            StringAssert.Contains(pathResolver, "PowerToysRegistryKey");
            StringAssert.Contains(pathResolver, "KitExe = \"Kit.exe\"");
            StringAssert.Contains(pathResolver, "PowerToysExe = \"PowerToys.exe\"");
        }

        [TestMethod]
        public void KitMainSolutionAndRunnerProjectShouldUseKitNames()
        {
            var solutionPath = FindSourceFile("Kit.slnx");
            var repoRoot = Path.GetDirectoryName(solutionPath);
            var solution = File.ReadAllText(solutionPath);
            var runnerProjectPath = FindSourceFile("src", "runner", "Kit.vcxproj");
            var runnerProject = File.ReadAllText(runnerProjectPath);
            var runnerFilters = File.ReadAllText(FindSourceFile("src", "runner", "Kit.vcxproj.filters"));
            var runnerDirectory = Path.GetDirectoryName(runnerProjectPath);

            Assert.IsFalse(File.Exists(Path.Combine(repoRoot, "PowerToys.slnx")), "Kit should expose Kit.slnx as the primary solution file.");
            Assert.IsFalse(File.Exists(Path.Combine(runnerDirectory, "runner.vcxproj")), "Kit runner project should use Kit.vcxproj as the primary project file.");
            StringAssert.Contains(solution, "src/runner/Kit.vcxproj");
            Assert.IsFalse(solution.Contains("src/runner/runner.vcxproj", StringComparison.Ordinal));
            StringAssert.Contains(runnerProject, "<RootNamespace>Kit</RootNamespace>");
            StringAssert.Contains(runnerProject, "<ProjectName>Kit</ProjectName>");
            StringAssert.Contains(runnerProject, "<Manifest Include=\"Kit.exe.manifest\" />");
            StringAssert.Contains(runnerProject, @"<IntDir>$(Platform)\$(Configuration)\$(MSBuildProjectName)\</IntDir>");
            StringAssert.Contains(runnerFilters, "<Manifest Include=\"Kit.exe.manifest\" />");
            _ = FindSourceFile("src", "runner", "Kit.exe.manifest");
        }

        [TestMethod]
        public void KitStartupTaskShouldUseKitSchedulerFolder()
        {
            var autoStartHelper = File.ReadAllText(FindSourceFile("src", "runner", "auto_start_helper.cpp"));

            StringAssert.Contains(autoStartHelper, "L\"\\\\Kit\"");
            Assert.IsFalse(autoStartHelper.Contains("L\"\\\\PowerToys\"", StringComparison.Ordinal), "Kit startup tasks must not share the PowerToys Task Scheduler folder.");
        }

        [TestMethod]
        public void KitRuntimeSingletonsShouldNotSharePowerToysGlobals()
        {
            var appMutex = File.ReadAllText(FindSourceFile("src", "common", "utils", "appMutex.h"));
            var runnerMain = File.ReadAllText(FindSourceFile("src", "runner", "main.cpp"));
            var trayIconHeader = File.ReadAllText(FindSourceFile("src", "runner", "tray_icon.h"));
            var shellPage = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "ShellPage.xaml.cs"));

            StringAssert.Contains(appMutex, "KIT_MSI_MUTEX_NAME");
            StringAssert.Contains(appMutex, "L\"Local\\\\Kit_Runner_MSI_InstanceMutex\"");
            StringAssert.Contains(runnerMain, "createAppMutex(KIT_MSI_MUTEX_NAME)");
            StringAssert.Contains(trayIconHeader, "L\"KitTrayIconWindow\"");
            StringAssert.Contains(shellPage, "\"KitTrayIconWindow\"");
            Assert.IsFalse(appMutex.Contains("PowerToys_Runner_MSI_InstanceMutex", StringComparison.Ordinal), "Kit runner must not share the PowerToys single-instance mutex.");
            Assert.IsFalse(runnerMain.Contains("L\"PToyTrayIconWindow\"", StringComparison.Ordinal), "Kit runner must not look up the PowerToys tray window class.");
            Assert.IsFalse(shellPage.Contains("\"PToyTrayIconWindow\"", StringComparison.Ordinal), "Kit Settings must send close commands to the Kit tray window class.");
        }

        [TestMethod]
        public void KitBundledRuntimeEventsShouldUseKitNames()
        {
            var sharedConstants = File.ReadAllText(FindSourceFile("src", "common", "interop", "shared_constants.h"));
            var lightSwitchInterface = File.ReadAllText(FindSourceFile("src", "modules", "LightSwitch", "LightSwitchModuleInterface", "dllmain.cpp"));
            var lightSwitchService = File.ReadAllText(FindSourceFile("src", "modules", "LightSwitch", "LightSwitchService", "LightSwitchService.cpp"));
            var powerDisplayPathConstants = File.ReadAllText(FindSourceFile("src", "modules", "powerdisplay", "PowerDisplay.Lib", "PathConstants.cs"));

            StringAssert.Contains(sharedConstants, "KitRunnerTerminateSettingsEvent");
            StringAssert.Contains(sharedConstants, "KitAwakeExitEvent");
            StringAssert.Contains(sharedConstants, "KitMonitorExitEvent");
            StringAssert.Contains(sharedConstants, "KitMonitorScanCompletedEvent");
            StringAssert.Contains(sharedConstants, "Kit-LightSwitch-ToggleEvent");
            StringAssert.Contains(sharedConstants, "KitPowerDisplay-ToggleEvent");
            StringAssert.Contains(sharedConstants, "KitPowerDisplay-SettingsUpdatedEvent");
            StringAssert.Contains(sharedConstants, "KitLightSwitch-LightThemeEvent");
            StringAssert.Contains(sharedConstants, "KitLightSwitch-DarkThemeEvent");
            StringAssert.Contains(lightSwitchInterface, "CommonSharedConstants::LIGHTSWITCH_TOGGLE_EVENT");
            StringAssert.Contains(lightSwitchInterface, "KIT_LIGHTSWITCH_MANUAL_OVERRIDE");
            StringAssert.Contains(lightSwitchService, "KIT_LIGHTSWITCH_MANUAL_OVERRIDE");
            Assert.IsFalse(sharedConstants.Contains("PowerToysRunnerTerminateSettingsEvent", StringComparison.Ordinal), "Kit Settings IPC must not share the PowerToys terminate event.");
            Assert.IsFalse(sharedConstants.Contains("PowerToysAwakeExitEvent", StringComparison.Ordinal), "Kit Awake must not share the PowerToys exit event.");
            Assert.IsFalse(sharedConstants.Contains("PowerToysMonitorExitEvent", StringComparison.Ordinal), "Kit Monitor must not share the PowerToys exit event.");
            Assert.IsFalse(sharedConstants.Contains("PowerToysMonitorScanCompletedEvent", StringComparison.Ordinal), "Kit Monitor scan completion event must not share PowerToys names.");
            Assert.IsFalse(sharedConstants.Contains("PowerToys-LightSwitch-ToggleEvent", StringComparison.Ordinal), "Kit LightSwitch must not share the PowerToys toggle event.");
            Assert.IsFalse(sharedConstants.Contains("PowerToysPowerDisplay", StringComparison.Ordinal), "Kit PowerDisplay must not share PowerToys event names.");
            Assert.IsFalse(sharedConstants.Contains("PowerToysLightSwitch-LightThemeEvent", StringComparison.Ordinal), "Kit LightSwitch-to-PowerDisplay light theme event must not share PowerToys names.");
            Assert.IsFalse(sharedConstants.Contains("PowerToysLightSwitch-DarkThemeEvent", StringComparison.Ordinal), "Kit LightSwitch-to-PowerDisplay dark theme event must not share PowerToys names.");
            Assert.IsFalse(lightSwitchInterface.Contains("POWERTOYS_LIGHTSWITCH", StringComparison.Ordinal), "Kit LightSwitch interface must not use PowerToys event names.");
            Assert.IsFalse(lightSwitchInterface.Contains("PowerToys-LightSwitch-ToggleEvent", StringComparison.Ordinal), "Kit LightSwitch interface must listen on the shared Kit toggle event.");
            Assert.IsFalse(lightSwitchService.Contains("POWERTOYS_LIGHTSWITCH", StringComparison.Ordinal), "Kit LightSwitch service must not use PowerToys event names.");
            StringAssert.Contains(powerDisplayPathConstants, "Path.Combine(_localAppDataPath.Value, \"Kit\")");
            Assert.IsFalse(powerDisplayPathConstants.Contains("\"Microsoft\", \"PowerToys\"", StringComparison.Ordinal), "Kit PowerDisplay must not store module state in the PowerToys app data folder.");
        }

        [TestMethod]
        public void KitRuntimePipePrefixesShouldUseKitNames()
        {
            var settingsWindow = File.ReadAllText(FindSourceFile("src", "runner", "settings_window.cpp"));
            var quickAccessHost = File.ReadAllText(FindSourceFile("src", "runner", "quick_access_host.cpp"));
            var powerDisplayProcessManager = File.ReadAllText(FindSourceFile("src", "modules", "powerdisplay", "PowerDisplayModuleInterface", "PowerDisplayProcessManager.cpp"));

            StringAssert.Contains(settingsWindow, @"\\\\.\\pipe\\kit_runner_");
            StringAssert.Contains(settingsWindow, @"\\\\.\\pipe\\kit_settings_");
            StringAssert.Contains(quickAccessHost, "Local\\\\KitQuickAccess_");
            StringAssert.Contains(quickAccessHost, @"\\\\.\\pipe\\kit_quick_access_runner_");
            StringAssert.Contains(quickAccessHost, @"\\\\.\\pipe\\kit_quick_access_ui_");
            StringAssert.Contains(powerDisplayProcessManager, "kit_power_display_");
            Assert.IsFalse(settingsWindow.Contains(@"\\\\.\\pipe\\powertoys_runner_", StringComparison.Ordinal));
            Assert.IsFalse(settingsWindow.Contains(@"\\\\.\\pipe\\powertoys_settings_", StringComparison.Ordinal));
            Assert.IsFalse(quickAccessHost.Contains("Local\\\\PowerToysQuickAccess_", StringComparison.Ordinal));
            Assert.IsFalse(quickAccessHost.Contains(@"\\\\.\\pipe\\powertoys_quick_access_", StringComparison.Ordinal));
            Assert.IsFalse(powerDisplayProcessManager.Contains("powertoys_power_display_", StringComparison.Ordinal));
        }

        [TestMethod]
        public void SettingsXamlNamedElementsShouldUseXNameForReleaseGeneratedFields()
        {
            var awakePage = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "AwakePage.xaml"));
            var generalPage = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "GeneralPage.xaml"));

            AssertUsesXName(awakePage, "AwakeEnableSettingsCard");
            AssertUsesXName(awakePage, "AwakeModeSettingsCard");
            AssertUsesXName(awakePage, "AwakeExpirationSettingsExpander");
            AssertUsesXName(awakePage, "AwakeIntervalSettingsCard");
            AssertUsesXName(awakePage, "AwakeExpirationSettingsExpanderDate");
            AssertUsesXName(awakePage, "AwakeExpirationSettingsExpanderTime");

            AssertUsesXName(generalPage, "AdminModeRunningAs");
            AssertUsesXName(generalPage, "LanguageHeader");
            AssertUsesXName(generalPage, "ColorModeHeader");
            AssertUsesXName(generalPage, "GeneralPageRunAtStartUp");
            AssertUsesXName(generalPage, "GeneralPageEnableQuickAccess");
            AssertUsesXName(generalPage, "QuickAccessShortcut");
            AssertUsesXName(generalPage, "GeneralSettingsBackupAndRestore");
            AssertUsesXName(generalPage, "GeneralSettingsBackupAndRestoreLocationText");
            AssertUsesXName(generalPage, "GeneralSettingsBackupAndRestoreStatusInfo");
            AssertUsesXName(generalPage, "GeneralPageEnableExperimentation");
        }

        [TestMethod]
        public void QuickAccessWindowShouldUseCurrentWinUiSystemBackdropApi()
        {
            var quickAccessMainWindow = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "QuickAccessXAML", "MainWindow.xaml"));

            StringAssert.Contains(quickAccessMainWindow, "<Window.SystemBackdrop>");
            StringAssert.Contains(quickAccessMainWindow, "<DesktopAcrylicBackdrop");
            Assert.IsFalse(quickAccessMainWindow.Contains("WindowEx.Backdrop", StringComparison.Ordinal), "Quick Access should not use the deprecated WinUIEx Backdrop attached property.");
            Assert.IsFalse(quickAccessMainWindow.Contains("AcrylicSystemBackdrop", StringComparison.Ordinal), "Quick Access should not use the deprecated WinUIEx AcrylicSystemBackdrop type.");
        }

        [TestMethod]
        public void MonitorActionSwitchesShouldAppearBeforeRunInBackground()
        {
            var monitorPage = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "MonitorPage.xaml"));

            var organizeIndex = monitorPage.IndexOf("Monitor_OrganizeDownloadsSettingsCard", StringComparison.Ordinal);
            var cleanIndex = monitorPage.IndexOf("Monitor_CleanInstallersSettingsCard", StringComparison.Ordinal);
            var runInBackgroundIndex = monitorPage.IndexOf("Monitor_RunInBackgroundSettingsCard", StringComparison.Ordinal);

            Assert.IsTrue(organizeIndex >= 0, "OrganizeDownloads switch should be present.");
            Assert.IsTrue(cleanIndex >= 0, "CleanInstallers switch should be present.");
            Assert.IsTrue(runInBackgroundIndex >= 0, "Run in background switch should be present.");
            Assert.IsTrue(organizeIndex < runInBackgroundIndex, "OrganizeDownloads should appear above Run in background.");
            Assert.IsTrue(cleanIndex < runInBackgroundIndex, "CleanInstallers should appear above Run in background.");
            Assert.AreEqual(organizeIndex, monitorPage.LastIndexOf("Monitor_OrganizeDownloadsSettingsCard", StringComparison.Ordinal), "OrganizeDownloads switch should not be duplicated.");
            Assert.AreEqual(cleanIndex, monitorPage.LastIndexOf("Monitor_CleanInstallersSettingsCard", StringComparison.Ordinal), "CleanInstallers switch should not be duplicated.");
        }

        [TestMethod]
        public void MonitorActionSwitchesShouldUseShortLabels()
        {
            var resources = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Strings", "en-us", "Resources.resw"));

            StringAssert.Contains(resources, "name=\"Monitor_OrganizeDownloadsSettingsCard.Header\"");
            StringAssert.Contains(resources, "<value>Organize</value>");
            StringAssert.Contains(resources, "name=\"Monitor_CleanInstallersSettingsCard.Header\"");
            StringAssert.Contains(resources, "<value>Clean</value>");
        }

        private static void AssertUsesXName(string xaml, string elementName)
        {
            StringAssert.Contains(xaml, $"x:Name=\"{elementName}\"");
            Assert.IsFalse(xaml.Contains($" Name=\"{elementName}\"", StringComparison.Ordinal), $"{elementName} should use x:Name so Release XAML compilation emits a backing field.");
        }

        private static void AssertHasGpoBranch(string gpoConfiguration, string moduleName)
        {
            Assert.IsTrue(HasGpoBranch(gpoConfiguration, moduleName), $"Settings GPO helper should keep active {moduleName} branch.");
        }

        private static bool HasGpoBranch(string gpoConfiguration, string moduleName)
        {
            return gpoConfiguration.Contains($"case ModuleType.{moduleName}:", StringComparison.Ordinal) ||
                   gpoConfiguration.Contains($"ModuleType.{moduleName} =>", StringComparison.Ordinal);
        }

        private static void AssertNoFiles(string root, string pattern)
        {
            var directory = Path.GetDirectoryName(pattern);
            var searchPattern = Path.GetFileName(pattern);
            var searchRoot = string.IsNullOrEmpty(directory) ? root : Path.Combine(root, directory);

            if (!Directory.Exists(searchRoot))
            {
                return;
            }

            var matches = Directory.GetFiles(searchRoot, searchPattern, SearchOption.TopDirectoryOnly);
            Assert.AreEqual(0, matches.Length, $"Inactive source pattern should have no files: {pattern}");
        }
    }
}
