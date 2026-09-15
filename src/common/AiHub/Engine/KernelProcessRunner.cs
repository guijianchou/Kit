namespace Kit.AiHub.Engine;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

internal enum KernelProcessFailure
{
    None,
    Cancelled,
    Timeout,
    OutputLimit,
    StartFailed,
    AccessDenied,
    CleanupFailed,
}

internal sealed record KernelProcessResult(int ExitCode, string StandardOutput, string StandardError, KernelProcessFailure Failure);

internal static class KernelProcessRunner
{
    internal static async Task<KernelProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        string? input,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int maximumOutputCharacters = 8 * 1024 * 1024,
        int maximumErrorCharacters = 64 * 1024)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutputCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumErrorCharacters);
        if (cancellationToken.IsCancellationRequested)
        {
            return new(-1, string.Empty, string.Empty, KernelProcessFailure.Cancelled);
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardInputEncoding = new UTF8Encoding(false);
        startInfo.StandardOutputEncoding = new UTF8Encoding(false);
        startInfo.StandardErrorEncoding = new UTF8Encoding(false);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var process = new Process { StartInfo = startInfo };
        using SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        JobExtendedLimits limits = default;
        limits.BasicLimits.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimits>()))
        {
            return new(-1, string.Empty, string.Empty, Marshal.GetLastWin32Error() == 5 ? KernelProcessFailure.AccessDenied : KernelProcessFailure.StartFailed);
        }

        bool started = false;
        int exitCode = -1;
        KernelProcessFailure failure = KernelProcessFailure.None;
        Task<string> outputTask = Task.FromResult(string.Empty);
        Task<string> errorTask = Task.FromResult(string.Empty);
        Task inputTask = Task.CompletedTask;
        Task exitTask = Task.CompletedTask;
        CancellationTokenRegistration cancellationRegistration = default;
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started)
            {
                failure = KernelProcessFailure.StartFailed;
            }
            else if (!AssignProcessToJobObject(job, process.SafeHandle))
            {
                failure = Marshal.GetLastWin32Error() == 5 ? KernelProcessFailure.AccessDenied : KernelProcessFailure.StartFailed;
            }
            else
            {
                // Closing the host closes its job handle and terminates every assigned child.
                cancellationRegistration = lifetime.Token.Register(() => StopProcess(process, job));
                outputTask = ReadBoundedAsync(process.StandardOutput, maximumOutputCharacters, lifetime.Token);
                errorTask = ReadBoundedAsync(process.StandardError, maximumErrorCharacters, lifetime.Token);
                inputTask = WriteInputAsync(process.StandardInput, input, lifetime.Token);
                exitTask = process.WaitForExitAsync(lifetime.Token);
                var pending = new List<Task> { outputTask, errorTask, inputTask, exitTask };
                while (pending.Count > 0)
                {
                    Task completed = await Task.WhenAny(pending).ConfigureAwait(false);
                    await completed.ConfigureAwait(false);
                    pending.Remove(completed);
                    if (completed == exitTask)
                    {
                        // A detached descendant must not keep the redirected pipes open.
                        StopProcess(process, job);
                    }
                }

                exitCode = process.ExitCode;
            }
        }
        catch (OperationCanceledException)
        {
            failure = cancellationToken.IsCancellationRequested ? KernelProcessFailure.Cancelled : KernelProcessFailure.Timeout;
        }
        catch (OutputLimitException)
        {
            failure = KernelProcessFailure.OutputLimit;
        }
        catch (UnauthorizedAccessException)
        {
            failure = KernelProcessFailure.AccessDenied;
        }
        catch (Win32Exception exception)
        {
            failure = exception.NativeErrorCode == 5 ? KernelProcessFailure.AccessDenied : KernelProcessFailure.StartFailed;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
        {
            failure = KernelProcessFailure.StartFailed;
        }
        finally
        {
            if (started)
            {
                StopProcess(process, job);
                lifetime.Cancel();
                try
                {
                    await Task.WhenAll(outputTask, errorTask, inputTask, exitTask)
                        .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    failure = KernelProcessFailure.CleanupFailed;
                    _ = Task.WhenAll(outputTask, errorTask, inputTask, exitTask).ContinueWith(
                        completed => _ = completed.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                catch (Exception)
                {
                    // The main wait already classified these bounded I/O failures.
                }

                try
                {
                    using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(cleanupDeadline.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    failure = KernelProcessFailure.CleanupFailed;
                }
            }

            await cancellationRegistration.DisposeAsync().ConfigureAwait(false);
        }

        if (failure == KernelProcessFailure.None && cancellationToken.IsCancellationRequested)
        {
            failure = KernelProcessFailure.Cancelled;
        }

        return failure == KernelProcessFailure.None
            ? new(exitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false), failure)
            : new(exitCode, string.Empty, string.Empty, failure);
    }

    internal static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var text = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (count > maximumCharacters - text.Length)
            {
                throw new OutputLimitException();
            }

            text.Append(buffer, 0, count);
        }

        return text.ToString();
    }

    private static async Task WriteInputAsync(StreamWriter writer, string? input, CancellationToken cancellationToken)
    {
        try
        {
            if (input is not null)
            {
                await writer.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Disposing the pipe avoids a synchronous StreamWriter flush on cancellation.
            writer.BaseStream.Dispose();
        }
    }

    private static void StopProcess(Process process, SafeFileHandle job)
    {
        if (!job.IsClosed && !job.IsInvalid)
        {
            _ = TerminateJobObject(job, 1);
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Kill-on-close remains the fallback when the process exits concurrently.
        }
    }

    internal sealed class OutputLimitException : IOException
    {
        internal OutputLimitException()
            : base("Kernel output exceeded the response limit.")
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobIoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimits
    {
        internal JobBasicLimits BasicLimits;
        internal JobIoCounters IoCounters;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobExtendedLimits information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
