// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kit.WingetLib;

/// <summary>Gives WinGet a real, invisible console so it emits its native progress.</summary>
internal sealed class WingetPseudoConsoleRunner(IWingetCliProcessRunner? fallback = null) : IWingetCliProcessRunner
{
    private readonly IWingetCliProcessRunner _fallback = fallback ?? new WingetCliProcessRunner();

    public async Task<int> RunAsync(ProcessStartInfo startInfo, Action<string> outputReceived, Action cancellationRequested, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(startInfo);
        ConsoleSession session;
        try
        {
            session = new ConsoleSession();
        }
        catch (Exception exception) when (exception is Win32Exception or COMException or EntryPointNotFoundException or DllNotFoundException)
        {
            // Preparation failed before CreateProcess: retrying cannot duplicate an installation.
            return await _fallback.RunAsync(startInfo, outputReceived, cancellationRequested, cancellationToken).ConfigureAwait(false);
        }

        using (session)
        {
            var output = new WingetOutputBuffer(terminal: true);
            var clock = Stopwatch.StartNew();
            using var drainCancellation = new CancellationTokenSource();
            using var publishCancellation = new CancellationTokenSource();
            Task drain = DrainAsync(session.Output, output, drainCancellation.Token);
            Task publish = PublishAsync(output, outputReceived, clock, publishCancellation.Token);
            bool started = false;
            int exitCode = 0;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                started = session.TryStart(startInfo);
                if (started)
                {
                    using var registration = cancellationToken.Register(() => Notify(cancellationRequested));

                    // Cancellation is only a notification. Neither this CLI nor an elevated
                    // installer is terminated; its real exit remains the operation boundary.
                    exitCode = await session.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                // ClosePseudoConsole may synchronously flush terminal output. Keep the
                // reader alive on a separate task until the console has finished closing.
                await Task.Run(session.CloseConsole, CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAny(drain, Task.Delay(WingetOutputBuffer.PublishInterval, CancellationToken.None)).ConfigureAwait(false);
                await drainCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await drain.ConfigureAwait(false);
                }
                finally
                {
                    await publishCancellation.CancelAsync().ConfigureAwait(false);
                    await publish.ConfigureAwait(false);
                    Publish(output.TakeChangedSnapshot(clock.Elapsed, force: true), outputReceived);
                }
            }

            if (started)
            {
                return exitCode;
            }
        }

        // CreateProcess returned false and produced no process. Once it returns true,
        // no exception, output failure or exit code can reach this fallback path.
        return await _fallback.RunAsync(startInfo, outputReceived, cancellationRequested, cancellationToken).ConfigureAwait(false);
    }

    internal static string QuoteArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Contains('\0'))
        {
            throw new ArgumentException("A process argument cannot contain a null character.", nameof(argument));
        }

        var quoted = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            quoted.Append('\\', character == '"' ? (slashes * 2) + 1 : slashes);
            quoted.Append(character);
            slashes = 0;
        }

        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void Validate(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute || !Path.IsPathFullyQualified(startInfo.FileName)
            || !string.IsNullOrEmpty(startInfo.Arguments) || !string.IsNullOrEmpty(startInfo.Verb)
            || !string.IsNullOrEmpty(startInfo.UserName))
        {
            throw new ArgumentException("The console runner requires an absolute executable, ArgumentList and the current user without a shell.", nameof(startInfo));
        }
    }

    private static async Task DrainAsync(SafeFileHandle pipe, WingetOutputBuffer output, CancellationToken cancellationToken)
    {
        var bytes = new byte[4096];
        var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!Native.PeekNamedPipe(pipe, nint.Zero, 0, nint.Zero, out uint available, nint.Zero))
                {
                    break;
                }

                if (available == 0)
                {
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // There is exactly one reader; reading only the bytes PeekNamedPipe
                // observed keeps synchronous anonymous-pipe reads non-blocking.
                if (!Native.ReadFile(pipe, bytes, Math.Min(available, (uint)bytes.Length), out uint read, nint.Zero) || read == 0)
                {
                    break;
                }

                int count = decoder.GetChars(bytes, 0, checked((int)read), characters, 0);
                output.Append(characters.AsSpan(0, count));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            int count = decoder.GetChars([], 0, 0, characters, 0, flush: true);
            output.Append(characters.AsSpan(0, count));
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

    private static void Publish(string? snapshot, Action<string> observer)
    {
        if (snapshot is not null)
        {
            Notify(() => observer(snapshot));
        }
    }

    private static void Notify(Action observer)
    {
        try
        {
            observer();
        }
        catch (Exception)
        {
            // A display callback cannot abandon a running installer or change its result.
        }
    }

    private sealed class ConsoleSession : IDisposable
    {
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint StartfUseShowWindow = 0x00000001;
        private const uint StartfUseStdHandles = 0x00000100;
        private static readonly nuint PseudoConsoleAttribute = 0x00020016;
        private SafeFileHandle? _input;
        private SafeFileHandle? _output;
        private SafeProcessHandle? _process;
        private nint _console;
        private nint _attributes;
        private bool _attributesInitialized;

        internal ConsoleSession()
        {
            try
            {
                if (!Native.CreatePipe(out var inputRead, out var inputWrite, nint.Zero, 0))
                {
                    int error = Marshal.GetLastWin32Error();
                    inputRead?.Dispose();
                    inputWrite?.Dispose();
                    throw new Win32Exception(error);
                }

                _input = inputWrite;
                using (inputRead)
                {
                    if (!Native.CreatePipe(out var outputRead, out var outputWrite, nint.Zero, 64 * 1024))
                    {
                        int error = Marshal.GetLastWin32Error();
                        outputRead?.Dispose();
                        outputWrite?.Dispose();
                        throw new Win32Exception(error);
                    }

                    _output = outputRead;
                    using (outputWrite)
                    {
                        int result = Native.CreatePseudoConsole(new Coord { X = 160, Y = 40 }, inputRead, outputWrite, 0, out _console);
                        Marshal.ThrowExceptionForHR(result);
                    }
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal SafeFileHandle Output => _output!;

        internal bool TryStart(ProcessStartInfo startInfo)
        {
            // Attribute setup happens after the output reader starts, so even a setup
            // failure can close ConPTY while its final output is being drained.
            nuint size = 0;
            _ = Native.InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref size);
            if (size == 0)
            {
                return false;
            }

            _attributes = Marshal.AllocHGlobal(checked((nint)size));
            if (!Native.InitializeProcThreadAttributeList(_attributes, 1, 0, ref size))
            {
                return false;
            }

            _attributesInitialized = true;
            if (!Native.UpdateProcThreadAttribute(_attributes, 0, PseudoConsoleAttribute, _console, (nuint)nint.Size, nint.Zero, nint.Zero))
            {
                return false;
            }

            var command = new StringBuilder(QuoteArgument(startInfo.FileName));
            foreach (string argument in startInfo.ArgumentList)
            {
                command.Append(' ').Append(QuoteArgument(argument));
            }

            if (command.Length >= 32767)
            {
                throw new ArgumentException("The process command line exceeds the Windows limit.", nameof(startInfo));
            }

            var environment = new StringBuilder();
            foreach (var pair in startInfo.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (pair.Key.Contains('\0') || pair.Value?.Contains('\0') == true)
                {
                    throw new ArgumentException("An environment entry cannot contain a null character.", nameof(startInfo));
                }

                if (pair.Value is not null)
                {
                    environment.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
                }
            }

            // StringToHGlobalUni supplies the second terminating null, including an empty block.
            nint environmentBlock = Marshal.StringToHGlobalUni(environment.Append('\0').ToString());
            try
            {
                var startup = new StartupInfoEx
                {
                    // Like Windows Terminal's ConptyConnection, explicitly clear all
                    // standard handles with STARTF_USESTDHANDLES. Without it Windows
                    // preserves a parent's redirected handles even with inherit=false,
                    // bypassing ConPTY and suppressing WinGet's console progress.
                    StartupInfo = new StartupInfo
                    {
                        Size = Marshal.SizeOf<StartupInfoEx>(),
                        Flags = StartfUseStdHandles | StartfUseShowWindow,
                        ShowWindow = 0,
                        StandardInput = nint.Zero,
                        StandardOutput = nint.Zero,
                        StandardError = nint.Zero,
                    },
                    AttributeList = _attributes,
                };
                bool created = Native.CreateProcessW(
                    startInfo.FileName,
                    command,
                    nint.Zero,
                    nint.Zero,
                    false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    environmentBlock,
                    string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
                    ref startup,
                    out var information);
                if (!created)
                {
                    return false;
                }

                // The success boundary is deliberately followed only by handle adoption.
                // Do not fall back after this point, even if the CLI immediately fails.
                _process = new SafeProcessHandle(information.Process, ownsHandle: true);
                using var thread = new SafeFileHandle(information.Thread, ownsHandle: true);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }

        internal async Task<int> WaitForExitAsync()
        {
            // No cancellation token is passed: the original process handle is kept alive
            // until Windows signals exit, including while an elevated installer runs.
            while (true)
            {
                bool signaled = Native.WaitForSingleObject(_process!, 0) == 0;
                if (Native.GetExitCodeProcess(_process!, out uint exitCode) && (signaled || exitCode != 259))
                {
                    return unchecked((int)exitCode);
                }

                // A failed monitor call is not exit evidence. Retain both the slot and
                // HPCON and retry; closing the console here could terminate a live CLI.
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
        }

        internal void CloseConsole()
        {
            nint console = Interlocked.Exchange(ref _console, nint.Zero);
            if (console != nint.Zero)
            {
                Native.ClosePseudoConsole(console);
            }
        }

        public void Dispose()
        {
            CloseConsole();
            if (_attributes != nint.Zero)
            {
                if (_attributesInitialized)
                {
                    Native.DeleteProcThreadAttributeList(_attributes);
                }

                Marshal.FreeHGlobal(_attributes);
                _attributes = nint.Zero;
            }

            _process?.Dispose();
            _input?.Dispose();
            _output?.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        internal short X;
        internal short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size;
        internal nint Reserved;
        internal nint Desktop;
        internal nint Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort ReservedSize;
        internal nint ReservedBytes;
        internal nint StandardInput;
        internal nint StandardOutput;
        internal nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal nint Process;
        internal nint Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint security, uint size);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        internal static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        internal static extern void ClosePseudoConsole(nint console);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        internal static extern void DeleteProcThreadAttributeList(nint list);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(
            string application,
            StringBuilder command,
            nint processSecurity,
            nint threadSecurity,
            [MarshalAs(UnmanagedType.Bool)] bool inherit,
            uint flags,
            nint environment,
            string? directory,
            ref StartupInfoEx startup,
            out ProcessInformation process);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekNamedPipe(SafeFileHandle pipe, nint buffer, uint size, nint read, out uint available, nint left);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadFile(SafeFileHandle pipe, [Out] byte[] buffer, uint size, out uint read, nint overlapped);
    }
}
