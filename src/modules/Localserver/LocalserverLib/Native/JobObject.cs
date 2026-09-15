using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocalServerHub.Windows.Native;

/// <summary>
/// A Windows job object used to manage a service process tree.
/// </summary>
/// <remarks>
/// Node, Python and conda launchers routinely spawn grandchildren that outlive a
/// naive kill of the process we started; anything assigned to this job can be
/// terminated together, including processes the hub never saw. Recoverable jobs
/// deliberately omit kill-on-close so the next Hub instance can identify and
/// reclaim them after an unexpected Hub exit.
/// </remarks>
public sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectBasicProcessIdList = 3;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const uint JobObjectAssignProcess = 0x0001;
    private const uint JobObjectQuery = 0x0004;
    private const uint JobObjectTerminate = 0x0008;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    private const int ErrorFileNotFound = 2;
    private const int ErrorInvalidName = 123;
    private const int ErrorAlreadyExists = 183;

    private readonly SafeJobHandle _handle;
    private bool _disposed;

    /// <summary>
    /// Creates a job object. Named service jobs intentionally do not use
    /// kill-on-close: the name is the ownership tag that lets a later Hub
    /// instance recover a service after the previous Hub died unexpectedly.
    /// Normal Hub shutdown still calls <see cref="TerminateAll"/> explicitly.
    /// </summary>
    public JobObject(string? name = null, bool killOnClose = true)
    {
        _handle = CreateJobObjectW(nint.Zero, name);
        int error = Marshal.GetLastWin32Error();
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(error, "Failed to create the job object.");
        }

        WasAlreadyExisting = name is not null && error == ErrorAlreadyExists;

        if (killOnClose && !WasAlreadyExisting)
        {
            ConfigureKillOnClose();
        }
    }

    /// <summary>True when a named object was opened instead of created.</summary>
    public bool WasAlreadyExisting { get; }

    /// <summary>
    /// Creates a fresh named ownership job. It starts with kill-on-close enabled
    /// until the durable ownership record has been written.
    /// </summary>
    public static JobObject CreateNamed(string name, bool killOnClose = true)
    {
        JobObject job = new(name, killOnClose);
        if (!job.WasAlreadyExisting)
        {
            return job;
        }

        job.Dispose();
        throw new InvalidOperationException("The service ownership job already exists.");
    }

    private JobObject(SafeJobHandle handle)
    {
        _handle = handle;
        WasAlreadyExisting = false;
    }

    /// <summary>
    /// Returns the stable per-service kernel object name used as the
    /// LocalServerHub ownership tag. Hashing keeps arbitrary service IDs out of
    /// the Windows object namespace while retaining stable lookup across runs.
    /// </summary>
    public static string GetServiceJobName(string serviceId, string ownerTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerTag);

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{serviceId}\n{ownerTag}"));
        return $"Local\\LocalServerHub.Service.{Convert.ToHexString(digest[..16])}";
    }

    /// <summary>Opens a tagged service job left by an earlier Hub instance.</summary>
    public static JobObject? TryOpen(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        SafeJobHandle handle = OpenJobObjectW(
            JobObjectAssignProcess | JobObjectQuery | JobObjectTerminate,
            false,
            name);
        int error = Marshal.GetLastWin32Error();
        if (!handle.IsInvalid)
        {
            return new JobObject(handle);
        }

        handle.Dispose();
        if (error is ErrorFileNotFound or ErrorInvalidName)
        {
            return null;
        }

        throw new Win32Exception(error, "Failed to open the service ownership job object.");
    }

    /// <summary>Changes whether closing the last Job handle terminates its processes.</summary>
    public void SetKillOnClose(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ConfigureKillOnClose(enabled);
    }

    private void ConfigureKillOnClose(bool enabled = true)
    {
        JobObjectExtendedLimitInfo info = default;
        info.BasicLimitInformation.LimitFlags = enabled ? JobObjectLimitKillOnJobClose : 0;

        int size = Marshal.SizeOf<JobObjectExtendedLimitInfo>();
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Failed to set kill-on-close on the job object.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Puts a process and every descendant it later spawns under this job.
    /// </summary>
    public void Assign(nint processHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!AssignProcessToJobObject(_handle, processHandle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Failed to assign the process to the job object.");
        }
    }

    /// <summary>Terminates every process in the job immediately.</summary>
    public void TerminateAll(uint exitCode = 1)
    {
        _ = TryTerminateAll(exitCode);
    }

    /// <summary>Terminates every process and reports whether Windows accepted the request.</summary>
    public bool TryTerminateAll(uint exitCode = 1)
    {
        if (_disposed || _handle.IsInvalid)
        {
            return false;
        }

        // A failure here is not actionable: the job may already be empty because
        // the tree exited on its own between the check and the call.
        return TerminateJobObject(_handle, exitCode);
    }

    /// <summary>Returns the process IDs currently assigned to this job.</summary>
    public IReadOnlyList<int> GetProcessIds()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int size = 8 + (16 * IntPtr.Size);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            nint buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (QueryInformationJobObject(
                        _handle,
                        JobObjectBasicProcessIdList,
                        buffer,
                        (uint)size,
                        out uint returnedLength))
                {
                    uint count = Math.Min(
                        (uint)Marshal.ReadInt32(buffer, sizeof(uint)),
                        (uint)((size - 8) / IntPtr.Size));
                    List<int> processIds = new((int)count);
                    for (int index = 0; index < count; index++)
                    {
                        nint processId = Marshal.ReadIntPtr(buffer, 8 + (index * IntPtr.Size));
                        if (processId != nint.Zero && processId.ToInt64() <= int.MaxValue)
                        {
                            processIds.Add((int)processId);
                        }
                    }

                    return processIds;
                }

                int error = Marshal.GetLastWin32Error();
                if (error is not (ErrorInsufficientBuffer or ErrorMoreData))
                {
                    throw new Win32Exception(error, "Failed to query the service process tree.");
                }

                size = checked((int)Math.Max((long)size * 2, returnedLength));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new Win32Exception(ErrorMoreData, "The service process tree changed repeatedly during the query.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInfo
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInfo
    {
        public JobObjectBasicLimitInfo BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObjectW(nint securityAttributes, string? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle OpenJobObjectW(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int infoClass,
        nint info,
        uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        nint information,
        uint informationLength,
        out uint returnLength);
}
