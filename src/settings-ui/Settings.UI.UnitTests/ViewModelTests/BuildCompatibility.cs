// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ViewModelTests
{
    [TestClass]
    public class BuildCompatibility
    {
        private static string FindSourceFile(params string[] relativePathParts)
        {
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var pathParts = new string[relativePathParts.Length + 1];
                pathParts[0] = directory.FullName;
                Array.Copy(relativePathParts, 0, pathParts, 1, relativePathParts.Length);

                var candidate = Path.Combine(pathParts);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            Assert.Fail($"Could not find source file: {Path.Combine(relativePathParts)}");
            return string.Empty;
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
        public void KitSolutionShouldNotDirectlyBuildInactiveCommonAndDscProjects()
        {
            var solution = File.ReadAllText(FindSourceFile("Kit.slnx"));

            string[] inactiveProjects =
            {
                "src/common/CalculatorEngineCommon/CalculatorEngineCommon.vcxproj",
                "src/common/FilePreviewCommon/FilePreviewCommon.csproj",
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
        public void KitSettingsShouldNotPublishInactiveOobeAssets()
        {
            var settingsProject = File.ReadAllText(FindSourceFile("src", "settings-ui", "Settings.UI", "PowerToys.Settings.csproj"));

            StringAssert.Contains(settingsProject, @"<Content Remove=""Assets\Settings\Modules\OOBE\**\*"" />");
            StringAssert.Contains(settingsProject, @"<None Remove=""Assets\Settings\Modules\OOBE\**\*"" />");
        }

        [TestMethod]
        public void KitSettingsShouldNotBuildAdvancedPasteLanguageModelProvider()
        {
            var solution = File.ReadAllText(FindSourceFile("Kit.slnx"));
            var settingsFilter = File.ReadAllText(FindSourceFile("src", "settings-ui", "PowerToys.Settings.slnf"));
            var settingsProjectPath = FindSourceFile("src", "settings-ui", "Settings.UI", "PowerToys.Settings.csproj");
            var settingsProject = File.ReadAllText(settingsProjectPath);
            var settingsUiRoot = Path.GetDirectoryName(settingsProjectPath);

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
                @"<PackageVersion Include=""Microsoft.CommandPalette.Extensions"" Version=""0.9.260303001"" />",
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
            var buildTemplate = File.ReadAllText(FindSourceFile(".pipelines", "v2", "templates", "job-build-project.yml"));
            var publishScript = File.ReadAllText(FindSourceFile("installer", "PowerToysSetupVNext", "publish.cmd"));
            var devDocPluginChecklist = File.ReadAllText(FindSourceFile("doc", "devdoc", "modules", "launcher", "new-plugin-checklist.md"));
            var devDocsPluginChecklist = File.ReadAllText(FindSourceFile("doc", "devdocs", "modules", "launcher", "new-plugin-checklist.md"));

            StringAssert.Contains(settingsProject, @"Targets=""Restore;Build""");
            StringAssert.Contains(buildTemplate, "TargetFramework=net10.0-windows10.0.26100.0");
            StringAssert.Contains(publishScript, "TargetFramework=net10.0-windows10.0.26100.0");
            StringAssert.Contains(devDocPluginChecklist, "net10.0-windows10.0.22621.0");
            StringAssert.Contains(devDocPluginChecklist, ".NET 10");
            StringAssert.Contains(devDocsPluginChecklist, "net10.0-windows10.0.22621.0");
            StringAssert.Contains(devDocsPluginChecklist, ".NET 10");
            Assert.IsFalse(settingsProject.Contains(@"<PackageReference Include=""System.Net.Http""", StringComparison.Ordinal));
            Assert.IsFalse(settingsProject.Contains(@"<PackageReference Include=""System.Private.Uri""", StringComparison.Ordinal));
            Assert.IsFalse(settingsProject.Contains(@"<PackageReference Include=""System.Text.RegularExpressions""", StringComparison.Ordinal));
            Assert.IsFalse(settingsProject.Contains(@"<PackageReference Include=""System.Text.Json""", StringComparison.Ordinal));
            Assert.IsFalse(settingsUnitTestsProject.Contains(@"<PackageReference Include=""System.Net.Http""", StringComparison.Ordinal));
            Assert.IsFalse(settingsUnitTestsProject.Contains(@"<PackageReference Include=""System.Private.Uri""", StringComparison.Ordinal));
            Assert.IsFalse(settingsUnitTestsProject.Contains(@"<PackageReference Include=""System.Text.RegularExpressions""", StringComparison.Ordinal));
            Assert.IsFalse(xamlIndexBuilderProject.Contains(@"<PackageReference Include=""System.Text.Json""", StringComparison.Ordinal));
            Assert.IsFalse(uiTestAutomationProject.Contains(@"<PackageReference Include=""System.Text.RegularExpressions""", StringComparison.Ordinal));
            Assert.IsFalse(buildTemplate.Contains("net9.0-windows10.0.26100.0", StringComparison.Ordinal));
            Assert.IsFalse(publishScript.Contains("net9.0-windows10.0.26100.0", StringComparison.Ordinal));
            Assert.IsFalse(devDocPluginChecklist.Contains(".NET 9", StringComparison.Ordinal));
            Assert.IsFalse(devDocPluginChecklist.Contains("net9.0-windows10.0.22621.0", StringComparison.Ordinal));
            Assert.IsFalse(devDocsPluginChecklist.Contains(".NET 9", StringComparison.Ordinal));
            Assert.IsFalse(devDocsPluginChecklist.Contains("net9.0-windows10.0.22621.0", StringComparison.Ordinal));
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
        public void KitRunnerShouldOnlyRunImageResizerAiDetectionWhenImageResizerIsActive()
        {
            var runnerMain = File.ReadAllText(FindSourceFile("src", "runner", "main.cpp"));

            StringAssert.Contains(runnerMain, "KitKnownModules");
            StringAssert.Contains(runnerMain, "is_known_module_registered");
            StringAssert.Contains(runnerMain, "is_image_resizer_registered_for_kit");
            StringAssert.Contains(runnerMain, "package::IsWin11OrGreater() && is_image_resizer_registered_for_kit()");
            StringAssert.Contains(runnerMain, "AI capability detection skipped: Image Resizer is not an active Kit module");
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
