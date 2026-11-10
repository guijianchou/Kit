// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System.Globalization;
using System.Linq;
using Kit.WingetLib;

namespace Kit.Settings.UI.ViewModels;

public sealed partial class WingetViewModel
{
    public bool HasSearchResults => SearchResults.Count > 0;

    public bool SearchResultsLimited => ActiveTabIndex == (int)WingetWorkspace.Discover && status.SearchResultsTruncated;

    public bool IsSearching => IsEnabled && connected && ((requestPending && pendingCommand == "search") || (IsBusy && status.OperationKind == "search"));

    public string SearchStatusText => IsSearching
        ? string.Format(CultureInfo.CurrentCulture, L("SearchingQuery"), pendingSearchQuery ?? SearchQuery.Trim()) + (HasSearchResults ? " " + L("SearchKeepsResults") : string.Empty)
        : status.LastResult is { Kind: "search", Succeeded: false }
            ? SearchResultText + " · " + L(HasSearchResults ? "SearchRetryPrevious" : "SearchRetry")
            : SearchResultText;

    public bool HasDetails => SelectedDetails != null;

    public bool IsLoadingDetails => (loadingDetails && ReferenceEquals(detailsRequestRow, ActiveSelection)) || (IsBusy && status.OperationKind == "details");

    public bool HasDetailsError => !IsLoadingDetails && !HasDetails;

    public string DetailsErrorText => ReferenceEquals(detailsRequestRow, ActiveSelection) && !string.IsNullOrWhiteSpace(detailsError) ? detailsError : L("DetailsUnavailable");

    public bool CanOpenDetails => HasSelection && (HasDetails || CanActOnPackage);

    public bool CanRetryDetails => !IsLoadingDetails && CanActOnPackage;

    public bool HasOperationFeedback => IsBusy || requestPending || status.Queue.Count > 0 || status.History.Count > 0 || lastPackageResult != null;

    public bool CanCancelOperation => IsBusy && !cancellationPending && status.Phase != "Cancelling";

    // Presentation state stays separate from localized text, so semantic colors
    // never depend on the current language or on parsing worker messages.
    public string ReadinessTone => !IsEnabled ? "Neutral" :
        !connected ? string.IsNullOrWhiteSpace(connectionError) ? "Accent" : "Warning" :
        status.Availability is not { IsAvailable: true } ? "Warning" : IsBusy ? "Accent" : "Success";

    public string ReadinessGlyph => ReadinessTone switch
    {
        "Neutral" => "\uE7E8",
        "Accent" => "\uE895",
        "Success" => "\uE73E",
        _ => "\uE7BA",
    };

    public string OperationTone => IsBusy || requestPending ? "Accent"
        : status.Queue.Any(item => item.State == "Failed") ? "Error"
        : status.Queue.Any(item => item.State is "NeedsVerification" or "Cancelled") ? "Warning"
        : LatestOperationItem is { } latest ? latest.State switch
        {
            "Succeeded" => "Success",
            "Failed" => "Error",
            "NeedsVerification" or "Cancelled" => "Warning",
            _ => "Neutral",
        }
        : lastPackageResult is { } result ? result.Install?.VerificationPending == true ? "Warning" : result.Succeeded ? "Success" : "Error" : "Neutral";

    public string OperationGlyph => OperationTone switch
    {
        "Accent" => "\uE895",
        "Success" => "\uE73E",
        "Error" => "\uEA39",
        "Warning" => "\uE7BA",
        _ => "\uE81C",
    };

    public string OperationSummary
    {
        get
        {
            if (IsBusy)
            {
                var running = status.Queue.FirstOrDefault(item => item.State == "Running");
                return IsQueueRunning && running != null
                    ? string.Format(CultureInfo.CurrentCulture, L("QueueRunning"), status.Queue.Count(item => item.State != "Pending" && item.State != "Running") + 1, status.Queue.Count, L("Action_" + running.Kind))
                    : OperationText;
            }

            if (requestPending)
            {
                return L("OperationPending");
            }

            if (status.Queue.Count > 0)
            {
                return string.Format(CultureInfo.CurrentCulture, L("QueueSummary"), status.Queue.Count(item => item.State == "Succeeded"), status.Queue.Count(item => item.State == "Failed"), status.Queue.Count(item => item.State == "Cancelled"), status.Queue.Count(item => item.State == "NeedsVerification"));
            }

            return lastPackageResult == null ? L("LastOperation") : lastPackageResult.Succeeded ? L("Completed") : L("RequestFailed");
        }
    }

    public string OperationDescription
    {
        get
        {
            if (IsBusy)
            {
                var running = status.Queue.FirstOrDefault(item => item.State == "Running");
                return running != null ? running.Package.Name + " · " + OperationText : status.Message ?? string.Empty;
            }

            var latest = LatestOperationItem;
            return latest != null
                ? latest.Package.Name + " · " + L("Queue_" + latest.State) + (string.IsNullOrWhiteSpace(latest.Message) ? string.Empty : " · " + latest.Message)
                : lastPackageResult?.Message ?? string.Empty;
        }
    }

    public string OperationOutput => IsBusy ? status.Output ?? string.Empty
        : LatestOperationItem is { } latest
            ? latest.Result?.Output ?? string.Empty
            : lastPackageResult?.Install?.Output ?? string.Empty;

    public bool HasOperationOutput => !string.IsNullOrWhiteSpace(OperationOutput);

    private WingetQueueItem? LatestOperationItem => status.Queue.Count > 0 ? status.Queue[^1] : status.History.Count > 0 ? status.History[0] : null;

    private void NotifyState()
    {
        if (applyingStatus || refreshingWorkspace || disposed)
        {
            return;
        }

        // A status reply is a value snapshot. Publishing an empty property name
        // invalidates every XAML binding, including templates, selection and
        // focus. Only changed presentation values belong on the UI hot path.
        PublishValue(nameof(IsEnabled), IsEnabled);
        PublishValue(nameof(DailyCheckEnabled), DailyCheckEnabled);
        PublishValue(nameof(NotifyUpdates), NotifyUpdates);
        PublishValue(nameof(FilterText), FilterText);
        PublishValue(nameof(ExactMatch), ExactMatch);
        PublishValue(nameof(IncludeUnknown), IncludeUnknown);
        PublishValue(nameof(ShowIgnored), ShowIgnored);
        PublishValue(nameof(SortIndex), SortIndex);
        PublishValue(nameof(SortDescending), SortDescending);
        PublishValue(nameof(ViewModeIndex), ViewModeIndex);
        PublishValue(nameof(ActiveSelection), ActiveSelection);
        PublishValue(nameof(HasSelection), HasSelection);
        PublishValue(nameof(SelectedCount), SelectedCount);
        PublishValue(nameof(IsAllVisibleSelected), IsAllVisibleSelected);
        PublishValue(nameof(SelectionSummary), SelectionSummary);
        PublishValue(nameof(WorkspaceTitle), WorkspaceTitle);
        PublishValue(nameof(WorkspaceSubtitle), WorkspaceSubtitle);
        PublishValue(nameof(PrimaryActionLabel), PrimaryActionLabel);
        PublishValue(nameof(CanPrepareSelection), CanPrepareSelection);
        PublishValue(nameof(CanReload), CanReload);
        PublishValue(nameof(HasRows), HasRows);
        PublishValue(nameof(HasSearchResults), HasSearchResults);
        PublishValue(nameof(SearchResultsLimited), SearchResultsLimited);
        PublishValue(nameof(SearchResultText), SearchResultText);
        PublishValue(nameof(SearchEmptyText), SearchEmptyText);
        PublishValue(nameof(IsSearching), IsSearching);
        PublishValue(nameof(SearchStatusText), SearchStatusText);
        PublishValue(nameof(EmptyText), EmptyText);
        PublishValue(nameof(IsQueueRunning), IsQueueRunning);
        PublishValue(nameof(RecoveryRequired), RecoveryRequired);
        PublishValue(nameof(IsBusy), IsBusy);
        PublishValue(nameof(CanCheck), CanCheck);
        PublishValue(nameof(CanSearch), CanSearch);
        PublishValue(nameof(CanActOnPackage), CanActOnPackage);
        PublishValue(nameof(SelectedDetails), SelectedDetails);
        PublishValue(nameof(HasDetails), HasDetails);
        PublishValue(nameof(IsLoadingDetails), IsLoadingDetails);
        PublishValue(nameof(HasDetailsError), HasDetailsError);
        PublishValue(nameof(DetailsErrorText), DetailsErrorText);
        PublishValue(nameof(CanOpenDetails), CanOpenDetails);
        PublishValue(nameof(CanRetryDetails), CanRetryDetails);
        PublishValue(nameof(CanDownload), CanDownload);
        PublishValue(nameof(CanInstall), CanInstall);
        PublishValue(nameof(HasError), HasError);
        PublishValue(nameof(ErrorText), ErrorText);
        PublishValue(nameof(HasNotificationError), HasNotificationError);
        PublishValue(nameof(NotificationErrorText), NotificationErrorText);
        PublishValue(nameof(RestartRequired), RestartRequired);
        PublishValue(nameof(StatusText), StatusText);
        PublishValue(nameof(ReadinessTone), ReadinessTone);
        PublishValue(nameof(ReadinessGlyph), ReadinessGlyph);
        PublishValue(nameof(VersionText), VersionText);
        PublishValue(nameof(LastCheckText), LastCheckText);
        PublishValue(nameof(NextCheckText), NextCheckText);
        PublishValue(nameof(Progress), Progress);
        PublishValue(nameof(IsProgressIndeterminate), IsProgressIndeterminate);
        PublishValue(nameof(OperationText), OperationText);
        PublishValue(nameof(HasOperationFeedback), HasOperationFeedback);
        PublishValue(nameof(CanCancelOperation), CanCancelOperation);
        PublishValue(nameof(OperationTone), OperationTone);
        PublishValue(nameof(OperationGlyph), OperationGlyph);
        PublishValue(nameof(OperationSummary), OperationSummary);
        PublishValue(nameof(OperationDescription), OperationDescription);
        PublishValue(nameof(OperationOutput), OperationOutput);
        PublishValue(nameof(HasOperationOutput), HasOperationOutput);
        PublishValue(nameof(SelectionTitle), SelectionTitle);
        PublishValue(nameof(SelectionIdentity), SelectionIdentity);
        PublishValue(nameof(SelectionPublisher), SelectionPublisher);
        PublishValue(nameof(SelectionVersions), SelectionVersions);
        PublishValue(nameof(ReleaseNotes), ReleaseNotes);
        PublishValue(nameof(PackageDescription), PackageDescription);
        PublishValue(nameof(InstallExplanation), InstallExplanation);
        PublishValue(nameof(ManualCommand), ManualCommand);
        PublishValue(nameof(HasReleaseNotesLink), HasReleaseNotesLink);
        PublishValue(nameof(HasDownload), HasDownload);
        PublishValue(nameof(DownloadDirectory), DownloadDirectory);
        PublishValue(nameof(Activity), Activity);
    }
}
