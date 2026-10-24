// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ManagedCommon
{
    public static class PipeServerIdentity
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;

        public static bool IsTrustedServer(PipeStream pipe, string expectedExeNameOrPath)
        {
            if (pipe == null || string.IsNullOrWhiteSpace(expectedExeNameOrPath))
            {
                return false;
            }

            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId) || processId == 0)
            {
                return false;
            }

            using SafeProcessHandle process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process.IsInvalid)
            {
                return false;
            }

            var builder = new StringBuilder(4096);
            uint length = (uint)builder.Capacity;
            if (!QueryFullProcessImageName(process, 0, builder, ref length))
            {
                return false;
            }

            string actualPath = builder.ToString();
            string expectedPath = expectedExeNameOrPath;

            if (!expectedPath.Contains(Path.DirectorySeparatorChar) && !expectedPath.Contains(Path.AltDirectorySeparatorChar))
            {
                return string.Equals(Path.GetFileName(actualPath), expectedPath, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder imageName, ref uint length);
    }
}
