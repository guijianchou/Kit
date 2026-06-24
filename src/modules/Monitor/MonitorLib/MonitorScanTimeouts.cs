// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Shared Monitor scan timing constants so the worker, status store, and Settings UI agree on a single value.
/// </summary>
public static class MonitorScanTimeouts
{
    /// <summary>
    /// The maximum time a scan may report no progress before it is treated as stalled.
    /// </summary>
    public static readonly TimeSpan NoProgress = TimeSpan.FromMinutes(30);
}
