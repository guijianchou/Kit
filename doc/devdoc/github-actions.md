# GitHub Actions

`.github/workflows/windows-ci.yml` runs on pull requests targeting `main`, pushes to `main`, and manual dispatch. It uses a Windows Server 2022 runner with Visual Studio MSBuild and .NET 10.

The workflow restores and builds `Kit.slnx` in x64 Debug mode, then runs the AI Hub, Settings UI, and UDP Test unit suites in sequence. It uploads their TRX reports and the solution build logs, including when a step fails. The minimum test counts guard against a workflow that silently discovers no tests.

The CI build requests the repository's unsigned sparse-package mode. It does not install the package, sign binaries, create a GitHub Release, publish a distributable, or run interactive WinUI smoke tests. Settings tests operate on the disposable hosted runner's user profile and restore their shared files before completion. If the full build fails, the step prints the MSBuild error log and adds compiler errors to the run annotations and step summary.

Test count thresholds must be updated when intentional test additions or removals change the checked suites. Keep the workflow's build and tests sequential; parallel MSBuild invocations in this solution can race while copying shared satellite resources.
