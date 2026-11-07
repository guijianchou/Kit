// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.WingetLib;

public sealed record WingetServiceStatus
{
    public bool IsBusy { get; init; }

    public string? OperationId { get; init; }

    public string? OperationKind { get; init; }

    public string Phase { get; init; } = "Idle";

    public double? ProgressPercent { get; init; }

    public string? Message { get; init; }

    public string? Output { get; init; }

    public DateTimeOffset? LastAttemptUtc { get; init; }

    public DateTimeOffset? LastSuccessUtc { get; init; }

    public DateTimeOffset? NextCheckUtc { get; init; }

    public IReadOnlyList<WingetPackageUpdate> Packages { get; init; } = [];

    public IReadOnlyList<WingetPackageUpdate> InstalledPackages { get; init; } = [];

    public IReadOnlyList<WingetPackageIdentity> IgnoredPackages { get; init; } = [];

    public WingetPreparedOperation? PreparedOperation { get; init; }

    public IReadOnlyList<WingetQueueItem> Queue { get; init; } = [];

    public IReadOnlyList<WingetQueueItem> History { get; init; } = [];

    public bool RecoveryRequired { get; init; }

    public string? SearchQuery { get; init; }

    public IReadOnlyList<WingetPackageUpdate> SearchResults { get; init; } = [];

    public bool SearchResultsTruncated { get; init; }

    public WingetPackageDetails? SelectedDetails { get; init; }

    public WingetServiceOperationResult? LastResult { get; init; }

    public WingetAvailability? Availability { get; init; }

    public string? NotificationError { get; init; }

    public DateTimeOffset? LastNotifiedUtc { get; init; }

    public bool DailyCheckEnabled { get; init; } = true;

    public bool NotifyUpdates { get; init; } = true;
}

public sealed record WingetServiceOperationResult(
    string OperationId,
    string Kind,
    bool Succeeded,
    string? Message = null,
    WingetDownloadResult? Download = null,
    WingetInstallResult? Install = null)
{
    public long? ElapsedMilliseconds { get; init; }
}

public sealed record WingetServiceRequest
{
    public string Command { get; init; } = "get-status";

    public string? PackageId { get; init; }

    public string? Query { get; init; }

    public string? Source { get; init; }

    public string? AvailableVersion { get; init; }

    public string? OperationId { get; init; }

    public bool? DailyCheckEnabled { get; init; }

    public bool? NotifyUpdates { get; init; }

    public bool AcceptPackageAgreements { get; init; }

    public string? OperationKind { get; init; }

    public IReadOnlyList<WingetPackageIdentity> Packages { get; init; } = [];

    public WingetPackageOptions? Options { get; init; }

    public string? PreparationId { get; init; }
}

public sealed record WingetServiceAck(bool Accepted, string? OperationId = null, string? Error = null);

public sealed record WingetPackageIdentity(string Id, string Source, string InstalledVersion, string AvailableVersion);

public sealed record WingetPreparedOperation(
    string Id,
    string Kind,
    IReadOnlyList<WingetPackageDetails> Packages,
    WingetPackageOptions Options,
    DateTimeOffset CreatedUtc);

public sealed record WingetQueueItem(
    string Id,
    WingetPackageUpdate Package,
    string Kind,
    string State,
    string? Message = null,
    DateTimeOffset? StartedUtc = null,
    DateTimeOffset? CompletedUtc = null)
{
    public WingetInstallResult? Result { get; init; }
}
