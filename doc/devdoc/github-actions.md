# GitHub Actions

`.github/workflows/windows-ci.yml` runs on pull requests targeting `main`, pushes to `main`, and manual dispatch. It uses a Windows Server 2022 runner with Node 24-compatible action versions (`checkout@v7`, `setup-msbuild@v3`, `setup-dotnet@v6`, `cache@v6`, `upload-artifact@v7`), Visual Studio MSBuild, and .NET 10.

The workflow restores and builds `Kit.slnx` in x64 Debug mode, verifies the runtime layout (confirming all seven `.NET` worker executables and WinGet assets/icons exist without duplicate WinUI binaries in the output root), and runs the Settings UI, WinGet, NetMap, UDP Test, Awake, LightSwitch CLI, Localserver, and AI Hub unit suites in sequence. It uploads their TRX reports, test stdout/stderr logs, and solution build logs, including when a step fails. The minimum test counts guard against a workflow that silently discovers no tests.

The CI build requests the repository's unsigned sparse-package mode and validates clean-checkout tests without requiring the gitignored `Source/` directory. It does not install the package, sign binaries, create a GitHub Release, publish a distributable, or run interactive WinUI smoke tests. Settings tests operate on the disposable hosted runner's user profile and restore their shared files before completion. If the build or Settings test suite fails, the step prints diagnostics/failed test names and appends them to the run annotations or step summary.

Test count thresholds must be updated when intentional test additions or removals change the checked suites. Keep the workflow's build and tests sequential; parallel MSBuild invocations in this solution can race while copying shared satellite resources.
