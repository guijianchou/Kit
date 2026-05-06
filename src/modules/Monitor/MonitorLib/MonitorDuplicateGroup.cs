// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Describes a group of files that share the same SHA1.
/// </summary>
/// <param name="Sha1">The duplicate SHA1 value.</param>
/// <param name="Files">The files in the duplicate group.</param>
/// <param name="WastedBytes">The reclaimable bytes if all but one copy are removed.</param>
public sealed record MonitorDuplicateGroup(string Sha1, IReadOnlyList<MonitorFileRecord> Files, long WastedBytes);
