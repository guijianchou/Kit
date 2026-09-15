using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace LocalServerHub.Windows.Native;

/// <summary>Who is listening on a TCP port right now.</summary>
public sealed record PortOwner(int Port, int ProcessId, string ProcessName, long? StartTimeUtcFileTime = null);

/// <summary>
/// Answers "is this port free, and if not, who has it" (plan.md §3.2).
/// </summary>
/// <remarks>
/// Uses GetExtendedTcpTable rather than the managed
/// IPGlobalProperties.GetActiveTcpListeners because the managed API returns
/// endpoints without owning PIDs, and a port conflict the user cannot attribute
/// to a process is not actionable.
/// </remarks>
public static class PortInspector
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const int ErrorInsufficientBuffer = 122;

    public static bool IsPortFree(int port) => FindOwner(port) is null;

    public static PortOwner? FindOwner(int port)
    {
        foreach (PortOwner owner in EnumerateListeners())
        {
            if (owner.Port == port)
            {
                return owner;
            }
        }

        return null;
    }

    public static async Task ReleaseAsync(PortOwner expected, CancellationToken cancellationToken = default)
    {
        if (expected.StartTimeUtcFileTime is not { } startTime)
            throw new InvalidOperationException("The port owner's identity could not be verified. Refresh the environment check.");

        using Process process = Process.GetProcessById(expected.ProcessId);
        // Pin the process handle across validation and termination so PID reuse
        // cannot redirect Kill to a different process after the confirmation.
        _ = process.SafeHandle;
        if (process.HasExited || process.StartTime.ToUniversalTime().ToFileTimeUtc() != startTime
            || !EnumerateListeners().Any(owner => owner.Port == expected.Port
                && owner.ProcessId == expected.ProcessId && owner.StartTimeUtcFileTime == startTime))
        {
            throw new InvalidOperationException("The port owner changed. Refresh the environment check before terminating it.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        process.Kill();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(1);
        while (!IsPortFree(expected.Port) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50, timeout.Token).ConfigureAwait(false);
        if (!IsPortFree(expected.Port))
            throw new InvalidOperationException("The process exited, but the port is still occupied. Refresh the environment check.");
    }

    /// <summary>
    /// The first free port at or above <paramref name="startPort"/>, so a conflict can
    /// be reported with a port to move to rather than only the one that failed
    /// (plan.md §212). Enumerates listeners once instead of per candidate.
    /// </summary>
    /// <returns>The suggestion, or null if nothing in the window is free.</returns>
    public static int? FindNextFreePort(int startPort, int searchWindow = 64)
    {
        if (startPort is < 1 or > 65535)
        {
            return null;
        }

        HashSet<int> taken = [];
        foreach (PortOwner owner in EnumerateListeners())
        {
            taken.Add(owner.Port);
        }

        int last = Math.Min(65535, startPort + searchWindow);
        for (int candidate = startPort + 1; candidate <= last; candidate++)
        {
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static IReadOnlyList<PortOwner> EnumerateListeners() =>
        [.. EnumerateListeners(AfInet), .. EnumerateListeners(AfInet6)];

    private static IReadOnlyList<PortOwner> EnumerateListeners(int family)
    {
        uint size = 0;
        uint result = GetExtendedTcpTable(nint.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        if (result == 0 && size == 0) return [];
        if (result != ErrorInsufficientBuffer || size == 0)
        {
            throw new System.ComponentModel.Win32Exception((int)result);
        }

        nint buffer = nint.Zero;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (buffer != nint.Zero) Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(checked((int)size));
                result = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0);
                if (result != ErrorInsufficientBuffer) break;
            }
            if (result != 0)
            {
                throw new System.ComponentModel.Win32Exception((int)result);
            }

            int count = Marshal.ReadInt32(buffer);
            List<PortOwner> owners = new(count);
            nint rowPointer = buffer + sizeof(int);
            int rowSize = family == AfInet ? Marshal.SizeOf<TcpRowOwnerPid>() : 56;

            for (int index = 0; index < count; index++)
            {
                // MIB_TCP6ROW_OWNER_PID: local port at 20, PID at 52 (IPv4: 8/20).
                uint localPort = unchecked((uint)Marshal.ReadInt32(rowPointer, family == AfInet ? 8 : 20));
                int processId = Marshal.ReadInt32(rowPointer, family == AfInet ? 20 : 52);
                rowPointer += rowSize;

                // The port arrives as two big-endian bytes in the low word.
                int port = (int)IPAddress.NetworkToHostOrder((short)(localPort & 0xFFFF)) & 0xFFFF;
                (string name, long? startTime) = DescribeProcess(processId);
                owners.Add(new PortOwner(port, processId, name, startTime));
            }

            return owners;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (string Name, long? StartTime) DescribeProcess(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return (process.ProcessName, process.StartTime.ToUniversalTime().ToFileTimeUtc());
        }
        catch (ArgumentException)
        {
            // Exited between enumeration and lookup.
            return ("(exited)", null);
        }
        catch (InvalidOperationException)
        {
            return ("(unknown)", null);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ("(unavailable)", null);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        nint tcpTable,
        ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);
}
