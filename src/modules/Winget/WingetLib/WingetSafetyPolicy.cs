// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.WingetLib;

internal static class WingetSafetyPolicy
{
    internal static WingetPackageOptions ValidateOptions(WingetPackageOptions? options, bool uninstall = false)
    {
        options ??= new();
        string? scope = options.Scope?.ToLowerInvariant();
        string? architecture = options.Architecture?.ToLowerInvariant();
        if (scope is not (null or "user" or "machine"))
        {
            throw new ArgumentException("Installation scope must be User, Machine, or unspecified.", nameof(options));
        }

        if (architecture is not (null or "x64" or "x86" or "arm64"))
        {
            throw new ArgumentException("Architecture must be x64, x86, arm64, or unspecified.", nameof(options));
        }

        if (uninstall && architecture is not null)
        {
            throw new ArgumentException("WinGet uninstall does not support an architecture option.", nameof(options));
        }

        return options with { Scope = scope, Architecture = architecture };
    }

    internal static bool IsUnknownVersion(string version) => string.Equals(version, "Unknown", StringComparison.OrdinalIgnoreCase);

    internal static bool CanUpgradeVersion(string installedVersion, bool isNewer, bool includeUnknown) =>
        !string.IsNullOrEmpty(installedVersion) && (IsUnknownVersion(installedVersion) ? includeUnknown : isNewer);

    internal static (WingetInstallEligibility Eligibility, string Reason) Evaluate(string? installedScope, string? installerScope, string? elevationRequirement, bool processElevated)
    {
        if (!processElevated && (string.IsNullOrEmpty(installedScope) || string.Equals(installedScope, "User", StringComparison.OrdinalIgnoreCase))
            && string.Equals(installerScope, "User", StringComparison.OrdinalIgnoreCase)
            && string.Equals(elevationRequirement, "ElevationProhibited", StringComparison.Ordinal))
        {
            return (WingetInstallEligibility.UserWithoutElevation, "The selected user-scoped installer explicitly prohibits elevation.");
        }

        return (WingetInstallEligibility.RequiresElevation, "WinGet will run the selected installer normally. Windows may request administrator approval.");
    }

    internal static string CreateManualUpgradeCommand(string id, string version, string source)
        => CreateManualCommand(id, version, source, upgrade: true);

    internal static string CreateManualInstallCommand(string id, string version, string source)
        => CreateManualCommand(id, version, source, upgrade: false);

    private static string CreateManualCommand(string id, string version, string source, bool upgrade)
    {
        ValidateIdentityPart(id, nameof(id));
        ValidateIdentityPart(version, nameof(version));
        ValidateIdentityPart(source, nameof(source));

        // A PowerShell command for copying only; no shell executes this string in Kit.
        return $"winget {(upgrade ? "upgrade" : "install")} --id {QuotePowerShell(id)} --exact --version {QuotePowerShell(version)} --source {QuotePowerShell(source)} --interactive{(upgrade ? string.Empty : " --no-upgrade")}";
    }

    internal static void ValidateIdentityPart(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl) || value.StartsWith('-'))
        {
            throw new ArgumentException("Package identifiers must be nonempty text without control characters or a leading option prefix.", parameterName);
        }
    }

    internal static string ValidateSearchQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length is < 2 or > 200 || query.Any(char.IsControl))
        {
            throw new ArgumentException("Enter between 2 and 200 characters to search the winget catalog.", nameof(query));
        }

        return query.Trim();
    }

    private static string QuotePowerShell(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
