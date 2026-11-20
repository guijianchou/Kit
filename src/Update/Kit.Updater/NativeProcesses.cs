using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kit.Updater;

internal static partial class NativeProcesses
{
    public static void VerifyRunner(int pid, string root)
    {
        using var runner = Process.GetProcessById(pid);
        if (!string.Equals(runner.MainModule?.FileName, Path.Combine(root, "Kit.exe"), StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("invalidInstall", "Update caller does not match Kit installation.");
        }
    }

    public static async Task CloseAndWaitAsync(int runnerPid, string root)
    {
        var window = FindWindowW("KitTrayIconWindow", null);
        _ = GetWindowThreadProcessId(window, out var windowPid);
        if (window == IntPtr.Zero || windowPid != runnerPid || !PostMessageW(window, 0x0010, 0, 0))
        {
            throw new UpdateException("closeFailed", "Could not request Kit shutdown.");
        }

        var timeout = Stopwatch.StartNew();
        while (ProcessesInRoot(root).Count != 0)
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(60))
            {
                throw new UpdateException("closeFailed", "Kit processes did not exit in time. No files were replaced.");
            }

            await Task.Delay(250);
        }
    }

    internal static List<int> ProcessesInRoot(string root)
    {
        var pids = new List<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && process.MainModule?.FileName is string path &&
                        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        pids.Add(process.Id);
                    }
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Other users/system processes are inaccessible. Locked target files are checked before replacement.
                }
            }
        }

        return pids;
    }

    public static void StartKit(string root)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(root, "Kit.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = root,
            Arguments = "--open-settings=General",
        }) ?? throw new IOException("Kit could not be restarted.");
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowW(string className, string? windowName);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint pid);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(IntPtr window, uint message, nuint wparam, nint lparam);
}
