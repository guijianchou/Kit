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
            var versionHeader = File.ReadAllText(FindSourceFile("src", "common", "version", "version.h"));
            var updateResources = File.ReadAllText(FindSourceFile("src", "Update", "Resources.resx"));
            var updateResourceHeader = File.ReadAllText(FindSourceFile("src", "Update", "resource.base.h"));
            var runnerResources = File.ReadAllText(FindSourceFile("src", "runner", "Resources.resx"));

            StringAssert.Contains(directoryBuildProps, "<AssemblyProduct>Kit</AssemblyProduct>");
            StringAssert.Contains(directoryBuildProps, "<Product>Kit</Product>");
            StringAssert.Contains(directoryBuildProps, "<PackageTags>Kit</PackageTags>");
            StringAssert.Contains(versionHeader, "#define PRODUCT_NAME \"Kit\"");
            StringAssert.Contains(updateResources, "<value>Kit installation error</value>");
            StringAssert.Contains(updateResources, "<value>An update to Kit is available.</value>");
            StringAssert.Contains(updateResources, "<value>An update to Kit is available. Visit our GitHub page to update.</value>");
            StringAssert.Contains(updateResources, "<value>Kit Update</value>");
            StringAssert.Contains(updateResourceHeader, "#define FILE_DESCRIPTION \"Kit Update\"");
            StringAssert.Contains(runnerResources, "<value>Kit Update</value>");
            Assert.IsFalse(versionHeader.Contains("#define PRODUCT_NAME \"PowerToys\"", StringComparison.Ordinal), "Shared file metadata should use the Kit product name.");
            Assert.IsFalse(updateResources.Contains("PowerToys installation error", StringComparison.Ordinal), "Update UI resources should not show upstream product branding.");
            Assert.IsFalse(updateResources.Contains("PowerToys Update", StringComparison.Ordinal), "Update toast title should not show upstream product branding.");
            Assert.IsFalse(runnerResources.Contains("PowerToys", StringComparison.Ordinal), "Runner user-visible resources should not show upstream product branding.");
        }

        [TestMethod]
        public void ReleaseBuildShouldKeepSlimPublishDefaults()
        {
            var directoryBuildProps = File.ReadAllText(FindSourceFile("Directory.Build.props"));
            var directoryBuildTargets = File.ReadAllText(FindSourceFile("Directory.Build.targets"));
            var cppBuildProps = File.ReadAllText(FindSourceFile("Cpp.Build.props"));
            var commonUiProject = File.ReadAllText(FindSourceFile("src", "common", "Common.UI", "Common.UI.csproj"));

            StringAssert.Contains(directoryBuildProps, "<SatelliteResourceLanguages>en-US</SatelliteResourceLanguages>");
            StringAssert.Contains(directoryBuildProps, @"$(USERPROFILE)\AppData\LocalLow\Kit\**");
            StringAssert.Contains(directoryBuildProps, @"\**\dotnet\dotnet.exe");
            StringAssert.Contains(directoryBuildProps, @"\**\vbcscompiler.exe");
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
            StringAssert.Contains(directoryBuildTargets, @"$(OutDir)**\*.exp;$(OutDir)**\*.lib.lastcodeanalysissucceeded");
            Assert.IsFalse(directoryBuildTargets.Contains(@"$(OutDir)**\*.lib;", StringComparison.Ordinal), "Release Build must not delete native .lib outputs before later solution projects finish linking.");
            StringAssert.Contains(directoryBuildTargets, "KitRemoveInactiveModelProviderArtifactsFromRuntimeOutput");
            StringAssert.Contains(directoryBuildTargets, @"$(OutDir)**\Assets\Settings\Icons\Models\*.svg;$(OutDir)**\*Foundry*");
            StringAssert.Contains(directoryBuildTargets, "KitRemoveInactiveManagedTelemetryArtifactsFromOutput");
            StringAssert.Contains(directoryBuildTargets, @"$(OutDir)**\PowerToys.ManagedTelemetry.*");
            StringAssert.Contains(directoryBuildTargets, @"$(OutDir)**\Dia2Lib.dll");
            StringAssert.Contains(directoryBuildTargets, @"$(OutDir)**\TraceReloggerLib.dll");
            StringAssert.Contains(directoryBuildTargets, @"$(OutDir)**\KernelTraceControl.dll");
            StringAssert.Contains(directoryBuildTargets, @"$(OutDir)**\msdia140.dll");
            StringAssert.Contains(directoryBuildTargets, "RemoveUnusedVCRuntimeDlls");
            StringAssert.Contains(directoryBuildTargets, "$(OutDir)mfc140*.dll");
            StringAssert.Contains(directoryBuildTargets, "$(OutDir)mfcm140*.dll");
            StringAssert.Contains(directoryBuildTargets, "$(OutDir)vcamp140*.dll");
            StringAssert.Contains(directoryBuildTargets, "$(OutDir)vcomp140*.dll");
            StringAssert.Contains(cppBuildProps, "<VcpkgEnabled>false</VcpkgEnabled>");
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
            var buildEssentialsScript = File.ReadAllText(FindSourceFile("tools", "build", "build-essentials.ps1"));

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

            StringAssert.Contains(buildEssentialsScript, @".\src\runner\Kit.vcxproj");
            StringAssert.Contains(buildEssentialsScript, @".\src\settings-ui\Settings.UI\PowerToys.Settings.csproj");
            StringAssert.Contains(buildEssentialsScript, @".\src\settings-ui\QuickAccess.UI\PowerToys.QuickAccess.csproj");
        }

        [TestMethod]
        public void NativeResourceGenerationShouldBypassBlockedPowerShellZoneChecks()
        {
            string[][] projectPaths =
            {
                new[] { "src", "ActionRunner", "actionRunner.vcxproj" },
                new[] { "src", "runner", "Kit.vcxproj" },
                new[] { "src", "Update", "PowerToys.Update.vcxproj" },
            };

            foreach (var pathParts in projectPaths)
            {
                var project = File.ReadAllText(FindSourceFile(pathParts));
                StringAssert.Contains(project, "convert-resx-to-rc.ps1");
                StringAssert.Contains(project, @"&quot;$(RepoRoot)tools\build\convert-resx-to-rc.ps1&quot;");
                StringAssert.Contains(project, "-ExecutionPolicy Bypass");
                StringAssert.Contains(project, " -File ");
                StringAssert.Contains(project, @"&quot;$(MSBuildThisFileDirectory).&quot;");
                Assert.IsFalse(project.Contains("-executionpolicy Unrestricted", StringComparison.OrdinalIgnoreCase), $"{Path.Combine(pathParts)} should not use Unrestricted direct script execution for resource generation.");
                Assert.IsFalse(project.Contains(@"&quot;$(MSBuildThisFileDirectory)&quot;", StringComparison.Ordinal), $"{Path.Combine(pathParts)} should avoid quoting a directory property that ends with a trailing slash.");
            }
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
                "FileExplorerPreview",
                "FileLocksmith",
                "FilePicker_ZoomIt",
                "GPO_AdvancedPasteAi",
                "Hosts",
                "ImageResizer",
                "KeyboardManager",
                "Launch_ColorPicker",
                "Launch_ShortcutGuide",
                "Launch_Hosts",
                "LearnMore_AdvancedPaste",
                "LearnMore_AlwaysOnTop",
                "LearnMore_CmdPal",
                "LearnMore_ColorPicker",
                "LearnMore_CropAndLock",
                "LearnMore_EnvironmentVariables",
                "LearnMore_FancyZones",
                "LearnMore_FileLocksmith",
                "LearnMore_GrabAndMove",
                "LearnMore_Hosts",
                "LearnMore_ImageResizer",
                "LearnMore_KeyboardManager",
                "LearnMore_MeasureTool",
                "LearnMore_MouseUtils",
                "LearnMore_MouseWithoutBorders",
                "LearnMore_MouseUtilities",
                "LearnMore_Peek",
                "LearnMore_PowerPreview",
                "LearnMore_RegistryPreview",
                "LearnMore_PowerRename",
                "LearnMore_Run",
                "LearnMore_ShortcutGuide",
                "LearnMore_Workspaces",
                "LearnMore_ZoomIt",
                "MeasureTool",
                "MouseWithoutBorders",
                "MouseUtils",
                "MWB_",
                "NewPlus",
                "OOBE_",
                "Oobe",
                "OobeWindow",
                "Peek",
                "PowerLauncher",
                "PowerPreview",
                "PowerRename",
                "RegistryPreview",
                "Radio_ShortcutGuide",
                "Run_",
                "Run_CheckOutCmdPal",
                "Run_NavigateCmdPalSettings",
                "Shell_AlwaysOnTop",
                "Shell_AdvancedPaste",
                "Shell_CmdPal",
                "Shell_ColorPicker",
                "Shell_CropAndLock",
                "Shell_EnvironmentVariables",
                "Shell_FancyZones",
                "Shell_FileLocksmith",
                "Shell_GrabAndMove",
                "Shell_Hosts",
                "Shell_ImageResizer",
                "Shell_KeyboardManager",
                "Shell_MeasureTool",
                "Shell_MouseUtilities",
                "Shell_MouseWithoutBorders",
                "Shell_Peek",
                "Shell_PowerLauncher",
                "Shell_PowerPreview",
                "Shell_RegistryPreview",
                "Shell_PowerRename",
                "Shell_ShortcutGuide",
                "Shell_Workspaces",
                "Shell_ZoomIt",
                "ShortcutGuide",
                "ScreenRuler",
                "Scoobe",
                "ScoobeWindow",
                "Workspaces",
                "ZoomIt",
            };

            foreach (var prefix in inactiveResourceNamePrefixes)
            {
                Assert.IsFalse(resources.Contains($"name=\"{prefix}", StringComparison.Ordinal), $"Inactive Settings resource prefix should be deleted: {prefix}");
            }

            StringAssert.Contains(resources, "<value>To access settings, run the Kit executable again</value>");
            Assert.IsFalse(resources.Contains("PowerToys executable", StringComparison.Ordinal), "Settings resources should not direct users back to the upstream PowerToys executable.");
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
        public void KitShouldDeleteInactivePreviewAndCmdPalSharedAssets()
        {
            var solutionPath = FindSourceFile("Kit.slnx");
            var repoRoot = Path.GetDirectoryName(solutionPath);
            var solution = File.ReadAllText(solutionPath);
            var centralPackages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));
            var notice = File.ReadAllText(FindSourceFile("NOTICE.md"));
            var loggerSettings = File.ReadAllText(FindSourceFile("src", "common", "logger", "logger_settings.h"));
            var commonUtilsTestsProject = File.ReadAllText(FindSourceFile("src", "common", "UnitTests-CommonUtils", "UnitTests-CommonUtils.vcxproj"));
            var commonUtilsTestsFilters = File.ReadAllText(FindSourceFile("src", "common", "UnitTests-CommonUtils", "UnitTests-CommonUtils.vcxproj.filters"));

            string[] inactiveFiles =
            {
                Path.Combine("src", "common", "CalculatorEngineCommon", "CalculatorEngineCommon.vcxproj"),
                Path.Combine("src", "common", "CalculatorEngineCommon", "Calculator.cpp"),
                Path.Combine("src", "common", "FilePreviewCommon", "FilePreviewCommon.csproj"),
                Path.Combine("src", "common", "FilePreviewCommon", "MonacoHelper.cs"),
                Path.Combine("src", "Monaco", "index.html"),
                Path.Combine("src", "Monaco", "monacoSRC", "min", "vs", "loader.js"),
                Path.Combine("src", "Monaco.props"),
                Path.Combine("src", "common", "utils", "shell_ext_registration.h"),
                Path.Combine("src", "common", "utils", "modulesRegistry.h"),
                Path.Combine("src", "common", "UnitTests-CommonUtils", "ModulesRegistry.Tests.cpp"),
            };

            foreach (var relativePath in inactiveFiles)
            {
                Assert.IsFalse(File.Exists(Path.Combine(repoRoot!, relativePath)), $"Kit should delete inactive upstream shared source file: {relativePath}");
            }

            Assert.IsFalse(solution.Contains("src/Monaco.props", StringComparison.Ordinal), "Kit.slnx should not list deleted Monaco assets as solution items.");
            Assert.IsFalse(commonUtilsTestsProject.Contains("ModulesRegistry.Tests.cpp", StringComparison.Ordinal), "CommonUtils tests should not build deleted File Explorer shell-extension registry tests.");
            Assert.IsFalse(commonUtilsTestsFilters.Contains("ModulesRegistry.Tests.cpp", StringComparison.Ordinal), "CommonUtils test filters should not list deleted File Explorer shell-extension registry tests.");
            Assert.IsFalse(centralPackages.Contains(@"PackageVersion Include=""UTF.Unknown""", StringComparison.Ordinal), "Kit should not keep the FilePreviewCommon-only UTF.Unknown central package pin.");

            string[] inactiveLoggerTokens =
            {
                "fileExplorerLoggerName",
                "fileExplorerLogPath",
                "launcherLoggerName",
                "launcherLogPath",
                "mouseWithoutBordersLoggerName",
                "mouseWithoutBordersLogPath",
                "powerAccentLogPath",
                "fancyZonesLoggerName",
                "fancyZonesLogPath",
                "fancyZonesOldLogPath",
                "shortcutGuideLoggerName",
                "shortcutGuideLogPath",
                "powerOcrLogPath",
                "keyboardManagerLoggerName",
                "keyboardManagerLogPath",
                "findMyMouseLoggerName",
                "mouseHighlighterLoggerName",
                "mouseJumpLoggerName",
                "mousePointerCrosshairsLoggerName",
                "cursorWrapLoggerName",
                "imageResizerLoggerName",
                "powerRenameLoggerName",
                "alwaysOnTopLoggerName",
                "powerOcrLoggerName",
                "fileLocksmithLoggerName",
                "alwaysOnTopLogPath",
                "hostsLoggerName",
                "hostsLogPath",
                "registryPreviewLoggerName",
                "registryPreviewLogPath",
                "environmentVariablesLoggerName",
                "cmdNotFoundLogPath",
                "cmdNotFoundLoggerName",
                "newLoggerName",
                "workspacesLauncherLoggerName",
                "workspacesLauncherLogPath",
                "workspacesWindowArrangerLoggerName",
                "workspacesWindowArrangerLogPath",
                "workspacesSnapshotToolLoggerName",
                "workspacesSnapshotToolLogPath",
                "zoomItLoggerName",
                "grabAndMoveLoggerName",
                "GcodePrevHandler",
                "GcodeThumbnailProvider",
                "bgcodePrevHandler",
                "BgcodeThumbnailProvider",
                "MDPrevHandler",
                "MonacoPrevHandler",
                "PdfPrevHandler",
                "PdfThumbnailProvider",
                "QoiPrevHandler",
                "QoiThumbnailProvider",
                "StlThumbnailProvider",
                "SvgPrevHandler",
                "SvgThumbnailProvider",
                "FileExplorer_localLow",
            };

            foreach (var token in inactiveLoggerTokens)
            {
                Assert.IsFalse(loggerSettings.Contains(token, StringComparison.Ordinal), $"Logger settings should not keep deleted File Explorer add-in token: {token}");
            }

            StringAssert.Contains(loggerSettings, "awakeLoggerName");
            var awakeModule = File.ReadAllText(FindSourceFile("src", "modules", "awake", "AwakeModuleInterface", "dllmain.cpp"));
            Assert.IsFalse(awakeModule.Contains("launcherLoggerName", StringComparison.Ordinal), "Awake should not initialize its active module logger with the deleted launcher logger name.");
            Assert.IsFalse(awakeModule.Contains("Launcher object is constructing", StringComparison.Ordinal), "Awake startup logging should not use stale Launcher wording.");

            string[] inactiveNoticeTokens =
            {
                "- Command Palette",
                "- File Explorer Add-ins",
                "- Peek",
                "## Utility: Command palette built-in extensions",
                "## Utility: File Explorer add-ins",
                "## Utility: Peek",
                "#### exprtk",
                "### Monaco Editor",
                "### The Quite OK image format reference decoder",
                "UTF.Unknown",
                "phoboslab/qoi",
                "CharsetDetector/UTF-unknown",
            };

            foreach (var token in inactiveNoticeTokens)
            {
                Assert.IsFalse(notice.Contains(token, StringComparison.Ordinal), $"Third-party notices should not keep deleted Preview/Peek/CmdPal dependency token: {token}");
            }
        }

        [TestMethod]
        public void KitQuickAccessFlyoutShouldOpenSettingsForModulesWithoutDirectActions()
        {
            var launcherViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "ViewModels", "LauncherViewModel.cs"));
            var coordinatorInterface = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "Services", "IQuickAccessCoordinator.cs"));
            var coordinator = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "Services", "QuickAccessCoordinator.cs"));
            var allAppsViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "ViewModels", "AllAppsViewModel.cs"));
            var settingsDeepLink = File.ReadAllText(FindSourceFile("src", "common", "Common.UI", "SettingsDeepLink.cs"));

            StringAssert.Contains(launcherViewModel, "fallbackLauncher: OpenModuleSettings");
            StringAssert.Contains(launcherViewModel, "private bool OpenModuleSettings(ModuleType moduleType)");
            StringAssert.Contains(coordinatorInterface, "void OpenModuleSettings(ModuleType moduleType);");
            StringAssert.Contains(coordinator, "ModuleType.Monitor => SettingsDeepLink.SettingsWindow.Monitor");
            StringAssert.Contains(settingsDeepLink, "Monitor,");
            StringAssert.Contains(settingsDeepLink, "return \"Monitor\";");
            Assert.IsFalse(coordinator.Contains("PowerDisplay", StringComparison.Ordinal), "Quick Access should not route to removed PowerDisplay settings.");
            Assert.IsFalse(settingsDeepLink.Contains("PowerDisplay", StringComparison.Ordinal), "Settings deep links should not expose removed PowerDisplay windows.");
            StringAssert.Contains(allAppsViewModel, "if (!_coordinator.UpdateModuleEnabled(flyoutItem.Tag, flyoutItem.IsEnabled))");
            StringAssert.Contains(allAppsViewModel, "flyoutItem.UpdateStatus(!isEnabled)");
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
                "PowerDisplay",
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

            StringAssert.Contains(settingsDeepLink, "\"Kit.exe\"");
            StringAssert.Contains(settingsDeepLink, "PowerToysPathResolver.GetKitInstallPath()");
            Assert.IsFalse(settingsDeepLink.Contains("GetPowerToysInstallPath()", StringComparison.Ordinal), "Common Settings deep links should use the Kit-only install resolver.");
            Assert.IsFalse(settingsDeepLink.Contains("\"PowerToys.exe\"", StringComparison.Ordinal), "Common Settings deep links should launch Kit.exe, not the upstream runner.");
            Assert.IsFalse(settingsDeepLink.Contains("Kit or PowerToys exe path", StringComparison.Ordinal), "Common Settings deep-link logging should not describe PowerToys.exe as a supported fallback.");
        }

        [TestMethod]
        public void KitSettingsShouldNotRegisterRemovedPowerDisplaySerializationAndModels()
        {
            var settingsProject = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "PowerToys.Settings.csproj"));
            var settingsLibraryProject = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "Settings.UI.Library.csproj"));
            var serializationContext = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "SettingsSerializationContext.cs"));

            Assert.IsFalse(settingsProject.Contains(@"..\..\modules\powerdisplay", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(settingsLibraryProject.Contains(@"..\..\modules\powerdisplay", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(settingsLibraryProject.Contains(@"<Compile Remove=""MonitorInfo.cs""", StringComparison.Ordinal));

            StringAssert.Contains(serializationContext, "[JsonSerializable(typeof(MonitorInfo))]");
            StringAssert.Contains(serializationContext, "[JsonSerializable(typeof(List<MonitorInfo>))]");
            Assert.IsFalse(serializationContext.Contains("PowerDisplay", StringComparison.Ordinal), "Settings serialization context should not register removed PowerDisplay models.");
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
            StringAssert.Contains(quickAccessLauncher, "ModuleType.LightSwitch");
            Assert.IsFalse(quickAccessViewModel.Contains("ModuleType.PowerDisplay", StringComparison.Ordinal), "Quick Access should not include removed PowerDisplay.");
            Assert.IsFalse(quickAccessLauncher.Contains("ModuleType.PowerDisplay", StringComparison.Ordinal), "Quick Access launcher should not include removed PowerDisplay.");
        }

        [TestMethod]
        public void KitQuickAccessShouldNotKeepUnusedElevationState()
        {
            var controlsLauncher = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Controls", "QuickAccess", "QuickAccessLauncher.cs"));
            var flyoutLauncher = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "Services", "QuickAccessLauncher.cs"));
            var coordinatorInterface = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "Services", "IQuickAccessCoordinator.cs"));
            var coordinator = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "Services", "QuickAccessCoordinator.cs"));
            var dashboardViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "DashboardViewModel.cs"));

            StringAssert.Contains(controlsLauncher, "ModuleType.LightSwitch");
            Assert.IsFalse(controlsLauncher.Contains("ModuleType.PowerDisplay", StringComparison.Ordinal), "Quick Access direct launch actions should not include removed PowerDisplay.");
            Assert.IsFalse(controlsLauncher.Contains("_isElevated", StringComparison.Ordinal), "Kit Quick Access direct launch actions do not use elevation state and should not keep a dead field.");
            Assert.IsFalse(controlsLauncher.Contains("QuickAccessLauncher(bool isElevated)", StringComparison.Ordinal), "Kit Quick Access launcher should not require an unused elevation argument.");
            Assert.IsFalse(flyoutLauncher.Contains("IsRunnerElevated", StringComparison.Ordinal), "Quick Access flyout should not depend on a coordinator elevation property that is never wired.");
            Assert.IsFalse(coordinatorInterface.Contains("IsRunnerElevated", StringComparison.Ordinal), "Quick Access coordinator contract should not expose an unused runner elevation state.");
            Assert.IsFalse(coordinator.Contains("IsRunnerElevated", StringComparison.Ordinal), "Quick Access coordinator should not hard-code runner elevation state.");
            Assert.IsFalse(coordinator.Contains("TODO: wire up real elevation state", StringComparison.Ordinal), "Quick Access should not keep a TODO for a state path that active Kit actions do not use.");
            Assert.IsFalse(dashboardViewModel.Contains("new QuickAccessLauncher(App.IsElevated)", StringComparison.Ordinal), "Settings dashboard Quick Access should not pass an unused elevation state.");
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
            Assert.IsFalse(settingsFactory.Contains("PowerDisplaySettings.ModuleName", StringComparison.Ordinal), "SettingsFactory should not resolve removed PowerDisplay hotkey settings.");

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
        public void KitRunnerShouldHonorQuickAccessSettingAndUpdateToastBoundary()
        {
            var generalSettings = File.ReadAllText(FindSourceFile("src", "runner", "general_settings.cpp"));
            var updateUtils = File.ReadAllText(FindSourceFile("src", "runner", "UpdateUtils.cpp"));
            var normalizedGeneralSettings = NormalizeLineEndings(generalSettings);

            StringAssert.Contains(generalSettings, "enable_quick_access = loaded.GetNamedBoolean(L\"enable_quick_access\", false);");
            StringAssert.Contains(generalSettings, "general_configs.GetNamedBoolean(L\"enable_quick_access\", enable_quick_access)");
            Assert.IsFalse(normalizedGeneralSettings.Contains("\n    enable_quick_access = false;\n", StringComparison.Ordinal), "Runner should not force Quick Access off while Settings exposes the toggle.");
            Assert.IsFalse(generalSettings.Contains("bool new_enable_quick_access = false;", StringComparison.Ordinal), "Runner should apply the Settings-provided Quick Access value.");
            StringAssert.Contains(updateUtils, "get_general_settings().showNewUpdatesToastNotification");
            StringAssert.Contains(updateUtils, "mode == UpdateCheckMode::Periodic && !alreadyNotified && get_general_settings().showNewUpdatesToastNotification");
        }

        [TestMethod]
        public void AwakeDestroyShouldSignalChildShutdown()
        {
            var awakeModule = File.ReadAllText(FindSourceFile("src", "modules", "awake", "AwakeModuleInterface", "dllmain.cpp"));

            StringAssert.Contains(awakeModule, "virtual void destroy() override");
            StringAssert.Contains(awakeModule, "disable();");
            StringAssert.Contains(awakeModule, "WaitForSingleObject(p_info.hProcess");
            StringAssert.Contains(awakeModule, "CloseHandle(p_info.hThread)");
            StringAssert.Contains(awakeModule, "void terminate_process_if_running()");
            StringAssert.Contains(awakeModule, "if (!exitEvent)");
            StringAssert.Contains(awakeModule, "terminate_process_if_running();");
            StringAssert.Contains(awakeModule, "close_process_handles();");
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
            Assert.IsFalse(quickAccessGpoHelper.Contains("PowerDisplay", StringComparison.Ordinal), "Quick Access GPO helper should not include removed PowerDisplay policy.");

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
            Assert.IsFalse(HasGpoBranch(gpoConfiguration, "PowerDisplay"), "Settings GPO helper should not expose removed PowerDisplay.");
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
        public void KitSettingsLaunchFailureShouldNotLeaveLaunchInProgress()
        {
            var settingsWindow = NormalizeLineEndings(File.ReadAllText(FindSourceFile("src", "runner", "settings_window.cpp")));

            StringAssert.Contains(settingsWindow, "g_isLaunchInProgress.compare_exchange_strong");
            StringAssert.Contains(settingsWindow, "if (!CreateProcessW(executable_path.c_str(),");
            StringAssert.Contains(settingsWindow, "g_isLaunchInProgress = false;\n            goto LExit;");
        }

        [TestMethod]
        public void KitSettingsLaunchGuardShouldStaySetUntilProcessIsRegistered()
        {
            var settingsWindow = NormalizeLineEndings(File.ReadAllText(FindSourceFile("src", "runner", "settings_window.cpp")));

            var createProcessSuccess = settingsWindow.IndexOf("if (!CreateProcessW(executable_path.c_str(),", StringComparison.Ordinal);
            var openSettingsWindow = settingsWindow.IndexOf("void open_settings_window(std::optional<std::wstring> settings_window)", StringComparison.Ordinal);
            var closeSettingsWindow = settingsWindow.IndexOf("void close_settings_window()", openSettingsWindow, StringComparison.Ordinal);
            var runSettingsWindow = settingsWindow.IndexOf("void run_settings_window(std::optional<std::wstring> settings_window)", StringComparison.Ordinal);
            var openToken = settingsWindow.IndexOf("if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &hToken))", StringComparison.Ordinal);
            var ipcStart = settingsWindow.IndexOf("current_settings_ipc->start(hToken);", StringComparison.Ordinal);
            var processIdAssignment = settingsWindow.IndexOf("g_settings_process_id = process_info.dwProcessId;", StringComparison.Ordinal);
            var processIdAfterCreateProcess = settingsWindow.IndexOf("g_settings_process_id = process_info.dwProcessId;", createProcessSuccess, StringComparison.Ordinal);
            var launchGuardRelease = settingsWindow.IndexOf("g_isLaunchInProgress = false;", processIdAfterCreateProcess, StringComparison.Ordinal);

            Assert.AreNotEqual(-1, createProcessSuccess, "Settings launch should still use direct CreateProcessW.");
            Assert.AreNotEqual(-1, openSettingsWindow, "Settings launch should keep the open_settings_window entry point.");
            Assert.AreNotEqual(-1, closeSettingsWindow, "Settings launch should keep close_settings_window after open_settings_window.");
            Assert.AreNotEqual(-1, runSettingsWindow, "Settings launch should keep the run_settings_window worker.");
            Assert.AreNotEqual(-1, openToken, "Settings launch should open the runner token before IPC setup.");
            Assert.AreNotEqual(-1, ipcStart, "Settings launch should start runner/settings IPC.");
            Assert.AreNotEqual(-1, processIdAssignment, "Settings launch should register the Settings process id.");
            Assert.AreNotEqual(-1, processIdAfterCreateProcess, "Settings launch should register the Settings process id after CreateProcessW succeeds.");
            Assert.AreNotEqual(-1, launchGuardRelease, "Settings launch should release the launch-in-progress guard after registration.");

            var openWindowBody = settingsWindow.Substring(openSettingsWindow, closeSettingsWindow - openSettingsWindow);
            StringAssert.Contains(openWindowBody, "compare_exchange_strong");
            StringAssert.Contains(openWindowBody, "g_isLaunchInProgress");

            var launchSetupWindow = settingsWindow.Substring(openToken, processIdAssignment - openToken);
            Assert.IsFalse(launchSetupWindow.Contains("g_isLaunchInProgress = false;", StringComparison.Ordinal), "Settings launch guard should stay held throughout the CreateProcess success to process-id registration window.");
            Assert.IsTrue(processIdAssignment < launchGuardRelease, "Settings launch guard should not be released before g_settings_process_id is set.");
            Assert.IsTrue(ipcStart < launchGuardRelease, "Settings launch guard should not be released before IPC is ready for a follow-up ShowYourself request.");
            Assert.IsFalse(settingsWindow.Contains("else\n        {\n            g_isLaunchInProgress = false;\n        }", StringComparison.Ordinal), "Settings launch should not clear the guard immediately after CreateProcessW succeeds.");
        }

        [TestMethod]
        public void KitSettingsLaunchShouldTerminateCreatedProcessWhenIpcSetupFails()
        {
            var settingsWindow = NormalizeLineEndings(File.ReadAllText(FindSourceFile("src", "runner", "settings_window.cpp")));

            StringAssert.Contains(settingsWindow, "void terminate_created_settings_process(PROCESS_INFORMATION& process_info)");
            StringAssert.Contains(settingsWindow, "SetEvent(g_terminateSettingsEvent);");
            StringAssert.Contains(settingsWindow, "constexpr DWORD timeout_ms = 1500;");
            StringAssert.Contains(settingsWindow, "WaitForSingleObject(process_info.hProcess, timeout_ms)");
            StringAssert.Contains(settingsWindow, "TerminateProcess(process_info.hProcess, 0)");
            StringAssert.Contains(settingsWindow, "ResetEvent(g_terminateSettingsEvent)");

            var openTokenFailure = settingsWindow.IndexOf("if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &hToken))", StringComparison.Ordinal);
            var cleanupCall = settingsWindow.IndexOf("terminate_created_settings_process(process_info);", openTokenFailure, StringComparison.Ordinal);
            var exitJump = settingsWindow.IndexOf("goto LExit;", openTokenFailure, StringComparison.Ordinal);

            Assert.AreNotEqual(-1, openTokenFailure, "Settings launch should keep an OpenProcessToken failure branch.");
            Assert.AreNotEqual(-1, cleanupCall, "Settings launch should terminate a created Settings child if runner IPC setup cannot continue.");
            Assert.AreNotEqual(-1, exitJump, "Settings launch should still exit after IPC setup failure.");
            Assert.IsTrue(cleanupCall < exitJump, "Settings launch should terminate the created Settings process before leaving the failure path.");
        }

        [TestMethod]
        public void KitSettingsLaunchShouldCleanUpWhenIpcStartThrows()
        {
            var settingsWindow = NormalizeLineEndings(File.ReadAllText(FindSourceFile("src", "runner", "settings_window.cpp")));

            var ipcStart = settingsWindow.IndexOf("current_settings_ipc->start(hToken);", StringComparison.Ordinal);
            var processIdAssignment = settingsWindow.IndexOf("g_settings_process_id = process_info.dwProcessId;", ipcStart, StringComparison.Ordinal);
            Assert.AreNotEqual(-1, ipcStart, "Settings launch should still start runner/settings IPC.");
            Assert.AreNotEqual(-1, processIdAssignment, "Settings launch should register the Settings process id after IPC setup.");

            var ipcSetupWindow = settingsWindow.Substring(ipcStart, processIdAssignment - ipcStart);
            StringAssert.Contains(ipcSetupWindow, "catch (const std::exception& ex)");
            StringAssert.Contains(ipcSetupWindow, "catch (...)");
            StringAssert.Contains(ipcSetupWindow, "end_settings_ipc();");
            StringAssert.Contains(ipcSetupWindow, "terminate_created_settings_process(process_info);");
            StringAssert.Contains(ipcSetupWindow, "goto LExit;");
        }

        [TestMethod]
        public void KitTwoWayPipeIpcEndShouldToleratePartialStart()
        {
            var twoWayPipeIpc = NormalizeLineEndings(File.ReadAllText(FindSourceFile("src", "common", "interop", "two_way_pipe_message_ipc.cpp")));

            StringAssert.Contains(twoWayPipeIpc, "if (input_queue_thread.joinable())");
            StringAssert.Contains(twoWayPipeIpc, "if (output_queue_thread.joinable())");
            StringAssert.Contains(twoWayPipeIpc, "if (input_pipe_thread.joinable())");
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
        public void KitSparsePackageIdentityShouldNotDeclareDeletedModuleApps()
        {
            var solution = File.ReadAllText(FindSourceFile("Kit.slnx"));
            var versionProps = File.ReadAllText(FindSourceFile("src", "Version.props"));
            var manifest = File.ReadAllText(FindSourceFile("src", "PackageIdentity", "AppxManifest.xml"));
            var readme = File.ReadAllText(FindSourceFile("src", "PackageIdentity", "readme.md"));
            var directoryPackages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));
            var buildScript = File.ReadAllText(FindSourceFile("src", "PackageIdentity", "BuildSparsePackage.ps1"));
            var certSignPackageScript = File.ReadAllText(FindSourceFile("tools", "build", "cert-sign-package.ps1"));
            var selfSignScript = File.ReadAllText(FindSourceFile("tools", "build", "self-sign.ps1"));
            var certManagementScript = File.ReadAllText(FindSourceFile("tools", "build", "cert-management.ps1"));
            var buildCommonScript = File.ReadAllText(FindSourceFile("tools", "build", "build-common.ps1"));
            var quickBuildScript = File.ReadAllText(FindSourceFile("tools", "build", "build.ps1"));
            var essentialsBuildScript = File.ReadAllText(FindSourceFile("tools", "build", "build-essentials.ps1"));

            StringAssert.Contains(solution, "src/PackageIdentity/PackageIdentity.vcxproj");
            StringAssert.Contains(solution, "<BuildDependency Project=\"src/PackageIdentity/PackageIdentity.vcxproj\" />");
            StringAssert.Contains(versionProps, "<Version>2.0.5</Version>");
            StringAssert.Contains(manifest, "Version=\"2.0.5.0\"");
            StringAssert.Contains(readme, "Debug builds use `-NoSign`");
            StringAssert.Contains(manifest, "Local.Kit.SparseApp");
            StringAssert.Contains(manifest, "Kit.SparseApp");
            StringAssert.Contains(manifest, "Kit.SettingsUI");
            Assert.IsFalse(manifest.Contains("Microsoft.PowerToys.SparseApp", StringComparison.Ordinal), "Kit sparse package identity should not collide with official PowerToys sparse registration.");
            Assert.IsFalse(manifest.Contains("PowerToys.SparseApp", StringComparison.Ordinal), "Kit sparse package display name should not use the upstream sparse package name.");
            StringAssert.Contains(manifest, @"WinUI3Apps\PowerToys.Settings.exe");
            StringAssert.Contains(certSignPackageScript, "exit 1");
            StringAssert.Contains(certSignPackageScript, "$signedCount++");
            StringAssert.Contains(certSignPackageScript, "$LASTEXITCODE");
            StringAssert.Contains(certSignPackageScript, "Get-Command \"signtool\"");
            StringAssert.Contains(certSignPackageScript, "Windows Kits\\10\\bin");
            StringAssert.Contains(certSignPackageScript, "& $signToolPath sign");
            StringAssert.Contains(selfSignScript, "KitSparse.msix");
            StringAssert.Contains(selfSignScript, "Join-Path $directoryPath \"KitSparse.msix\"");
            StringAssert.Contains(selfSignScript, "[switch]$AllPackages");
            StringAssert.Contains(selfSignScript, "[switch]$RequireMachineRoot");
            StringAssert.Contains(selfSignScript, "exit 1");
            StringAssert.Contains(selfSignScript, "$signedCount++");
            StringAssert.Contains(selfSignScript, "$LASTEXITCODE");
            StringAssert.Contains(certManagementScript, "[switch]$RequireMachineRoot");
            StringAssert.Contains(certManagementScript, "Continuing with CurrentUser certificate trust");
            StringAssert.Contains(certManagementScript, "=== Kit Certificate Management ===");
            StringAssert.Contains(certManagementScript, "Kit-CodeSigning.cer");
            StringAssert.Contains(buildCommonScript, "[string[]]$ExtraArgs = @()");
            StringAssert.Contains(buildCommonScript, "$cmd = $base + $extra");
            StringAssert.Contains(buildCommonScript, "$script:MSBuildExe");
            StringAssert.Contains(buildCommonScript, "$preferredOrder = @('.sln', '.slnx', '.slnf', '.csproj', '.vcxproj')");
            StringAssert.Contains(buildCommonScript, "$solutionExtensions = @('.sln', '.slnx', '.slnf')");
            StringAssert.Contains(buildCommonScript, "$solutionExtensions -contains $f.Extension.ToLowerInvariant()");
            StringAssert.Contains(buildCommonScript, "$envVars['Path']");
            StringAssert.Contains(buildCommonScript, "$name -ieq 'Path'");
            StringAssert.Contains(buildCommonScript, "Visual Studio|MSBuild");
            StringAssert.Contains(buildCommonScript, "Normalize-ProcessPathEnvironment");
            StringAssert.Contains(buildCommonScript, "SetEnvironmentVariable('PATH', $null, 'Process')");
            StringAssert.Contains(buildCommonScript, "SetEnvironmentVariable('Path', $pathValue, 'Process')");
            StringAssert.Contains(buildCommonScript, "/nodeReuse:false");
            Assert.IsFalse(buildCommonScript.Contains("-split ' '", StringComparison.Ordinal), "Build helper should preserve MSBuild argument boundaries instead of splitting ExtraArgs on spaces.");
            Assert.IsFalse(quickBuildScript.Contains("$ExtraArgs -join ' '", StringComparison.Ordinal), "Build entry point should forward ExtraArgs as an array.");
            StringAssert.Contains(essentialsBuildScript, "$ExtraArgs = @(");
            StringAssert.Contains(essentialsBuildScript, "-ExtraArgs $ExtraArgs");
            StringAssert.Contains(quickBuildScript, "/p:PowerToysSkipCopyOnWriteSdk=true");
            StringAssert.Contains(quickBuildScript, "/p:PowerToysSkipRunVSTestSdk=true");
            StringAssert.Contains(quickBuildScript, "'^[/-](?:p|property):PowerToysSkipCopyOnWriteSdk='");
            StringAssert.Contains(quickBuildScript, "'^[/-](?:p|property):PowerToysSkipRunVSTestSdk='");
            StringAssert.Contains(essentialsBuildScript, "/p:PowerToysSkipCopyOnWriteSdk=true");
            StringAssert.Contains(essentialsBuildScript, "/p:PowerToysSkipRunVSTestSdk=true");
            StringAssert.Contains(certSignPackageScript, "[switch]$RequireMachineRoot");
            StringAssert.Contains(certSignPackageScript, @". ""$PSScriptRoot\cert-management.ps1"" -certSubject $certSubject -RequireMachineRoot:$RequireMachineRoot");
            StringAssert.Contains(certSignPackageScript, "-RequireMachineRoot:$RequireMachineRoot");
            StringAssert.Contains(certManagementScript, "[switch]$RequireMachineRoot");
            StringAssert.Contains(certManagementScript, "EnsureCertificate -certSubject $certSubject -RequireMachineRoot:$RequireMachineRoot");
            StringAssert.Contains(selfSignScript, "Current-user trust is used by default");
            StringAssert.Contains(buildScript, "IdentityName   = \"Local.Kit.SparseApp\"");
            StringAssert.Contains(buildScript, "SparseMsixName = \"KitSparse.msix\"");
            StringAssert.Contains(buildScript, "CertPrefix     = \"KitSparse\"");
            StringAssert.Contains(buildScript, "CertSubject    = 'CN=Kit Dev'");
            StringAssert.Contains(buildScript, "$versionPropsPath = Join-Path $KitRoot 'src\\Version.props'");
            Assert.IsFalse(buildScript.Contains("$PowerToysRoot", StringComparison.Ordinal), "Kit sparse package script should not reference an obsolete upstream root variable after identity renaming.");
            StringAssert.Contains(buildScript, "$currentPublisherHint = $script:Config.CertSubject");
            StringAssert.Contains(buildScript, "$registerManifestPath = Join-Path $UserFolder \"$($script:Config.CertPrefix).AppxManifest.xml\"");
            StringAssert.Contains(buildScript, "Copy-Item -Path $manifestStagingPath -Destination $registerManifestPath -Force");
            StringAssert.Contains(buildScript, "Add-AppxPackage -Register `\"$registerManifestPath`\"");
            StringAssert.Contains(certSignPackageScript, "[string]$certSubject = \"CN=Kit Dev\"");
            StringAssert.Contains(certManagementScript, "[string]$certSubject = \"CN=Kit Dev\"");
            StringAssert.Contains(selfSignScript, "$certSubject = \"CN=Kit Dev\"");
            StringAssert.Contains(readme, "- `-ForceCert` regenerates the local dev certificate (`.cer` and `.thumbprint`)");
            Assert.IsFalse(readme.Contains(".pfx/.cer/.pwd/.thumbprint", StringComparison.Ordinal), "PackageIdentity docs should not describe certificate artifacts that BuildSparsePackage no longer creates.");
            Assert.IsFalse(certSignPackageScript.Contains("CN=Microsoft Corporation, O=Microsoft Corporation", StringComparison.Ordinal), "Package signing helper should not mint local self-signed certs that look like the Microsoft publisher.");
            Assert.IsFalse(certManagementScript.Contains("CN=Microsoft Corporation, O=Microsoft Corporation", StringComparison.Ordinal), "Certificate helper should not default to the Microsoft publisher subject for local self-signing.");
            Assert.IsFalse(certManagementScript.Contains("PowerToys Certificate Management", StringComparison.Ordinal), "Certificate helper should not expose upstream branding in direct-run output.");
            Assert.IsFalse(certManagementScript.Contains("PowerToys-CodeSigning.cer", StringComparison.Ordinal), "Certificate helper should not export direct-run certificate artifacts under the upstream product name.");
            Assert.IsFalse(selfSignScript.Contains("CN=Microsoft Corporation, O=Microsoft Corporation", StringComparison.Ordinal), "self-sign should not default to the Microsoft publisher subject for local self-signing.");
            Assert.IsFalse(buildScript.Contains("Microsoft.PowerToys.SparseApp", StringComparison.Ordinal), "Sparse package build script should not register or remove the upstream sparse identity.");
            Assert.IsFalse(readme.Contains("Microsoft.PowerToys.SparseApp", StringComparison.Ordinal), "Sparse package docs should not instruct users to register or remove the upstream sparse identity.");
            Assert.IsFalse(buildScript.IndexOf("$currentPublisherHint = $script:Config.CertSubject", StringComparison.Ordinal) < buildScript.IndexOf("$script:Config = @", StringComparison.Ordinal), "BuildSparsePackage should not read Config.CertSubject before Config is initialized.");
            Assert.IsFalse(directoryPackages.Contains("Microsoft.VariantAssignment", StringComparison.Ordinal), "Kit should not keep experimentation package pins without active project references.");
            Assert.IsFalse(directoryPackages.Contains("IsExperimentationLive", StringComparison.Ordinal), "Kit should not keep an unused experimentation package pin condition.");
            Assert.IsFalse(certSignPackageScript.Contains("& signtool sign", StringComparison.Ordinal), "Package signing helper should use resolved SignTool path instead of assuming PATH contains signtool.");
            Assert.IsFalse(NormalizeLineEndings(certManagementScript).Contains("else {\n        if (-not (ImportAndVerifyCertificate -cerPath $cerPath -storePath \"Cert:\\LocalMachine\\Root\"))", StringComparison.Ordinal), "LocalMachine Root trust should not be attempted during normal current-user development signing.");
            StringAssert.Contains(NormalizeLineEndings(selfSignScript), "if ($RequireMachineRoot) {\n    if (-not (Import-And-VerifyCertificate -cerPath $cerPath -storePath \"Cert:\\LocalMachine\\Root\"))");
            Assert.IsFalse(NormalizeLineEndings(selfSignScript).Contains("Get-ChildItem -Path $directoryPath -Recurse | Where-Object {\n    $_.Extension -eq \".msix\" -or $_.Extension -eq \".appx\"\n}\n\nif ($filePaths.Count -eq 0)", StringComparison.Ordinal), "self-sign should not recursively sign every package by default.");

            string[] inactiveSparseIdentityTokens =
            {
                "PowerToys.OCR",
                "PowerToys.PowerOCR.exe",
                "systemAIModels",
                "PowerToys.ImageResizerUI",
                "PowerToys.ImageResizer.exe",
                "PowerToys.ImageResizer",
                "PowerToys.CmdPalExtension",
                "Microsoft.CmdPal.Ext.PowerToys.exe",
                "PowerToys.CommandPaletteExtension",
                "com.microsoft.commandpalette",
                "CmdPalProvider",
            };

            foreach (var inactiveSparseIdentityToken in inactiveSparseIdentityTokens)
            {
                Assert.IsFalse(manifest.Contains(inactiveSparseIdentityToken, StringComparison.Ordinal), $"PackageIdentity manifest should not declare inactive sparse package app identity: {inactiveSparseIdentityToken}");
            }

            string[] inactiveReadmeExamples =
            {
                "PowerOCR",
                "Image Resizer",
                "imageresizer",
                "Command Palette",
                "CmdPal",
                "Microsoft.CmdPal",
                @"C:\PowerToys",
                "C:/git/PowerToys",
            };

            foreach (var inactiveReadmeExample in inactiveReadmeExamples)
            {
                Assert.IsFalse(readme.Contains(inactiveReadmeExample, StringComparison.OrdinalIgnoreCase), $"PackageIdentity docs should not use inactive sparse package examples: {inactiveReadmeExample}");
                Assert.IsFalse(buildScript.Contains(inactiveReadmeExample, StringComparison.OrdinalIgnoreCase), $"PackageIdentity build script comments should not use inactive sparse package examples: {inactiveReadmeExample}");
                Assert.IsFalse(certSignPackageScript.Contains(inactiveReadmeExample, StringComparison.OrdinalIgnoreCase), $"Package signing helper should not default to inactive sparse package examples: {inactiveReadmeExample}");
                Assert.IsFalse(selfSignScript.Contains(inactiveReadmeExample, StringComparison.OrdinalIgnoreCase), $"Package signing helper should not default to inactive sparse package examples: {inactiveReadmeExample}");
            }
        }

        [TestMethod]
        public void KitUiTestAutomationShouldOnlyCarryActiveKitModuleLaunchTargets()
        {
            var moduleConfigData = File.ReadAllText(FindSourceFile("src", "common", "UITestAutomation", "ModuleConfigData.cs"));
            var session = File.ReadAllText(FindSourceFile("src", "common", "UITestAutomation", "Session.cs"));
            var sessionHelper = File.ReadAllText(FindSourceFile("src", "common", "UITestAutomation", "SessionHelper.cs"));
            var uiTestBase = File.ReadAllText(FindSourceFile("src", "common", "UITestAutomation", "UITestBase.cs"));
            var kitProcessCleanup = File.ReadAllText(FindSourceFile("src", "common", "UITestAutomation", "KitProcessCleanup.cs"));
            var settingsConfigHelper = File.ReadAllText(FindSourceFile("src", "common", "UITestAutomation", "SettingsConfigHelper.cs"));
            var textBox = File.ReadAllText(FindSourceFile("src", "common", "UITestAutomation", "Element", "TextBox.cs"));
            var lightSwitchUiTestsProject = File.ReadAllText(FindSourceFile("src", "modules", "LightSwitch", "Tests", "LightSwitch.UITests", "LightSwitch.UITests.csproj"));

            StringAssert.Contains(moduleConfigData, "PowerToysSettings");
            StringAssert.Contains(moduleConfigData, "Runner");
            StringAssert.Contains(moduleConfigData, "Awake");
            StringAssert.Contains(moduleConfigData, "LightSwitch");
            StringAssert.Contains(moduleConfigData, "Monitor");
            StringAssert.Contains(moduleConfigData, "Kit.exe");
            StringAssert.Contains(moduleConfigData, "PowerToys.Settings.exe");
            StringAssert.Contains(moduleConfigData, "PowerToys.Awake.exe");
            StringAssert.Contains(moduleConfigData, "PowerToys.LightSwitchService.exe");
            StringAssert.Contains(moduleConfigData, "PowerToys.Monitor.exe");
            Assert.IsFalse(moduleConfigData.Contains("PowerDisplay", StringComparison.Ordinal), "UITestAutomation should not carry removed PowerDisplay launch targets.");
            Assert.IsFalse(moduleConfigData.Contains("PowerToys.PowerDisplay.exe", StringComparison.Ordinal), "UITestAutomation should not launch removed PowerDisplay.");
            StringAssert.Contains(moduleConfigData, "C:\\Program Files\\Kit");
            StringAssert.Contains(moduleConfigData, "C:\\Program Files (x86)\\Kit");
            StringAssert.Contains(moduleConfigData, "%LocalAppData%\\Kit");
            StringAssert.Contains(lightSwitchUiTestsProject, @"common\UITestAutomation\UITestAutomation.csproj");
            StringAssert.Contains(sessionHelper, "Kit Settings");
            StringAssert.Contains(sessionHelper, "KillKitProcesses");
            StringAssert.Contains(sessionHelper, "KitProcessCleanup.KillByExecutablePath");
            StringAssert.Contains(sessionHelper, "KitProcessCleanup.KillKnownKitProcesses");
            StringAssert.Contains(uiTestBase, "KitProcessCleanup.KillKnownKitProcesses");
            StringAssert.Contains(kitProcessCleanup, "CleanupResult");
            StringAssert.Contains(kitProcessCleanup, "HandledAnyProcess");
            StringAssert.Contains(kitProcessCleanup, "KillKnownKitProcessesByName");
            StringAssert.Contains(kitProcessCleanup, "KilledAnyProcess");
            StringAssert.Contains(kitProcessCleanup, "FailedAnyProcess");
            StringAssert.Contains(kitProcessCleanup, "Console.WriteLine($\"[KitProcessCleanup] Failed to terminate process");
            StringAssert.Contains(kitProcessCleanup, "ModuleConfigData.Instance.GetModulePath");
            StringAssert.Contains(kitProcessCleanup, "process.MainModule?.FileName");
            StringAssert.Contains(kitProcessCleanup, "PathMatches");
            StringAssert.Contains(session, "cleanupResult.MatchedKnownExecutable");

            string[] inactiveModuleConfigTokens =
            {
                "FancyZone",
                "Hosts",
                "Workspaces",
                "PowerRename",
                "CommandPalette",
                "ScreenRuler",
                "PowerToys.FancyZonesEditor.exe",
                "PowerToys.Hosts.exe",
                "PowerToys.WorkspacesEditor.exe",
                "PowerToys.PowerRename.exe",
                "PowerToys.LightSwitch.exe",
                "Microsoft.CmdPal.UI.exe",
                "PowerToys.MeasureToolUI.exe",
                "\"PowerToys.exe\"",
                "C:\\Program Files\\PowerToys",
                "C:\\Program Files (x86)\\PowerToys",
                "%LocalAppData%\\PowerToys",
            };

            foreach (var inactiveToken in inactiveModuleConfigTokens)
            {
                Assert.IsFalse(moduleConfigData.Contains(inactiveToken, StringComparison.Ordinal), $"UITestAutomation module config should not carry inactive module launch target: {inactiveToken}");
            }

            foreach (var inactiveSessionToken in new[]
            {
                "PowerToysModule.CommandPalette",
                "TryLaunchCommandPalette",
                "Microsoft.CmdPal.UI",
                "Microsoft.CommandPalette_8wekyb3d8bbwe",
                "Command Palette",
            })
            {
                Assert.IsFalse(sessionHelper.Contains(inactiveSessionToken, StringComparison.Ordinal), $"UITestAutomation session helper should not carry inactive Command Palette launch or cleanup token: {inactiveSessionToken}");
            }

            Assert.IsFalse(uiTestBase.Contains("PowerToys.FancyZonesEditor", StringComparison.Ordinal), "UITestAutomation cleanup should not target inactive FancyZones Editor.");
            Assert.IsFalse(settingsConfigHelper.Contains("\"Peek\"", StringComparison.Ordinal), "UITestAutomation settings examples should not cite inactive Peek.");
            Assert.IsFalse(settingsConfigHelper.Contains("\"FancyZones\"", StringComparison.Ordinal), "UITestAutomation settings examples should not cite inactive FancyZones.");
            Assert.IsFalse(textBox.Contains("CmdPal", StringComparison.Ordinal), "Generic UI test controls should not document inactive CmdPal-specific workarounds.");
            Assert.IsFalse(moduleConfigData.Contains("PowerToys Settings", StringComparison.Ordinal), "UITestAutomation should attach to Kit's Settings window title, not the upstream PowerToys title.");
            Assert.IsFalse(sessionHelper.Contains("KillPowerToysProcesses", StringComparison.Ordinal), "UITestAutomation cleanup should use Kit process ownership.");
            Assert.IsFalse(NormalizeLineEndings(sessionHelper).Contains("foreach (var process in processes)\n                    {\n                        process.Kill();", StringComparison.Ordinal), "SessionHelper should not globally kill every process with a Kit-compatible executable name.");
            Assert.IsFalse(NormalizeLineEndings(uiTestBase).Contains("foreach (var process in Process.GetProcessesByName(processName))\n                {\n                    process.Kill();", StringComparison.Ordinal), "UITestBase cleanup should not globally kill every process with a Kit-compatible executable name.");
        }

        [TestMethod]
        public void KitSettingsLibraryShouldNotProbeInactiveCmdPalPackageState()
        {
            var cmdPalProperties = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "CmdPalProperties.cs"));

            Assert.IsFalse(cmdPalProperties.Contains("Microsoft.CommandPalette_8wekyb3d8bbwe", StringComparison.Ordinal), "Compatibility DTOs should not probe inactive Command Palette package state.");
            Assert.IsFalse(cmdPalProperties.Contains("Microsoft.CommandPalette.Dev_8wekyb3d8bbwe", StringComparison.Ordinal), "Compatibility DTOs should not probe inactive Command Palette dev package state.");
            Assert.IsFalse(cmdPalProperties.Contains("File.ReadAllText", StringComparison.Ordinal), "Compatibility DTOs should not do disk I/O for inactive Command Palette settings.");
        }

        [TestMethod]
        public void KitSettingsCommandLineShouldOnlyResolveActiveModuleSettings()
        {
            var kitModuleCatalog = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "Helpers", "KitModuleCatalog.cs"));
            var commandLineUtils = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "Utilities", "CommandLineUtils.cs"));
            var setSettingCommandTests = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.UnitTests", "Cmd", "SetSettingCommandTests.cs"));

            StringAssert.Contains(kitModuleCatalog, "ActiveSettingsModuleKeys");
            StringAssert.Contains(kitModuleCatalog, "ActiveEnabledModuleKeys");
            StringAssert.Contains(commandLineUtils, "KitModuleCatalog.ActiveSettingsModuleKeys");
            StringAssert.Contains(commandLineUtils, "KitModuleCatalog.ActiveEnabledModuleKeys");
            foreach (var activeModule in new[] { "AwakeSettings.ModuleName", "LightSwitchSettings.ModuleName", "MonitorSettings.ModuleName" })
            {
                StringAssert.Contains(kitModuleCatalog, activeModule);
                Assert.IsFalse(commandLineUtils.Contains(activeModule, StringComparison.Ordinal), $"Settings command-line allowlist should read active module '{activeModule}' from KitModuleCatalog.");
            }

            Assert.IsFalse(commandLineUtils.Contains("PowerDisplaySettings.ModuleName", StringComparison.Ordinal), "Settings command-line allowlist should not include removed PowerDisplay.");

            foreach (var inactiveModule in new[] { "FancyZonesSettings", "PowerLauncherSettings", "MouseWithoutBordersSettings", "PowerRenameSettings", "ColorPickerSettings" })
            {
                Assert.IsFalse(setSettingCommandTests.Contains($"typeof({inactiveModule})", StringComparison.Ordinal), $"Settings command-line tests should not preserve inactive {inactiveModule} as callable active surface.");
                Assert.IsFalse(commandLineUtils.Contains($"{inactiveModule}.ModuleName", StringComparison.Ordinal), $"Settings command-line allowlist should not include inactive {inactiveModule}.");
            }
        }

        [TestMethod]
        public void KitModuleHelperShouldOnlyExposeActiveKitModuleBehavior()
        {
            var moduleHelper = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "Helpers", "ModuleHelper.cs"));

            foreach (var activeToken in new[]
            {
                "ModuleType.Awake",
                "ModuleType.LightSwitch",
                "ModuleType.Monitor",
                "ModuleType.GeneralSettings",
            })
            {
                StringAssert.Contains(moduleHelper, activeToken);
            }

            Assert.IsFalse(moduleHelper.Contains("ModuleType.PowerDisplay", StringComparison.Ordinal), "ModuleHelper should not expose removed PowerDisplay.");

            foreach (var compatibilityModuleKey in new[]
            {
                "AdvancedPasteSettings.ModuleName",
                "AlwaysOnTopSettings.ModuleName",
                "ColorPickerSettings.ModuleName",
                "CropAndLockSettings.ModuleName",
                "CursorWrapSettings.ModuleName",
                "EnvironmentVariablesSettings.ModuleName",
                "FancyZonesSettings.ModuleName",
                "FileLocksmithSettings.ModuleName",
                "FindMyMouseSettings.ModuleName",
                "HostsSettings.ModuleName",
                "ImageResizerSettings.ModuleName",
                "KeyboardManagerSettings.ModuleName",
                "MouseHighlighterSettings.ModuleName",
                "\"MouseJump\"",
                "MousePointerCrosshairsSettings.ModuleName",
                "MouseWithoutBordersSettings.ModuleName",
                "NewPlusSettings.ModuleName",
                "PeekSettings.ModuleName",
                "PowerRenameSettings.ModuleName",
                "PowerLauncherSettings.ModuleName",
                "PowerAccentSettings.ModuleName",
                "RegistryPreviewSettings.ModuleName",
                "MeasureToolSettings.ModuleName",
                "ShortcutGuideSettings.ModuleName",
                "PowerOcrSettings.ModuleName",
                "WorkspacesSettings.ModuleName",
                "GrabAndMoveSettings.ModuleName",
                "ZoomItSettings.ModuleName",
                "\"CmdPal\"",
            })
            {
                Assert.IsFalse(moduleHelper.Contains(compatibilityModuleKey, StringComparison.Ordinal), $"ModuleHelper should not expose inactive module key: {compatibilityModuleKey}");
            }

            foreach (var inactiveModuleArm in new[]
            {
                "ModuleType.AdvancedPaste",
                "ModuleType.AlwaysOnTop",
                "ModuleType.CmdPal",
                "ModuleType.ColorPicker",
                "ModuleType.CropAndLock",
                "ModuleType.CursorWrap",
                "ModuleType.EnvironmentVariables",
                "ModuleType.FancyZones",
                "ModuleType.FileLocksmith",
                "ModuleType.FindMyMouse",
                "ModuleType.Hosts",
                "ModuleType.ImageResizer",
                "ModuleType.KeyboardManager",
                "ModuleType.MouseHighlighter",
                "ModuleType.MouseJump",
                "ModuleType.MousePointerCrosshairs",
                "ModuleType.MouseWithoutBorders",
                "ModuleType.NewPlus",
                "ModuleType.Peek",
                "ModuleType.PowerRename",
                "ModuleType.PowerLauncher",
                "ModuleType.PowerAccent",
                "ModuleType.RegistryPreview",
                "ModuleType.MeasureTool",
                "ModuleType.ShortcutGuide",
                "ModuleType.PowerOCR",
                "ModuleType.Workspaces",
                "ModuleType.GrabAndMove",
                "ModuleType.ZoomIt",
            })
            {
                Assert.IsFalse(moduleHelper.Contains($"{inactiveModuleArm} => generalSettingsConfig.Enabled", StringComparison.Ordinal), $"ModuleHelper should not expose inactive enabled-state behavior: {inactiveModuleArm}");
                Assert.IsFalse(moduleHelper.Contains($"{inactiveModuleArm} => $\"ms-appx", StringComparison.Ordinal), $"ModuleHelper should not expose inactive icon behavior: {inactiveModuleArm}");
                Assert.IsFalse(moduleHelper.Contains($"{inactiveModuleArm} => $\"{{moduleType}}/ModuleTitle", StringComparison.Ordinal), $"ModuleHelper should not expose inactive label behavior: {inactiveModuleArm}");
            }
        }

        [TestMethod]
        public void KitFirstPluginDocsShouldNameTheFullActiveModuleSet()
        {
            var pluginDoc = File.ReadAllText(FindSourceFile("doc", "devdoc", "kit-first-plugin.md"));

            foreach (var activeModule in new[] { "Awake", "LightSwitch", "Monitor" })
            {
                StringAssert.Contains(pluginDoc, activeModule);
            }

            StringAssert.Contains(pluginDoc, "three active modules");
            Assert.IsFalse(pluginDoc.Contains("PowerDisplay", StringComparison.Ordinal), "First-plugin docs should not list removed PowerDisplay as active.");
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

            StringAssert.Contains(admx, "SUPPORTED_KIT_2_0_5");
            StringAssert.Contains(adml, "SUPPORTED_KIT_2_0_5");
            foreach (var obsoleteSupportMarker in new[] { "SUPPORTED_KIT_1" + "_2_2", "SUPPORTED_KIT_2" + "_0_2", "SUPPORTED_KIT_2" + "_0_3", "SUPPORTED_KIT_2" + "_0_4", "SUPPORTED_KIT_3" + "_0_1" })
            {
                Assert.IsFalse(admx.Contains(obsoleteSupportMarker, StringComparison.Ordinal), $"GPO ADMX should not keep obsolete support marker after the version bump: {obsoleteSupportMarker}");
                Assert.IsFalse(adml.Contains(obsoleteSupportMarker, StringComparison.Ordinal), $"GPO ADML should not keep obsolete support marker after the version bump: {obsoleteSupportMarker}");
            }

            foreach (var inactivePolicyToken in new[]
            {
                "ConfigureAllUtilityGlobalEnabledState",
                "ConfigureEnabledUtilityAdvancedPaste",
                "ConfigureEnabledUtilityAlwaysOnTop",
                "ConfigureEnabledUtilityPowerDisplay",
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
                "PowerDisplay.png",
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
                "PowerDisplay.png",
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
            Assert.IsFalse(settingsFilter.Contains(@"src\\modules\\powerdisplay", StringComparison.OrdinalIgnoreCase), "Settings solution filter should not reference removed PowerDisplay projects.");
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
            var notice = File.ReadAllText(FindSourceFile("NOTICE.md"));

            Assert.IsFalse(centralPackages.Contains(@"PackageVersion Include=""SkiaSharp.Views.WinUI""", StringComparison.Ordinal), "Kit should not keep the Registry Preview-only SkiaSharp.Views.WinUI central package pin.");
            Assert.IsFalse(centralPackages.Contains("Registry Preview", StringComparison.Ordinal), "Central package comments should not explain package pins through inactive Registry Preview behavior.");
            Assert.IsFalse(centralPackages.Contains("HexBox", StringComparison.Ordinal), "Central package comments should not keep inactive Registry Preview HexBox details.");
            Assert.IsFalse(notice.Contains("- SkiaSharp.Views.WinUI", StringComparison.Ordinal), "Third-party notices should not list dependencies that only served deleted Registry Preview paths.");
            Assert.IsFalse(notice.Contains("- Registry Preview", StringComparison.Ordinal), "Third-party notices should not list deleted Registry Preview as a utility with active third-party material.");
            Assert.IsFalse(notice.Contains("## Utility: Registry Preview", StringComparison.Ordinal), "Third-party notices should not keep deleted Registry Preview utility sections.");
            Assert.IsFalse(notice.Contains("### HexBox.WinUI", StringComparison.Ordinal), "Third-party notices should not keep the deleted Registry Preview HexBox license section.");
            Assert.IsFalse(notice.Contains("hotkidfamily/HexBox.WinUI", StringComparison.Ordinal), "Third-party notices should not link deleted Registry Preview HexBox sources.");

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
            Assert.IsFalse(notice.Contains("- PowerToys Run", StringComparison.Ordinal), "Third-party notices should not list deleted PowerToys Run as a utility with active third-party material.");
            Assert.IsFalse(notice.Contains("## Utility: PowerToys Run", StringComparison.Ordinal), "Third-party notices should not keep deleted PowerToys Run utility sections.");
            Assert.IsFalse(notice.Contains("### Wox license", StringComparison.Ordinal), "Third-party notices should not keep the deleted PowerToys Run Wox license section.");
            Assert.IsFalse(notice.Contains("Wox-launcher/Wox", StringComparison.Ordinal), "Third-party notices should not link deleted PowerToys Run Wox sources.");
            Assert.IsFalse(notice.Contains("### Beta Tadele's Window Walker license", StringComparison.Ordinal), "Third-party notices should not keep the deleted PowerToys Run Window Walker license section.");
            Assert.IsFalse(notice.Contains("betsegaw/windowwalker", StringComparison.Ordinal), "Third-party notices should not link deleted PowerToys Run Window Walker sources.");

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
        public void KitCentralPackagesShouldNotKeepCommandPaletteToolkitAndHostPins()
        {
            var centralPackages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));
            var notice = File.ReadAllText(FindSourceFile("NOTICE.md"));
            string[] commandPalettePackages =
            {
                "Shmuelie.WinRTServer",
                "ToolGood.Words.Pinyin",
            };

            foreach (var packageName in commandPalettePackages)
            {
                Assert.IsFalse(centralPackages.Contains($@"PackageVersion Include=""{packageName}""", StringComparison.Ordinal), $"Kit should not keep the inactive Command Palette toolkit or host central package pin: {packageName}");
                Assert.IsFalse(notice.Contains($"- {packageName}", StringComparison.Ordinal), $"Third-party notices should not list the removed Command Palette toolkit or host package dependency: {packageName}");
            }

            Assert.IsFalse(notice.Contains("### ToolGood.Words.Pinyin", StringComparison.Ordinal), "Third-party notices should not keep the removed Command Palette ToolGood.Words.Pinyin license section.");

            foreach (var projectFile in Directory.EnumerateFiles(Path.GetDirectoryName(FindSourceFile("Kit.slnx"))!, "*.*proj", SearchOption.AllDirectories))
            {
                if (projectFile.Contains($"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var project = File.ReadAllText(projectFile);
                foreach (var packageName in commandPalettePackages)
                {
                    Assert.IsFalse(project.Contains($@"PackageReference Include=""{packageName}""", StringComparison.Ordinal), $"Kit project should not reference the inactive Command Palette toolkit or host package {packageName}: {projectFile}");
                }
            }
        }

        [TestMethod]
        public void KitCentralPackagesShouldNotKeepDeletedModulePackagePins()
        {
            var centralPackages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));
            var notice = File.ReadAllText(FindSourceFile("NOTICE.md"));
            string[] deletedModulePackages =
            {
                "ModernWpfUI",
                "NJsonSchema",
                "ScipBe.Common.Office.OneNote",
                "SharpCompress",
            };

            foreach (var packageName in deletedModulePackages)
            {
                Assert.IsFalse(centralPackages.Contains($@"PackageVersion Include=""{packageName}""", StringComparison.Ordinal), $"Kit should not keep central package pins that only served deleted PowerToys modules: {packageName}");
                Assert.IsFalse(notice.Contains($"- {packageName}", StringComparison.Ordinal), $"Third-party notices should not list package dependencies that only served deleted PowerToys modules: {packageName}");
            }

            foreach (var projectFile in Directory.EnumerateFiles(Path.GetDirectoryName(FindSourceFile("Kit.slnx"))!, "*.*proj", SearchOption.AllDirectories))
            {
                if (projectFile.Contains($"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var project = File.ReadAllText(projectFile);
                foreach (var packageName in deletedModulePackages)
                {
                    Assert.IsFalse(project.Contains($@"PackageReference Include=""{packageName}""", StringComparison.Ordinal), $"Kit project should not reference the deleted-module-only package {packageName}: {projectFile}");
                }
            }
        }

        [TestMethod]
        public void KitCentralPackagesShouldNotKeepDeletedUtilityPackagePins()
        {
            var centralPackages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));
            var notice = File.ReadAllText(FindSourceFile("NOTICE.md"));
            string[] deletedUtilityPackages =
            {
                "CommunityToolkit.WinUI.Collections",
                "CommunityToolkit.WinUI.UI.Controls.DataGrid",
                "ControlzEx",
                "Interop.Microsoft.Office.Interop.OneNote",
                "LazyCache",
                "Microsoft.Toolkit.Uwp.Notifications",
                "RtfPipe",
                "WPF-UI",
            };

            foreach (var packageName in deletedUtilityPackages)
            {
                Assert.IsFalse(centralPackages.Contains($@"PackageVersion Include=""{packageName}""", StringComparison.Ordinal), $"Kit should not keep central package pins for utilities that only served deleted PowerToys modules: {packageName}");
                Assert.IsFalse(notice.Contains($"- {packageName}", StringComparison.Ordinal), $"Third-party notices should not list utility package dependencies that only served deleted PowerToys modules: {packageName}");
            }

            foreach (var projectFile in Directory.EnumerateFiles(Path.GetDirectoryName(FindSourceFile("Kit.slnx"))!, "*.*proj", SearchOption.AllDirectories))
            {
                if (projectFile.Contains($"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var project = File.ReadAllText(projectFile);
                foreach (var packageName in deletedUtilityPackages)
                {
                    Assert.IsFalse(project.Contains($@"PackageReference Include=""{packageName}""", StringComparison.Ordinal), $"Kit project should not reference the deleted-utility-only package {packageName}: {projectFile}");
                }
            }
        }

        [TestMethod]
        public void KitCentralPackagesShouldNotKeepDeletedLauncherAiAndCmdPalPins()
        {
            var centralPackages = File.ReadAllText(FindSourceFile("Directory.Packages.props"));
            var notice = File.ReadAllText(FindSourceFile("NOTICE.md"));
            string[] deletedLauncherAiAndCmdPalPackages =
            {
                "Microsoft.Data.Sqlite",
                "Microsoft.Graphics.Win2D",
                "Microsoft.WindowsAppSDK.AI",
                "NLog",
                "NLog.Extensions.Logging",
                "NLog.Schema",
                "System.ClientModel",
                "System.Numerics.Tensors",
                "WyHash",
            };

            foreach (var packageName in deletedLauncherAiAndCmdPalPackages)
            {
                Assert.IsFalse(centralPackages.Contains($@"PackageVersion Include=""{packageName}""", StringComparison.Ordinal), $"Kit should not keep central package pins for deleted Launcher, AI, or CmdPal paths: {packageName}");
                Assert.IsFalse(notice.Contains($"- {packageName}", StringComparison.Ordinal), $"Third-party notices should not list dependencies that only served deleted Launcher, AI, or CmdPal paths: {packageName}");
            }

            Assert.IsFalse(notice.Contains("### wyhash", StringComparison.OrdinalIgnoreCase), "Third-party notices should not keep the removed CmdPal WyHash license section.");

            foreach (var projectFile in Directory.EnumerateFiles(Path.GetDirectoryName(FindSourceFile("Kit.slnx"))!, "*.*proj", SearchOption.AllDirectories))
            {
                if (projectFile.Contains($"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var project = File.ReadAllText(projectFile);
                foreach (var packageName in deletedLauncherAiAndCmdPalPackages)
                {
                    Assert.IsFalse(project.Contains($@"PackageReference Include=""{packageName}""", StringComparison.Ordinal), $"Kit project should not reference the deleted Launcher, AI, or CmdPal package {packageName}: {projectFile}");
                }
            }
        }

        [TestMethod]
        public void KitDotNetBuildLayerShouldFollowPowerToysNet10Versions()
        {
            var dotnetProps = File.ReadAllText(FindSourceFile("src", "Common.Dotnet.CsWinRT.props"));
            var versionProject = File.ReadAllText(FindSourceFile("src", "common", "version", "version.vcxproj"));

            StringAssert.Contains(dotnetProps, "<CoreTargetFramework>net10.0</CoreTargetFramework>");
            Assert.IsFalse(dotnetProps.Contains("<CoreTargetFramework>net9.0</CoreTargetFramework>", StringComparison.Ordinal), "Shared .NET build props should not leave default CsWinRT projects on net9.");
            StringAssert.Contains(versionProject, "<AdditionalOptions>/FS %(AdditionalOptions)</AdditionalOptions>");

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
                @"<PackageVersion Include=""Microsoft.Bcl.AsyncInterfaces"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""Microsoft.CodeAnalysis.NetAnalyzers"" Version=""10.0.102"" />",
                @"<PackageVersion Include=""Microsoft.Extensions.Caching.Abstractions"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""Microsoft.Extensions.Caching.Memory"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""Microsoft.Extensions.DependencyInjection"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""Microsoft.Extensions.Hosting"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""Microsoft.Extensions.Hosting.WindowsServices"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""Microsoft.Extensions.Logging"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""Microsoft.Extensions.Logging.Abstractions"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""Microsoft.Web.WebView2"" Version=""1.0.3719.77"" />",
                @"<PackageVersion Include=""Microsoft.Win32.SystemEvents"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""Microsoft.Windows.Compatibility"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""Microsoft.Windows.CsWin32"" Version=""0.3.269"" />",
                @"<PackageVersion Include=""Microsoft.WindowsAppSDK"" Version=""2.0.1"" />",
                @"<PackageVersion Include=""Microsoft.WindowsAppSDK.Foundation"" Version=""2.0.20"" />",
                @"<PackageVersion Include=""Microsoft.WindowsAppSDK.Runtime"" Version=""2.0.1"" />",
                @"<PackageVersion Include=""Newtonsoft.Json"" Version=""13.0.4"" />",
                @"<PackageVersion Include=""System.CodeDom"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""System.ComponentModel.Composition"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""System.Configuration.ConfigurationManager"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""System.Data.OleDb"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""System.Data.SqlClient"" Version=""4.9.1"" />",
                @"<PackageVersion Include=""System.Diagnostics.EventLog"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""System.Diagnostics.PerformanceCounter"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""System.Drawing.Common"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""System.Management"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""System.Runtime.Caching"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""System.ServiceProcess.ServiceController"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""System.Text.Encoding.CodePages"" Version=""10.0.8"" />",
                @"<PackageVersion Include=""System.Text.Json"" Version=""10.0.8"" />",
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
            Assert.IsFalse(runnerMain.Contains("PowerToys.PowerDisplayModuleInterface.dll", StringComparison.Ordinal), "Runner should not load removed PowerDisplay.");
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
        public void KitRuntimeCommentsShouldNotDescribeInactiveModuleSpecialCases()
        {
            var moduleInterface = File.ReadAllText(FindSourceFile("src", "modules", "interface", "powertoy_module_interface.h"));
            var settingsWindow = File.ReadAllText(FindSourceFile("src", "runner", "settings_window.cpp"));
            var lightSwitchStateManager = File.ReadAllText(FindSourceFile("src", "modules", "LightSwitch", "LightSwitchService", "LightSwitchStateManager.h"));
            var lightSwitchSettings = File.ReadAllText(FindSourceFile("src", "modules", "LightSwitch", "LightSwitchService", "LightSwitchSettings.cpp"));
            var runnerTraceHeader = File.ReadAllText(FindSourceFile("src", "runner", "trace.h"));

            Assert.IsFalse(moduleInterface.Contains("AdvancedPaste", StringComparison.Ordinal), "Shared module interface comments should not describe inactive AdvancedPaste special cases.");
            Assert.IsFalse(settingsWindow.Contains("PowerToys Run hotkeys", StringComparison.Ordinal), "Runner settings comments should not describe inactive PowerToys Run hotkey behavior as current.");
            Assert.IsFalse(settingsWindow.Contains("PowerToys Run settings", StringComparison.Ordinal), "Runner settings comments should not describe inactive PowerToys Run settings as current.");
            Assert.IsFalse(lightSwitchStateManager.Contains("debugging or telemetry", StringComparison.Ordinal), "LightSwitch comments should not imply telemetry use for exposed state accessors.");
            Assert.IsFalse(lightSwitchSettings.Contains("log telemetry", StringComparison.Ordinal), "LightSwitch settings comments should describe trace updates without telemetry wording.");
            Assert.IsFalse(runnerTraceHeader.Contains("Auto-update telemetry", StringComparison.Ordinal), "Runner trace comments should not describe update events as telemetry.");
            Assert.IsFalse(runnerTraceHeader.Contains("Tray icon interaction telemetry", StringComparison.Ordinal), "Runner trace comments should not describe tray icon events as telemetry.");
        }

        [TestMethod]
        public void KitNoticeShouldNotCarryDeletedUtilityLicenseSections()
        {
            var notice = File.ReadAllText(FindSourceFile("NOTICE.md"));

            string[] deletedUtilityNoticeTokens =
            {
                "## Utility: Color Picker",
                "martinchrzan/ColorPicker",
                "Copyright (c) 2020 martinchrzan",
                "## Utility: ImageResizer",
                "bricelam/ImageResizer",
                "Brice Lambson",
                "## Utility: PowerRename",
                "chrdavis/SmartRename",
                "Chris Davis",
            };

            foreach (var deletedUtilityNoticeToken in deletedUtilityNoticeTokens)
            {
                Assert.IsFalse(notice.Contains(deletedUtilityNoticeToken, StringComparison.Ordinal), $"NOTICE.md should not carry third-party license surface for deleted utility source: {deletedUtilityNoticeToken}");
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
            var searchIndex = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "Assets", "Settings", "search.index.json"));

            Assert.IsFalse(xamlIndexBuilderProgram.Contains("PanelPageMapping", StringComparison.Ordinal), "Kit has no active Settings panels, so search indexing should not carry inactive panel-to-page fallback mappings.");
            Assert.IsFalse(xamlIndexBuilderProgram.Contains("MouseJumpPanel", StringComparison.Ordinal), "Kit search indexing should not explicitly include deleted Mouse Jump panels.");
            Assert.IsFalse(moduleIconResolver.Contains("FileNameOverrides", StringComparison.Ordinal), "Kit search indexing should derive active page icons from XAML instead of carrying inactive upstream page overrides.");
            StringAssert.Contains(xamlIndexBuilderProgram, "\"SearchResultsPage.xaml\"");
            StringAssert.Contains(xamlIndexBuilderProgram, "\"ShortcutConflictWindow.xaml\"");
            Assert.IsFalse(searchIndex.Contains("\"pageTypeName\": \"SearchResultsPage\"", StringComparison.Ordinal), "Generated search index should not include the search results page itself.");
            Assert.IsFalse(searchIndex.Contains("\"pageTypeName\": \"ShortcutConflictWindow\"", StringComparison.Ordinal), "Generated search index should not include non-Views shortcut conflict window entries.");

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
            var settingsHelper = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI.Library", "Utilities", "Helper.cs"));

            StringAssert.Contains(runnerProject, "<TargetName>Kit</TargetName>");
            StringAssert.Contains(runnerResource, "#define ORIGINAL_FILENAME \"Kit.exe\"");
            StringAssert.Contains(runnerHelper, "\"Kit.exe\"");
            StringAssert.Contains(runnerHelper, "\"PowerToys.exe\"");
            StringAssert.Contains(settingsHelper, "Process.GetProcessesByName(\"Kit\")");
            Assert.IsFalse(settingsHelper.Contains("Process.GetProcessesByName(\"PowerToys\")", StringComparison.Ordinal), "Settings foreground authorization should target the Kit runner process.");
            StringAssert.Contains(pathResolver, "KitRegistryKey");
            StringAssert.Contains(pathResolver, "PowerToysRegistryKey");
            StringAssert.Contains(pathResolver, "KitExe = \"Kit.exe\"");
            StringAssert.Contains(pathResolver, "PowerToysExe = \"PowerToys.exe\"");
            StringAssert.Contains(pathResolver, "GetKitInstallPath()");
            StringAssert.Contains(pathResolver, "GetPowerToysCompatibleInstallPath()");
            StringAssert.Contains(pathResolver, "GetPathFromRegistry(RegistryHive.CurrentUser, kitOnly: true)");
            StringAssert.Contains(pathResolver, "GetPathFromRegistry(RegistryHive.CurrentUser, kitOnly: false)");
            StringAssert.Contains(pathResolver, "(!kitOnly && File.Exists(Path.Combine(directory, PowerToysExe)))");
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

            StringAssert.Contains(sharedConstants, "KitRunnerTerminateSettingsEvent");
            StringAssert.Contains(sharedConstants, "KitAwakeExitEvent");
            StringAssert.Contains(sharedConstants, "KitMonitorExitEvent");
            StringAssert.Contains(sharedConstants, "KitMonitorScanCompletedEvent");
            StringAssert.Contains(sharedConstants, "Kit-LightSwitch-ToggleEvent");
            StringAssert.Contains(lightSwitchInterface, "CommonSharedConstants::LIGHTSWITCH_TOGGLE_EVENT");
            StringAssert.Contains(lightSwitchInterface, "KIT_LIGHTSWITCH_MANUAL_OVERRIDE");
            StringAssert.Contains(lightSwitchInterface, "KIT_LIGHTSWITCH_SERVICE_STOP");
            StringAssert.Contains(lightSwitchInterface, "SetEvent(m_service_stop_event_handle)");
            StringAssert.Contains(lightSwitchInterface, "ResetEvent(m_service_stop_event_handle)");
            StringAssert.Contains(lightSwitchInterface, "if (m_process && WaitForSingleObject(m_process, 0) != WAIT_TIMEOUT)");
            StringAssert.Contains(lightSwitchInterface, "CloseEventHandles();");
            StringAssert.Contains(lightSwitchInterface, "CloseHandleIfSet(m_manual_override_event_handle);");
            StringAssert.Contains(lightSwitchInterface, "CloseHandleIfSet(m_service_stop_event_handle);");
            StringAssert.Contains(lightSwitchInterface, "CloseHandleIfSet(m_toggle_event_handle);");
            StringAssert.Contains(lightSwitchService, "KIT_LIGHTSWITCH_SERVICE_STOP");
            StringAssert.Contains(lightSwitchService, "KIT_LIGHTSWITCH_MANUAL_OVERRIDE");
            Assert.IsFalse(lightSwitchInterface.Contains("CloseHandle(m_manual_override_event_handle);\n            m_manual_override_event_handle = nullptr;", StringComparison.Ordinal), "LightSwitch disable should not close one event handle conditionally while leaking the other module event handles.");
            Assert.IsFalse(sharedConstants.Contains("PowerToysRunnerTerminateSettingsEvent", StringComparison.Ordinal), "Kit Settings IPC must not share the PowerToys terminate event.");
            Assert.IsFalse(sharedConstants.Contains("PowerToysAwakeExitEvent", StringComparison.Ordinal), "Kit Awake must not share the PowerToys exit event.");
            Assert.IsFalse(sharedConstants.Contains("PowerToysMonitorExitEvent", StringComparison.Ordinal), "Kit Monitor must not share the PowerToys exit event.");
            Assert.IsFalse(sharedConstants.Contains("PowerToysMonitorScanCompletedEvent", StringComparison.Ordinal), "Kit Monitor scan completion event must not share PowerToys names.");
            Assert.IsFalse(sharedConstants.Contains("PowerToys-LightSwitch-ToggleEvent", StringComparison.Ordinal), "Kit LightSwitch must not share the PowerToys toggle event.");
            Assert.IsFalse(sharedConstants.Contains("PowerDisplay", StringComparison.Ordinal), "Shared runtime events should not expose removed PowerDisplay.");
            Assert.IsFalse(sharedConstants.Contains("LightThemeEvent", StringComparison.Ordinal), "LightSwitch-to-PowerDisplay light theme event should be removed with PowerDisplay.");
            Assert.IsFalse(sharedConstants.Contains("DarkThemeEvent", StringComparison.Ordinal), "LightSwitch-to-PowerDisplay dark theme event should be removed with PowerDisplay.");
            Assert.IsFalse(lightSwitchInterface.Contains("POWERTOYS_LIGHTSWITCH", StringComparison.Ordinal), "Kit LightSwitch interface must not use PowerToys event names.");
            Assert.IsFalse(lightSwitchInterface.Contains("PowerToys-LightSwitch-ToggleEvent", StringComparison.Ordinal), "Kit LightSwitch interface must listen on the shared Kit toggle event.");
            Assert.IsFalse(lightSwitchService.Contains("POWERTOYS_LIGHTSWITCH", StringComparison.Ordinal), "Kit LightSwitch service must not use PowerToys event names.");
        }

        [TestMethod]
        public void KitLightSwitchShouldNotKeepDisabledForceModeActions()
        {
            var lightSwitchInterface = File.ReadAllText(FindSourceFile("src", "modules", "LightSwitch", "LightSwitchModuleInterface", "dllmain.cpp"));
            var lightSwitchViewModel = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "ViewModels", "LightSwitchViewModel.cs"));
            var lightSwitchPage = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "SettingsXAML", "Views", "LightSwitchPage.xaml"));

            StringAssert.Contains(lightSwitchInterface, "void ToggleTheme()");
            StringAssert.Contains(lightSwitchInterface, "SetEvent(m_manual_override_event_handle)");
            Assert.IsFalse(lightSwitchInterface.Contains("KIT_LIGHTSWITCH_FORCE_LIGHT", StringComparison.Ordinal), "LightSwitch should not create a named force-light event that no code waits on.");
            Assert.IsFalse(lightSwitchInterface.Contains("KIT_LIGHTSWITCH_FORCE_DARK", StringComparison.Ordinal), "LightSwitch should not create a named force-dark event that no code waits on.");
            Assert.IsFalse(lightSwitchInterface.Contains("m_force_light_event_handle", StringComparison.Ordinal), "LightSwitch should not keep unused force-light event handles.");
            Assert.IsFalse(lightSwitchInterface.Contains("m_force_dark_event_handle", StringComparison.Ordinal), "LightSwitch should not keep unused force-dark event handles.");
            Assert.IsFalse(lightSwitchInterface.Contains("L\"forceLight\"", StringComparison.Ordinal), "LightSwitch module config should not advertise disabled force-light custom actions.");
            Assert.IsFalse(lightSwitchInterface.Contains("L\"forceDark\"", StringComparison.Ordinal), "LightSwitch module config should not advertise disabled force-dark custom actions.");
            Assert.IsFalse(lightSwitchInterface.Contains("force dark mode shortcut", StringComparison.Ordinal), "LightSwitch settings parse errors should refer to the active toggle-theme shortcut, not the removed force-dark action.");
            Assert.IsFalse(lightSwitchViewModel.Contains("ForceLightCommand", StringComparison.Ordinal), "LightSwitch settings view model should not keep commands for a commented-out force-light UI.");
            Assert.IsFalse(lightSwitchViewModel.Contains("ForceDarkCommand", StringComparison.Ordinal), "LightSwitch settings view model should not keep commands for a commented-out force-dark UI.");
            Assert.IsFalse(lightSwitchViewModel.Contains("SendCustomAction(\"forceLight\")", StringComparison.Ordinal), "LightSwitch settings should not send an unreachable force-light custom action.");
            Assert.IsFalse(lightSwitchViewModel.Contains("SendCustomAction(\"forceDark\")", StringComparison.Ordinal), "LightSwitch settings should not send an unreachable force-dark custom action.");
            Assert.IsFalse(lightSwitchPage.Contains("Force mode buttons", StringComparison.Ordinal), "LightSwitch page should not keep disabled force-mode UI in comments.");
            Assert.IsFalse(lightSwitchPage.Contains("ForceLightCommand", StringComparison.Ordinal), "LightSwitch page should not bind disabled force-light commands.");
            Assert.IsFalse(lightSwitchPage.Contains("ForceDarkCommand", StringComparison.Ordinal), "LightSwitch page should not bind disabled force-dark commands.");
        }

        [TestMethod]
        public void KitLightSwitchScheduleOffShouldStopServiceInsteadOfRestartingIt()
        {
            var lightSwitchInterface = File.ReadAllText(FindSourceFile("src", "modules", "LightSwitch", "LightSwitchModuleInterface", "dllmain.cpp"));

            StringAssert.Contains(lightSwitchInterface, "void stop_worker_only()");
            StringAssert.Contains(lightSwitchInterface, "void stop_service_if_running()");
            StringAssert.Contains(lightSwitchInterface, "if (newMode == ScheduleMode::Off)");
            StringAssert.Contains(lightSwitchInterface, "stop_service_if_running();");
            StringAssert.Contains(lightSwitchInterface, "start_service_if_needed();");
            Assert.IsTrue(
                lightSwitchInterface.IndexOf("if (newMode == ScheduleMode::Off)", StringComparison.Ordinal) <
                lightSwitchInterface.IndexOf("start_service_if_needed();", StringComparison.Ordinal),
                "LightSwitch schedule changes should branch on Off before starting the service.");
            Assert.IsFalse(lightSwitchInterface.Contains("/*virtual void stop_worker_only()", StringComparison.Ordinal), "LightSwitch should not keep disabled stop-worker lifecycle code in comments.");
            Assert.IsFalse(lightSwitchInterface.Contains("/*virtual void stop_service_if_running()", StringComparison.Ordinal), "LightSwitch should not keep disabled stop-service lifecycle code in comments.");
        }

        [TestMethod]
        public void KitLightSwitchEnableShouldReportEnabledOnlyAfterServiceLaunchSucceeds()
        {
            var lightSwitchInterface = File.ReadAllText(FindSourceFile("src", "modules", "LightSwitch", "LightSwitchModuleInterface", "dllmain.cpp"));

            var enableStart = lightSwitchInterface.IndexOf("virtual void enable()", StringComparison.Ordinal);
            Assert.AreNotEqual(-1, enableStart, "LightSwitch module interface should expose enable().");
            var enableBody = lightSwitchInterface[enableStart..lightSwitchInterface.IndexOf("// Disable the powertoy", enableStart, StringComparison.Ordinal)];

            StringAssert.Contains(enableBody, "CreateProcessW");
            StringAssert.Contains(enableBody, "m_enabled = true;");
            StringAssert.Contains(enableBody, "Trace::Enable(true);");
            Assert.IsTrue(
                enableBody.IndexOf("CreateProcessW", StringComparison.Ordinal) <
                enableBody.IndexOf("m_enabled = true;", StringComparison.Ordinal),
                "LightSwitch should mark the module enabled only after the service process is created.");
            Assert.IsTrue(
                enableBody.IndexOf("m_enabled = true;", StringComparison.Ordinal) <
                enableBody.IndexOf("Trace::Enable(true);", StringComparison.Ordinal),
                "LightSwitch enable tracing should reflect a successfully launched service.");
            Assert.IsFalse(enableBody.TrimStart().StartsWith("virtual void enable()\r\n    {\r\n        m_enabled = true;", StringComparison.Ordinal), "LightSwitch should not set m_enabled before any launch failure path.");
            Assert.IsFalse(enableBody.Contains("Logger::error(L\"Failed to launch Light Switch process.", StringComparison.Ordinal) && enableBody.Contains("m_enabled = true;\r\n        Logger::info(L\"Enabling Light Switch module...\"", StringComparison.Ordinal), "LightSwitch create-process failure path should not leave m_enabled true.");
        }

        [TestMethod]
        public void KitLightSwitchToggleHotkeyShouldToggleThemeWithoutRestartingService()
        {
            var lightSwitchInterface = File.ReadAllText(FindSourceFile("src", "modules", "LightSwitch", "LightSwitchModuleInterface", "dllmain.cpp"));

            var hotkeyStart = lightSwitchInterface.IndexOf("virtual bool on_hotkey(size_t hotkeyId) override", StringComparison.Ordinal);
            Assert.AreNotEqual(-1, hotkeyStart, "LightSwitch module interface should expose on_hotkey().");
            var classEnd = lightSwitchInterface.IndexOf("void LightSwitchInterface::EnsureEventHandles()", hotkeyStart, StringComparison.Ordinal);
            Assert.AreNotEqual(-1, classEnd, "LightSwitch module interface class should close before its out-of-line member definitions.");
            var hotkeyAndTail = lightSwitchInterface[hotkeyStart..classEnd];

            StringAssert.Contains(hotkeyAndTail, "ToggleTheme();");
            Assert.IsFalse(hotkeyAndTail.Contains("enable();", StringComparison.Ordinal), "The toggle-theme hotkey should toggle the theme directly, not relaunch the scheduler service when the schedule is Off and the worker has been stopped.");
            Assert.IsFalse(hotkeyAndTail.Contains("is_process_running", StringComparison.Ordinal), "The toggle-theme hotkey should not gate the theme toggle on a running scheduler service, and the now-unused is_process_running helper should be removed.");
        }

        [TestMethod]
        public void KitRuntimePipePrefixesShouldUseKitNames()
        {
            var settingsWindow = File.ReadAllText(FindSourceFile("src", "runner", "settings_window.cpp"));
            var quickAccessHost = File.ReadAllText(FindSourceFile("src", "runner", "quick_access_host.cpp"));

            StringAssert.Contains(settingsWindow, @"\\\\.\\pipe\\kit_runner_");
            StringAssert.Contains(settingsWindow, @"\\\\.\\pipe\\kit_settings_");
            StringAssert.Contains(quickAccessHost, "Local\\\\KitQuickAccess_");
            StringAssert.Contains(quickAccessHost, @"\\\\.\\pipe\\kit_quick_access_runner_");
            StringAssert.Contains(quickAccessHost, @"\\\\.\\pipe\\kit_quick_access_ui_");
            Assert.IsFalse(settingsWindow.Contains(@"\\\\.\\pipe\\powertoys_runner_", StringComparison.Ordinal));
            Assert.IsFalse(settingsWindow.Contains(@"\\\\.\\pipe\\powertoys_settings_", StringComparison.Ordinal));
            Assert.IsFalse(quickAccessHost.Contains("Local\\\\PowerToysQuickAccess_", StringComparison.Ordinal));
            Assert.IsFalse(quickAccessHost.Contains(@"\\\\.\\pipe\\powertoys_quick_access_", StringComparison.Ordinal));
            Assert.IsFalse(Directory.Exists(Path.Combine(Path.GetDirectoryName(FindSourceFile("Kit.slnx"))!, "src", "modules", "powerdisplay")), "Removed PowerDisplay should not keep a module-local pipe prefix implementation.");
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
            var quickAccessMainWindowCodeBehind = File.ReadAllText(FindSourceFile("src", "settings-ui", "QuickAccess.UI", "QuickAccessXAML", "MainWindow.xaml.cs"));

            StringAssert.Contains(quickAccessMainWindow, "<Window.SystemBackdrop>");
            StringAssert.Contains(quickAccessMainWindow, "<DesktopAcrylicBackdrop");
            StringAssert.Contains(quickAccessMainWindow, "Title=\"Kit Quick Access\"");
            StringAssert.Contains(quickAccessMainWindowCodeBehind, "Title = \"Kit Quick Access\";");
            Assert.IsFalse(quickAccessMainWindow.Contains("WindowEx.Backdrop", StringComparison.Ordinal), "Quick Access should not use the deprecated WinUIEx Backdrop attached property.");
            Assert.IsFalse(quickAccessMainWindow.Contains("AcrylicSystemBackdrop", StringComparison.Ordinal), "Quick Access should not use the deprecated WinUIEx AcrylicSystemBackdrop type.");
            Assert.IsFalse(quickAccessMainWindow.Contains("PowerToys Quick Access", StringComparison.Ordinal), "Quick Access window XAML title should not keep the upstream PowerToys brand.");
            Assert.IsFalse(quickAccessMainWindowCodeBehind.Contains("PowerToys Quick Access", StringComparison.Ordinal), "Quick Access runtime title should not keep the upstream PowerToys brand.");
            Assert.IsFalse(quickAccessMainWindow.Contains("Quick Access (Preview)", StringComparison.Ordinal), "Quick Access window title should not keep the upstream preview label.");
            Assert.IsFalse(quickAccessMainWindowCodeBehind.Contains("Quick Access (Preview)", StringComparison.Ordinal), "Quick Access runtime title should not keep the upstream preview label.");
        }

        [TestMethod]
        public void CommonFlyoutWindowHelperShouldAvoidPublicInteropFields()
        {
            var flyoutWindowHelper = File.ReadAllText(FindSourceFile("src", "common", "Common.UI.Controls", "Flyout", "FlyoutWindowHelper.cs"));

            StringAssert.Contains(flyoutWindowHelper, "[StructLayout(LayoutKind.Sequential)]");
            Assert.IsFalse(flyoutWindowHelper.Contains("public int X;", StringComparison.Ordinal), "Interop structs should not expose public fields that trigger CA1051.");
            Assert.IsFalse(flyoutWindowHelper.Contains("public int Y;", StringComparison.Ordinal), "Interop structs should not expose public fields that trigger CA1051.");
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

        private static string NormalizeLineEndings(string text)
        {
            return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
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
