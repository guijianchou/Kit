// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Awake.Core.Models
{
#pragma warning disable CA1051 // Native interop struct fields must remain layout-compatible.
    public struct BatteryReportingScale
    {
        public uint Granularity;
        public uint Capacity;
    }
#pragma warning restore CA1051
}
