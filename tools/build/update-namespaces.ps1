param (
    [string]$RepoRoot = (Get-Item -Path $PSScriptRoot\..\..).FullName
)

$targetDirs = @(
    "src\common\Common.UI",
    "src\common\Common.UI.Controls",
    "src\settings-ui\Settings.UI.Library",
    "src\settings-ui\Settings.UI.Controls",
    "src\settings-ui\Settings.UI",
    "src\settings-ui\QuickAccess.UI",
    "src\settings-ui\Settings.UI.XamlIndexBuilder",
    "src\common\UITestAutomation",
    "src\modules\awake\Awake.ModuleServices",
    "src\modules\awake\Awake",
    "src\modules\LightSwitch\Tests\LightSwitch.UITests"
)

$unitTestFiles = @(
    "src\settings-ui\Settings.UI.UnitTests\BackwardsCompatibility\BackCompatTestProperties.cs",
    "src\settings-ui\Settings.UI.UnitTests\Cmd\ICmdReprParsableTests.cs",
    "src\settings-ui\Settings.UI.UnitTests\Cmd\SetSettingCommandTests.cs",
    "src\settings-ui\Settings.UI.UnitTests\Mocks\IIOProviderMocks.cs",
    "src\settings-ui\Settings.UI.UnitTests\Mocks\ISettingsUtilsMocks.cs",
    "src\settings-ui\Settings.UI.UnitTests\ModelsTests\BasePTModuleSettingsSerializationTests.cs",
    "src\settings-ui\Settings.UI.UnitTests\ModelsTests\BasePTSettingsTest.cs",
    "src\settings-ui\Settings.UI.UnitTests\ModelsTests\HelperTest.cs",
    "src\settings-ui\Settings.UI.UnitTests\ModelsTests\SettingsRepositoryTest.cs",
    "src\settings-ui\Settings.UI.UnitTests\ModelsTests\SettingsUtilsTests.cs",
    "src\settings-ui\Settings.UI.UnitTests\TestSettingsSerializationContext.cs",
    "src\settings-ui\Settings.UI.UnitTests\ViewModelTests\General.cs",
    "src\settings-ui\Settings.UI.UnitTests\ViewModelTests\LightSwitch.cs",
    "src\settings-ui\Settings.UI.UnitTests\ViewModelTests\Localserver.cs"
)

$replacements = [ordered]@{
    "Microsoft.PowerToys.Settings.UI.UnitTests" = "Kit.Settings.UI.UnitTests"
    "Microsoft.PowerToys.Settings.UI.XamlIndexBuilder" = "Kit.Settings.UI.XamlIndexBuilder"
    "Microsoft.PowerToys.Settings.UI.Library" = "Kit.Settings.UI.Library"
    "Microsoft.PowerToys.Settings.UI.Controls" = "Kit.Settings.UI.Controls"
    "Microsoft.PowerToys.Settings.UI.Views" = "Kit.Settings.UI.Views"
    "Microsoft.PowerToys.Settings.UI.ViewModels" = "Kit.Settings.UI.ViewModels"
    "Microsoft.PowerToys.Settings.UI.Helpers" = "Kit.Settings.UI.Helpers"
    "Microsoft.PowerToys.Settings.UI.Services" = "Kit.Settings.UI.Services"
    "Microsoft.PowerToys.Settings.UI" = "Kit.Settings.UI"
    "Microsoft.PowerToys.Common.UI.Controls" = "Kit.Common.UI.Controls"
    "Microsoft.PowerToys.Common.UI" = "Kit.Common.UI"
    "Microsoft.PowerToys.QuickAccess" = "Kit.QuickAccess"
    "Microsoft.PowerToys.UITest" = "Kit.UITest"
    "Microsoft.PowerToys.Tools.XamlIndexBuilder" = "Kit.Settings.UI.XamlIndexBuilder"
    "Microsoft.PowerToys.Settings.UnitTest" = "Kit.Settings.UnitTest"
    "PowerToys.ModuleContracts" = "Kit.ModuleContracts"
    "PowerToys.GPOWrapper" = "Kit.GPOWrapper"
    "PowerToys.Interop" = "Kit.Interop"
    "namespace Settings.UI.Library" = "namespace Kit.Settings.UI.Library"
    "using Settings.UI.Library;" = "using Kit.Settings.UI.Library;"
    "using Settings.UI.Library." = "using Kit.Settings.UI.Library."
    "Settings.UI.Library.Attributes" = "Kit.Settings.UI.Library.Attributes"
    "Settings.UI.Library.Enumerations" = "Kit.Settings.UI.Library.Enumerations"
    "Settings.UI.Library.Resources" = "Kit.Settings.UI.Library.Resources"
    "Settings.UI.Library.Helpers" = "Kit.Settings.UI.Library.Helpers"
}

$filesToProcess = New-Object System.Collections.Generic.List[string]

foreach ($relDir in $targetDirs) {
    $absDir = Join-Path $RepoRoot $relDir
    if (Test-Path $absDir) {
        $found = Get-ChildItem -Path $absDir -Recurse -File | Where-Object {
            $_.Extension -in @(".cs", ".xaml", ".resw")
        }
        foreach ($f in $found) {
            $filesToProcess.Add($f.FullName)
        }
    }
}

foreach ($relFile in $unitTestFiles) {
    $absFile = Join-Path $RepoRoot $relFile
    if (Test-Path $absFile) {
        $filesToProcess.Add($absFile)
    }
}

$modifiedCount = 0
foreach ($filePath in $filesToProcess) {
    $content = [System.IO.File]::ReadAllText($filePath, [System.Text.Encoding]::UTF8)
    $original = $content
    foreach ($entry in $replacements.GetEnumerator()) {
        $content = $content.Replace($entry.Key, $entry.Value)
    }
    if ($content -ne $original) {
        [System.IO.File]::WriteAllText($filePath, $content, [System.Text.Encoding]::UTF8)
        $modifiedCount++
    }
}

Write-Host "Updated $modifiedCount files."
