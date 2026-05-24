// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Reflection;
using System.Threading.Tasks;

namespace ManagedCommon
{
    public static class RunnerHelper
    {
        public static void WaitForPowerToysRunner(int powerToysPID, Action act, [System.Runtime.CompilerServices.CallerMemberName] string memberName = "")
        {
            var assembly = Assembly.GetCallingAssembly().GetName();
            Logger.LogDebug($"[{assembly}][{memberName}]WaitForPowerToysRunner waiting for Event powerToysPID={powerToysPID}");
            Task.Run(() =>
            {
                const uint INFINITE = 0xFFFFFFFF;
                const uint WAIT_OBJECT_0 = 0x00000000;
                const uint SYNCHRONIZE = 0x00100000;

                IntPtr powerToysProcHandle = NativeMethods.OpenProcess(SYNCHRONIZE, false, powerToysPID);
                if (powerToysProcHandle == IntPtr.Zero)
                {
                    Logger.LogWarning($"[{assembly}][{memberName}]WaitForPowerToysRunner could not open runner process powerToysPID={powerToysPID}");
                    return;
                }

                try
                {
                    if (NativeMethods.WaitForSingleObject(powerToysProcHandle, INFINITE) == WAIT_OBJECT_0)
                    {
                        Logger.LogDebug($"[{assembly}][{memberName}]WaitForPowerToysRunner Event Notified powerToysPID={powerToysPID}");
                        act.Invoke();
                    }
                }
                finally
                {
                    NativeMethods.CloseHandle(powerToysProcHandle);
                }
            });
        }

        private static readonly string[] RunnerProcessNames = new[] { "Kit.exe", "PowerToys.exe" };

        // In case we don't have a permission to open user's processes with a SYNCHRONIZE access right, e.g. LocalSystem processes, we could use GetExitCodeProcess to check the process' exit code periodically.
        public static void WaitForPowerToysRunnerExitFallback(Action act)
        {
            int[] processIds = new int[1024];
            uint bytesCopied;

            NativeMethods.EnumProcesses(processIds, (uint)processIds.Length * sizeof(uint), out bytesCopied);

            const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            var handleAccess = PROCESS_QUERY_LIMITED_INFORMATION;

            IntPtr runnerHandle = IntPtr.Zero;
            foreach (var processId in processIds)
            {
                IntPtr hProcess = NativeMethods.OpenProcess(handleAccess, false, processId);
                if (hProcess == IntPtr.Zero)
                {
                    continue;
                }

                System.Text.StringBuilder name = new System.Text.StringBuilder(1024);
                uint length = 1024;
                try
                {
                    if (!NativeMethods.QueryFullProcessImageName(hProcess, 0, name, ref length))
                    {
                        continue;
                    }

                    if (Array.IndexOf(RunnerProcessNames, System.IO.Path.GetFileName(name.ToString())) >= 0)
                    {
                        runnerHandle = hProcess;
                        hProcess = IntPtr.Zero;
                        break;
                    }
                }
                finally
                {
                    if (hProcess != IntPtr.Zero)
                    {
                        NativeMethods.CloseHandle(hProcess);
                    }
                }
            }

            if (runnerHandle == IntPtr.Zero)
            {
                Logger.LogError("Couldn't determine Kit.exe or PowerToys.exe pid");
                return;
            }

            Task.Run(() =>
            {
                const int STILL_ACTIVE = 0x103;
                uint exit_status;
                try
                {
                    do
                    {
                        System.Threading.Thread.Sleep(1000);
                        NativeMethods.GetExitCodeProcess(runnerHandle, out exit_status);
                    }
                    while (exit_status == STILL_ACTIVE);
                }
                finally
                {
                    NativeMethods.CloseHandle(runnerHandle);
                }

                act.Invoke();
            });
        }
    }
}
