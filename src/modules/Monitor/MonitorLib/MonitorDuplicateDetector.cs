// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Detects duplicate Monitor records by SHA1.
/// </summary>
public static class MonitorDuplicateDetector
{
    /// <summary>
    /// Finds duplicate file groups.
    /// </summary>
    /// <param name="records">The records to inspect.</param>
    /// <returns>Duplicate groups keyed by SHA1.</returns>
    public static IReadOnlyList<MonitorDuplicateGroup> FindDuplicates(IEnumerable<MonitorFileRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        return records
            .Where(record => !string.IsNullOrEmpty(record.Sha1) && !string.Equals(record.Sha1, MonitorHasher.Sha1SkippedTooLarge, StringComparison.Ordinal))
            .GroupBy(record => record.Sha1!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                List<MonitorFileRecord> files = group.ToList();
                long firstFileSize = files[0].FileSize ?? 0;
                return new MonitorDuplicateGroup(group.Key, files, firstFileSize * (files.Count - 1));
            })
            .ToList();
    }
}
