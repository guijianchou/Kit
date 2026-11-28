// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Kit.WingetLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winget.UnitTests;

[TestClass]
public sealed class WingetPseudoConsoleRunnerTests
{
    [TestMethod]
    public void WindowsArgumentQuotingRoundTripsWhitespaceQuotesBackslashesAndShellCharacters()
    {
        string[] arguments = [string.Empty, "plain", "two words", "a\tb", "quoted\"value", "tail \\", "before\\\"quote", "中文 & $() ; ' %PATH%"];
        string command = "test.exe " + string.Join(' ', arguments.Select(WingetPseudoConsoleRunner.QuoteArgument));
        nint pointer = CommandLineToArgvW(command, out int count);
        Assert.AreNotEqual(nint.Zero, pointer);
        try
        {
            var actual = Enumerable.Range(1, count - 1).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * nint.Size))).ToArray();
            CollectionAssert.AreEqual(arguments, actual);
        }
        finally
        {
            _ = LocalFree(pointer);
        }
    }

    [TestMethod]
    public async Task NativeConsoleCapturesOutputAndEnvironmentAndReturnsOriginalExitWithoutFallback()
    {
        RequireConPty();
        var fallback = new RecordingFallback();
        var runner = new WingetPseudoConsoleRunner(fallback);
        var start = PowerShell("[Console]::WriteLine('REDIRECTED=' + [Console]::IsOutputRedirected); [Console]::WriteLine([Environment]::GetEnvironmentVariable('KIT_PTY_TEST')); [Console]::WriteLine('100%'); exit 73");
        start.Environment["KIT_PTY_TEST"] = "ConPTY 中文 ' $ & = preserved";
        string output = string.Empty;
        int exit = await runner.RunAsync(start, text => output = text, () => Assert.Fail("Unexpected cancellation notification."), CancellationToken.None);
        Assert.AreEqual(73, exit);
        Assert.AreEqual(0, fallback.Calls);
        StringAssert.Contains(output, "REDIRECTED=False");
        StringAssert.Contains(output, start.Environment["KIT_PTY_TEST"]!);
        StringAssert.Contains(output, "100%");
        Assert.IsTrue(output.Length <= WingetOutputBuffer.MaximumCharacters);
    }

    [TestMethod]
    public async Task InstalledWinGetAliasRunsReadOnlyVersionInThePseudoConsoleWithoutFallback()
    {
        RequireConPty();
        if (!File.Exists(WingetCliInstaller.ExecutablePath))
        {
            Assert.Inconclusive("The current user has no registered WinGet executable alias.");
        }

        var start = new ProcessStartInfo(WingetCliInstaller.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(WingetCliInstaller.ExecutablePath)!,
        };
        start.ArgumentList.Add("--version");
        var fallback = new RecordingFallback();
        string output = string.Empty;
        int result = await new WingetPseudoConsoleRunner(fallback).RunAsync(start, text => output = text, () => { }, CancellationToken.None);
        Assert.AreEqual(0, result, output);
        Assert.AreEqual(0, fallback.Calls, "AppExecutionAlias launch must succeed directly through CreateProcess and ConPTY.");
        Assert.IsTrue(output.Contains('v') && output.Any(char.IsDigit), "WinGet's read-only version command must produce actual output.");
    }

    [TestMethod]
    public async Task NativeConsoleRedrawsPublishTwentyFiveThenSeventyFiveBeforeInstalling()
    {
        RequireConPty();
        const string script = "[Console]::WriteLine('Downloading https://example.invalid/synthetic.exe'); "
            + "[Console]::Write('  1.00 MB / 4.00 MB'); Start-Sleep -Milliseconds 900; "
            + "[Console]::SetCursorPosition(2, [Console]::CursorTop); [Console]::Write('3'); Start-Sleep -Milliseconds 900; "
            + "[Console]::SetCursorPosition(0, [Console]::CursorTop); [Console]::Write('  2 MB / 4 MB       '); Start-Sleep -Milliseconds 900; "
            + "[Console]::Write([char]13); [Console]::Write('  3.00 MB / 4.00 MB'); Start-Sleep -Milliseconds 900; "
            + "[Console]::WriteLine(); [Console]::WriteLine('Starting package install...'); Start-Sleep -Milliseconds 900; exit 0";
        var fallback = new RecordingFallback();
        var parser = new WingetCliProgress();
        var observations = new List<WingetOperationProgress>();
        int exit = await new WingetPseudoConsoleRunner(fallback).RunAsync(
            PowerShell(script),
            snapshot => observations.Add(parser.Read(snapshot)),
            () => { },
            CancellationToken.None);
        Assert.AreEqual(0, exit);
        Assert.AreEqual(0, fallback.Calls);
        string evidence = string.Join("\n--- snapshot ---\n", observations.Select(item => item.Output));
        int quarter = observations.FindIndex(item => item.Stage == "Downloading" && item.Percentage == 25);
        int threeQuarters = observations.FindIndex(item => item.Stage == "Downloading" && item.Percentage == 75);
        int half = observations.FindIndex(item => item.Stage == "Downloading" && item.Percentage == 50);
        Assert.IsTrue(quarter >= 0, "The first real ConPTY download frame was not parsed. " + evidence);
        Assert.IsTrue(
            threeQuarters > quarter && half > threeQuarters,
            "The single-character cursor edit must produce 75% before the later complete 50% redraw. " + evidence);
        Assert.IsTrue(
            observations.FindLastIndex(item => item.Stage == "Downloading" && item.Percentage == 75) > half,
            "The later carriage-return redraw must return from 50% to 75%. " + evidence);
        Assert.AreEqual("Installing", observations[^1].Stage, evidence);
        Assert.IsNull(observations[^1].Percentage, "Installation must clear the preceding download percentage. " + evidence);
    }

    [TestMethod]
    public async Task CancelNotifiesButWaitsForTheNativeProcessAndPreservesItsActualResult()
    {
        RequireConPty();
        var fallback = new RecordingFallback();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        int notices = 0;
        Task<int> operation = new WingetPseudoConsoleRunner(fallback).RunAsync(
            PowerShell("[Console]::WriteLine('READY'); Start-Sleep -Milliseconds 1500; [Console]::WriteLine('FINISHED'); exit 1223"),
            text =>
            {
                if (text.Contains("READY", StringComparison.Ordinal))
                {
                    ready.TrySetResult(true);
                }
            },
            () => Interlocked.Increment(ref notices),
            cancellation.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        cancellation.Cancel();
        Assert.AreEqual(1, notices);
        Assert.IsFalse(operation.IsCompleted, "Cancellation must not free the installer slot before its real exit.");
        Assert.AreEqual(1223, await operation);
        Assert.AreEqual(0, fallback.Calls);
    }

    [TestMethod]
    public async Task OutputIsBoundedAndObserverFailureCannotChangeNativeExitOrRepeatTheCommand()
    {
        RequireConPty();
        var fallback = new RecordingFallback();
        int snapshots = 0;
        bool oversized = false;
        var runner = new WingetPseudoConsoleRunner(fallback);
        int result = await runner.RunAsync(
            PowerShell("1..2500 | ForEach-Object { [Console]::WriteLine(('x' * 80)) }; [Console]::WriteLine('LAST'); [Environment]::Exit(-1978334967)"),
            text =>
            {
                oversized |= text.Length > WingetOutputBuffer.MaximumCharacters;
                Interlocked.Increment(ref snapshots);
                throw new InvalidOperationException("Synthetic broken display observer.");
            },
            () => { },
            CancellationToken.None);
        Assert.AreEqual(-1978334967, result);
        Assert.IsTrue(snapshots > 0);
        Assert.IsFalse(oversized);
        Assert.AreEqual(0, fallback.Calls);
    }

    [TestMethod]
    public async Task FailedProcessCreationFallsBackOnceButPreCancelledRequestsNeverStart()
    {
        var fallback = new RecordingFallback();
        var runner = new WingetPseudoConsoleRunner(fallback);
        var missing = new ProcessStartInfo(Path.Combine(Path.GetTempPath(), "missing-winget-test-" + Guid.NewGuid().ToString("N") + ".exe"))
        {
            UseShellExecute = false,
        };
        Assert.AreEqual(42, await runner.RunAsync(missing, _ => { }, () => { }, CancellationToken.None));
        Assert.AreEqual(1, fallback.Calls);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => runner.RunAsync(missing, _ => { }, () => { }, cancelled.Token));
        Assert.AreEqual(1, fallback.Calls);
    }

    private static ProcessStartInfo PowerShell(string script)
    {
        // Windows PowerShell 5.1 uses its legacy OEM output encoding by default and
        // can replace Chinese with '?' before ConPTY sees it. Set the fixture's writer
        // encoding explicitly; ConPTY's transport itself always remains UTF-8.
        script = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); " + script;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    private static void RequireConPty()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            Assert.Inconclusive("ConPTY requires Windows 10 1809 or newer.");
        }
    }

    private sealed class RecordingFallback : IWingetCliProcessRunner
    {
        internal int Calls { get; private set; }

        public Task<int> RunAsync(ProcessStartInfo startInfo, Action<string> outputReceived, Action cancellationRequested, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(42);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string command, out int count);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LocalFree(nint memory);
}
