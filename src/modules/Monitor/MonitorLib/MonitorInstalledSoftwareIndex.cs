// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Immutable installed software index used for safer installer matching.
/// </summary>
public sealed class MonitorInstalledSoftwareIndex
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MonitorInstalledSoftwareIndex"/> class.
    /// </summary>
    /// <param name="entries">Installed software entries.</param>
    public MonitorInstalledSoftwareIndex(IEnumerable<MonitorInstalledSoftwareEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Entries = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.DisplayName))
            .ToArray();
    }

    /// <summary>
    /// Gets installed software entries.
    /// </summary>
    public IReadOnlyList<MonitorInstalledSoftwareEntry> Entries { get; }

    /// <summary>
    /// Gets installed software display names.
    /// </summary>
    public IReadOnlyList<string> DisplayNames => Entries.Select(entry => entry.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
