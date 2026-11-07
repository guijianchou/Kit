// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Text;

namespace Kit.WingetLib;

internal interface IWingetCliProcessRunner
{
    Task<int> RunAsync(ProcessStartInfo startInfo, Action<string> outputReceived, Action cancellationRequested, CancellationToken cancellationToken);
}

internal sealed class WingetCliProcessRunner : IWingetCliProcessRunner
{
    public async Task<int> RunAsync(ProcessStartInfo startInfo, Action<string> outputReceived, Action cancellationRequested, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new WingetOperationException("Windows Package Manager could not be started.");
        }

        // Retain a bounded plain-text tail without interpreting localized messages
        // as percentages. Native installer UI and UAC remain owned by WinGet.
        var outputBuffer = new WingetOutputBuffer();
        var clock = Stopwatch.StartNew();
        using var drainCancellation = new CancellationTokenSource();
        using var publishCancellation = new CancellationTokenSource();
        Task output = DrainAsync(process.StandardOutput, outputBuffer, standardError: false, drainCancellation.Token);
        Task error = DrainAsync(process.StandardError, outputBuffer, standardError: true, drainCancellation.Token);
        Task drains = Task.WhenAll(output, error);
        Task publish = PublishAsync(outputBuffer, outputReceived, clock, publishCancellation.Token);
        using var cancellation = cancellationToken.Register(cancellationRequested);

        // Once WinGet starts, killing it cannot prove its elevated installer stopped.
        // Keep this operation occupied until the real CLI exit; the user can cancel
        // through the native installer. A cancellation request is not a result.
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            // Drain the final buffered output, but bound EOF waiting because an
            // installer descendant can inherit a pipe handle beyond the CLI exit.
            await Task.WhenAny(drains, Task.Delay(WingetOutputBuffer.PublishInterval, CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            await drainCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await drains.ConfigureAwait(false);
            }
            finally
            {
                await publishCancellation.CancelAsync().ConfigureAwait(false);
                await publish.ConfigureAwait(false);
                Publish(outputBuffer.TakeChangedSnapshot(clock.Elapsed, force: true), outputReceived);
            }
        }

        return process.ExitCode;
    }

    private static async Task DrainAsync(StreamReader reader, WingetOutputBuffer output, bool standardError, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                output.Append(buffer.AsSpan(0, read), standardError);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The native process already exited; a descendant still owns the pipe.
        }
        catch (IOException)
        {
            // A closed output pipe must not change the native installer result.
        }
    }

    private static async Task PublishAsync(WingetOutputBuffer output, Action<string> outputReceived, Stopwatch clock, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(WingetOutputBuffer.PublishInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Publish(output.TakeChangedSnapshot(clock.Elapsed), outputReceived);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static void Publish(string? snapshot, Action<string> outputReceived)
    {
        if (snapshot is null)
        {
            return;
        }

        try
        {
            outputReceived(snapshot);
        }
        catch (Exception)
        {
            // Display observers cannot stop pipe draining or change native results.
        }
    }
}

internal sealed class WingetCliInstaller(IWingetCliProcessRunner? runner = null)
{
    private readonly IWingetCliProcessRunner _runner = runner ?? new WingetCliProcessRunner();

    internal static string ExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");

    internal static ProcessStartInfo CreateStartInfo(string executablePath, WingetPackageUpdate package, bool upgrade, bool acceptPackageAgreements, WingetPackageOptions? options = null)
    {
        options = WingetSafetyPolicy.ValidateOptions(options);
        WingetSafetyPolicy.ValidateIdentityPart(package.Id, nameof(package.Id));
        WingetSafetyPolicy.ValidateIdentityPart(package.AvailableVersion, nameof(package.AvailableVersion));
        if (!string.Equals(package.Source, "winget", StringComparison.OrdinalIgnoreCase))
        {
            throw new WingetOperationException("Only the official winget community source is supported.");
        }

        var start = CreateProcessStartInfo(executablePath);
        foreach (string argument in new[]
        {
            upgrade ? "upgrade" : "install", "--id", package.Id, "--exact", "--version", package.AvailableVersion,
            "--source", "winget", options.Silent ? "--silent" : "--interactive", "--disable-interactivity",
        })
        {
            start.ArgumentList.Add(argument);
        }

        if (!upgrade)
        {
            start.ArgumentList.Add("--no-upgrade");
        }

        if (acceptPackageAgreements)
        {
            start.ArgumentList.Add("--accept-package-agreements");
        }

        AddOptions(start, options);
        if (upgrade && options.IncludeUnknown)
        {
            start.ArgumentList.Add("--include-unknown");
        }

        return start;
    }

    internal static ProcessStartInfo CreateUninstallStartInfo(string executablePath, WingetPackageUpdate package, WingetPackageOptions? options = null)
    {
        options = WingetSafetyPolicy.ValidateOptions(options, uninstall: true);
        WingetSafetyPolicy.ValidateIdentityPart(package.Id, nameof(package.Id));
        WingetSafetyPolicy.ValidateIdentityPart(package.InstalledVersion, nameof(package.InstalledVersion));
        if (!package.CanUninstall || package.Source is not ("winget" or "msstore" or "local" or "msix"))
        {
            throw new WingetOperationException("The installed package cannot be uniquely selected for removal.");
        }

        var start = CreateProcessStartInfo(executablePath);
        foreach (string argument in new[]
        {
            "uninstall", "--id", package.Id, "--exact", "--version", package.InstalledVersion,
            options.Silent ? "--silent" : "--interactive", "--disable-interactivity",
        })
        {
            start.ArgumentList.Add(argument);
        }

        if (package.Source is "winget" or "msstore")
        {
            start.ArgumentList.Add("--source");
            start.ArgumentList.Add(package.Source);
        }

        AddOptions(start, options);
        return start;
    }

    internal Task<WingetInstallResult> RunAsync(WingetPackageUpdate package, bool upgrade, bool acceptPackageAgreements, IProgress<WingetOperationProgress>? progress, CancellationToken token, WingetPackageOptions? options = null)
    {
        token.ThrowIfCancellationRequested();
        return RunProcessAsync(CreateStartInfo(ExecutablePath, package, upgrade, acceptPackageAgreements, options), progress, token);
    }

    internal Task<WingetInstallResult> RunUninstallAsync(WingetPackageUpdate package, IProgress<WingetOperationProgress>? progress, CancellationToken token, WingetPackageOptions? options = null)
    {
        token.ThrowIfCancellationRequested();
        return RunProcessAsync(CreateUninstallStartInfo(ExecutablePath, package, options), progress, token);
    }

    internal static WingetInstallResult MapExitCode(int exitCode)
    {
        bool reboot = exitCode is 3010 or 1641 or unchecked((int)0x8A150109) or unchecked((int)0x8A15010B);
        bool cancelled = exitCode is 1223 or 1602 or unchecked((int)0x800704C7) or unchecked((int)0x80004004)
            or unchecked((int)0x8A150005) or unchecked((int)0x8A15010C);
        bool success = exitCode == 0 || reboot;
        return new WingetInstallResult(
            success,
            reboot,
            false,
            success ? null : cancelled ? "The WinGet installer or administrator approval was cancelled." : $"Windows Package Manager exited with code 0x{exitCode:X8}. Check WinGet's installation log for details.",
            exitCode) { Cancelled = cancelled };
    }

    private async Task<WingetInstallResult> RunProcessAsync(ProcessStartInfo start, IProgress<WingetOperationProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string? output = null;
        Report(progress, "WaitingForInstaller");
        int exitCode = await _runner.RunAsync(
            start,
            snapshot =>
            {
                output = snapshot;
                Report(progress, "WaitingForInstaller", snapshot);
            },
            () => Report(progress, "Cancelling"),
            token).ConfigureAwait(false);
        return MapExitCode(exitCode) with { CancellationRequested = token.IsCancellationRequested, Output = output };
    }

    private static ProcessStartInfo CreateProcessStartInfo(string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath) || !string.Equals(Path.GetFullPath(executablePath), ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new WingetOperationException("Windows Package Manager must use its registered per-user executable path.");
        }

        return new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
        };
    }

    private static void AddOptions(ProcessStartInfo start, WingetPackageOptions options)
    {
        if (options.Scope is not null)
        {
            start.ArgumentList.Add("--scope");
            start.ArgumentList.Add(options.Scope);
        }

        if (options.Architecture is not null)
        {
            start.ArgumentList.Add("--architecture");
            start.ArgumentList.Add(options.Architecture);
        }
    }

    private static void Report(IProgress<WingetOperationProgress>? progress, string stage, string? output = null)
    {
        try
        {
            progress?.Report(new WingetOperationProgress(stage, null) { Output = output });
        }
        catch (Exception)
        {
            // Progress observers cannot orphan a native installation or alter its result.
        }
    }
}
