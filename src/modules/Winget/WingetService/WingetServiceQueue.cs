// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Kit.WingetLib;
using ManagedCommon;

namespace Kit.WingetService;

internal sealed partial class WingetServiceRuntime
{
    private bool _queueRunning;
    private bool _cancelRemaining;

    private bool IsPreparationCurrent(WingetPreparedOperation? preparation)
        => preparation is not null && _utcNow() >= preparation.CreatedUtc
            && _utcNow() - preparation.CreatedUtc < WingetServicePolicy.PreparationLifetime;

    private IReadOnlyList<WingetPackageUpdate> Candidates(string kind) => kind switch
    {
        "install" => _status.SearchResults,
        "upgrade" => _status.Packages,
        "uninstall" => _status.InstalledPackages,
        _ => [],
    };

    private static string? ValidateOptions(string kind, WingetPackageOptions options)
    {
        if (options.Scope is not null && !new[] { "user", "machine" }.Contains(options.Scope, StringComparer.OrdinalIgnoreCase))
        {
            return "Choose user scope, machine scope, or the package default.";
        }

        if (options.Architecture is not null && !new[] { "x86", "x64", "arm64" }.Contains(options.Architecture, StringComparer.OrdinalIgnoreCase))
        {
            return "Choose x86, x64, arm64, or the package default architecture.";
        }

        if (kind == "uninstall" && options.Architecture is not null)
        {
            return "WinGet does not support selecting an architecture for uninstallation.";
        }

        return options.IncludeUnknown && kind != "upgrade" ? "Include unknown versions applies only to upgrades." : null;
    }

    private static bool ValidIdentity(WingetPackageIdentity? package)
        => package is not null && !string.IsNullOrWhiteSpace(package.Id) && package.Id.Length <= 512 && !package.Id.Any(char.IsControl)
            && !string.IsNullOrWhiteSpace(package.Source) && package.Source.Length <= 128 && !package.Source.Any(char.IsControl)
            && package.InstalledVersion is { Length: <= 512 } && !package.InstalledVersion.Any(char.IsControl)
            && package.AvailableVersion is { Length: <= 512 } && !package.AvailableVersion.Any(char.IsControl);

    private string? ValidatePreparation(WingetServiceRequest request)
    {
        if (_state.PendingInstall is not null)
        {
            return "Check the result of the previous operation before preparing another operation.";
        }

        if (request.OperationKind is not ("install" or "upgrade" or "uninstall"))
        {
            return "Choose install, upgrade, or uninstall.";
        }

        if (request.Packages is null || request.Packages.Count is < 1 or > WingetServicePolicy.MaximumQueueItems || request.Packages.Any(package => !ValidIdentity(package)))
        {
            return "Select between 1 and 50 valid packages.";
        }

        if (request.Packages.DistinctBy(package => (package.Source.ToUpperInvariant(), package.Id.ToUpperInvariant(), package.InstalledVersion, package.AvailableVersion)).Count() != request.Packages.Count)
        {
            return "A package can appear only once in a batch.";
        }

        if (ValidateOptions(request.OperationKind, request.Options ?? new()) is { } error)
        {
            return error;
        }

        foreach (var identity in request.Packages)
        {
            var matches = Candidates(request.OperationKind).Where(package => WingetServicePolicy.Matches(package, identity)).ToArray();
            if (matches.Length != 1)
            {
                return "A selected package changed or is no longer in this workspace. Refresh and select it again.";
            }

            if (PackageActionError(request.OperationKind, matches[0], requireDetails: false) is { } actionError)
            {
                return actionError;
            }
        }

        return null;
    }

    private static string? PackageActionError(string kind, WingetPackageUpdate package, bool requireDetails)
    {
        if (!package.IsMetadataResolved)
        {
            return !requireDetails && kind == "install" && package.Source == "winget"
                && package.InstalledVersion.Length == 0 && package.AvailableVersion.Length == 0 && !package.CanUninstall
                ? null : "Load package details to verify the installed state and available version first.";
        }

        if (kind == "uninstall")
        {
            return !package.CanUninstall || string.IsNullOrEmpty(package.InstalledVersion) ? "This installed package cannot be uninstalled by WinGet." : null;
        }

        if (requireDetails && package.InstallEligibility is not (WingetInstallEligibility.UserWithoutElevation or WingetInstallEligibility.RequiresElevation))
        {
            return package.InstallEligibilityReason;
        }

        if (kind == "install" && !string.IsNullOrEmpty(package.InstalledVersion))
        {
            return "A selected package is already installed. Use software updates instead.";
        }

        return kind == "upgrade" && string.IsNullOrEmpty(package.InstalledVersion)
            ? "A selected package is not installed. Use software discovery instead." : null;
    }

    private async Task PrepareAsync(WingetServiceRequest request, CancellationToken token)
    {
        string kind = request.OperationKind!;
        var reviewed = new List<WingetPackageDetails>();
        foreach (var identity in request.Packages)
        {
            token.ThrowIfCancellationRequested();
            WingetPackageUpdate package;
            lock (_gate)
            {
                package = Candidates(kind).Single(candidate => WingetServicePolicy.Matches(candidate, identity));
                _status = _status with
                {
                    ProgressPercent = 100.0 * reviewed.Count / request.Packages.Count,
                    Message = $"{reviewed.Count + 1}/{request.Packages.Count} · {package.Name}",
                };
            }

            // Installed-only/local packages need no remote manifest or license agreement.
            var details = kind == "uninstall"
                ? new WingetPackageDetails(package, null, null, null, null, null, [])
                : await _client.GetDetailsAsync(package, request.Options, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!WingetServicePolicy.IsValidResolution(package, details.Package))
            {
                throw new WingetOperationException("A selected package's identity or version changed while preparing confirmation. Refresh and try again.");
            }

            if (PackageActionError(kind, details.Package, requireDetails: true) is { } error)
            {
                lock (_gate)
                {
                    // A rejected install may have discovered an installed package.
                    // Show that verified state without creating a confirmation.
                    UpdateReviewedPackage(details.Package);
                    _status = _status with { SelectedDetails = details };
                }

                throw new WingetOperationException(error);
            }

            reviewed.Add(details);
        }

        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (ValidatePreparation(request) is { } error)
            {
                throw new WingetOperationException(error);
            }

            foreach (var details in reviewed)
            {
                UpdateReviewedPackage(details.Package);
            }

            _status = _status with
            {
                PreparedOperation = new(Guid.NewGuid().ToString("N"), kind, reviewed.ToArray(), request.Options ?? new(), _utcNow()),
                SelectedDetails = reviewed.Count == 1 ? reviewed[0] : null,
                ProgressPercent = 100,
                Message = null,
            };
        }
    }

    private void UpdateReviewedPackage(WingetPackageUpdate reviewed)
    {
        static WingetPackageUpdate[] Replace(IReadOnlyList<WingetPackageUpdate> rows, WingetPackageUpdate replacement, bool preserveInstalledCapability = false)
            => rows.Select(row => WingetServicePolicy.Matches(row, WingetServicePolicy.Identity(replacement))
                || (!row.IsMetadataResolved && WingetServicePolicy.IsValidResolution(row, replacement))
                ? preserveInstalledCapability ? replacement with { CanUninstall = row.CanUninstall } : replacement : row).ToArray();
        var updates = Replace(_status.Packages, reviewed);
        _status = _status with
        {
            Packages = updates,
            SearchResults = Replace(_status.SearchResults, reviewed),
            InstalledPackages = Replace(_status.InstalledPackages, reviewed, preserveInstalledCapability: true),
        };
        _state = _state with { Packages = updates };
    }

    private WingetServiceAck Enqueue(WingetServiceRequest request)
    {
        lock (_gate)
        {
            if (_stopping || _status.IsBusy || _state.PendingInstall is not null)
            {
                return new(false, Error: "Finish the current operation and any required read-only check before starting another batch.");
            }

            var prepared = _status.PreparedOperation;
            if (!IsPreparationCurrent(prepared) || string.IsNullOrEmpty(request.PreparationId) || prepared!.Id != request.PreparationId)
            {
                return new(false, Error: "This confirmation expired or was already used. Prepare the selected packages again.");
            }

            if ((request.OperationKind is not null && request.OperationKind != prepared.Kind)
                || (request.Options is not null && request.Options != prepared.Options)
                || (request.Packages is { Count: > 0 } && (request.Packages.Count != prepared.Packages.Count
                    || !request.Packages.Zip(prepared.Packages).All(pair => pair.First is not null && WingetServicePolicy.Matches(pair.Second.Package, pair.First)))))
            {
                return new(false, Error: "The selected packages or options differ from the reviewed confirmation.");
            }

            if (prepared.Packages.Any(details => !details.Package.IsMetadataResolved
                || Candidates(prepared.Kind).Count(package => package == details.Package) != 1))
            {
                _status = _status with { PreparedOperation = null };
                return new(false, Error: "The package snapshot changed after confirmation. Prepare it again.");
            }

            if (prepared.Packages.Any(details => details.Agreements.Count > 0) && !request.AcceptPackageAgreements)
            {
                return new(false, Error: "Review and accept the displayed package agreements first.");
            }

            string operationId = Guid.NewGuid().ToString("N");
            _state = _state with
            {
                Queue = prepared.Packages.Select(details => new WingetQueueItem(Guid.NewGuid().ToString("N"), details.Package, prepared.Kind, "Pending")).ToArray(),
            };
            _queueRunning = true;
            _cancelRemaining = false;
            _operationCancellation?.Dispose();
            _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var token = _operationCancellation.Token;
            _status = _status with
            {
                IsBusy = true, OperationId = operationId, OperationKind = "queue", Phase = "Queued", ProgressPercent = null,
                PreparedOperation = null, LastResult = null, Message = null, Output = null,
            };

            // Consuming the token and reserving the only operation slot are atomic.
            // ExecuteQueueAsync persists admission before invoking any native operation.
            _activeTask = Task.Run(() => ExecuteQueueAsync(operationId, prepared, request.AcceptPackageAgreements, token));
            return new(true, operationId);
        }
    }

    private WingetServiceAck SetIgnored(bool ignored, WingetServiceRequest request)
    {
        lock (_gate)
        {
            if (_stopping || _status.IsBusy)
            {
                return new(false, Error: "Wait for the current operation before changing ignored updates.");
            }

            if (request.Packages is null || request.Packages.Count is < 1 or > 2048 || request.Packages.Any(package => !ValidIdentity(package)))
            {
                return new(false, Error: "Select valid package identities to change ignored updates.");
            }

            var identities = _state.IgnoredPackages.ToList();
            foreach (var identity in request.Packages)
            {
                identities.RemoveAll(item => WingetServicePolicy.SameIdentity(item.Source, item.Id, identity.Source, identity.Id));
                if (ignored)
                {
                    if (!_status.Packages.Any(package => WingetServicePolicy.Matches(package, identity)))
                    {
                        return new(false, Error: "The update changed. Refresh before ignoring it.");
                    }

                    identities.Add(identity);
                }
            }

            if (identities.Count > 2048)
            {
                return new(false, Error: "The ignored-package list is full.");
            }

            _state = _state with { IgnoredPackages = identities.ToArray(), NotificationFingerprint = null };
            _status = _status with { PreparedOperation = null };
        }

        PersistState();
        return new(true);
    }

    private WingetServiceAck CancelQueue(string? operationId)
    {
        WingetServiceAck result;
        lock (_gate)
        {
            result = CancelQueueCore(operationId);
        }

        if (result.Accepted)
        {
            SaveState();
        }

        return result;
    }

    private WingetServiceAck CancelQueueCore(string? operationId)
    {
        if (!_queueRunning || !_status.IsBusy || (operationId is not null && operationId != _status.OperationId))
        {
            return new(false, Error: "The requested queue is no longer running.");
        }

        _cancelRemaining = true;
        CancelPendingItems("Cancelled before starting.");
        _status = _status with { Phase = "Cancelling", Message = "Remaining operations are cancelled. Waiting for the current native installer to exit." };

        // Deliberately do not cancel the current installer's token or process.
        return new(true, _status.OperationId);
    }

    private void CancelPendingItems(string message)
    {
        foreach (var item in _state.Queue.Where(item => item.State == "Pending").ToArray())
        {
            CompleteQueueItem(item with { State = "Cancelled", Message = message, CompletedUtc = _utcNow() });
        }
    }

    private void CompleteQueueItem(WingetQueueItem item)
    {
        _state = _state with
        {
            Queue = _state.Queue.Select(existing => existing.Id == item.Id ? item : existing).ToArray(),
            History = new[] { item }.Concat(_state.History.Where(existing => existing.Id != item.Id)).Take(WingetServicePolicy.MaximumHistoryItems).ToArray(),
        };
    }

    private async Task ExecuteQueueAsync(string operationId, WingetPreparedOperation prepared, bool acceptAgreements, CancellationToken token)
    {
        try
        {
            PersistState();
            string[] ids;
            lock (_gate)
            {
                ids = _state.Queue.Select(item => item.Id).ToArray();
            }

            foreach (string id in ids)
            {
                WingetQueueItem item;
                lock (_gate)
                {
                    if (_cancelRemaining || token.IsCancellationRequested || _state.PendingInstall is not null)
                    {
                        CancelPendingItems("Stopped before starting; refresh and confirm a new batch when ready.");
                        break;
                    }

                    item = _state.Queue.Single(candidate => candidate.Id == id);
                    if (item.State != "Pending")
                    {
                        continue;
                    }

                    if (!Candidates(item.Kind).Any(package => WingetServicePolicy.Matches(package, WingetServicePolicy.Identity(item.Package))))
                    {
                        CompleteQueueItem(item with { State = "Failed", Message = "The package changed before its turn. Refresh and select it again.", CompletedUtc = _utcNow() });
                        continue;
                    }

                    item = item with { State = "Running", StartedUtc = _utcNow() };
                    _state = _state with
                    {
                        Queue = _state.Queue.Select(candidate => candidate.Id == item.Id ? item : candidate).ToArray(),
                        PendingInstall = new(item.Package.Id, item.Package.Source, item.Package.AvailableVersion, _utcNow()),
                        RetryAfterUtc = null,
                    };
                    _status = _status with { Phase = item.Kind == "uninstall" ? "Uninstalling" : "Installing", ProgressPercent = null, Message = item.Package.Name, Output = null };
                }

                WingetInstallResult result;
                bool prerequisiteSaved = false;
                try
                {
                    PersistState();
                    prerequisiteSaved = true;
                    var progress = new OperationProgress(this, operationId, item.Id);
                    result = item.Kind switch
                    {
                        "install" => await _client.InstallAsync(item.Package, progress, acceptAgreements, prepared.Options, token).ConfigureAwait(false),
                        "upgrade" => await _client.UpgradeAsync(item.Package, progress, acceptAgreements, prepared.Options, token).ConfigureAwait(false),
                        _ => await _client.UninstallAsync(item.Package, progress, prepared.Options, token).ConfigureAwait(false),
                    };
                }
                catch (OperationCanceledException)
                {
                    result = new(false, false, false, "Cancelled; installed state requires a read-only check.", null) { Cancelled = true };
                }
                catch (Exception exception)
                {
                    Logger.LogError($"[Winget] Queued {item.Kind} failed.", exception);
                    result = new(false, false, false, exception.Message, (exception as WingetOperationException)?.ErrorCode);
                }

                lock (_gate)
                {
                    result = result with { Output = BoundOutput(result.Output ?? _status.Output) };
                    _status = _status with { Output = result.Output };
                }

                bool needsRecovery = !prerequisiteSaved || result.VerificationPending || result.Cancelled || result.RequiresManualAction
                    || result.ErrorCode is 1223 or unchecked((int)0x800704C7) || token.IsCancellationRequested;
                if (!needsRecovery && !result.Succeeded)
                {
                    needsRecovery = !await RecoverAfterQueueFailureAsync(token).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    if (result.Succeeded)
                    {
                        ApplySuccessfulPackageAction(item.Kind, item.Package);
                    }

                    if (!needsRecovery)
                    {
                        _state = _state with { PendingInstall = null };
                    }

                    CompleteQueueItem(item with
                    {
                        State = needsRecovery ? "NeedsVerification" : result.Succeeded ? "Succeeded" : "Failed",
                        Message = result.Error ?? (needsRecovery ? "Read-only verification is required before another operation." : null),
                        CompletedUtc = _utcNow(),
                        Result = result,
                    });
                    _status = _status with { LastResult = new(item.Id, item.Kind, result.Succeeded, result.Error, Install: result) };
                    if (needsRecovery)
                    {
                        _cancelRemaining = true;
                        CancelPendingItems("Stopped because the previous operation requires a read-only check.");
                    }
                }

                // Completion durability is also required before the next native action.
                PersistState();
            }
        }
        catch (Exception exception)
        {
            Logger.LogError("[Winget] Queue stopped before another package could start.", exception);
            lock (_gate)
            {
                var interrupted = _state.Queue.FirstOrDefault(item => item.State == "Running")
                    ?? _state.Queue.LastOrDefault(item => item.StartedUtc is not null);
                if (interrupted is not null)
                {
                    _state = _state with
                    {
                        PendingInstall = new(interrupted.Package.Id, interrupted.Package.Source, interrupted.Package.AvailableVersion, interrupted.StartedUtc ?? _utcNow()),
                    };
                    CompleteQueueItem(interrupted with { State = "NeedsVerification", Message = exception.Message, CompletedUtc = _utcNow() });
                }

                CancelPendingItems("Stopped because queue state could not be saved or verified.");
                _status = _status with { Message = exception.Message };
            }
        }
        finally
        {
            lock (_gate)
            {
                CancelPendingItems("Cancelled before starting.");
                _queueRunning = false;
                bool succeeded = _state.Queue.Count > 0 && _state.Queue.All(item => item.State == "Succeeded");
                int completedCount = _state.Queue.Count(item => item.State == "Succeeded");
                int cancelledCount = _state.Queue.Count(item => item.State == "Cancelled");
                int failedCount = _state.Queue.Count - completedCount - cancelledCount;
                string summary = $"{completedCount} completed; {failedCount} failed or awaiting verification; {cancelledCount} cancelled.";
                var aggregate = new WingetInstallResult(succeeded, _state.Queue.Any(item => item.Result?.RebootRequired == true), false, succeeded ? null : summary, null)
                {
                    VerificationPending = _state.PendingInstall is not null,
                    Cancelled = cancelledCount > 0 && failedCount == 0,
                    Output = _status.Output,
                };
                _status = _status with
                {
                    Phase = _state.PendingInstall is not null ? "VerificationPending" : succeeded ? "Completed" : _cancelRemaining ? "Cancelled" : "Failed",
                    Packages = _state.PendingInstall is null ? _status.Packages : [],
                    SearchResults = _state.PendingInstall is null ? _status.SearchResults : [],
                    InstalledPackages = _state.PendingInstall is null ? _status.InstalledPackages : [],
                    SelectedDetails = null,
                    Message = summary,
                    LastResult = new(operationId, prepared.Kind, succeeded, summary, Install: aggregate),
                };
            }

            SaveState();
            lock (_gate)
            {
                _status = _status with { IsBusy = false };
            }
        }
    }

    private async Task<bool> RecoverAfterQueueFailureAsync(CancellationToken token)
    {
        WingetPendingInstall? pending;
        lock (_gate)
        {
            pending = _state.PendingInstall;
        }

        try
        {
            lock (_gate)
            {
                _status = _status with { Phase = "VerifyingInstall" };
            }

            var updates = await _client.CheckUpdatesAsync(token).ConfigureAwait(false);
            var installed = await _client.ListInstalledAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _state = WingetServicePolicy.CompleteCheck(_state, _utcNow(), updates);
                _status = _status with { Packages = _state.Packages, InstalledPackages = installed.Take(4096).ToArray(), SelectedDetails = null };
            }

            PersistState();
            return true;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _state = _state with { PendingInstall = pending };
            }

            Logger.LogWarning($"[Winget] Queue recovery check failed: {exception.Message}");
            return false;
        }
    }

    private void ApplySuccessfulPackageAction(string kind, WingetPackageUpdate package)
    {
        bool Matches(WingetPackageUpdate row) => WingetServicePolicy.SameIdentity(row.Source, row.Id, package.Source, package.Id)
            && (kind == "install" || row.InstalledVersion == package.InstalledVersion);
        var updates = _state.Packages.Where(row => !Matches(row)).ToArray();
        var installed = _status.InstalledPackages.Where(row => !Matches(row)).ToList();
        if (kind != "uninstall")
        {
            installed.Add(package with { InstalledVersion = package.AvailableVersion, IsUpdateAvailable = false, CanUninstall = true });
        }

        _state = _state with { Packages = updates };
        _status = _status with
        {
            Packages = updates,
            InstalledPackages = installed.ToArray(),
            SearchResults = _status.SearchResults.Select(row => Matches(row)
                ? row with { InstalledVersion = kind == "uninstall" ? string.Empty : package.AvailableVersion, CanUninstall = kind != "uninstall", IsUpdateAvailable = false } : row).ToArray(),
            SelectedDetails = null,
        };
    }

    private void RecoverInterruptedQueue()
    {
        if (!_state.Queue.Any(item => item.State is "Pending" or "Running"))
        {
            return;
        }

        foreach (var item in _state.Queue.Where(item => item.State is "Pending" or "Running").ToArray())
        {
            if (item.State == "Running" && _state.PendingInstall is null)
            {
                _state = _state with { PendingInstall = new(item.Package.Id, item.Package.Source, item.Package.AvailableVersion, item.StartedUtc ?? _utcNow()) };
            }

            CompleteQueueItem(item with
            {
                State = item.State == "Running" ? "NeedsVerification" : "Cancelled",
                Message = item.State == "Running" ? "Kit restarted during this operation. Check installed state; it will not be repeated automatically." : "Kit restarted before this operation began. It was cancelled.",
                CompletedUtc = _utcNow(),
            });
        }

        SaveState();
    }
}
