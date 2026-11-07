// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kit.WingetLib;

namespace Kit.WingetService;

internal sealed record WingetServiceSettings(bool DailyCheckEnabled = true, bool NotifyUpdates = true)
{
    internal static WingetServiceSettings Read(string settingsPath, WingetServiceSettings previous)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return new();
            }

            using var stream = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object)
            {
                return previous;
            }

            static bool ReadBoolean(JsonElement properties, string key, bool fallback)
                => properties.TryGetProperty(key, out var property)
                    && property.ValueKind == JsonValueKind.Object
                    && property.TryGetProperty("value", out var value)
                    && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean() : fallback;

            return new(
                ReadBoolean(properties, "daily_check_enabled", previous.DailyCheckEnabled),
                ReadBoolean(properties, "notify_updates", previous.NotifyUpdates));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A partially written settings document must not flip an explicit opt-out.
            return previous;
        }
    }
}

internal sealed record WingetPersistentState
{
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public DateTimeOffset? RetryAfterUtc { get; init; }
    public int ConsecutiveFailures { get; init; }
    public string? NotificationFingerprint { get; init; }
    public DateTimeOffset? LastNotifiedUtc { get; init; }
    public IReadOnlyList<WingetPackageUpdate> Packages { get; init; } = [];
    public IReadOnlyList<WingetPackageIdentity> IgnoredPackages { get; init; } = [];
    public IReadOnlyList<WingetQueueItem> Queue { get; init; } = [];
    public IReadOnlyList<WingetQueueItem> History { get; init; } = [];
    public WingetPendingInstall? PendingInstall { get; init; }
}

internal sealed record WingetPendingInstall(string PackageId, string Source, string AvailableVersion, DateTimeOffset StartedUtc);

internal static class WingetServicePolicy
{
    internal const int MaximumQueueItems = 50;
    internal const int MaximumHistoryItems = 100;
    internal static readonly TimeSpan PreparationLifetime = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(5);

    internal static DateTimeOffset? NextCheck(WingetPersistentState state, bool enabled, DateTimeOffset now)
    {
        if (!enabled && state.PendingInstall is null)
        {
            return null;
        }

        DateTimeOffset due = state.PendingInstall is not null ? now
            : state.LastSuccessUtc is { } success ? success.Add(CheckInterval) : now;
        if (state.RetryAfterUtc is { } retry && retry > due)
        {
            due = retry;
        }

        return due < now ? now : due;
    }

    internal static bool IsDue(WingetPersistentState state, bool enabled, DateTimeOffset now)
        => NextCheck(state, enabled, now) is { } next && next <= now;

    internal static bool SameIdentity(string leftSource, string leftId, string rightSource, string rightId)
        => string.Equals(leftSource, rightSource, StringComparison.OrdinalIgnoreCase)
            && string.Equals(leftId, rightId, StringComparison.OrdinalIgnoreCase);

    internal static WingetPackageIdentity Identity(WingetPackageUpdate package)
        => new(package.Id, package.Source, package.InstalledVersion, package.AvailableVersion);

    internal static bool Matches(WingetPackageUpdate package, WingetPackageIdentity identity)
        => SameIdentity(package.Source, package.Id, identity.Source, identity.Id)
            && package.InstalledVersion == identity.InstalledVersion && package.AvailableVersion == identity.AvailableVersion;

    internal static bool IsValidResolution(WingetPackageUpdate original, WingetPackageUpdate resolved)
        => resolved.IsMetadataResolved && SameIdentity(original.Source, original.Id, resolved.Source, resolved.Id)
            && (original.IsMetadataResolved ? Matches(resolved, Identity(original))
                : original.Source == "winget" && original.AvailableVersion.Length == 0 && original.InstalledVersion.Length == 0
                    && !original.CanUninstall && !string.IsNullOrWhiteSpace(resolved.AvailableVersion));

    internal static IReadOnlyList<WingetPackageUpdate> VisibleUpdates(WingetPersistentState state)
        => state.Packages.Where(package => IsDefaultVisibleUpdate(state, package)).ToArray();

    private static bool IsDefaultVisibleUpdate(WingetPersistentState state, WingetPackageUpdate package)
        => !string.Equals(package.InstalledVersion, "Unknown", StringComparison.OrdinalIgnoreCase)
            && !state.IgnoredPackages.Any(ignored => SameIdentity(package.Source, package.Id, ignored.Source, ignored.Id));

    internal static WingetPersistentState BeginAttempt(WingetPersistentState state, DateTimeOffset now)
        => state with { LastAttemptUtc = now, RetryAfterUtc = now.Add(InitialRetryDelay) };

    internal static WingetPersistentState CompleteCheck(WingetPersistentState state, DateTimeOffset now, IReadOnlyList<WingetPackageUpdate> packages)
        => state with
        {
            LastSuccessUtc = now,
            RetryAfterUtc = null,
            ConsecutiveFailures = 0,
            Packages = packages.ToArray(),
            NotificationFingerprint = packages.Any(package => IsDefaultVisibleUpdate(state, package)) ? state.NotificationFingerprint : null,
            PendingInstall = null,
        };

    internal static WingetPersistentState FailCheck(WingetPersistentState state, DateTimeOffset now)
    {
        int failures = Math.Clamp(state.ConsecutiveFailures + 1, 1, 8);
        double minutes = Math.Min(InitialRetryDelay.TotalMinutes * Math.Pow(2, failures - 1), 360);
        return state with { ConsecutiveFailures = failures, RetryAfterUtc = now.AddMinutes(minutes) };
    }

    internal static string Fingerprint(IReadOnlyList<WingetPackageUpdate> packages)
    {
        var text = new StringBuilder();
        foreach (var package in packages.OrderBy(p => p.Source, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal).ThenBy(p => p.AvailableVersion, StringComparer.Ordinal))
        {
            foreach (string value in new[] { package.Source, package.Id, package.AvailableVersion })
            {
                text.Append(value.Length).Append(':').Append(value);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
