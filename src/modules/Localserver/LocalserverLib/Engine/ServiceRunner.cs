using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using LocalServerHub.Core.Commands;
using LocalServerHub.Core.Configuration;
using LocalServerHub.Core.Logging;
using LocalServerHub.Core.Models;
using LocalServerHub.Windows.Native;

namespace LocalServerHub.Windows;

public sealed record ServiceStateChangedEventArgs(
    ServiceState Previous,
    ServiceState Current,
    string? Reason);

/// <summary>
/// Owns one service's process for its whole lifetime: preflight, launch, log
/// pumping, health probing, restart backoff and stop (plan.md §3.3 / §5).
/// </summary>
/// <remarks>
/// Every state transition is published before the UI can observe its effects, and
/// the UI is never allowed to invent one. That is why <see cref="StartAsync"/>
/// moves to Preflight synchronously and only reaches Running once the health
/// probe has actually passed.
/// </remarks>
public sealed class ServiceRunner : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly Func<string, string, object?[], string>? _formatMessage;

    private ServiceDefinition _definition;
    private Process? _process;
    private JobObject? _job;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _outputCts;
    private Task _outputTask = Task.CompletedTask;
    private ServiceState _state = ServiceState.Stopped;
    private int _restartAttempt;
    private volatile bool _disposed;
    private CancellationTokenSource? _activeOperationCts;
    private CancellationTokenSource? _restartCts;
    private long _stopGeneration;
    private bool _stopRequested;
    private string? _lastPresetName;
    private IReadOnlyDictionary<string, object?>? _lastOverrides;
    private Regex? _logPattern;
    private int _logPatternMatched;
    private IReadOnlyList<string> _secretValues = [];
    private string? _ownerTag;
    private string? _ownershipJobName;
    private bool _ownsRecoverableProcess;
    private bool _isRecoveredProcess;
    private IReadOnlyList<int> _unassignedProcessIds = [];
    private IReadOnlyList<int> _lastPersistedProcessIds = [];
    private DateTimeOffset _lastOwnershipSnapshotUtc = DateTimeOffset.MinValue;
    private SecretStore? _secretStore;

    public ServiceRunner(
        ServiceDefinition definition,
        int logCapacity = 5000,
        Func<string, string, object?[], string>? formatMessage = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        _definition = definition;
        _formatMessage = formatMessage;
        Log = new LogRingBuffer(logCapacity);
    }

    /// <summary>
    /// Injects the secret store for DPAPI-encrypted parameter expansion (plan.md §585).
    /// Must be called before StartAsync so that ${secret.NAME} tokens resolve correctly.
    /// </summary>
    public void SetSecretStore(SecretStore secretStore)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        _secretStore = secretStore;
    }

    public event EventHandler<ServiceStateChangedEventArgs>? StateChanged;

    public event EventHandler<LogLine>? LogAppended;
    public event EventHandler? HealthChanged;

    public LogRingBuffer Log { get; }

    public ServiceDefinition Definition
    {
        get
        {
            lock (_gate)
            {
                return _definition;
            }
        }
    }

    public ServiceState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public int? ProcessId { get; private set; }

    /// <summary>Changes whenever a new process instance is launched.</summary>
    public long ProcessGeneration { get; private set; }

    public int? Port { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    /// <summary>True when this runner is attached to a process tagged by this Hub.</summary>
    public bool IsOwnedProcess => _ownsRecoverableProcess;

    /// <summary>
    /// True while this runner is monitoring a process tree it adopted from a
    /// previous Hub instance rather than one it launched. Such a tree has no
    /// captured stdout and its readiness cannot be replayed, so the UI has to be
    /// able to say so instead of showing an ordinary Running row with no logs.
    /// </summary>
    public bool IsRecovered => _isRecoveredProcess;

    /// <summary>Whether the current process instance has passed its configured health probe.</summary>
    public bool? HealthProbePassed { get; private set; }
    public DateTimeOffset? HealthCheckedAtUtc { get; private set; }
    public double? HealthLatencyMilliseconds { get; private set; }

    public bool CanSendInput
    {
        get
        {
            lock (_gate)
            {
                return !_disposed && !_stopRequested && !_isRecoveredProcess && _process is not null
                    && _state is ServiceState.Starting or ServiceState.Running;
            }
        }
    }

    public async Task SendInputAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 65536) throw new ArgumentException("Input is limited to 64 KiB.", nameof(text));
        Process process;
        CancellationTokenSource timeout;
        lock (_gate)
        {
            if (!CanSendInput || _process is null || _runCts is null)
                throw new InvalidOperationException("Input requires a starting or running process started in this session.");
            process = _process;
            timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runCts.Token);
        }
        using (timeout)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await _inputGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                StreamWriter input;
                lock (_gate)
                {
                    if (!CanSendInput || !ReferenceEquals(_process, process) || process.HasExited)
                        throw new InvalidOperationException("The input process is no longer available.");
                    input = process.StandardInput;
                }
                await input.WriteLineAsync(text.AsMemory(), timeout.Token).ConfigureAwait(false);
                await input.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            finally { _inputGate.Release(); }
        }
    }

    /// <summary>Last failure, kept for the LAST ERROR column and its tooltip.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Recovers the process tree left by a previous Hub instance only when its
    /// durable ownership record and process identity still agree.
    /// </summary>
    public bool TryRecover() => TryRecover(processSnapshot: null);

    public bool TryRecover(ProcessTreeSnapshot? processSnapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State != ServiceState.Stopped)
        {
            return false;
        }

        ServiceDefinition definition = Definition;
        ServiceOwnershipRecord? record = ServiceOwnershipStore.Load(definition.Id);
        if (record is null)
        {
            // Nothing claims to be running. This is the ordinary cold start, so it
            // is not worth a log line for every service in the catalog.
            return false;
        }

        // Every refusal below is reported. A service that silently stays Stopped
        // while its port is still held is the one failure mode of this path that
        // used to leave no trace at all.
        List<string> diagnostics = [];
        if (!IsValidOwnershipRecord(record, definition))
        {
            AppendResumeSkipped(FormatMessage("Localserver_LogResumeMismatch",
                "the ownership record no longer matches this target's command, working directory or owner tag."));
            return false;
        }

        PreparedOwnership? prepared = null;
        Process? process = null;
        try
        {
            if (!TryPrepareRecordedOwnership(
                    definition,
                    record,
                    processSnapshot ?? ProcessTreeSnapshot.Capture(),
                    diagnostics,
                    out prepared)
                || prepared is null)
            {
                AppendResumeSkipped(DescribeDiagnostics(diagnostics));
                return false;
            }

            int processId = SelectRecoveryProcessId(record, prepared);
            process = Process.GetProcessById(processId);
            ServiceProcessRecord selected = prepared.LiveProcesses[processId];
            ServiceOwnershipRecord recoveredRecord = record with
            {
                ProcessId = processId,
                ProcessStartTimeUtcFileTime = selected.ProcessStartTimeUtcFileTime,
                ProcessImagePath = selected.ProcessImagePath,
                ProcessChain = [.. prepared.LiveProcesses.Values],
            };
            if (!TryAttachRecoveredProcess(definition, recoveredRecord, prepared, process))
            {
                AppendResumeSkipped(FormatMessage("Localserver_LogResumeExited", "PID {0} exited while it was being adopted.", processId));
                return false;
            }

            foreach (string diagnostic in diagnostics)
            {
                AppendHub(FormatMessage("Localserver_LogResumeNote", "Resume note: {0}", diagnostic));
            }

            prepared = null;
            process = null;
            return true;
        }
        catch (Exception ex)
        {
            AppendResumeSkipped(FormatMessage("Localserver_LogResumeFailed", "adopting the recorded process tree failed: {0}",
                string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message));
            return false;
        }
        finally
        {
            prepared?.Job.Dispose();
            process?.Dispose();
        }
    }

    private void AppendResumeSkipped(string reason) =>
        AppendHub(FormatMessage("Localserver_LogResumeSkipped", "Resume skipped: {0}", reason));

    private string DescribeDiagnostics(IReadOnlyList<string> diagnostics) =>
        diagnostics.Count == 0
            ? FormatMessage("Localserver_LogResumeNoProcesses", "no process from the recorded tree is still alive.")
            : string.Join(" ", diagnostics);

    /// <summary>
    /// The outcome of rebuilding one service's ownership from its durable record.
    /// </summary>
    /// <remarks>
    /// The three sets are deliberately distinct. A process that fails one check is
    /// no longer a reason to abandon the whole tree: the Job Object is the kernel's
    /// own ownership boundary, so a member that cannot be re-validated through its
    /// environment block is still owned, and a validated process that cannot be
    /// assigned to the Job is still monitorable. Collapsing those into "recovery
    /// failed" left healthy trees orphaned and unreachable from the UI.
    /// </remarks>
    private sealed record PreparedOwnership(
        JobObject Job,
        Dictionary<int, ServiceProcessRecord> LiveProcesses,
        IReadOnlyList<int> JobOnlyProcessIds,
        IReadOnlyList<int> UnassignedProcessIds);

    /// <summary>
    /// Rebuilds one service's live process set from the PIDs recorded for that
    /// service. A missing Job Object is normal after the previous Hub process died:
    /// the new Hub creates it again and reassigns only these validated processes.
    /// </summary>
    private bool TryPrepareRecordedOwnership(
        ServiceDefinition definition,
        ServiceOwnershipRecord record,
        ProcessTreeSnapshot snapshot,
        List<string> diagnostics,
        out PreparedOwnership? prepared)
    {
        prepared = null;
        JobObject? job = null;

        try
        {
            IReadOnlyList<ServiceProcessRecord> expectedChain = GetRecordedProcessChain(record);
            Dictionary<int, ServiceProcessRecord> expectedById = [];
            foreach (ServiceProcessRecord expected in expectedChain)
            {
                if (expected.ProcessId <= 0 || !expectedById.TryAdd(expected.ProcessId, expected))
                {
                    diagnostics.Add(FormatMessage("Localserver_LogResumeInvalidPid", "The ownership record contains a duplicate or invalid PID."));
                    return false;
                }
            }

            Dictionary<int, ServiceProcessRecord> liveProcesses = [];
            foreach ((int processId, ServiceProcessRecord expected) in expectedById)
            {
                if (TryReadOwnedProcess(processId, definition, record.OwnerTag, expected, out ServiceProcessRecord? actual)
                    && actual is not null)
                {
                    liveProcesses[processId] = actual;
                }
            }

            // If a recorded parent is still alive, include descendants created
            // after the last write. This is bounded by the already validated local
            // chain; it does not inspect arbitrary processes for the tag.
            foreach (int parentProcessId in liveProcesses.Keys.ToArray())
            {
                foreach (int descendantProcessId in snapshot.GetTreeIds(parentProcessId))
                {
                    if (liveProcesses.ContainsKey(descendantProcessId))
                    {
                        continue;
                    }

                    if (TryReadOwnedProcess(
                            descendantProcessId,
                            definition,
                            record.OwnerTag,
                            expected: null,
                            out ServiceProcessRecord? actual)
                        && actual is not null)
                    {
                        liveProcesses[descendantProcessId] = actual;
                    }
                }
            }

            if (liveProcesses.Count == 0)
            {
                return false;
            }

            // conhost.exe is a console infrastructure process, not the service
            // itself. It can outlive a cmd launcher, so do not let it become the
            // recovered runner when the real child is still present. If it is the
            // only survivor, there is no service process to recover.
            if (!liveProcesses.ContainsKey(record.ProcessId))
            {
                foreach (int processId in liveProcesses.Keys.ToArray())
                {
                    if (IsConsoleHostProcess(liveProcesses[processId]))
                    {
                        liveProcesses.Remove(processId);
                    }
                }

                if (liveProcesses.Count == 0)
                {
                    diagnostics.Add(FormatMessage("Localserver_LogResumeConsoleOnly", "Only the console host survived; the service process is gone."));
                    return false;
                }
            }

            job = JobObject.TryOpen(record.JobName)
                ?? JobObject.CreateNamed(record.JobName, killOnClose: false);

            List<int> unassignedProcessIds = [];
            HashSet<int> assignedProcessIds = [.. job.GetProcessIds()];
            foreach ((int processId, ServiceProcessRecord expected) in liveProcesses.ToArray())
            {
                if (assignedProcessIds.Contains(processId))
                {
                    continue;
                }

                if (TryAssignProcess(job, processId, definition, record.OwnerTag, expected))
                {
                    assignedProcessIds.Add(processId);
                    continue;
                }

                if (TryReadOwnedProcess(processId, definition, record.OwnerTag, expected, out _))
                {
                    // Still alive and still ours, but Windows refused the Job
                    // assignment - an already-nested Job or an elevation boundary.
                    // Monitoring it is strictly better than orphaning the tree; it
                    // is simply outside the Job's reach when stopping.
                    unassignedProcessIds.Add(processId);
                    diagnostics.Add(FormatMessage("Localserver_LogResumeOutsideJob",
                        "PID {0} could not be reassigned to this service's job object; stopping will fall back to killing its process tree.", processId));
                    continue;
                }

                liveProcesses.Remove(processId);
            }

            // A pre-existing Job can contain a descendant that was not in the last
            // JSON snapshot, or one that no longer exposes the inherited tag in its
            // environment block. Job membership is the kernel's own ownership
            // record, so such a process is kept for termination purposes but is
            // never promoted to the monitored root.
            List<int> jobOnlyProcessIds = [];
            foreach (int processId in job.GetProcessIds())
            {
                if (liveProcesses.ContainsKey(processId))
                {
                    continue;
                }

                if (TryReadOwnedProcess(
                        processId,
                        definition,
                        record.OwnerTag,
                        expected: null,
                        out ServiceProcessRecord? actual)
                    && actual is not null)
                {
                    liveProcesses[processId] = actual;
                    continue;
                }

                jobOnlyProcessIds.Add(processId);
                diagnostics.Add(FormatMessage("Localserver_LogResumeWithoutOwnerTag",
                    "PID {0} is in this service's job object but no longer reports the owner tag; it is kept for stop and force-kill only.", processId));
            }

            if (liveProcesses.Count == 0)
            {
                return false;
            }

            prepared = new PreparedOwnership(job, liveProcesses, jobOnlyProcessIds, unassignedProcessIds);
            job = null;
            return true;
        }
        catch (Exception ex)
        {
            diagnostics.Add(FormatMessage("Localserver_LogOwnershipRebuildFailed", "Rebuilding ownership failed: {0}",
                string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message));
            return false;
        }
        finally
        {
            job?.Dispose();
        }
    }

    private static IReadOnlyList<ServiceProcessRecord> GetRecordedProcessChain(
        ServiceOwnershipRecord record)
    {
        if (record.ProcessChain.Count > 0)
        {
            return record.ProcessChain;
        }

        // Records written before the chain field was introduced remain safely
        // recoverable when their recorded root PID is still alive. They do not
        // trigger a fallback scan.
        return record.ProcessId > 0 && record.ProcessStartTimeUtcFileTime > 0
            ? [new ServiceProcessRecord(
                record.ProcessId,
                0,
                record.ProcessStartTimeUtcFileTime,
                record.ProcessImagePath)]
            : [];
    }

    private static bool TryAssignProcess(
        JobObject job,
        int processId,
        ServiceDefinition definition,
        string ownerTag,
        ServiceProcessRecord expected)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!TryReadOwnedProcess(process, definition, ownerTag, expected, out _))
            {
                return false;
            }

            job.Assign(process.Handle);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryReadOwnedProcess(
        int processId,
        ServiceDefinition definition,
        string ownerTag,
        ServiceProcessRecord? expected,
        out ServiceProcessRecord? actual)
    {
        actual = null;
        try
        {
            using Process process = Process.GetProcessById(processId);
            return TryReadOwnedProcess(process, definition, ownerTag, expected, out actual);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryReadOwnedProcess(
        Process process,
        ServiceDefinition definition,
        string ownerTag,
        ServiceProcessRecord? expected,
        out ServiceProcessRecord? actual)
    {
        actual = null;
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            ProcessIdentity? identity = ProcessIdentityInspector.Read(
                process.Id,
                ServiceOwnershipStore.OwnerTagEnvironmentVariable);
            if (!MatchesProcessIdentity(identity, definition, ownerTag))
            {
                return false;
            }

            long startTime = process.StartTime.ToUniversalTime().ToFileTimeUtc();
            if (expected is not null
                && (startTime != expected.ProcessStartTimeUtcFileTime
                    || (!string.IsNullOrWhiteSpace(expected.ProcessImagePath)
                        && !string.Equals(
                            NormalizePath(identity?.ExecutablePath),
                            NormalizePath(expected.ProcessImagePath),
                            StringComparison.OrdinalIgnoreCase))))
            {
                return false;
            }

            actual = new ServiceProcessRecord(
                process.Id,
                expected?.ParentProcessId ?? 0,
                startTime,
                identity?.ExecutablePath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int SelectRecoveryProcessId(
        ServiceOwnershipRecord record,
        PreparedOwnership prepared)
    {
        if (prepared.LiveProcesses.ContainsKey(record.ProcessId)
            && !prepared.UnassignedProcessIds.Contains(record.ProcessId))
        {
            return record.ProcessId;
        }

        Dictionary<int, ServiceProcessRecord> allProcesses = [];
        foreach (ServiceProcessRecord process in GetRecordedProcessChain(record))
        {
            allProcesses[process.ProcessId] = process;
        }

        // A process the Job could not take back is the last resort: choosing it as
        // the monitored root would put the root outside the boundary that stop and
        // force-kill rely on.
        return prepared.LiveProcesses.Values
            .Where(process => !IsConsoleHostProcess(process))
            .OrderBy(process => prepared.UnassignedProcessIds.Contains(process.ProcessId) ? 1 : 0)
            .ThenBy(process => GetRecordedProcessDepth(process, allProcesses))
            .ThenBy(process => process.ProcessId)
            .DefaultIfEmpty(prepared.LiveProcesses.Values.First())
            .First()
            .ProcessId;
    }

    private static bool IsConsoleHostProcess(ServiceProcessRecord process) =>
        string.Equals(
            Path.GetFileName(process.ProcessImagePath),
            "conhost.exe",
            StringComparison.OrdinalIgnoreCase);

    private static int GetRecordedProcessDepth(
        ServiceProcessRecord process,
        IReadOnlyDictionary<int, ServiceProcessRecord> allProcesses)
    {
        int depth = 0;
        HashSet<int> visited = [];
        int parentProcessId = process.ParentProcessId;
        while (parentProcessId > 0
            && visited.Add(parentProcessId)
            && allProcesses.TryGetValue(parentProcessId, out ServiceProcessRecord? parent))
        {
            depth++;
            parentProcessId = parent.ParentProcessId;
        }

        return depth;
    }

    private bool TryAttachRecoveredProcess(
        ServiceDefinition definition,
        ServiceOwnershipRecord record,
        PreparedOwnership prepared,
        Process process)
    {
        try
        {
            ServiceOwnershipStore.Save(record);
        }
        catch (Exception)
        {
            // The validated process can still be monitored when the runtime file
            // cannot be refreshed; the next start will use the same local record.
        }

        CancellationToken recoveredToken;
        lock (_gate)
        {
            _job = prepared.Job;
            _process = process;
            _ownerTag = record.OwnerTag;
            _ownershipJobName = record.JobName;
            _ownsRecoverableProcess = true;
            _isRecoveredProcess = true;
            _unassignedProcessIds = prepared.UnassignedProcessIds;
            ProcessId = process.Id;
            Port = record.Port;
            StartedAtUtc = record.StartedAtUtc;
            ProcessGeneration++;
            _runCts = new CancellationTokenSource();
            recoveredToken = _runCts.Token;
            _logPattern = CreateLogPattern(definition.Health.Pattern);
            Volatile.Write(ref _logPatternMatched, 0);
            _secretValues = [];
            HealthProbePassed = definition.Health.Kind == HealthCheckKind.None;
            _state = ServiceState.Starting;
        }

        process.Exited += OnProcessExited;
        process.EnableRaisingEvents = true;
        if (process.HasExited)
        {
            OnProcessExited(process, EventArgs.Empty);
            return true;
        }

        AppendHub(FormatMessage("Localserver_LogProcessAdopted",
            "Adopted PID {0} from the previous Hub instance ({1} process(es) in the tree). Output from before this session was not captured.",
            process.Id, prepared.LiveProcesses.Count));
        StateChanged?.Invoke(
            this,
            new ServiceStateChangedEventArgs(
                ServiceState.Stopped,
                ServiceState.Starting,
                "Recovered tagged process"));
        _ = MonitorRecoveredProcessAsync(definition, recoveredToken);
        return true;
    }

    private static bool HasValidOwnershipIdentity(
        ServiceOwnershipRecord record,
        ServiceDefinition definition)
    {
        try
        {
            return string.Equals(record.ServiceId, definition.Id, StringComparison.Ordinal)
                && ServiceOwnershipStore.IsOwnerTagForService(record.OwnerTag, definition.Id)
                && string.Equals(
                    record.JobName,
                    JobObject.GetServiceJobName(definition.Id, record.OwnerTag),
                    StringComparison.Ordinal)
                && MatchesDefinition(record, definition);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsValidOwnershipRecord(
        ServiceOwnershipRecord record,
        ServiceDefinition definition) =>
        HasValidOwnershipIdentity(record, definition)
        && record.ProcessId > 0
        && record.ProcessStartTimeUtcFileTime > 0;

    private static bool MatchesProcessIdentity(
        ProcessIdentity? identity,
        ServiceDefinition definition,
        string ownerTag) =>
        identity is not null
        && string.Equals(identity.OwnerTag, ownerTag, StringComparison.Ordinal)
        && string.Equals(
            NormalizePath(identity.OwnerExecutablePath),
            NormalizePath(ResolveExecutable(definition)),
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            NormalizePath(identity.WorkingDirectory),
            NormalizePath(definition.Cwd),
            StringComparison.OrdinalIgnoreCase);

    private static bool MatchesDefinition(
        ServiceOwnershipRecord record,
        ServiceDefinition definition) =>
        string.Equals(record.ServiceId, definition.Id, StringComparison.Ordinal)
        && string.Equals(
            NormalizePath(record.ExecutablePath),
            NormalizePath(ResolveExecutable(definition)),
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            NormalizePath(record.WorkingDirectory),
            NormalizePath(definition.Cwd),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces the definition. Rejected while the service is running, because a
    /// definition that no longer matches the live process makes every readout a
    /// lie (plan.md §13.8 rule 3).
    /// </summary>
    public void UpdateDefinition(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            if (!_state.AllowsEditing())
            {
                throw new InvalidOperationException(
                    $"'{_definition.Name}' cannot be updated while it is {_state.ToDisplayLabel()}.");
            }

            _definition = definition;
        }
    }

    public async Task StartAsync(
        string? presetName = null,
        IReadOnlyDictionary<string, object?>? overrides = null,
        CancellationToken cancellationToken = default)
    {
        long stopGeneration;
        lock (_gate) { stopGeneration = _stopGeneration; }
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_stopGeneration != stopGeneration) return;
                _stopRequested = false;
            }
            await StartCoreAsync(
                presetName,
                overrides,
                resetRestartAttempt: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task StartCoreAsync(
        string? presetName,
        IReadOnlyDictionary<string, object?>? overrides,
        bool resetRestartAttempt,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ServiceDefinition definition = Definition;
        ServiceState state = State;
        if (!state.CanStart())
        {
            throw new InvalidOperationException(
                $"'{definition.Name}' cannot start from {state.ToDisplayLabel()}.");
        }

        using CancellationTokenSource operationCts = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            if (_stopRequested || _disposed) return;
            _activeOperationCts = operationCts;
        }

        try
        {
            TransitionTo(ServiceState.Preflight, "Start requested");
            LastError = null;

            Dictionary<string, object?> values = CommandLineBuilder.ResolveValues(definition, presetName, overrides);
            CommandPlan plan = CommandLineBuilder.Build(definition, values);
            lock (_gate)
            {
                _lastPresetName = presetName;
                _lastOverrides = overrides is null ? null : new Dictionary<string, object?>(overrides);
            }

            string? preflightError = RunPreflight(definition, plan);
            if (preflightError is null)
            {
                if (await TryReleasePreviousOwnershipAsync(definition, operationCts.Token).ConfigureAwait(false))
                {
                    AppendHub(FormatMessage("Localserver_LogPreviousProcessReleased", "Released the previous LocalServerHub-owned process for '{0}'.", definition.Name));
                }

                preflightError = CheckPortPreflight(definition, plan.Port);
            }

            if (preflightError is not null)
            {
                Fail(preflightError);
                return;
            }

            operationCts.Token.ThrowIfCancellationRequested();
            IReadOnlyList<EnvironmentCheckItem> runtimeChecks =
                await EnvironmentInspector.CheckRuntimesAsync(definition, operationCts.Token, _formatMessage)
                    .ConfigureAwait(false);
            EnvironmentCheckItem? runtimeError = runtimeChecks
                .FirstOrDefault(item => item.Status == EnvironmentCheckStatus.Error);
            if (runtimeError is not null)
            {
                Fail(FormatMessage("Localserver_RuntimePreflightFailed", "Runtime preflight failed: {0}", runtimeError.Summary));
                return;
            }

            foreach (EnvironmentCheckItem warning in runtimeChecks
                .Where(item => item.Status == EnvironmentCheckStatus.Warning))
            {
                AppendHub(FormatMessage("Localserver_RuntimeWarning", "Runtime warning: {0}", warning.Summary));
            }

            operationCts.Token.ThrowIfCancellationRequested();
            Port = plan.Port;
            if (resetRestartAttempt)
            {
                _restartAttempt = 0;
            }

            await LaunchAsync(definition, plan, operationCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (_process is not null)
            {
                TerminateCurrentProcess();
                CleanUpProcess(deleteOwnership: true);
            }

            if (State is ServiceState.Preflight or ServiceState.Starting)
            {
                TransitionTo(ServiceState.Stopped, "Start cancelled");
            }

            throw;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeOperationCts, operationCts))
                {
                    _activeOperationCts = null;
                }
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => StopCoreAsync(false, cancellationToken);

    private async Task StopCoreAsync(bool force, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource? activeOperation;
        CancellationTokenSource? runCts;
        CancellationTokenSource? restartCts;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _stopRequested = true;
            _stopGeneration++;
            activeOperation = _activeOperationCts;
            runCts = _runCts;
            restartCts = _restartCts;
        }

        foreach (CancellationTokenSource? pending in new[] { activeOperation, runCts, restartCts }.Distinct())
        {
            try { if (pending is not null) await pending.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }

        // Once cancellation was requested, finish the stop even if its caller goes away.
        await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed || State == ServiceState.Stopped)
            {
                return;
            }

            if (State == ServiceState.Backoff)
            {
                TransitionTo(ServiceState.Stopped, "Restart cancelled");
                return;
            }

            if (State == ServiceState.Preflight)
            {
                TransitionTo(ServiceState.Stopped, "Start cancelled");
                return;
            }

            if (!State.CanStop() && State != ServiceState.StopFailed && _process is null)
            {
                return;
            }

            TransitionTo(ServiceState.Stopping, force ? "Force kill requested" : "Stop requested");
            Process? process = _process;
            int timeoutSec = Math.Max(1, Definition.StopTimeoutSec);
            IReadOnlyList<int> processIds = [];
            try { processIds = GetManagedProcessIds(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                AppendHub(FormatMessage("Localserver_LogStopQueryFailed", "Process tree query failed; exit must be verified before releasing ownership."));
            }
            bool TreeAlive()
            {
                try
                {
                    return processIds.Any(id => !HasProcessExited(id))
                        || GetManagedProcessIds().Any(id => !HasProcessExited(id));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return true; }
            }
            AppendHub(force
                ? FormatMessage("Localserver_LogForceKilling", "Force killing the process tree...")
                : FormatMessage("Localserver_LogStopping", "Stopping (timeout {0}s)...", timeoutSec));
            try
            {
                if (!force && process is not null && !process.HasExited)
                {
                    bool closeRequested = process.CloseMainWindow();
                    if (!_isRecoveredProcess && process.StartInfo.RedirectStandardInput)
                    {
                        await _inputGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                        try { process.StandardInput.Close(); }
                        finally { _inputGate.Release(); }
                        closeRequested = true;
                    }
                    if (!closeRequested)
                    {
                        AppendHub(FormatMessage("Localserver_LogGracefulStopUnavailable", "No graceful channel is available; escalating immediately."));
                    }
                    else
                    {
                        AppendHub(FormatMessage("Localserver_LogGracefulStopSent", "Sent window close / stdin EOF; waiting for exit."));
                        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSec);
                        while (TreeAlive() && DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
                            await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                AppendHub(FormatMessage("Localserver_LogGracefulStopEscalating", "Graceful stop could not complete ({0}); escalating.", ex.GetType().Name));
            }

            if (TreeAlive())
            {
                TerminateCurrentProcess();
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
                while (TreeAlive() && DateTimeOffset.UtcNow < deadline)
                    await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
            }
            if (TreeAlive())
            {
                LastError = FormatMessage("Localserver_StopFailedDetail", "Stop failed: the process tree is still alive or its exit could not be verified. Retry force kill.");
                AppendHub(LastError);
                TransitionTo(ServiceState.StopFailed, LastError);
            }
            else
            {
                await Task.WhenAny(_outputTask, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
                CleanUpProcess(deleteOwnership: true);
                LastError = null;
                AppendHub(FormatMessage("Localserver_LogStopped", "Stopped."));
                TransitionTo(ServiceState.Stopped, "Stopped");
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        string? presetName;
        IReadOnlyDictionary<string, object?>? overrides;
        long restartGeneration;
        lock (_gate)
        {
            presetName = _lastPresetName;
            overrides = _lastOverrides;
            restartGeneration = _stopGeneration + 1;
        }
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed || _stopGeneration != restartGeneration || !_state.CanStart()) return;
                _stopRequested = false;
            }
            await StartCoreAsync(presetName, overrides, resetRestartAttempt: true, cancellationToken).ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
    }

    /// <summary>
    /// Immediately terminates every process in the tree without waiting for a graceful
    /// shutdown. This is the dangerous "force kill" path required by plan.md §13.8
    /// rule 4 and §220, and must only be reachable through a confirmation dialog
    /// warning about data loss.
    /// </summary>
    public Task ForceKillAsync(CancellationToken cancellationToken = default) => StopCoreAsync(true, cancellationToken);

    /// <summary>Returns the process IDs currently assigned to this service.</summary>
    public IReadOnlyList<int> GetManagedProcessIds()
    {
        JobObject? job;
        int? processId;
        IReadOnlyList<int> unassignedProcessIds;
        lock (_gate)
        {
            job = _job;
            processId = ProcessId;
            unassignedProcessIds = _unassignedProcessIds;
        }

        if (job is not null)
        {
            try
            {
                IReadOnlyList<int> jobProcessIds = job.GetProcessIds();
                return unassignedProcessIds.Count == 0
                    ? jobProcessIds
                    : [.. jobProcessIds.Concat(unassignedProcessIds).Distinct()];
            }
            catch (ObjectDisposedException)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_job, job))
                        throw new InvalidOperationException("The current service process tree cannot be queried.");
                }
            }

            return [];
        }

        return processId is { } rootProcessId ? [rootProcessId] : [];
    }

    /// <summary>
    /// Rewrites the durable ownership snapshot from the live Job membership.
    /// </summary>
    /// <remarks>
    /// The snapshot taken at launch only knows the processes that existed in the
    /// first seconds of the run. A launcher chain - cmd to python, npm to node, a
    /// reloading web server replacing its worker - moves the real service into
    /// PIDs that were never recorded, and once the recorded root exits there is no
    /// live anchor left for the next Hub instance to walk down from. Refreshing
    /// from the Job keeps recovery working for those chains, not just for the ones
    /// whose original root happens to survive. Cheap by design: it writes only
    /// when membership changed or the snapshot has gone stale.
    /// </remarks>
    public void RefreshOwnershipSnapshot()
    {
        JobObject? job;
        Process? process;
        string? ownerTag;
        string serviceId;
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (_state is not (ServiceState.Running or ServiceState.Starting)
                || !_ownsRecoverableProcess)
            {
                return;
            }

            job = _job;
            process = _process;
            ownerTag = _ownerTag;
            serviceId = _definition.Id;
        }

        if (job is null || process is null || ownerTag is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<int> processIds = job.GetProcessIds();
            bool membershipChanged = processIds.Count != _lastPersistedProcessIds.Count
                || !processIds.OrderBy(id => id).SequenceEqual(_lastPersistedProcessIds.OrderBy(id => id));
            if (!membershipChanged
                && DateTimeOffset.UtcNow - _lastOwnershipSnapshotUtc < OwnershipSnapshotMaxAge)
            {
                return;
            }

            ServiceOwnershipRecord? record = ServiceOwnershipStore.Load(serviceId);
            if (record is null
                || !string.Equals(record.OwnerTag, ownerTag, StringComparison.Ordinal))
            {
                return;
            }

            IReadOnlyList<ServiceProcessRecord> chain = CaptureProcessChain(job, process);
            if (chain.Count == 0)
            {
                return;
            }

            // Stop/Restart can overlap this background sampling call. Do the final
            // runner identity check and the file update under the same gate used by
            // cleanup, so an old sampler cannot write its record after a new process
            // generation has already been started.
            lock (_gate)
            {
                if (_disposed
                    || _state is not (ServiceState.Running or ServiceState.Starting)
                    || !string.Equals(_ownerTag, ownerTag, StringComparison.Ordinal)
                    || !ReferenceEquals(_job, job)
                    || !ReferenceEquals(_process, process))
                {
                    return;
                }

                ServiceOwnershipRecord? current = ServiceOwnershipStore.Load(serviceId);
                if (current is null
                    || !string.Equals(current.OwnerTag, ownerTag, StringComparison.Ordinal))
                {
                    return;
                }

                ServiceOwnershipStore.Save(current with { ProcessChain = chain });
                _lastPersistedProcessIds = processIds;
                _lastOwnershipSnapshotUtc = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or IOException
            or UnauthorizedAccessException)
        {
            // Keeping the snapshot current is an optimisation for the next launch,
            // never a reason to disturb a running service.
        }
    }

    /// <summary>How long a still-accurate ownership snapshot may go unwritten.</summary>
    private static readonly TimeSpan OwnershipSnapshotMaxAge = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The command line this service would run right now, formatted for display and
    /// copy-paste. Includes the generated parameter tokens and any interpreter the
    /// launcher will introduce, so what the UI shows matches what starts
    /// (plan.md §4.5). Never used to spawn anything.
    /// </summary>
    public string DescribeCommand(string? presetName = null)
    {
        ServiceDefinition definition = Definition;
        if (string.IsNullOrWhiteSpace(definition.Executable))
        {
            return string.Empty;
        }

        CommandPlan plan;
        try
        {
            plan = CommandLineBuilder.Build(
                definition,
                CommandLineBuilder.ResolveValues(definition, presetName));
        }
        catch (Exception)
        {
            // Describing a command must never be able to break the UI that shows it.
            return definition.Executable;
        }

        ScriptLaunch launch = ScriptLauncher.ResolveForService(definition, ResolveExecutable(definition));
        if (!launch.IsIndirect)
        {
            // Show the path as configured rather than the resolved absolute one:
            // it is what the user typed and what the settings window round-trips.
            return CommandLineBuilder.ToDisplayCommand(
                definition.Executable,
                plan.Arguments,
                plan.SecretArgumentIndexes);
        }

        List<string> arguments = [.. launch.PrefixArguments, .. plan.Arguments];
        int shift = launch.PrefixArguments.Count;
        HashSet<int> secretIndexes = [.. plan.SecretArgumentIndexes.Select(index => index + shift)];
        return CommandLineBuilder.ToDisplayCommand(launch.FileName, arguments, secretIndexes);
    }

    /// <summary>
    /// Checks everything that can be checked before a single byte is spawned.
    /// Returns null when the service is clear to start.
    /// </summary>
    private string? RunPreflight(ServiceDefinition definition, CommandPlan plan)
    {
        if (plan.HasErrors)
        {
            CommandDiagnostic first = plan.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error);
            return first.Message;
        }

        if (string.IsNullOrWhiteSpace(definition.Executable))
        {
            return FormatMessage("Localserver_CommandNotConfigured", "No command is configured for this service. Set one in the service settings.");
        }

        if (!Directory.Exists(definition.Cwd))
        {
            return FormatMessage("Localserver_WorkingDirectoryNotFound", "Working directory does not exist: {0}", definition.Cwd);
        }

        string executable = ResolveExecutable(definition);
        if (Path.IsPathRooted(executable) && !File.Exists(executable))
        {
            return FormatMessage("Localserver_ExecutableNotFound", "Executable not found: {0}", executable);
        }

        // A .py target is useless without py.exe, and CreateProcess would only
        // report a bare Win32 error. Say which interpreter is missing instead.
        if (ScriptLauncher.CheckInterpreterAvailable(
                ScriptLauncher.ResolveForService(definition, executable)) is { } interpreterError)
        {
            return interpreterError;
        }

        if (definition.Io == ServiceIoMode.Pty)
        {
            return FormatMessage("Localserver_ConPtyUnavailable", "ConPTY mode is not available in this build. Use io=pipe until interactive terminal support is enabled.");
        }

        if (plan.Port is < 1 or > 65535)
            return FormatMessage("Localserver_PortOutOfRange", "Port must be between 1 and 65535.");
        if (definition.Health.Kind == HealthCheckKind.Tcp && plan.Port is null)
            return FormatMessage("Localserver_TcpHealthPortRequired", "TCP health checks require a port.");
        _ = ResolveEncoding(definition.Encoding);

        string? unresolved = FindUnresolvedVariable(definition, plan);
        if (unresolved is not null)
        {
            return unresolved;
        }

        if (definition.Health.Kind == HealthCheckKind.LogPattern)
        {
            if (string.IsNullOrWhiteSpace(definition.Health.Pattern))
            {
                return FormatMessage("Localserver_LogHealthPatternRequired", "LogPattern health checks require a non-empty regular expression.");
            }

            if (CreateLogPattern(definition.Health.Pattern) is null)
            {
                return FormatMessage("Localserver_LogHealthPatternInvalid", "LogPattern health check contains an invalid regular expression.");
            }
        }

        return null;
    }

    private string? FindUnresolvedVariable(ServiceDefinition definition, CommandPlan plan)
    {
        IEnumerable<string?> templates = plan.Arguments
            .Cast<string?>()
            .Concat(definition.Env.Values)
            .Append(definition.Health.Url)
            .Append(definition.OpenUrl);
        VariableExpander expander = VariableExpander.ForService(definition, plan.Port, _secretStore);
        foreach (string? template in templates)
        {
            _ = expander.Expand(template);
            if (expander.Unresolved.Count > 0)
            {
                return FormatMessage("Localserver_ConfigurationVariableInvalid", "A configuration variable is missing, cyclic, or malformed. Check service, env and secret references.");
            }
        }

        _secretValues = [.. expander.ResolvedSecrets, .. plan.SecretValues.Select(expander.ExpandRequired)];
        if (definition.Health.Kind == HealthCheckKind.Http
            && (!Uri.TryCreate(expander.ExpandRequired(definition.Health.Url), UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https")))
            return FormatMessage("Localserver_HttpHealthUrlRequired", "HTTP health checks require an absolute http(s) URL.");

        return null;
    }

    /// <summary>
    /// Reports a busy port, and says so in the one way that is actionable: if the
    /// holder still carries this Hub's owner tag it is an orphan from a previous
    /// session rather than a foreign application. That distinction survives the
    /// loss of the runtime ownership file, which is otherwise the only thing that
    /// makes an orphan recognisable.
    /// </summary>
    private string? CheckPortPreflight(ServiceDefinition definition, int? port)
    {
        if (port is not { } configuredPort)
        {
            return null;
        }

        PortOwner? owner = PortInspector.FindOwner(configuredPort);
        if (owner is null)
        {
            return null;
        }

        if (IsTaggedOrphan(owner.ProcessId, definition))
        {
            return FormatMessage("Localserver_OwnedPortUnavailable",
                "Port {0} is still held by PID {1} ({2}), which this Hub started for this target earlier but can no longer adopt (its ownership record is missing, or the command or working directory changed since). End it with: taskkill /PID {1} /T /F",
                configuredPort, owner.ProcessId, owner.ProcessName);
        }

        return FormatMessage("Localserver_PortInUse",
            "Port {0} is already in use by PID {1} ({2}). Free that port or choose another one.",
            configuredPort, owner.ProcessId, owner.ProcessName);
    }

    /// <summary>
    /// True when a process outside this runner still carries an owner tag issued
    /// for this service. Bounded to the one PID the caller already identified; it
    /// never scans the machine.
    /// </summary>
    private static bool IsTaggedOrphan(int processId, ServiceDefinition definition)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            ProcessIdentity? identity = ProcessIdentityInspector.Read(
                processId,
                ServiceOwnershipStore.OwnerTagEnvironmentVariable);
            return identity is not null
                && ServiceOwnershipStore.IsOwnerTagForService(identity.OwnerTag, definition.Id);
        }
        catch (Exception)
        {
            // Reading another process's environment block is best-effort; a
            // refusal only means the message stays generic.
            return false;
        }
    }

    /// <summary>
    /// Releases a tagged process tree left behind after an earlier Hub instance
    /// stopped being available before recovery could attach to it. The durable
    /// record, derived Job Object name and inherited process tag must all agree;
    /// an untagged process that happens to use the same port is never touched.
    /// </summary>
    private async Task<bool> TryReleasePreviousOwnershipAsync(
        ServiceDefinition definition,
        CancellationToken cancellationToken)
    {
        ServiceOwnershipRecord? record = ServiceOwnershipStore.Load(definition.Id);
        if (record is null || !IsValidOwnershipRecord(record, definition))
        {
            return false;
        }

        PreparedOwnership? prepared = null;
        try
        {
            List<string> diagnostics = [];
            if (!TryPrepareRecordedOwnership(
                    definition,
                    record,
                    ProcessTreeSnapshot.Capture(),
                    diagnostics,
                    out prepared)
                || prepared is null)
            {
                return false;
            }

            // Job membership is the kernel's own ownership record for this
            // service's tag, so terminating the Job is legitimate even when a
            // member no longer exposes the inherited environment tag. Processes
            // the Job refused to take back are killed by tree instead - they were
            // validated against the tag, the executable and the working directory.
            bool terminated = prepared.Job.TryTerminateAll();
            foreach (int processId in prepared.UnassignedProcessIds)
            {
                terminated |= TryKillProcessTree(processId);
            }

            if (!terminated)
            {
                return false;
            }

            for (int attempt = 0; attempt < 40; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (prepared.Job.GetProcessIds().Count == 0
                    && prepared.UnassignedProcessIds.All(HasProcessExited))
                {
                    ServiceOwnershipStore.Delete(definition.Id, record.OwnerTag);
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }

            AppendHub(FormatMessage("Localserver_LogPreviousTreeStillRunning",
                "The previously owned process tree did not fully exit; the port preflight below will report what is still holding it."));
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            prepared?.Job.Dispose();
        }

        return false;
    }

    private static bool TryKillProcessTree(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool HasProcessExited(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static IReadOnlyList<ServiceProcessRecord> CaptureProcessChain(
        JobObject job,
        Process rootProcess)
    {
        IReadOnlyList<int> processIds = job.GetProcessIds();
        if (!processIds.Contains(rootProcess.Id))
        {
            processIds = [rootProcess.Id, .. processIds];
        }

        IReadOnlyDictionary<int, int> parentIds =
            ProcessResourceInspector.GetProcessParentIds(processIds);
        List<ServiceProcessRecord> records = [];
        foreach (int processId in processIds.Distinct())
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    continue;
                }

                records.Add(new ServiceProcessRecord(
                    processId,
                    parentIds.TryGetValue(processId, out int parentProcessId)
                        ? parentProcessId
                        : 0,
                    process.StartTime.ToUniversalTime().ToFileTimeUtc(),
                    TryGetProcessImagePath(process)));
            }
            catch (Exception ex) when (ex is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                // A short-lived launcher can exit while the ownership snapshot is
                // being captured. The Job remains the authoritative boundary.
            }
        }

        return records;
    }

    /// <summary>
    /// Resolves a secret argument to the value the child process actually receives, so
    /// log redaction masks the real secret rather than its ${secret.NAME} reference.
    /// </summary>
    private string ExpandSecretForRedaction(ServiceDefinition definition, CommandPlan plan, string value)
    {
        if (_secretStore is null || string.IsNullOrEmpty(value) || !value.Contains("${", StringComparison.Ordinal))
        {
            return value;
        }

        Dictionary<string, string> variables = new(StringComparer.Ordinal)
        {
            ["service.id"] = definition.Id,
            ["service.name"] = definition.Name,
            ["service.cwd"] = definition.Cwd,
            ["service.port"] = plan.Port?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        };

        return VariableExpander.ForService(definition, plan.Port, _secretStore).ExpandRequired(value);
    }

    private async Task LaunchAsync(
        ServiceDefinition definition,
        CommandPlan plan,
        CancellationToken cancellationToken)
    {
        string ownerTag = ServiceOwnershipStore.CreateOwnerTag(definition.Id);
        string jobName = ServiceOwnershipStore.CreateJobName(definition.Id, ownerTag);
        ProcessStartInfo startInfo = BuildStartInfo(definition, plan, ownerTag);
        cancellationToken.ThrowIfCancellationRequested();

        JobObject job = JobObject.CreateNamed(jobName);
        // Redaction has to match what the child actually received. plan.SecretValues holds
        // the unexpanded ${secret.NAME} reference, so masking on that would leave the real
        // value visible in output while hiding a token nothing ever prints. Both forms are
        // kept: the reference can still appear in output the service echoes back.
        IReadOnlyList<string> secretValues =
        [
            .. _secretValues,
            .. plan.SecretValues
                .SelectMany(value => new[] { value, ExpandSecretForRedaction(definition, plan, value) })
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal),
        ];
        Process process = new() { StartInfo = startInfo };

        bool processStarted = false;
        bool ownershipSaved = false;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(FormatMessage("Localserver_ProcessStartFailed", "The process failed to start."));
            }

            processStarted = true;
            job.Assign(process.Handle);
            await AttachExistingDescendantsAsync(job, process.Id, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
            IReadOnlyList<ServiceProcessRecord> processChain = CaptureProcessChain(job, process);
            ServiceOwnershipRecord ownership = new(
                definition.Id,
                ownerTag,
                jobName,
                process.Id,
                process.StartTime.ToUniversalTime().ToFileTimeUtc(),
                startedAtUtc,
                plan.Port,
                ResolveExecutable(definition),
                definition.Cwd,
                TryGetProcessImagePath(process))
            {
                ProcessChain = processChain,
            };
            ServiceOwnershipStore.Save(ownership);
            ownershipSaved = true;
            cancellationToken.ThrowIfCancellationRequested();
            job.SetKillOnClose(false);

            ServiceStateChangedEventArgs? startingChange = null;
            lock (_gate)
            {
                _process = process;
                _job = job;
                _ownerTag = ownerTag;
                _ownershipJobName = jobName;
                _ownsRecoverableProcess = true;
                _isRecoveredProcess = false;
                _unassignedProcessIds = [];
                _lastPersistedProcessIds = [.. processChain.Select(entry => entry.ProcessId)];
                _lastOwnershipSnapshotUtc = DateTimeOffset.UtcNow;
                ProcessId = process.Id;
                ProcessGeneration++;
                StartedAtUtc = startedAtUtc;
                _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _logPattern = CreateLogPattern(definition.Health.Pattern);
                Volatile.Write(ref _logPatternMatched, 0);
                _secretValues = secretValues;
                HealthProbePassed = definition.Health.Kind == HealthCheckKind.None;

                if (_state == ServiceState.Preflight)
                {
                    _state = ServiceState.Starting;
                    startingChange = new ServiceStateChangedEventArgs(
                        ServiceState.Preflight,
                        ServiceState.Starting,
                        "Process started");
                }
            }

            _outputCts = new CancellationTokenSource();
            _outputTask = Task.WhenAll(
                PumpOutputAsync(process, process.StandardOutput.BaseStream, LogStream.StdOut, definition.Encoding, secretValues, _outputCts.Token),
                PumpOutputAsync(process, process.StandardError.BaseStream, LogStream.StdErr, definition.Encoding, secretValues, _outputCts.Token));

            AppendHub(FormatMessage("Localserver_LogProcessStarted", "Started PID {0}: {1}", process.Id, CommandLineBuilder.ToDisplayCommand(plan)));
            if (startingChange is not null)
            {
                StateChanged?.Invoke(this, startingChange);
            }

            cancellationToken.ThrowIfCancellationRequested();

            process.Exited += OnProcessExited;
            process.EnableRaisingEvents = true;

            if (process.HasExited)
            {
                OnProcessExited(process, EventArgs.Empty);
            }

            CancellationToken runToken;
            lock (_gate)
            {
                if (!ReferenceEquals(_process, process) || _runCts is null)
                {
                    return;
                }

                runToken = _runCts.Token;
            }

            await WaitForHealthAsync(definition, runToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (State == ServiceState.Running)
                _ = MonitorHealthAsync(definition, process, runToken);
        }
        catch
        {
            if (ownershipSaved)
            {
                ServiceOwnershipStore.Delete(definition.Id, ownerTag);
            }

            job.TerminateAll();
            job.Dispose();
            if (processStarted)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }

            process.Dispose();
            throw;
        }
    }

    private async Task PumpOutputAsync(Process process, Stream stream, LogStream logStream, string encoding,
        IReadOnlyList<string> secrets, CancellationToken cancellationToken)
    {
        try
        {
            await ConsoleOutputReader.ReadAsync(stream, encoding,
                text => AppendIfPresent(process, logStream, text, secrets), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            if (!cancellationToken.IsCancellationRequested && !_disposed)
                AppendHub(FormatMessage("Localserver_LogOutputCaptureEnded", "Output capture ended ({0}).", ex.GetType().Name));
        }
    }

    private static async Task AttachExistingDescendantsAsync(
        JobObject job,
        int rootProcessId,
        CancellationToken cancellationToken)
    {
        HashSet<int> assignedProcessIds = [.. job.GetProcessIds()];
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            bool attachedAny = false;
            foreach (int processId in ProcessResourceInspector.GetProcessTreeIds(rootProcessId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!assignedProcessIds.Add(processId))
                {
                    continue;
                }

                try
                {
                    using Process descendant = Process.GetProcessById(processId);
                    if (!descendant.HasExited)
                    {
                        job.Assign(descendant.Handle);
                        attachedAny = true;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException
                    or InvalidOperationException
                    or System.ComponentModel.Win32Exception)
                {
                    // A short-lived child may disappear between the snapshot and
                    // the assignment. The Job still owns the root and later
                    // descendants inherit that boundary.
                }
            }

            if (!attachedAny)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private ProcessStartInfo BuildStartInfo(ServiceDefinition definition, CommandPlan plan, string? ownerTag = null)
    {
        string executable = ResolveExecutable(definition);

        ProcessStartInfo startInfo = new()
        {
            WorkingDirectory = definition.Cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = ResolveEncoding(definition.Encoding),
            StandardErrorEncoding = ResolveEncoding(definition.Encoding),
            StandardInputEncoding = ResolveEncoding(definition.Encoding),
        };

        // Batch files, .py, .ps1 and .js are not PE images, so they cannot be
        // handed to CreateProcess directly; ScriptLauncher routes each one through
        // its interpreter. ArgumentList still does the quoting, so this stays a
        // token list rather than a hand-built string.
        ScriptLauncher.Apply(startInfo, ScriptLauncher.ResolveForService(definition, executable));

        VariableExpander expander = VariableExpander.ForService(definition, plan.Port, _secretStore);

        // Arguments are expanded too, not just env values. CommandLineBuilder lives in
        // Core and stays platform-neutral, so it emits a secret parameter's ${secret.NAME}
        // reference verbatim; without expanding here the child process would receive the
        // literal token and the migration would turn a working secret into a broken one.
        foreach (string argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(expander.ExpandRequired(argument));
        }
        foreach ((string key, string value) in definition.Env)
        {
            startInfo.Environment[key] = expander.ExpandRequired(value);
        }
        _secretValues = [.. _secretValues.Concat(expander.ResolvedSecrets).Distinct(StringComparer.Ordinal)];

        if (ownerTag is not null)
        {
            startInfo.Environment[ServiceOwnershipStore.OwnerTagEnvironmentVariable] = ownerTag;
            startInfo.Environment[ServiceOwnershipStore.OwnerWorkingDirectoryEnvironmentVariable] = definition.Cwd;
            startInfo.Environment[ServiceOwnershipStore.OwnerExecutableEnvironmentVariable] = executable;
        }

        return startInfo;
    }

    /// <summary>
    /// Resolves the configured executable against the service's cwd. A target with
    /// no command yet is normal - the settings window creates one that way - so this
    /// returns empty rather than throwing out of Path.GetFullPath. RunPreflight is
    /// what turns that into a readable refusal.
    /// </summary>
    private static string ResolveExecutable(ServiceDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Executable))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(definition.Executable))
        {
            return definition.Executable;
        }

        try
        {
            return Path.GetFullPath(definition.Executable, Path.GetFullPath(definition.Cwd));
        }
        catch (ArgumentException)
        {
            // Invalid path characters. Leave it unresolved; preflight reports it.
            return definition.Executable;
        }
    }

    private static Encoding ResolveEncoding(string name) => ConsoleOutputReader.ResolveEncoding(name);

    /// <summary>
    /// Watches an adopted process tree. Nothing here may terminate it: this tree
    /// was already running and serving before the Hub restarted, so a probe that
    /// cannot be satisfied is a reporting problem, not grounds for a kill.
    /// </summary>
    private async Task MonitorRecoveredProcessAsync(ServiceDefinition definition, CancellationToken cancellationToken)
    {
        try
        {
            WarnOnForeignPortOwner(definition);

            if (definition.Health.Kind == HealthCheckKind.LogPattern)
            {
                // An adopted tree has no captured stdout, so the readiness pattern
                // can never match again. Treating that as a failed probe used to
                // end in Fail(), which killed a service that was working purely
                // because the Hub had been restarted.
                HealthProbePassed = null;
                AppendHub(FormatMessage("Localserver_LogAdoptedHealthUnverified",
                    "Health is reported as unverified: a log-pattern probe cannot be re-evaluated against a process this Hub adopted."));
                TryMarkRunning("Adopted process; log-pattern health unverified", healthy: null, resetRestartAttempt: false);
                return;
            }

            await WaitForHealthAsync(definition, cancellationToken, terminateOnTimeout: false)
                .ConfigureAwait(false);
            Process? process;
            lock (_gate) { process = _process; }
            if (process is not null && State == ServiceState.Running)
                await MonitorHealthAsync(definition, process, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Reports when the recorded port is held by something outside the adopted
    /// tree. The probe would then be answered by a different process, so a green
    /// row would be describing someone else's service.
    /// </summary>
    private void WarnOnForeignPortOwner(ServiceDefinition definition)
    {
        if (Port is not { } port || definition.Health.Kind == HealthCheckKind.None)
        {
            return;
        }

        try
        {
            PortOwner? owner = PortInspector.FindOwner(port);
            if (owner is null || GetManagedProcessIds().Contains(owner.ProcessId))
            {
                return;
            }

            AppendHub(FormatMessage("Localserver_LogAdoptedPortMismatch",
                "Warning: port {0} is held by PID {1} ({2}), which is not part of the adopted tree. Health results may describe another process.",
                port, owner.ProcessId, owner.ProcessName));
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            // A port readout is diagnostic only; failing to get one is not a fault.
        }
    }

    private static string? TryGetProcessImagePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Polls the configured probe until it passes or the timeout expires. A
    /// service with no probe is healthy as soon as it is alive.
    /// </summary>
    /// <param name="terminateOnTimeout">
    /// True for a process this runner launched: a service that never became ready
    /// is a failed start and its tree is torn down. False for an adopted tree,
    /// where the probe only decides what the row reports.
    /// </param>
    private async Task WaitForHealthAsync(
        ServiceDefinition definition,
        CancellationToken cancellationToken,
        bool terminateOnTimeout = true)
    {
        HealthCheck health = definition.Health;
        if (health.Kind == HealthCheckKind.None)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryMarkHealthy("No health probe configured", resetRestartAttempt: false);
            return;
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, health.TimeoutSec));
        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(1, health.IntervalSec));

        while (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            Process? process;
            bool exited;
            lock (_gate)
            {
                if (_state != ServiceState.Starting || _process is null)
                {
                    return;
                }

                process = _process;
                try { exited = process.HasExited; }
                catch (InvalidOperationException) { return; }
            }

            if (exited)
            {
                return; // OnProcessExited already moved the state.
            }

            if (await ProbeAsync(definition, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryMarkHealthy("Health probe passed", resetRestartAttempt: true);
                return;
            }

            HealthProbePassed = false;

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        HealthProbePassed = false;
        if (terminateOnTimeout)
        {
            Fail(FormatMessage("Localserver_HealthStartupTimedOut", "Did not become healthy within {0}s.", health.TimeoutSec));
            return;
        }

        // The adopted tree is still alive; only its readiness is unproven. Killing
        // it here would destroy work the Hub never started, so the row reports a
        // running service with a failing probe and lets the user decide.
        LastError = FormatMessage("Localserver_AdoptedHealthTimedOut", "Adopted process did not answer its health probe within {0}s.", health.TimeoutSec);
        AppendHub(FormatMessage("Localserver_LogAdoptedHealthFailed", "{0} The process tree is still running and was left alone.", LastError));
        TryMarkRunning("Adopted process; health probe not passing", healthy: false, resetRestartAttempt: false);
    }

    private bool TryMarkHealthy(string reason, bool resetRestartAttempt) =>
        TryMarkRunning(reason, healthy: true, resetRestartAttempt);

    /// <summary>
    /// Publishes the Starting -&gt; Running transition. <paramref name="healthy"/>
    /// is null when the configured probe cannot be evaluated at all, which is a
    /// different statement than a probe that ran and failed.
    /// </summary>
    private bool TryMarkRunning(string reason, bool? healthy, bool resetRestartAttempt)
    {
        ServiceStateChangedEventArgs? change;
        lock (_gate)
        {
            if (_process is null || _state != ServiceState.Starting || _stopRequested || _disposed)
            {
                return false;
            }
            try { if (_process.HasExited) return false; }
            catch (InvalidOperationException) { return false; }

            HealthProbePassed = healthy;
            if (resetRestartAttempt && StartedAtUtc is { } startedAt
                && DateTimeOffset.UtcNow - startedAt >= TimeSpan.FromMinutes(1))
            {
                _restartAttempt = 0;
            }
            _state = ServiceState.Running;
            change = new ServiceStateChangedEventArgs(
                ServiceState.Starting,
                ServiceState.Running,
                reason);
        }

        StateChanged?.Invoke(this, change);
        return true;
    }

    private async Task<bool> ProbeAsync(ServiceDefinition definition, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        bool healthy = definition.Health.Kind == HealthCheckKind.LogPattern
            ? Volatile.Read(ref _logPatternMatched) != 0
            : await HealthProbe.CheckAsync(definition, Port, _secretStore, _httpClient, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        HealthCheckedAtUtc = DateTimeOffset.UtcNow;
        HealthLatencyMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return healthy;
    }

    private async Task MonitorHealthAsync(ServiceDefinition definition, Process process, CancellationToken cancellationToken)
    {
        if (definition.Health.Kind is not (HealthCheckKind.Http or HealthCheckKind.Tcp)
            || definition.Health.MonitorIntervalSec <= 0) return;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(definition.Health.MonitorIntervalSec), cancellationToken).ConfigureAwait(false);
                bool healthy = await ProbeAsync(definition, cancellationToken).ConfigureAwait(false);
                bool changed;
                lock (_gate)
                {
                    if (!ReferenceEquals(_process, process) || _state != ServiceState.Running || _stopRequested || _disposed) return;
                    changed = HealthProbePassed != healthy;
                    HealthProbePassed = healthy;
                }
                if (changed) AppendHub(healthy
                    ? FormatMessage("Localserver_LogHealthRecovered", "Health probe recovered.")
                    : FormatMessage("Localserver_LogHealthFailed", "Health probe failed; process is still running."));
                HealthChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            if (!_disposed) AppendHub(FormatMessage("Localserver_LogHealthMonitoringStopped", "Health monitoring stopped ({0}).", ex.GetType().Name));
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process exitedProcess)
        {
            return;
        }

        _ = HandleProcessExitedAsync(exitedProcess);
    }

    private async Task HandleProcessExitedAsync(Process exitedProcess)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate) { if (!ReferenceEquals(_process, exitedProcess)) return; }
            await Task.WhenAny(_outputTask, Task.Delay(1000)).ConfigureAwait(false);
            HandleProcessExited(exitedProcess);
        }
        catch (Exception ex)
        {
            if (!_disposed) AppendHub(FormatMessage("Localserver_LogExitHandlingFailed", "Exit handling failed ({0}).", ex.GetType().Name));
        }
        finally { _operationGate.Release(); }
    }

    private void HandleProcessExited(Process exitedProcess)
    {

        int exitCode = -1;
        try
        {
            exitCode = exitedProcess.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // Handle already released.
        }

        Process? process;
        JobObject? job;
        string? ownerTag;
        bool wasRecovered;
        CancellationTokenSource? runCts;
        ServiceState current;
        bool disposed;
        bool cancelled;
        lock (_gate)
        {
            if (!ReferenceEquals(_process, exitedProcess))
            {
                return;
            }

            current = _state;
            disposed = _disposed;
            if (StartedAtUtc is { } startedAt && DateTimeOffset.UtcNow - startedAt >= TimeSpan.FromMinutes(1))
                _restartAttempt = 0;
            if (current is ServiceState.Stopping or ServiceState.Stopped or ServiceState.StopFailed or ServiceState.Failed)
            {
                return; // Expected exit or handled by StopAsync.
            }

            process = _process;
            job = _job;
            runCts = _runCts;
            cancelled = _stopRequested || runCts?.IsCancellationRequested == true;
            _process = null;
            _job = null;
            ownerTag = _ownerTag;
            _ownerTag = null;
            _ownershipJobName = null;
            _ownsRecoverableProcess = false;
            wasRecovered = _isRecoveredProcess;
            _isRecoveredProcess = false;
            _unassignedProcessIds = [];
            _lastPersistedProcessIds = [];
            _runCts = null;
            ProcessId = null;
            StartedAtUtc = null;
            _logPattern = null;
            _secretValues = [];
            HealthProbePassed = null;
            HealthCheckedAtUtc = null;
            HealthLatencyMilliseconds = null;
            Volatile.Write(ref _logPatternMatched, 0);
        }

        _outputCts?.Cancel();
        _outputCts?.Dispose();
        _outputCts = null;
        runCts?.Cancel();
        runCts?.Dispose();
        job?.TerminateAll();
        job?.Dispose();
        process?.Dispose();
        if (ownerTag is not null)
        {
            ServiceOwnershipStore.Delete(Definition.Id, ownerTag);
        }

        if (disposed || current is ServiceState.Stopping or ServiceState.Stopped or ServiceState.StopFailed or ServiceState.Failed)
        {
            return; // Expected exit or handled by StopAsync.
        }

        if (cancelled)
        {
            TransitionTo(ServiceState.Stopped, "Start cancelled");
            return;
        }

        AppendHub(FormatMessage("Localserver_LogProcessExited", "Process exited with code {0}.", exitCode));
        LastError = FormatMessage("Localserver_ProcessExitedDetail", "Exited with code {0}", exitCode);
        TransitionTo(ServiceState.Crashed, LastError);

        RestartSettings restart = Definition.Restart;
        bool shouldRestart = restart.Policy switch
        {
            RestartPolicy.Always => true,
            RestartPolicy.OnFailure => exitCode != 0,
            _ => false,
        };

        if (!disposed && !_stopRequested && shouldRestart && _restartAttempt < restart.MaxRetries)
        {
            _restartAttempt++;
            int delay = restart.BackoffForAttempt(_restartAttempt);
            CancellationTokenSource restartCts = new();
            lock (_gate)
            {
                _restartCts = restartCts;
            }

            // An adopted tree can exit because someone stopped it outside the Hub.
            // Say which rule is about to start it again, so an automatic restart is
            // never mistaken for the process refusing to die.
            if (wasRecovered)
            {
                AppendHub(FormatMessage("Localserver_LogAdoptedRestartPolicy",
                    "The exited process was adopted from a previous session; the '{0}' restart policy applies to it as well.", restart.Policy));
            }

            AppendHub(FormatMessage("Localserver_LogRestarting", "Restarting in {0}s (attempt {1}/{2}).", delay, _restartAttempt, restart.MaxRetries));
            TransitionTo(ServiceState.Backoff, $"Retry {_restartAttempt}/{restart.MaxRetries} in {delay}s");
            _ = ScheduleRestartAsync(delay, restartCts);
        }
    }

    private async Task ScheduleRestartAsync(int delaySeconds, CancellationTokenSource restartCts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), restartCts.Token).ConfigureAwait(false);
            if (State != ServiceState.Backoff)
            {
                return; // The user intervened.
            }

            restartCts.Token.ThrowIfCancellationRequested();
            await _operationGate.WaitAsync(restartCts.Token).ConfigureAwait(false);
            try
            {
                if (State != ServiceState.Backoff || _stopRequested)
                {
                    return;
                }

                TransitionTo(ServiceState.Stopped, "Backoff elapsed");
                await StartCoreAsync(
                    presetName: _lastPresetName,
                    overrides: _lastOverrides,
                    resetRestartAttempt: false,
                    restartCts.Token).ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A user stop or disposal cancelled the pending restart.
        }
        catch (Exception ex)
        {
            Fail(FormatMessage("Localserver_AutomaticRestartFailed", "Automatic restart failed: {0}",
                string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message));
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_restartCts, restartCts))
                {
                    _restartCts = null;
                }
            }
            restartCts.Dispose();
        }
    }

    /// <summary>
    /// Releases Hub-side handles and optionally cleans ownership records.
    /// </summary>
    /// <param name="deleteOwnership">
    /// true = delete the ownership record so the next launch sees no recoverable
    /// process; false = keep it so TryRecover can find the process.
    /// </param>
    private void CleanUpProcess(bool deleteOwnership = true)
    {
        CancellationTokenSource? runCts;
        JobObject? job;
        Process? process;
        string? ownerTag;
        IReadOnlyList<int> unassignedProcessIds;
        lock (_gate)
        {
            runCts = _runCts;
            job = _job;
            process = _process;
            _runCts = null;
            _job = null;
            _process = null;
            ProcessId = null;
            StartedAtUtc = null;
            ownerTag = _ownerTag;
            _ownerTag = null;
            _ownershipJobName = null;
            _ownsRecoverableProcess = false;
            _isRecoveredProcess = false;
            unassignedProcessIds = _unassignedProcessIds;
            _unassignedProcessIds = [];
            _lastPersistedProcessIds = [];
            _logPattern = null;
            _secretValues = [];
            HealthProbePassed = null;
            HealthCheckedAtUtc = null;
            HealthLatencyMilliseconds = null;
            Volatile.Write(ref _logPatternMatched, 0);
        }

        // Adopted processes the Job refused to take back are outside its reach, so
        // the Job's own teardown cannot cover them. Only kill them when explicitly
        // stopping the service (deleteOwnership = true); on Hub exit with the process
        // meant to survive, leave them alone.
        if (deleteOwnership)
        {
            foreach (int processId in unassignedProcessIds)
            {
                TryKillProcessTree(processId);
            }
        }

        _outputCts?.Cancel();
        _outputCts?.Dispose();
        _outputCts = null;
        runCts?.Cancel();
        runCts?.Dispose();
        job?.Dispose();
        process?.Dispose();
        if (deleteOwnership && ownerTag is not null)
        {
            ServiceOwnershipStore.Delete(Definition.Id, ownerTag);
        }
    }

    private void TerminateCurrentProcess()
    {
        JobObject? job;
        Process? process;
        IReadOnlyList<int> unassignedProcessIds;
        lock (_gate)
        {
            job = _job;
            process = _process;
            unassignedProcessIds = _unassignedProcessIds;
        }

        try
        {
            job?.TerminateAll();
        }
        catch (ObjectDisposedException)
        {
        }

        foreach (int processId in unassignedProcessIds)
        {
            TryKillProcessTree(processId);
        }

        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private void Fail(string? reason)
    {
        reason = string.IsNullOrWhiteSpace(reason)
            ? FormatMessage("Localserver_OperationFailed", "The operation failed.")
            : reason;
        reason = Redact(reason, _secretValues);
        LastError = reason;
        AppendHub(FormatMessage("Localserver_LogFailed", "Failed: {0}", reason));
        TransitionTo(ServiceState.Failed, reason);
        TerminateCurrentProcess();
        CleanUpProcess(deleteOwnership: true);
    }

    private void AppendIfPresent(
        Process source,
        LogStream stream,
        string? text,
        IReadOnlyList<string>? capturedSecretValues = null)
    {
        if (text is null || _disposed)
        {
            return;
        }

        Regex? pattern;
        IReadOnlyList<string> secretValues;
        lock (_gate)
        {
            pattern = ReferenceEquals(_process, source) ? _logPattern : null;
            secretValues = capturedSecretValues
                ?? (ReferenceEquals(_process, source) ? _secretValues : []);
        }

        // Before the pattern match, not after: a readiness pattern like "Listening on"
        // must still match a line that arrived as "\e[32mListening on\e[0m".
        string plain = StripEscapes(text);

        try
        {
            if (pattern is not null && pattern.IsMatch(plain))
                Volatile.Write(ref _logPatternMatched, 1);
        }
        catch (RegexMatchTimeoutException)
        {
            lock (_gate) { if (ReferenceEquals(_logPattern, pattern)) _logPattern = null; }
            AppendHub(FormatMessage("Localserver_LogHealthPatternTimedOut", "Log health pattern exceeded its time limit and was disabled for this run."));
        }

        LogLine line = Log.Append(stream, Redact(plain, secretValues));
        LogAppended?.Invoke(this, line);
    }

    /// <summary>
    /// Removes terminal control sequences from a captured line.
    /// </summary>
    /// <remarks>
    /// v1 renders plain text with keyword colouring; full ANSI is deliberately
    /// deferred (plan.md §24). Most tools drop colour when stdout is a pipe, but not
    /// all - and the ones that do not would otherwise show up as literal "←[32m"
    /// noise in the panel and in the log file that outlives it. Stripping is the
    /// behaviour that matches what v1 promises to render; when pty mode arrives, the
    /// sequences it exists to preserve will need a different path than this one.
    /// </remarks>
    private static string StripEscapes(string text)
    {
        // Cheap reject: the overwhelming majority of lines contain neither.
        if (!text.Contains('\u001b') && !text.Contains('\u009b'))
        {
            return text;
        }

        return EscapeSequencePattern.Replace(text, string.Empty);
    }

    /// <summary>
    /// CSI/OSC and the single-character escapes tools actually emit: colour, cursor
    /// moves, erase-line, and the window-title OSC that terminates on BEL or ST.
    /// </summary>
    private static readonly Regex EscapeSequencePattern = new(
        "\u001b\\][^\u0007\u001b]*(?:\u0007|\u001b\\\\)"  // OSC ... BEL | ST
            + "|[\u001b\u009b]\\[[0-?]*[ -/]*[@-~]"             // CSI ... final byte
            + "|\u001b[@-Z\\\\-_]",                               // single-char escapes
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static string Redact(string text, IReadOnlyList<string> secretValues)
    {
        string redacted = text;
        foreach (string secret in secretValues.Where(value => !string.IsNullOrEmpty(value)).OrderByDescending(value => value.Length))
        {
            redacted = redacted.Replace(secret, "***", StringComparison.Ordinal);
        }

        return redacted;
    }

    private static Regex? CreateLogPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        try
        {
            return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private string FormatMessage(string resourceKey, string fallback, params object?[] arguments) =>
        EnvironmentInspector.FormatMessage(_formatMessage, resourceKey, fallback, arguments);

    /// <summary>
    /// Records a Hub-authored line and then notifies whoever is listening.
    /// </summary>
    /// <remarks>
    /// The two steps are deliberately separate. Written as
    /// <c>LogAppended?.Invoke(this, Log.Append(...))</c> the null-conditional
    /// short-circuits its own argument, so with no subscriber attached the line was
    /// never written to the ring buffer either - and the lines that matter most
    /// here are the ones emitted during recovery, before the UI has hooked itself
    /// up. The buffer is the log; who is listening cannot decide what it contains.
    /// </remarks>
    private void AppendHub(string text)
    {
        LogLine line = Log.Append(LogStream.Hub, Redact(text, _secretValues));
        LogAppended?.Invoke(this, line);
    }

    private void TransitionTo(ServiceState next, string? reason)
    {
        ServiceState previous;
        lock (_gate)
        {
            if (_state == next)
            {
                return;
            }

            previous = _state;
            _state = next;
        }

        StateChanged?.Invoke(this, new ServiceStateChangedEventArgs(previous, next, reason));
    }

    public void Dispose()
    {
        CancellationTokenSource? startup;
        CancellationTokenSource? restart;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            startup = _activeOperationCts;
            restart = _restartCts;
        }

        // Cancellation must finish the in-flight start before its process handles
        // are released; otherwise the start cannot reap an unready service.
        foreach (CancellationTokenSource? pending in new[] { startup, restart })
        {
            try { pending?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        _ = ReleaseDisposedResourcesAsync();
    }

    private async Task ReleaseDisposedResourcesAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Established services survive closing Settings and remain recoverable.
            CleanUpProcess(deleteOwnership: false);
            _httpClient.Dispose();
        }
        finally
        {
            _operationGate.Release();
        }
    }
}
