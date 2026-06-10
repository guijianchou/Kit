// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Summary for a single Monitor worker pass.
/// </summary>
/// <param name="RecordCount">The number of records written to the CSV state.</param>
/// <param name="CsvPath">The CSV state path.</param>
/// <param name="OrganizeResult">The optional organization summary.</param>
/// <param name="InstallerCleanupResult">The optional installer cleanup summary.</param>
public sealed record MonitorWorkerResult(
    int RecordCount,
    string CsvPath,
    MonitorFileOrganizerResult? OrganizeResult,
    MonitorInstallerCleanupResult? InstallerCleanupResult);
