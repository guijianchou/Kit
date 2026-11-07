// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.WingetLib;

public sealed record WingetAvailability(bool IsAvailable, string? Version, string? ExecutablePath, string? Error);

public enum WingetInstallEligibility
{
    ManualOnly,
    UserWithoutElevation,
    RequiresElevation,
}

public sealed record WingetPackageOptions
{
    public bool Silent { get; init; }

    public string? Scope { get; init; }

    public string? Architecture { get; init; }

    public bool IncludeUnknown { get; init; }
}

// Empty discovery versions remain unknown until details are resolved. Keep the
// compatibility default in the constructor so generated JSON readers honor it.
public sealed record WingetPackageUpdate(
    string Id,
    string Name,
    string InstalledVersion,
    string AvailableVersion,
    string Source,
    string? ReleaseNotes,
    string? ReleaseNotesUrl,
    WingetInstallEligibility InstallEligibility,
    string InstallEligibilityReason,
    string ManualUpgradeCommand,
    bool IsMetadataResolved = true)
{
    public bool CanUninstall { get; init; }

    public bool IsUpdateAvailable { get; init; }
}

public sealed record WingetPackageDetails(
    WingetPackageUpdate Package,
    string? Publisher,
    string? Description,
    string? PackageUrl,
    string? License,
    string? LicenseUrl,
    IReadOnlyList<WingetPackageAgreement> Agreements);

public sealed record WingetPackageAgreement(string Label, string Text, string? Url);

public sealed record WingetOperationProgress(string Stage, double? Percentage, ulong BytesDownloaded = 0, ulong BytesRequired = 0)
{
    public string? Output { get; init; }
}

public sealed record WingetDownloadResult(string Directory, IReadOnlyList<string> Files, bool HashVerified);

public sealed record WingetInstallResult(bool Succeeded, bool RebootRequired, bool RequiresManualAction, string? Error, int? ErrorCode)
{
    public bool Cancelled { get; init; }

    public bool CancellationRequested { get; init; }

    public bool VerificationPending { get; init; }

    public string? Output { get; init; }
}

public sealed class WingetOperationException : Exception
{
    public WingetOperationException(string message, int? errorCode = null)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public int? ErrorCode { get; }
}
