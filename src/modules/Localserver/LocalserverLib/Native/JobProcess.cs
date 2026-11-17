using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocalServerHub.Windows.Native;

/// <summary>Starts a redirected service inside its job before its first instruction runs.</summary>
internal sealed partial class JobProcess : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private static readonly nuint ProcThreadAttributeHandleList = 0x00020002;

    private JobProcess(Process process, StreamWriter input, FileStream output, FileStream error)
    {
        Process = process;
        Input = input;
        Output = output;
        Error = error;
    }

    internal Process Process { get; }

    internal StreamWriter Input { get; }

    internal FileStream Output { get; }

    internal FileStream Error { get; }

    internal static JobProcess Start(ProcessStartInfo info, JobObject job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(job);
        if (info.UseShellExecute || !info.RedirectStandardInput || !info.RedirectStandardOutput || !info.RedirectStandardError)
            throw new ArgumentException("Job processes require all three redirected standard streams.", nameof(info));
        if (!string.IsNullOrEmpty(info.Arguments) && info.ArgumentList.Count != 0)
            throw new ArgumentException("Arguments and ArgumentList cannot both be specified.", nameof(info));
        cancellationToken.ThrowIfCancellationRequested();

        using var inputPipe = Pipe.Create(parentReads: false);
        using var outputPipe = Pipe.Create(parentReads: true);
        using var errorPipe = Pipe.Create(parentReads: true);
        using var attributes = new HandleList(inputPipe.Child, outputPipe.Child, errorPipe.Child);
        var startup = new StartupInfoEx
        {
            StartupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfoEx>(),
                Flags = StartfUseStdHandles,
                StandardInput = inputPipe.Child.DangerousGetHandle(),
                StandardOutput = outputPipe.Child.DangerousGetHandle(),
                StandardError = errorPipe.Child.DangerousGetHandle(),
            },
            AttributeList = attributes.Pointer,
        };

        var command = new StringBuilder(QuoteArgument(info.FileName));
        if (info.ArgumentList.Count != 0)
        {
            foreach (string argument in info.ArgumentList)
                command.Append(' ').Append(QuoteArgument(argument));
        }
        else if (!string.IsNullOrEmpty(info.Arguments))
        {
            command.Append(' ').Append(info.Arguments);
        }

        string environment = string.Concat(info.Environment
            .Where(entry => entry.Value is not null)
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Key + "=" + entry.Value + '\0')) + '\0';
        nint environmentBlock = Marshal.StringToHGlobalUni(environment);
        ProcessInformation native = default;
        Process? process = null;
        FileStream? inputStream = null;
        FileStream? outputStream = null;
        FileStream? errorStream = null;
        StreamWriter? input = null;
        try
        {
            uint flags = CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent;
            if (info.CreateNoWindow) flags |= CreateNoWindow;
            if (!CreateProcessW(null, command, nint.Zero, nint.Zero, true, flags,
                    environmentBlock, info.WorkingDirectory, ref startup, out native))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the suspended service process.");

            // No user code, including a fast launcher, can execute before this succeeds.
            job.Assign(native.Process);
            cancellationToken.ThrowIfCancellationRequested();
            process = Process.GetProcessById(checked((int)native.ProcessId));
            _ = process.SafeHandle;
            inputStream = new FileStream(inputPipe.TakeParent(), FileAccess.Write, 4096, isAsync: false);
            outputStream = new FileStream(outputPipe.TakeParent(), FileAccess.Read, 4096, isAsync: false);
            errorStream = new FileStream(errorPipe.TakeParent(), FileAccess.Read, 4096, isAsync: false);
            input = new StreamWriter(inputStream, info.StandardInputEncoding ?? new UTF8Encoding(false), 4096) { AutoFlush = true };
            inputStream = null; // StreamWriter now owns it.
            cancellationToken.ThrowIfCancellationRequested();
            if (ResumeThread(native.Thread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to resume the service process.");

            return new JobProcess(process, input, outputStream, errorStream);
        }
        catch
        {
            if (native.Process != nint.Zero)
            {
                _ = TerminateProcess(native.Process, 1);
                _ = WaitForSingleObject(native.Process, 5000);
            }

            input?.Dispose();
            inputStream?.Dispose();
            outputStream?.Dispose();
            errorStream?.Dispose();
            process?.Dispose();
            throw;
        }
        finally
        {
            if (native.Thread != nint.Zero) _ = CloseHandle(native.Thread);
            if (native.Process != nint.Zero) _ = CloseHandle(native.Process);
            Marshal.FreeHGlobal(environmentBlock);
        }
    }

    // Windows CRT argument quoting, matching ProcessStartInfo.ArgumentList: only
    // backslashes immediately before a quote or the closing delimiter are doubled.
    private static string QuoteArgument(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("An argument contains a null character.", nameof(value));
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            result.Append('\\', character == '"' ? (slashes * 2) + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }

        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public void Dispose()
    {
        try { Input.Dispose(); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 109 or 232 or 233)
        {
            // ERROR_BROKEN_PIPE, ERROR_NO_DATA and ERROR_PIPE_NOT_CONNECTED:
            // a terminated child cannot accept a final StreamWriter flush.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            try { Output.Dispose(); }
            finally
            {
                try { Error.Dispose(); }
                finally { Process.Dispose(); }
            }
        }
    }

    private sealed partial class Pipe : IDisposable
    {
        private SafeFileHandle? parent;

        private Pipe(SafeFileHandle parent, SafeFileHandle child)
        {
            this.parent = parent;
            Child = child;
        }

        internal SafeFileHandle Child { get; }

        internal SafeFileHandle TakeParent() => Interlocked.Exchange(ref parent, null)
            ?? throw new InvalidOperationException("The pipe parent handle was already transferred.");

        internal static Pipe Create(bool parentReads)
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            if (!CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref security, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create a service pipe.");
            var parent = parentReads ? read : write;
            var child = parentReads ? write : read;
            if (!SetHandleInformation(parent, HandleFlagInherit, 0))
            {
                int error = Marshal.GetLastWin32Error();
                read.Dispose();
                write.Dispose();
                throw new Win32Exception(error, "Failed to protect the parent service pipe.");
            }

            return new Pipe(parent, child);
        }

        public void Dispose()
        {
            parent?.Dispose();
            Child.Dispose();
        }
    }

    private sealed partial class HandleList : IDisposable
    {
        private nint handles;

        internal nint Pointer { get; private set; }

        internal HandleList(params SafeFileHandle[] inherited)
        {
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref size);
            Pointer = Marshal.AllocHGlobal(checked((nint)size));
            bool initialized = false;
            try
            {
                if (!InitializeProcThreadAttributeList(Pointer, 1, 0, ref size))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                initialized = true;
                handles = Marshal.AllocHGlobal(inherited.Length * nint.Size);
                for (int index = 0; index < inherited.Length; index++)
                    Marshal.WriteIntPtr(handles, index * nint.Size, inherited[index].DangerousGetHandle());
                if (!UpdateProcThreadAttribute(Pointer, 0, ProcThreadAttributeHandleList, handles,
                        (nuint)(inherited.Length * nint.Size), nint.Zero, nint.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch
            {
                if (initialized) DeleteProcThreadAttributeList(Pointer);
                Marshal.FreeHGlobal(Pointer);
                Marshal.FreeHGlobal(handles);
                Pointer = nint.Zero;
                handles = nint.Zero;
                throw;
            }
        }

        public void Dispose()
        {
            if (Pointer == nint.Zero) return;
            DeleteProcThreadAttributeList(Pointer);
            Marshal.FreeHGlobal(Pointer);
            Marshal.FreeHGlobal(handles);
            Pointer = nint.Zero;
            handles = nint.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal nint Descriptor;

        [MarshalAs(UnmanagedType.Bool)]
        internal bool InheritHandle;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? application, StringBuilder command, nint processSecurity, nint threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string directory,
        ref StartupInfoEx startup, out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(nint thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
