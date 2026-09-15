using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LocalServerHub.Windows.Native;

/// <summary>One sample of the complete process tree rooted at a service PID.</summary>
public sealed record ProcessResourceSample(
    int RootProcessId,
    IReadOnlyList<int> ProcessIds,
    TimeSpan TotalProcessorTime,
    long WorkingSetBytes,
    int ReadableProcessCount);

/// <summary>
/// One machine-wide parent/child snapshot, reusable for many tree walks.
/// </summary>
/// <remarks>
/// Recovery asks for the descendants of every recorded PID of every service.
/// Taking a fresh Toolhelp snapshot per question turned that into services x PIDs
/// full-machine scans during window activation; one snapshot answers all of them
/// and keeps the answers consistent with each other.
/// </remarks>
public sealed class ProcessTreeSnapshot
{
    private readonly Dictionary<int, List<int>> _children;
    private readonly Dictionary<int, int> _parents;

    internal ProcessTreeSnapshot(Dictionary<int, List<int>> children)
    {
        _children = children;
        _parents = [];
        foreach ((int parentId, List<int> descendants) in children)
        {
            foreach (int childId in descendants)
            {
                _parents[childId] = parentId;
            }
        }
    }

    /// <summary>Reads the current process table.</summary>
    public static ProcessTreeSnapshot Capture() =>
        new(ProcessResourceInspector.ReadProcessParents());

    /// <summary>The root PID plus every descendant recorded in this snapshot.</summary>
    public IReadOnlyList<int> GetTreeIds(int rootProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rootProcessId);

        HashSet<int> tree = [rootProcessId];
        Queue<int> pending = new();
        pending.Enqueue(rootProcessId);

        while (pending.Count > 0)
        {
            int parent = pending.Dequeue();
            if (!_children.TryGetValue(parent, out List<int>? descendants))
            {
                continue;
            }

            foreach (int child in descendants)
            {
                if (tree.Add(child))
                {
                    pending.Enqueue(child);
                }
            }
        }

        return [.. tree];
    }

    /// <summary>The recorded parent PID, or 0 when the process was not in the snapshot.</summary>
    public int GetParentProcessId(int processId) =>
        _parents.TryGetValue(processId, out int parentId) ? parentId : 0;
}

/// <summary>
/// Reads process-tree membership and aggregates CPU time/RSS for a managed
/// service. The root PID alone is not representative for cmd, node and Python
/// launchers that keep the real work in descendants.
/// </summary>
public static class ProcessResourceInspector
{
    private const uint SnapshotProcess = 0x00000002;
    private const int ErrorBadLength = 24;
    private const int MaxPath = 260;

    public static ProcessResourceSample Sample(
        int rootProcessId,
        IReadOnlyCollection<int>? managedProcessIds = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rootProcessId);

        IReadOnlyList<int> processIds = managedProcessIds is not null
            ? [.. managedProcessIds.Distinct()]
            : GetProcessTreeIds(rootProcessId);
        TimeSpan processorTime = TimeSpan.Zero;
        long workingSetBytes = 0;
        int readableProcessCount = 0;

        foreach (int processId in processIds)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                processorTime += process.TotalProcessorTime;
                long workingSet = process.WorkingSet64;
                readableProcessCount++;
                if (workingSet > 0 && workingSetBytes <= long.MaxValue - workingSet)
                {
                    workingSetBytes += workingSet;
                }
            }
            catch (ArgumentException)
            {
                // A process can exit between the snapshot and the read.
            }
            catch (InvalidOperationException)
            {
                // The process handle can become unusable during shutdown.
            }
            catch (Win32Exception)
            {
                // Access to a process can be denied even when it was visible in
                // the process snapshot.
            }
        }

        return new ProcessResourceSample(
            rootProcessId,
            processIds,
            processorTime,
            workingSetBytes,
            readableProcessCount);
    }

    public static IReadOnlyList<int> GetProcessTreeIds(int rootProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rootProcessId);

        return ProcessTreeSnapshot.Capture().GetTreeIds(rootProcessId);
    }

    /// <summary>Returns the parent PID for each process in the supplied set.</summary>
    public static IReadOnlyDictionary<int, int> GetProcessParentIds(
        IReadOnlyCollection<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);

        HashSet<int> members = [.. processIds.Where(static id => id > 0)];
        if (members.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        ProcessTreeSnapshot snapshot = ProcessTreeSnapshot.Capture();
        Dictionary<int, int> parentIds = [];
        foreach (int childId in members)
        {
            int parentId = snapshot.GetParentProcessId(childId);
            if (parentId > 0)
            {
                parentIds[childId] = parentId;
            }
        }

        return parentIds;
    }

    /// <summary>
    /// Finds the process in a supplied set whose parent is outside that set.
    /// A recovered Job Object gives us membership but not the root PID that the
    /// original runner stored, so this reconstructs that one piece of state.
    /// </summary>
    public static int? FindRootProcessId(IReadOnlyCollection<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        if (processIds.Count == 0)
        {
            return null;
        }

        HashSet<int> members = [.. processIds.Where(static id => id > 0)];
        if (members.Count == 0)
        {
            return null;
        }

        Dictionary<int, List<int>> children = ReadProcessParents();
        foreach ((int parentId, List<int> descendants) in children)
        {
            if (members.Contains(parentId))
            {
                continue;
            }

            int? root = descendants.FirstOrDefault(members.Contains);
            if (root is { } rootProcessId && rootProcessId > 0)
            {
                return rootProcessId;
            }
        }

        // The process snapshot can race with a process exiting. Returning a
        // remaining member lets the caller perform one final liveness/path check.
        return members.First();
    }

    internal static Dictionary<int, List<int>> ReadProcessParents()
    {
        nint snapshot = CreateToolhelp32Snapshot(SnapshotProcess, 0);
        if (snapshot == nint.Zero || snapshot == new nint(-1))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorBadLength)
            {
                snapshot = CreateToolhelp32Snapshot(SnapshotProcess, 0);
            }

            if (snapshot == nint.Zero || snapshot == new nint(-1))
            {
                return [];
            }
        }

        try
        {
            Dictionary<int, List<int>> children = [];
            ProcessEntry32 entry = new() { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return children;
            }

            do
            {
                int processId = unchecked((int)entry.ProcessId);
                int parentId = unchecked((int)entry.ParentProcessId);
                if (!children.TryGetValue(parentId, out List<int>? descendants))
                {
                    descendants = [];
                    children[parentId] = descendants;
                }

                descendants.Add(processId);
                entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            }
            while (Process32NextW(snapshot, ref entry));

            return children;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
        public string? ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
