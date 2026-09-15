using System.Runtime.InteropServices;
using System.Text;

namespace LocalServerHub.Windows.Native;

/// <summary>Identity data read from an already-running Windows process.</summary>
public sealed record ProcessIdentity(
    string? ExecutablePath,
    string? WorkingDirectory,
    string? OwnerTag,
    string? OwnerExecutablePath);

/// <summary>
/// Reads the small amount of process state needed to decide whether a tagged
/// service can be recovered after the Hub itself was terminated.
/// </summary>
public static class ProcessIdentityInspector
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const int ProcessBasicInformation = 0;
    private const int ProcessWow64Information = 26;
    private const ushort ImageFileMachineUnknown = 0;
    private const int NtSuccess = 0;
    private const int Parameters32EnvironmentOffset = 0x48;
    private const int Parameters64EnvironmentOffset = 0x80;
    private const int EnvironmentChunkBytes = 4096;
    private const int MaximumEnvironmentBytes = 1024 * 1024;

    public static ProcessIdentity? Read(int processId, string environmentVariableName)
        => Read(
            processId,
            environmentVariableName,
            ServiceOwnershipStore.OwnerWorkingDirectoryEnvironmentVariable,
            ServiceOwnershipStore.OwnerExecutableEnvironmentVariable);

    public static ProcessIdentity? Read(
        int processId,
        string ownerTagEnvironmentVariable,
        string workingDirectoryEnvironmentVariable,
        string executableEnvironmentVariable)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerTagEnvironmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectoryEnvironmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableEnvironmentVariable);

        nint process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (process == nint.Zero)
        {
            return null;
        }

        try
        {
            string? executablePath = QueryExecutablePath(process);
            if (!TryGetPeb(process, out nint peb, out bool isWow64))
            {
                return null;
            }

            int pointerSize = isWow64 ? 4 : IntPtr.Size;
            nint processParameters = ReadPointer(
                process,
                Add(peb, pointerSize == 4 ? 0x10 : 0x20),
                pointerSize);
            if (processParameters == nint.Zero)
            {
                return null;
            }

            nint environment = ReadPointer(
                process,
                Add(
                    processParameters,
                    pointerSize == 4 ? Parameters32EnvironmentOffset : Parameters64EnvironmentOffset),
                pointerSize);
            string? ownerTag = ReadEnvironmentVariable(process, environment, ownerTagEnvironmentVariable);
            string? workingDirectory = ReadEnvironmentVariable(process, environment, workingDirectoryEnvironmentVariable);
            string? ownerExecutable = ReadEnvironmentVariable(process, environment, executableEnvironmentVariable);

            return new ProcessIdentity(executablePath, workingDirectory, ownerTag, ownerExecutable);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static string? QueryExecutablePath(nint process)
    {
        StringBuilder path = new(32768);
        uint length = (uint)path.Capacity;
        return QueryFullProcessImageNameW(process, 0, path, ref length)
            ? path.ToString()
            : null;
    }

    private static bool TryGetPeb(nint process, out nint peb, out bool isWow64)
    {
        peb = nint.Zero;
        isWow64 = false;

        if (IsWow64Process2(process, out ushort processMachine, out _)
            && processMachine != ImageFileMachineUnknown)
        {
            isWow64 = true;
            int status = NtQueryInformationProcess(
                process,
                ProcessWow64Information,
                out peb,
                (uint)IntPtr.Size,
                out _);
            return status == NtSuccess && peb != nint.Zero;
        }

        int basicStatus = NtQueryInformationProcess(
            process,
            ProcessBasicInformation,
            out ProcessBasicInformationData basicInformation,
            (uint)Marshal.SizeOf<ProcessBasicInformationData>(),
            out _);
        if (basicStatus != NtSuccess)
        {
            return false;
        }

        peb = basicInformation.PebBaseAddress;
        return peb != nint.Zero;
    }

    private static nint ReadPointer(nint process, nint address, int pointerSize)
    {
        byte[] bytes = new byte[pointerSize];
        if (!ReadProcessMemory(process, address, bytes, (nint)bytes.Length, out nint read)
            || read.ToInt64() != bytes.Length)
        {
            return nint.Zero;
        }

        return pointerSize == 4
            ? new nint(BitConverter.ToInt32(bytes, 0))
            : new nint(BitConverter.ToInt64(bytes, 0));
    }

    private static string? ReadEnvironmentVariable(
        nint process,
        nint environment,
        string variableName)
    {
        if (environment == nint.Zero)
        {
            return null;
        }

        List<byte> bytes = [];
        for (int offset = 0; offset < MaximumEnvironmentBytes; offset += EnvironmentChunkBytes)
        {
            byte[] chunk = new byte[Math.Min(EnvironmentChunkBytes, MaximumEnvironmentBytes - offset)];
            bool success = ReadProcessMemory(
                process,
                Add(environment, offset),
                chunk,
                (nint)chunk.Length,
                out nint read);
            int byteCount = (int)Math.Clamp(read.ToInt64(), 0, chunk.Length);
            if (byteCount > 0)
            {
                bytes.AddRange(chunk.AsSpan(0, byteCount).ToArray());
            }

            if (bytes.Count >= 2)
            {
                string text = Encoding.Unicode.GetString(bytes.ToArray());
                if (text.Contains("\0\0", StringComparison.Ordinal))
                {
                    break;
                }
            }

            if (!success || byteCount == 0 || byteCount < chunk.Length)
            {
                break;
            }
        }

        string environmentText = Encoding.Unicode.GetString(bytes.ToArray());
        foreach (string entry in environmentText.Split('\0'))
        {
            int separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (string.Equals(entry[..separator], variableName, StringComparison.OrdinalIgnoreCase))
            {
                return entry[(separator + 1)..];
            }
        }

        return null;
    }

    private static nint Add(nint address, int offset) =>
        new(address.ToInt64() + offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformationData
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        nint process,
        uint flags,
        StringBuilder executablePath,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        nint process,
        int informationClass,
        out ProcessBasicInformationData information,
        uint informationLength,
        out uint returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        nint process,
        int informationClass,
        out nint information,
        uint informationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        nint process,
        nint baseAddress,
        [Out] byte[] buffer,
        nint size,
        out nint numberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
