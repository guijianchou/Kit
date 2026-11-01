// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;

namespace Awake.Core.Models
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public int CbSize;
        public IntPtr HWnd;
        public int UId;
        public int UFlags;
        public int UCallbackMessage;
        public IntPtr HIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string SzTip;
        public int DwState;
        public int DwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string SzInfo;
        public int UTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string SzInfoTitle;
        public int DwInfoFlags;
        public Guid GuidItem;
        public IntPtr HBalloonIcon;
    }
}
