// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Summarizes an installer cleanup run.
/// </summary>
/// <param name="Deleted">The number of installers deleted, or planned in dry-run mode.</param>
/// <param name="Skipped">The number of installers skipped.</param>
/// <param name="Errors">The number of installers that failed to delete.</param>
/// <param name="FreedBytes">The bytes freed, or planned in dry-run mode.</param>
public sealed record MonitorInstallerCleanupResult(int Deleted, int Skipped, int Errors, long FreedBytes);
