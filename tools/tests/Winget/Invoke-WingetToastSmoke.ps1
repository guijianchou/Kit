#requires -Version 7.0
<#
.SYNOPSIS
Builds an isolated probe and validates one real Winget Windows notification.
.DESCRIPTION
Requires a complete built Kit runtime. The generated project and all output stay
under TestResults/WingetToast. BuildOnly never starts the probe. A normal run uses
the production notifier, checks Windows notification history, and restores the
three registration subtrees captured by WingetToastProbe.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$RuntimeRoot,
    [string]$VisualStudioPath,
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $RuntimeRoot) { $RuntimeRoot = Join-Path $repoRoot "x64\$Configuration" }
$RuntimeRoot = [IO.Path]::GetFullPath($RuntimeRoot)
$requiredFiles = @(
    'Kit.exe', 'Kit.WingetService.dll', 'Kit.WingetLib.dll',
    'Microsoft.Management.Deployment.dll', 'Microsoft.Management.Deployment.winmd', 'Winget.ComInterop.NOTICE.txt'
)
foreach ($name in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $RuntimeRoot $name) -PathType Leaf)) {
        throw "Build the complete Kit runtime before this smoke test. Missing: $name"
    }
}
if (-not $BuildOnly -and @(Get-Process Kit,Kit.Settings,Kit.WingetService -ErrorAction SilentlyContinue).Count) {
    throw 'Close Kit and its Winget service before the notification smoke test.'
}

$reportRoot = Join-Path $repoRoot ('TestResults\WingetToast\' + [guid]::NewGuid().ToString('N'))
$projectRoot = Join-Path $reportRoot 'Probe'
[IO.Directory]::CreateDirectory($projectRoot) | Out-Null
$projectPath = Join-Path $projectRoot 'WingetToastSmoke.csproj'
$probeExe = Join-Path $projectRoot 'out\WingetToastSmoke.exe'
$utf8 = [Text.UTF8Encoding]::new($false)

# Use the selected runtime's compiled library so this probe never rebuilds or
# copies files into the live runtime, including when BuildOnly runs beside UI QA.
$commonPropsXml = [Security.SecurityElement]::Escape((Join-Path $repoRoot 'src\Common.Dotnet.CsWinRT.props'))
$probeSourceXml = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'WingetToastProbe.cs'))
$libraryXml = [Security.SecurityElement]::Escape((Join-Path $RuntimeRoot 'Kit.WingetLib.dll'))
$contentItems = foreach ($name in @('Microsoft.Management.Deployment.dll', 'Microsoft.Management.Deployment.winmd', 'Winget.ComInterop.NOTICE.txt')) {
    $sourceXml = [Security.SecurityElement]::Escape((Join-Path $RuntimeRoot $name))
    '    <Content Include="' + $sourceXml + '" Link="' + $name + '" CopyToOutputDirectory="PreserveNewest" />'
}
$project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="{0}" />
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>WingetToastSmoke</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
    <RunAnalyzers>false</RunAnalyzers>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <OutputPath>$(MSBuildProjectDirectory)\out\</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.cs" />
    <Compile Include="{1}" Link="WingetToastProbe.cs" />
    <Reference Include="Kit.WingetLib">
      <HintPath>{2}</HintPath>
      <Private>true</Private>
    </Reference>
{3}
  </ItemGroup>
</Project>
'@ -f $commonPropsXml, $probeSourceXml, $libraryXml, ($contentItems -join [Environment]::NewLine)
[IO.File]::WriteAllText($projectPath, $project, $utf8)
[IO.File]::WriteAllText((Join-Path $projectRoot 'Program.cs'), @'
if (args.Length != 2)
{
    Console.Error.WriteLine("Expected a complete Kit runtime directory and a report directory.");
    return 1;
}

try
{
    return await WingetToastProbe.RunAsync(args[0], args[1]);
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
'@, $utf8)

$buildStart = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh.exe).Source)
$buildStart.UseShellExecute = $false
$buildStart.CreateNoWindow = $true
$buildStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$buildStart.WorkingDirectory = $repoRoot
$buildStart.RedirectStandardOutput = $true
$buildStart.RedirectStandardError = $true
foreach ($argument in @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
    (Join-Path $repoRoot 'tools\build\build.ps1'), '-Platform', 'x64', '-Configuration', $Configuration,
    '-Path', $projectRoot, '-ExtraArgs', '/restore')) {
    $buildStart.ArgumentList.Add($argument)
}
if ($VisualStudioPath) {
    $VisualStudioPath = [IO.Path]::GetFullPath($VisualStudioPath)
    $msbuildDirectory = Join-Path $VisualStudioPath 'MSBuild\Current\Bin\amd64'
    if (-not (Test-Path -LiteralPath (Join-Path $msbuildDirectory 'MSBuild.exe') -PathType Leaf)) {
        throw "The selected Visual Studio installation has no x64 MSBuild: $VisualStudioPath"
    }
    $buildStart.Environment['VSINSTALLDIR'] = $VisualStudioPath
    $buildStart.Environment['Path'] = $msbuildDirectory + ';' + $buildStart.Environment['Path']
}

Write-Output "Building isolated toast probe: $projectPath"
$buildProcess = [Diagnostics.Process]::Start($buildStart)
try {
    $buildOutput = $buildProcess.StandardOutput.ReadToEndAsync()
    $buildErrors = $buildProcess.StandardError.ReadToEndAsync()
    $buildProcess.WaitForExit()
    [IO.File]::WriteAllText((Join-Path $reportRoot 'build-stdout.log'), $buildOutput.GetAwaiter().GetResult(), $utf8)
    [IO.File]::WriteAllText((Join-Path $reportRoot 'build-stderr.log'), $buildErrors.GetAwaiter().GetResult(), $utf8)
    if ($buildProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $probeExe -PathType Leaf)) {
        throw "Toast probe compilation failed. Inspect logs in $reportRoot"
    }
} finally {
    $buildProcess.Dispose()
}

if ($BuildOnly) {
    Write-Output 'BUILD ONLY passed: the probe was compiled without starting it or changing settings, registration, or notifications.'
    Write-Output "Probe: $probeExe"
    Write-Output "Report: $reportRoot"
    return
}

# TestResults can inherit a Low integrity label on development machines. Only
# this newly generated test executable needs Medium to use the desktop APIs.
& icacls.exe $probeExe /setintegritylevel M /L | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not set the generated notification probe to medium integrity.' }
$start = [Diagnostics.ProcessStartInfo]::new($probeExe)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.WorkingDirectory = $projectRoot
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add($RuntimeRoot)
$start.ArgumentList.Add($reportRoot)
Write-Output 'Running the real notification probe; no package downloads or installations are requested.'
$probeProcess = [Diagnostics.Process]::Start($start)
try {
    $output = $probeProcess.StandardOutput.ReadToEndAsync()
    $errors = $probeProcess.StandardError.ReadToEndAsync()
    $probeProcess.WaitForExit()
    $combinedOutput = $output.GetAwaiter().GetResult() + $errors.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $reportRoot 'toast-smoke.log'), $combinedOutput, $utf8)
    Write-Output $combinedOutput.TrimEnd()
    Write-Output "Report: $reportRoot"
    if ($probeProcess.ExitCode -eq 2) {
        throw 'Notification delivery was not validated: WinGet was unavailable or no actual updates were found. See the report.'
    }
    if ($probeProcess.ExitCode -ne 0) { throw "The notification smoke test failed. Inspect $reportRoot" }
} finally {
    $probeProcess.Dispose()
}
