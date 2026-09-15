using System.Runtime.InteropServices;

namespace LocalServerHub.Windows;

public sealed record SystemResourceSnapshot(
    double? CpuPercent,
    long TotalMemoryBytes,
    long AvailableMemoryBytes,
    int LogicalProcessorCount)
{
    public long UsedMemoryBytes => Math.Max(0, TotalMemoryBytes - AvailableMemoryBytes);
}

/// <summary>
/// Samples host CPU load and physical memory. CPU is calculated from the delta
/// between system idle/kernel/user counters, so it is independent of the hub's
/// own managed heap usage.
/// </summary>
public sealed class SystemResourceInspector
{
    private readonly Lock _gate = new();
    private ulong? _previousIdle;
    private ulong? _previousKernel;
    private ulong? _previousUser;

    public SystemResourceSnapshot Sample()
    {
        lock (_gate)
        {
            double? cpuPercent = null;
            if (GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
            {
                ulong idleTicks = idle.ToUInt64();
                ulong kernelTicks = kernel.ToUInt64();
                ulong userTicks = user.ToUInt64();

                if (_previousIdle is { } previousIdle
                    && _previousKernel is { } previousKernel
                    && _previousUser is { } previousUser
                    && idleTicks >= previousIdle
                    && kernelTicks >= previousKernel
                    && userTicks >= previousUser)
                {
                    ulong totalDelta = (kernelTicks - previousKernel) + (userTicks - previousUser);
                    ulong idleDelta = idleTicks - previousIdle;
                    if (totalDelta > 0)
                    {
                        cpuPercent = Math.Clamp(
                            (totalDelta - Math.Min(idleDelta, totalDelta)) * 100d / totalDelta,
                            0d,
                            100d);
                    }
                }

                _previousIdle = idleTicks;
                _previousKernel = kernelTicks;
                _previousUser = userTicks;
            }

            MemoryStatus memory = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            long totalMemory = 0;
            long availableMemory = 0;
            if (GlobalMemoryStatusEx(ref memory))
            {
                totalMemory = ToLong(memory.TotalPhysical);
                availableMemory = Math.Min(totalMemory, ToLong(memory.AvailablePhysical));
            }

            return new SystemResourceSnapshot(
                cpuPercent,
                totalMemory,
                availableMemory,
                Environment.ProcessorCount);
        }
    }

    private static long ToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
