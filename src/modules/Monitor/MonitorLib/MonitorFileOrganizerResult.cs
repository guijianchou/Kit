// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Summarizes a Monitor file organization run.
/// </summary>
/// <param name="TotalFiles">The number of root files considered.</param>
/// <param name="Organized">The number of files moved, or planned in dry-run mode.</param>
/// <param name="Skipped">The number of files skipped.</param>
/// <param name="Errors">The number of files that failed to move.</param>
public sealed record MonitorFileOrganizerResult(int TotalFiles, int Organized, int Skipped, int Errors);
