// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Text.Json;
using Kit.WingetLib;
using ManagedCommon;
using ManagedCommon.ServiceHost;

namespace Kit.WingetService;

internal sealed partial class WingetServiceRuntime : IAsyncDisposable
{
    private const int MaximumOutputCharacters = 16 * 1024;
    private readonly object _gate = new();
    private readonly object _stateWriteGate = new();
    private readonly IWingetClient _client;
    private readonly WingetStateStore _store;
    private readonly IWingetUpdateNotifier _notifier;
    private readonly string _settingsPath;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private WingetPersistentState _state;
    private WingetServiceSettings _settings = new();
    private WingetServiceStatus _status = new();
    private Task? _activeTask;
    private CancellationTokenSource? _operationCancellation;
    private bool _stopping;

    internal WingetServiceRuntime(IWingetClient client, string dataDirectory, IWingetUpdateNotifier notifier, Func<DateTimeOffset>? utcNow = null)
    {
        _client = client;
        _store = new WingetStateStore(dataDirectory);
        _settingsPath = Path.Combine(dataDirectory, "settings.json");
        _notifier = notifier;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _state = _store.Load();
        RecoverInterruptedQueue();
        _status = _status with { Packages = _state.PendingInstall is null ? _state.Packages : [] };
        ReloadSettings();
    }

    internal void ReloadSettings()
    {
        WingetServiceSettings previous;
        lock (_gate)
        {
            previous = _settings;
        }

        var settings = WingetServiceSettings.Read(_settingsPath, previous);
        lock (_gate)
        {
            _settings = settings;
        }
    }

    internal WingetServiceStatus GetStatus()
    {
        lock (_gate)
        {
            return _status with
            {
                DailyCheckEnabled = _settings.DailyCheckEnabled,
                NotifyUpdates = _settings.NotifyUpdates,
                LastAttemptUtc = _state.LastAttemptUtc,
                LastSuccessUtc = _state.LastSuccessUtc,
                LastNotifiedUtc = _state.LastNotifiedUtc,
                NextCheckUtc = WingetServicePolicy.NextCheck(_state, _settings.DailyCheckEnabled, _utcNow()),
                IgnoredPackages = _state.IgnoredPackages,
                Queue = _state.Queue,
                History = _state.History,
                RecoveryRequired = _state.PendingInstall is not null,
                PreparedOperation = IsPreparationCurrent(_status.PreparedOperation) ? _status.PreparedOperation : null,
            };
        }
    }

    internal void StartDetection() => StartOperation("detect", new WingetServiceRequest());

    internal void Tick()
    {
        ReloadSettings();
        lock (_gate)
        {
            if (!_stopping && !_status.IsBusy && WingetServicePolicy.IsDue(_state, _settings.DailyCheckEnabled, _utcNow()))
            {
                StartOperation("check", new WingetServiceRequest());
            }
        }
    }

    internal Task<KitPipeResponse> HandleRequestAsync(KitPipeRequest envelope, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(KitPipeResponse.Fail("CANCELLED", "The connection was cancelled."));
        }

        if (envelope.Version != 1)
        {
            return Task.FromResult(KitPipeResponse.Fail("VERSION", "Unsupported Winget service protocol version."));
        }

        string command = (envelope.Command ?? string.Empty).ToLowerInvariant();
        if (command == "get-status")
        {
            return Task.FromResult(KitPipeResponse.Ok(JsonSerializer.Serialize(GetStatus(), WingetServiceJsonContext.Default.WingetServiceStatus)));
        }

        if (command is not ("check" or "search" or "list-installed" or "details" or "download" or "install" or "upgrade" or "cancel"
            or "prepare" or "enqueue" or "cancel-queue" or "ignore" or "unignore"))
        {
            return Task.FromResult(KitPipeResponse.Fail("UNKNOWN_COMMAND", "Unknown Winget command."));
        }

        try
        {
            if (envelope.PayloadJson?.Length > 65536)
            {
                return Task.FromResult(KitPipeResponse.Fail("INVALID_REQUEST", "Winget command payload is too large."));
            }

            var request = string.IsNullOrWhiteSpace(envelope.PayloadJson)
                ? new WingetServiceRequest()
                : JsonSerializer.Deserialize(envelope.PayloadJson, WingetServiceJsonContext.Default.WingetServiceRequest) ?? new();
            WingetServiceAck response = command switch
            {
                "cancel" => Cancel(request.OperationId),
                "cancel-queue" => CancelQueue(request.OperationId),
                "enqueue" => Enqueue(request),
                "ignore" or "unignore" => SetIgnored(command == "ignore", request),
                _ => StartOperation(command, request),
            };
            return Task.FromResult(KitPipeResponse.Ok(JsonSerializer.Serialize(response, WingetServiceJsonContext.Default.WingetServiceAck)));
        }
        catch (JsonException)
        {
            return Task.FromResult(KitPipeResponse.Fail("INVALID_REQUEST", "Invalid Winget command payload."));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(KitPipeResponse.Ok(JsonSerializer.Serialize(new WingetServiceAck(false, Error: exception.Message), WingetServiceJsonContext.Default.WingetServiceAck)));
        }
    }

    private WingetServiceAck Cancel(string? operationId)
    {
        lock (_gate)
        {
            if (_queueRunning)
            {
                return CancelQueueCore(operationId);
            }

            if (!_status.IsBusy || string.IsNullOrEmpty(operationId) || operationId != _status.OperationId)
            {
                return new(false, Error: "The requested operation is no longer running.");
            }

            _status = _status with
            {
                Phase = "Cancelling",
                Message = _status.OperationKind is "install" or "upgrade"
                    ? "Cancel in the native installer window if it has already started. Kit is waiting for Windows Package Manager to finish." : null,
            };
            if (_operationCancellation is { } cancellation)
            {
                _ = cancellation.CancelAsync();
            }

            return new(true, operationId);
        }
    }

    private WingetServiceAck StartOperation(string command, WingetServiceRequest request)
    {
        lock (_gate)
        {
            if (_stopping)
            {
                return new(false, Error: "The Winget service is stopping.");
            }

            if (_status.IsBusy)
            {
                return new(false, _status.OperationId, "Another Winget operation is running.");
            }

            WingetPackageUpdate? package = null;
            if (command == "prepare")
            {
                string? error = ValidatePreparation(request);
                if (error is not null)
                {
                    return new(false, Error: error);
                }
            }

            if (command == "search" && (string.IsNullOrWhiteSpace(request.Query) || request.Query.Trim().Length is < 2 or > 200 || request.Query.Any(char.IsControl)))
            {
                return new(false, Error: "Enter between 2 and 200 characters to search the winget catalog.");
            }

            if (command is "details" or "download" or "install" or "upgrade")
            {
                if (_state.PendingInstall is not null)
                {
                    return new(false, Error: "Kit must check the result of the previous installation before another package action.");
                }

                var candidates = command == "install" ? _status.SearchResults : command == "upgrade" ? _status.Packages
                    : _status.Packages.Concat(_status.SearchResults).Concat(_status.InstalledPackages);
                if (request.Packages is { Count: > 0 })
                {
                    if (request.Packages.Count != 1 || !ValidIdentity(request.Packages[0]))
                    {
                        return new(false, Error: "Select exactly one valid package for this action.");
                    }

                    var matches = candidates.Where(candidate => WingetServicePolicy.Matches(candidate, request.Packages[0]))
                        .DistinctBy(candidate => (candidate.Source.ToUpperInvariant(), candidate.Id.ToUpperInvariant(), candidate.InstalledVersion, candidate.AvailableVersion)).ToArray();
                    package = matches.Length == 1 ? matches[0] : null;
                }
                else
                {
                    var matches = candidates.Where(candidate => string.Equals(candidate.Id, request.PackageId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidate.Source, request.Source, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidate.AvailableVersion, request.AvailableVersion, StringComparison.Ordinal))
                        .DistinctBy(candidate => (candidate.Source.ToUpperInvariant(), candidate.Id.ToUpperInvariant(), candidate.InstalledVersion, candidate.AvailableVersion)).ToArray();
                    package = matches.Length == 1 ? matches[0] : null;
                }

                if (package is null)
                {
                    return new(false, Error: "This package is no longer in the current results. Search or check for updates again.");
                }

                if (command is "download" or "install" or "upgrade")
                {
                    var details = _status.SelectedDetails;
                    if (details is null || !details.Package.IsMetadataResolved
                        || !WingetServicePolicy.Matches(details.Package, WingetServicePolicy.Identity(package)))
                    {
                        return new(false, Error: "Load this update's details before downloading or installing it.");
                    }

                    if (details.Agreements.Count > 0 && !request.AcceptPackageAgreements)
                    {
                        return new(false, Error: "Review and accept this package's agreements first.");
                    }

                    package = details.Package;
                    if ((command is "install" or "upgrade") && package.InstallEligibility == WingetInstallEligibility.ManualOnly)
                    {
                        return new(false, Error: package.InstallEligibilityReason);
                    }

                    if (command == "install" && !string.IsNullOrEmpty(package.InstalledVersion))
                    {
                        return new(false, Error: "The package is already installed. Use software updates instead.");
                    }

                    if (command == "upgrade" && string.IsNullOrEmpty(package.InstalledVersion))
                    {
                        return new(false, Error: "The package is not installed. Use software installation instead.");
                    }
                }
            }

            string operationId = Guid.NewGuid().ToString("N");
            _operationCancellation?.Dispose();
            _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            CancellationToken token = _operationCancellation.Token;
            _status = _status with
            {
                IsBusy = true,
                OperationId = operationId,
                OperationKind = command,
                Phase = command switch
                {
                    "detect" => "Detecting",
                    "check" => "Checking",
                    "search" => "Searching",
                    "list-installed" => "ListingInstalled",
                    "prepare" => "Preparing",
                    "details" => "LoadingDetails",
                    "download" => "Downloading",
                    "install" => "Installing",
                    "upgrade" => "Installing",
                    _ => "Starting",
                },
                ProgressPercent = null,
                Message = null,
                Output = null,
                LastResult = null,
                PreparedOperation = null,
            };
            _activeTask = Task.Run(() => ExecuteAsync(operationId, command, package, request, token));
            return new(true, operationId);
        }
    }

    private async Task ExecuteAsync(string operationId, string command, WingetPackageUpdate? package, WingetServiceRequest request, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        Logger.LogInfo($"[Winget] operation={operationId}; kind={command}; stage=start; queryLength={request.Query?.Trim().Length ?? 0}; packages={request.Packages?.Count ?? 0}");
        bool acceptAgreements = request.AcceptPackageAgreements;
        WingetServiceOperationResult? result = null;
        try
        {
            if (command == "check")
            {
                lock (_gate)
                {
                    _state = WingetServicePolicy.BeginAttempt(_state, _utcNow());
                }

                SaveState();
            }

            if (command is "detect" or "check")
            {
                var availability = await _client.DetectAsync(token).ConfigureAwait(false);
                lock (_gate)
                {
                    _status = _status with { Availability = availability, Phase = command == "check" ? "Checking" : "Completed" };
                }

                if (!availability.IsAvailable)
                {
                    throw new WingetOperationException(availability.Error ?? "Windows Package Manager is not available.");
                }
            }

            switch (command)
            {
                case "prepare":
                    await PrepareAsync(request, token).ConfigureAwait(false);
                    break;
                case "list-installed":
                    var installedPackages = await _client.ListInstalledAsync(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        _status = _status with { InstalledPackages = installedPackages.Take(4096).ToArray(), SelectedDetails = null };
                    }

                    break;
                case "search":
                    var found = await _client.SearchAsync(request.Query!.Trim(), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        // Keep the previous query, rows, and details together until a
                        // successful lookup can replace the entire search snapshot.
                        _status = _status with
                        {
                            SearchQuery = request.Query!.Trim(),
                            SearchResults = found.Take(100).ToArray(),
                            SearchResultsTruncated = found.Count >= 100,
                            SelectedDetails = null,
                        };
                    }

                    break;
                case "check":
                    var packages = await _client.CheckUpdatesAsync(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        _state = WingetServicePolicy.CompleteCheck(_state, _utcNow(), packages);
                        _status = _status with { Packages = _state.Packages, SelectedDetails = null };
                    }

                    SaveState();
                    NotifyUpdates(packages);
                    break;
                case "details":
                    var details = await _client.GetDetailsAsync(package!, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (!WingetServicePolicy.IsValidResolution(package!, details.Package))
                    {
                        throw new WingetOperationException("The package details changed. Check for updates again.");
                    }

                    lock (_gate)
                    {
                        UpdateReviewedPackage(details.Package);
                        _status = _status with { SelectedDetails = details };
                    }

                    break;
                case "download":
                    var download = await _client.DownloadAsync(package!, new OperationProgress(this, operationId), acceptPackageAgreements: acceptAgreements, cancellationToken: token).ConfigureAwait(false);
                    result = new(operationId, command, true, Download: download);
                    break;
                case "install":
                case "upgrade":
                    WingetPersistentState pendingInstall;
                    lock (_gate)
                    {
                        pendingInstall = _state with
                        {
                            PendingInstall = new(package!.Id, package.Source, package.AvailableVersion, _utcNow()),
                            RetryAfterUtc = null,
                        };
                    }

                    // This write is a prerequisite, not a best-effort cache save. If the
                    // host is terminated while an installer runs, restart must recheck.
                    lock (_gate)
                    {
                        _state = pendingInstall;
                    }

                    PersistState();

                    var install = command == "install"
                        ? await _client.InstallAsync(package!, new OperationProgress(this, operationId), acceptPackageAgreements: acceptAgreements, cancellationToken: token).ConfigureAwait(false)
                        : await _client.UpgradeAsync(package!, new OperationProgress(this, operationId), acceptPackageAgreements: acceptAgreements, cancellationToken: token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        install = install with { Output = BoundOutput(install.Output ?? _status.Output) };
                        _status = _status with { Output = install.Output };
                    }

                    result = new(operationId, command, install.Succeeded, install.Error, Install: install);
                    if (install.Succeeded)
                    {
                        lock (_gate)
                        {
                            ApplySuccessfulPackageAction(command, package!);
                            _state = _state with { PendingInstall = install.VerificationPending ? _state.PendingInstall : null };
                        }

                        SaveState();
                    }

                    break;
            }

            result ??= new(operationId, command, true);
        }
        catch (OperationCanceledException)
        {
            result = new(operationId, command, false, "Cancelled");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Winget] {command} failed.", ex);
            result = new(operationId, command, false, ex.Message);
            if (command == "check")
            {
                lock (_gate)
                {
                    _state = WingetServicePolicy.FailCheck(_state, _utcNow());
                }

                SaveState();
            }
        }
        finally
        {
            if (result is not null)
            {
                result = result with { ElapsedMilliseconds = elapsed.ElapsedMilliseconds };
            }

            Logger.LogInfo($"[Winget] operation={operationId}; kind={command}; stage=complete; elapsedMs={elapsed.ElapsedMilliseconds}; succeeded={result?.Succeeded == true}; cancelled={token.IsCancellationRequested}");
            lock (_gate)
            {
                // A completed, cancelled, or interrupted installer may have changed
                // installed state. Only a fresh read-only check clears the marker.
                _status = _status with
                {
                    IsBusy = false,
                    Phase = result?.Succeeded == true ? "Completed" : result?.Install?.Cancelled == true || (result?.Install is null && token.IsCancellationRequested) ? "Cancelled" : "Failed",
                    Message = result?.Message,
                    LastResult = result,
                    Packages = _state.PendingInstall is null ? _status.Packages : [],
                    SearchResults = _state.PendingInstall is null ? _status.SearchResults : [],
                    InstalledPackages = _state.PendingInstall is null ? _status.InstalledPackages : [],
                    SelectedDetails = _state.PendingInstall is null ? _status.SelectedDetails : null,
                };
            }

            if (command == "detect")
            {
                Tick();
            }
        }
    }

    // Notification identity survives a restart. Shell calls and disk writes run outside
    // the status lock so get-status/cancel keep answering while Windows is busy.
    private void NotifyUpdates(IReadOnlyList<WingetPackageUpdate> packages)
    {
        lock (_gate)
        {
            packages = WingetServicePolicy.VisibleUpdates(_state);
        }

        string fingerprint = WingetServicePolicy.Fingerprint(packages);
        lock (_gate)
        {
            if (!_settings.NotifyUpdates || packages.Count == 0 || fingerprint == _state.NotificationFingerprint || _stopping)
            {
                return;
            }
        }

        string? error = _notifier.Show(packages);
        bool notified = false;
        lock (_gate)
        {
            _status = _status with { NotificationError = error };
            if (error is null)
            {
                _state = _state with { NotificationFingerprint = fingerprint, LastNotifiedUtc = _utcNow() };
                notified = true;
            }
        }

        if (notified)
        {
            SaveState();
        }
    }

    private void SaveState()
    {
        try
        {
            PersistState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning($"[Winget] Could not persist service state: {ex.Message}");
        }
    }

    // Always capture the newest snapshot after acquiring the write gate. A queue
    // cancellation must never be overwritten by an older completion snapshot.
    private void PersistState()
    {
        lock (_stateWriteGate)
        {
            WingetPersistentState snapshot;
            lock (_gate)
            {
                snapshot = _state;
            }

            _store.Save(snapshot);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? active;
        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            active = _activeTask;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (active is not null)
        {
            await active.ConfigureAwait(false);
        }

        _operationCancellation?.Dispose();
        _shutdown.Dispose();
    }

    private static string? BoundOutput(string? output)
    {
        if (output is null || output.Length <= MaximumOutputCharacters)
        {
            return output;
        }

        int start = output.Length - MaximumOutputCharacters;
        if (char.IsLowSurrogate(output[start]) && char.IsHighSurrogate(output[start - 1]))
        {
            start++;
        }

        return output[start..];
    }

    private sealed class OperationProgress(WingetServiceRuntime owner, string operationId, string? queueItemId = null) : IProgress<WingetOperationProgress>
    {
        public void Report(WingetOperationProgress value)
        {
            lock (owner._gate)
            {
                if (owner._status.IsBusy && owner._status.OperationId == operationId
                    && (queueItemId is null || owner._state.Queue.Any(item => item.Id == queueItemId && item.State == "Running")))
                {
                    owner._status = owner._status with
                    {
                        Phase = owner._status.Phase == "Cancelling" ? "Cancelling" : value.Stage,
                        ProgressPercent = owner._status.Phase == "Cancelling" ? owner._status.ProgressPercent : value.Percentage,
                        Output = value.Output is null ? owner._status.Output : BoundOutput(value.Output),
                    };
                }
            }
        }
    }
}
