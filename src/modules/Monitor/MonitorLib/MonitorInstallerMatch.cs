// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Describes a candidate installer file that appears to match installed software.
/// </summary>
/// <param name="FilePath">The installer file path.</param>
/// <param name="SoftwareName">The installed software name that matched the installer.</param>
/// <param name="Confidence">The match confidence from 0.0 to 1.0.</param>
/// <param name="FileSize">The installer size in bytes.</param>
public sealed record MonitorInstallerMatch(string FilePath, string SoftwareName, double Confidence, long FileSize);
