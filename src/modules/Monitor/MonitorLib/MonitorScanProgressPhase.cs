// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Monitor scan progress phases written to scan-progress.json.
/// </summary>
public static class MonitorScanProgressPhase
{
    public const string Hashing = "hashing";
    public const string Categorizing = "categorizing";
    public const string Writing = "writing";
    public const string Completed = "completed";
}
