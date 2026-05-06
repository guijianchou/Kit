// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Describes one file discovered by the Monitor scanner.
/// </summary>
/// <param name="RootDir">The logical root directory marker. Python Monitor uses <c>~</c>.</param>
/// <param name="FolderName">The first-level category folder name, or <c>~</c> for files in the root.</param>
/// <param name="FileName">The file name.</param>
/// <param name="RelativePath">The path relative to the configured Downloads root.</param>
/// <param name="FullPath">The full filesystem path.</param>
/// <param name="Sha1">The SHA1 value, a sentinel value, or null when hashing is disabled or failed.</param>
/// <param name="Timestamp">The last write timestamp formatted as <c>yyyy-MM-ddTHH:mm:ss</c>.</param>
/// <param name="FileSize">The file size in bytes.</param>
/// <param name="Category">The Monitor category resolved from existing folder, smart rule, or extension.</param>
public sealed record MonitorFileRecord(
    string RootDir,
    string FolderName,
    string FileName,
    string RelativePath,
    string FullPath,
    string? Sha1,
    string? Timestamp,
    long? FileSize,
    string Category);
