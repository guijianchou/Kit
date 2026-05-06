// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Installed software metadata read from Windows uninstall entries.
/// </summary>
public sealed class MonitorInstalledSoftwareEntry
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MonitorInstalledSoftwareEntry"/> class.
    /// </summary>
    public MonitorInstalledSoftwareEntry(string displayName, string? displayVersion, string? installDate, string? installLocation)
    {
        DisplayName = displayName;
        DisplayVersion = displayVersion;
        InstallDate = installDate;
        InstallLocation = installLocation;
    }

    /// <summary>
    /// Gets the installed software display name.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the installed software display version.
    /// </summary>
    public string? DisplayVersion { get; }

    /// <summary>
    /// Gets the registry install date, if present.
    /// </summary>
    public string? InstallDate { get; }

    /// <summary>
    /// Gets the install location, if present.
    /// </summary>
    public string? InstallLocation { get; }
}
