using System.Diagnostics;
using System.Threading.Channels;
using UDPtestLib.Core;
using UDPtestLib.Logging;
using UDPtestLib.Probes;

namespace UDPtestLib.Engine
{
    public sealed class ProbeCoordinator : IAsyncDisposable
    {
        private const int ResultQueueCapacity = 64;
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan NatDetectionInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan GenerationBarrierTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ProbeFailureBackoff = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan NatAssessmentStaleAfter = TimeSpan.FromSeconds(30);

        private readonly MetricsEngine _metrics = new();
        private readonly object _metricsGate = new();
        private readonly object _networkGenerationGate = new();
        private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

        private CancellationTokenSource? _runCancellation;
        private Channel<WriterCommand>? _resultChannel;
        private SemaphoreSlim? _resultSlots;
        private Dictionary<string, LineRunControl> _lineControls = new(StringComparer.Ordinal);
        private Task[] _workers = [];
        private Task? _writer;
        private Task? _natWorker;
        private long _sessionStartedTimestamp;
        private long _networkGeneration;
        private CancellationTokenSource? _networkGenerationCancellation;
        private readonly List<CancellationTokenSource> _retiredNetworkGenerationSources = [];
        private long _pendingNetworkGeneration = -1;
        private HashSet<string>? _pendingNetworkGenerationLines;
        private TaskCompletionSource? _networkGenerationCompletion;
        private TaskCompletionSource? _networkGenerationReady;
        private IReadOnlyDictionary<string, ProbeLineDefinition> _runningLines =
            new Dictionary<string, ProbeLineDefinition>(StringComparer.Ordinal);

        public event Action<MonitorRunState>? StateChanged;
        public event Action<LineMetricSnapshot>? SnapshotCommitted;
        public event Action<UdpAssessment>? UdpAssessmentCommitted;
        public event Action<NatAssessment>? NatAssessmentCommitted;
        public event Action<Exception>? ErrorOccurred;

        public MonitorRunState State { get; private set; } = MonitorRunState.Stopped;

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            UDPtestLogSink.Info("ProbeCoordinator initialized.");
            return Task.CompletedTask;
        }

        public async Task RecreateProbeConnectionsAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (State != MonitorRunState.Running || _runningLines.Count == 0)
                {
                    return;
                }

                UDPtestLogSink.Info("Public IP change detected: triggering Generation Barrier to recreate probe connections.");
                CancellationTokenSource previousSource;
                Task resetCompleted;
                TaskCompletionSource ready;
                lock (_networkGenerationGate)
                {
                    previousSource = _networkGenerationCancellation
                        ?? throw new InvalidOperationException("The network generation is not initialized.");
                    _retiredNetworkGenerationSources.Add(previousSource);
                    _networkGenerationCancellation = new CancellationTokenSource();
                    _networkGeneration++;
                    _pendingNetworkGeneration = _networkGeneration;
                    _pendingNetworkGenerationLines = _runningLines.Keys.ToHashSet(StringComparer.Ordinal);
                    _networkGenerationCompletion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    resetCompleted = _networkGenerationCompletion.Task;
                    ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _networkGenerationReady = ready;
                }

                try
                {
                    previousSource.Cancel();
                    await resetCompleted.WaitAsync(GenerationBarrierTimeout, cancellationToken).ConfigureAwait(false);
                    await DrainWriterAsync(cancellationToken).ConfigureAwait(false);

                    IReadOnlyList<LineMetricSnapshot> snapshots;
                    UdpAssessment udpAssessment;
                    lock (_metricsGate)
                    {
                        snapshots = _metrics.ResetWindows();
                        udpAssessment = _metrics.AssessUdp();
                    }

                    foreach (LineMetricSnapshot snapshot in snapshots)
                    {
                        SnapshotCommitted?.Invoke(snapshot);
                    }

                    UdpAssessmentCommitted?.Invoke(udpAssessment);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _runCancellation?.Cancel();
                    throw;
                }
                catch (Exception exception)
                {
                    _runCancellation?.Cancel();
                    ErrorOccurred?.Invoke(exception);
                    throw;
                }
                finally
                {
                    ready.TrySetResult();
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private async Task DrainWriterAsync(CancellationToken cancellationToken)
        {
            ChannelWriter<WriterCommand> writer = _resultChannel?.Writer
                ?? throw new InvalidOperationException("The result writer is not active.");
            TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!writer.TryWrite(new WriterCommand(null, drained)))
            {
                throw new InvalidOperationException("The result writer is not accepting commands.");
            }

            await drained.Task.WaitAsync(GenerationBarrierTimeout, cancellationToken).ConfigureAwait(false);
        }

        public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureStopped("clear history");
                lock (_metricsGate)
                {
                    _metrics.Reset([], 1000);
                }

                UDPtestLogSink.Info("Probe history cleared.");
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public async Task SetLineEnabledAsync(
            string lineId,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (State != MonitorRunState.Running)
                {
                    throw new InvalidOperationException($"Cannot change a probe line while the coordinator is {State}.");
                }

                if (!_lineControls.TryGetValue(lineId, out LineRunControl? control))
                {
                    throw new InvalidOperationException($"Unknown probe line '{lineId}'.");
                }

                if (!isEnabled)
                {
                    control.SetEnabled(false);
                }

                LineMetricSnapshot snapshot;
                UdpAssessment udpAssessment;
                lock (_metricsGate)
                {
                    snapshot = _metrics.SetLineEnabled(lineId, isEnabled);
                    udpAssessment = _metrics.AssessUdp();
                }

                SnapshotCommitted?.Invoke(snapshot);
                UdpAssessmentCommitted?.Invoke(udpAssessment);

                if (isEnabled)
                {
                    control.SetEnabled(true);
                }

                UDPtestLogSink.Info($"Line '{lineId}' enabled changed to {isEnabled}.");
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public async Task StartAsync(
            IReadOnlyList<ProbeLineDefinition> lines,
            int refreshIntervalMilliseconds,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(lines);
            if (refreshIntervalMilliseconds is not (500 or 1000 or 2000 or 5000))
            {
                throw new ArgumentOutOfRangeException(nameof(refreshIntervalMilliseconds));
            }

            if (!lines.Any(static line => line.IsEnabled))
            {
                throw new InvalidOperationException("Enable at least one probe line before starting.");
            }

            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (State != MonitorRunState.Stopped)
                {
                    throw new InvalidOperationException($"Cannot start while the coordinator is {State}.");
                }

                SetState(MonitorRunState.Starting);
                lock (_metricsGate)
                {
                    _metrics.Reset(lines, refreshIntervalMilliseconds);
                }

                _runCancellation = new CancellationTokenSource();
                _resultChannel = Channel.CreateUnbounded<WriterCommand>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                });
                _resultSlots = new SemaphoreSlim(ResultQueueCapacity, ResultQueueCapacity);
                _sessionStartedTimestamp = Stopwatch.GetTimestamp();
                InitializeNetworkGeneration();
                _lineControls = lines.ToDictionary(
                    static line => line.Id,
                    static line => new LineRunControl(line.IsEnabled),
                    StringComparer.Ordinal);
                _runningLines = lines.ToDictionary(
                    static line => line.Id,
                    StringComparer.Ordinal);

                TaskCompletionSource startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _writer = WriterLoopAsync(_resultChannel.Reader);
                _workers = lines
                    .Select(line => WorkerLoopAsync(
                        line,
                        _lineControls[line.Id],
                        refreshIntervalMilliseconds,
                        startGate.Task,
                        _resultChannel.Writer,
                        _resultSlots,
                        _runCancellation.Token))
                    .ToArray();
                SetState(MonitorRunState.Running);
                startGate.SetResult();
                NatAssessmentCommitted?.Invoke(CreatePendingNatAssessment());
                _natWorker = NatDetectionLoopAsync(_runCancellation.Token);

                UDPtestLogSink.Info($"Coordinator started with {lines.Count} lines at {refreshIntervalMilliseconds}ms interval.");
            }
            catch
            {
                _runCancellation?.Cancel();
                try
                {
                    await Task.WhenAll(_workers).ConfigureAwait(false);
                }
                catch
                {
                }

                _resultChannel?.Writer.TryComplete();
                if (_writer is not null)
                {
                    try
                    {
                        await _writer.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                if (_natWorker is not null)
                {
                    try
                    {
                        await _natWorker.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                ClearSessionResources();
                SetState(MonitorRunState.Stopped);
                throw;
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (State == MonitorRunState.Stopped)
                {
                    return;
                }

                SetState(MonitorRunState.Stopping);
                _runCancellation?.Cancel();
                try
                {
                    await Task.WhenAll(_workers).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                }

                _resultChannel?.Writer.TryComplete();
                if (_writer is not null)
                {
                    try
                    {
                        await _writer.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                if (_natWorker is not null)
                {
                    try
                    {
                        await _natWorker.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                ClearSessionResources();
                SetState(MonitorRunState.Stopped);
                NatAssessmentCommitted?.Invoke(CreateStoppedNatAssessment());
                UDPtestLogSink.Info("Coordinator stopped.");
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (State != MonitorRunState.Stopped)
            {
                await StopAsync().ConfigureAwait(false);
            }

            _lifecycleGate.Dispose();
        }

        private async Task WorkerLoopAsync(
            ProbeLineDefinition line,
            LineRunControl runControl,
            int refreshIntervalMilliseconds,
            Task startGate,
            ChannelWriter<WriterCommand> writer,
            SemaphoreSlim slots,
            CancellationToken cancellationToken)
        {
            IProbe? probe = null;
            long probeGeneration = -1;

            try
            {
                await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                long sequenceNumber = 0;
                while (!cancellationToken.IsCancellationRequested)
                {
                    NetworkGenerationSnapshot generation = CaptureNetworkGeneration();
                    using CancellationTokenSource iterationSource =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken,
                            generation.CancellationToken);
                    CancellationToken iterationToken = iterationSource.Token;
                    bool slotReserved = false;
                    try
                    {
                        if (probeGeneration != generation.Generation)
                        {
                            probe = await DisposeQuietlyAsync(probe).ConfigureAwait(false);
                            probeGeneration = generation.Generation;
                            AcknowledgeNetworkGeneration(line.Id, probeGeneration);
                            await generation.Ready.WaitAsync(iterationToken).ConfigureAwait(false);
                            probe = ProbeFactory.Create(line);
                        }

                        await runControl.WaitUntilEnabledAsync(iterationToken).ConfigureAwait(false);
                        long roundStarted = Stopwatch.GetTimestamp();
                        await slots.WaitAsync(iterationToken).ConfigureAwait(false);
                        slotReserved = true;
                        if (!runControl.IsEnabled)
                        {
                            slots.Release();
                            slotReserved = false;
                            continue;
                        }

                        IProbe activeProbe = probe
                            ?? throw new InvalidOperationException("The probe was not initialized.");
                        ProbeResult result = await activeProbe.ExecuteAsync(
                            ++sequenceNumber,
                            DateTimeOffset.UtcNow,
                            GetSessionElapsedMilliseconds(),
                            ProbeTimeout,
                            iterationToken).ConfigureAwait(false);
                        if (!IsCurrentNetworkGeneration(probeGeneration))
                        {
                            slots.Release();
                            slotReserved = false;
                            continue;
                        }

                        await writer.WriteAsync(new WriterCommand(result, null), CancellationToken.None)
                            .ConfigureAwait(false);
                        slotReserved = false;

                        TimeSpan elapsed = Stopwatch.GetElapsedTime(roundStarted);
                        TimeSpan remaining = TimeSpan.FromMilliseconds(refreshIntervalMilliseconds) - elapsed;
                        if (remaining > TimeSpan.Zero)
                        {
                            await Task.Delay(remaining, iterationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        if (slotReserved)
                        {
                            slots.Release();
                        }

                        break;
                    }
                    catch (OperationCanceledException) when (generation.CancellationToken.IsCancellationRequested)
                    {
                        if (slotReserved)
                        {
                            slots.Release();
                        }

                        continue;
                    }
                    catch (Exception exception)
                    {
                        probe = await DisposeQuietlyAsync(probe).ConfigureAwait(false);
                        probeGeneration = -1;
                        await ReportInternalErrorAsync(
                                line,
                                ++sequenceNumber,
                                exception,
                                writer,
                                slots,
                                slotReserved,
                                cancellationToken)
                            .ConfigureAwait(false);

                        try
                        {
                            await Task.Delay(ProbeFailureBackoff, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                await DisposeQuietlyAsync(probe).ConfigureAwait(false);
            }
        }

        private static async ValueTask<IProbe?> DisposeQuietlyAsync(IProbe? probe)
        {
            if (probe is null)
            {
                return null;
            }

            try
            {
                await probe.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            return null;
        }

        private async Task ReportInternalErrorAsync(
            ProbeLineDefinition line,
            long sequenceNumber,
            Exception exception,
            ChannelWriter<WriterCommand> writer,
            SemaphoreSlim slots,
            bool slotReserved,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!slotReserved)
                {
                    await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            ProbeResult result = new(
                line.Id,
                line.Protocol,
                sequenceNumber,
                DateTimeOffset.UtcNow,
                GetSessionElapsedMilliseconds(),
                0,
                ProbeOutcome.InternalError,
                exception.GetType().Name,
                exception.HResult);
            try
            {
                await writer.WriteAsync(new WriterCommand(result, null), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                slots.Release();
            }
        }

        private async Task WriterLoopAsync(ChannelReader<WriterCommand> reader)
        {
            try
            {
                await foreach (WriterCommand command in reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    if (command.Barrier is TaskCompletionSource barrier)
                    {
                        barrier.TrySetResult();
                        continue;
                    }

                    ProbeResult result = command.Result
                        ?? throw new InvalidOperationException("The writer command contains no result.");
                    LineMetricSnapshot snapshot;
                    UdpAssessment udpAssessment;
                    lock (_metricsGate)
                    {
                        snapshot = _metrics.Apply(result);
                        udpAssessment = _metrics.AssessUdp();
                    }

                    _resultSlots?.Release();
                    SnapshotCommitted?.Invoke(snapshot);
                    UdpAssessmentCommitted?.Invoke(udpAssessment);
                }
            }
            catch (Exception exception)
            {
                _runCancellation?.Cancel();
                ErrorOccurred?.Invoke(exception);
                throw;
            }
        }

        private async Task NatDetectionLoopAsync(CancellationToken cancellationToken)
        {
            string? lastType = null;
            DateTimeOffset? lastSuccessfulAtUtc = null;
            try
            {
                NatTypeProbe probe = new();
                TimeSpan nextDelay = TimeSpan.Zero;
                long observedGeneration = -1;
                while (true)
                {
                    NetworkGenerationSnapshot generation = CaptureNetworkGeneration();
                    if (generation.Generation != observedGeneration)
                    {
                        observedGeneration = generation.Generation;
                        lastType = null;
                    }

                    using CancellationTokenSource delaySource =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken,
                            generation.CancellationToken);
                    try
                    {
                        await Task.Delay(nextDelay, delaySource.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        !cancellationToken.IsCancellationRequested
                        && generation.CancellationToken.IsCancellationRequested)
                    {
                        nextDelay = TimeSpan.Zero;
                        lastType = null;
                        continue;
                    }

                    try
                    {
                        generation = CaptureNetworkGeneration();
                        if (generation.Generation != observedGeneration)
                        {
                            observedGeneration = generation.Generation;
                            lastType = null;
                        }

                        using CancellationTokenSource evaluationSource =
                            CancellationTokenSource.CreateLinkedTokenSource(
                                cancellationToken,
                                generation.CancellationToken);
                        await generation.Ready.WaitAsync(evaluationSource.Token).ConfigureAwait(false);
                        NatAssessment assessment = await probe.EvaluateAsync(
                            TimeSpan.FromSeconds(1),
                            evaluationSource.Token).ConfigureAwait(false);
                        if (IsCurrentNetworkGeneration(generation.Generation))
                        {
                            if (assessment.Type != "N/A")
                            {
                                NatAssessmentCommitted?.Invoke(assessment);
                                lastType = assessment.Type;
                                lastSuccessfulAtUtc = assessment.EvaluatedAtUtc;
                            }
                            else if (lastType is null
                                || DateTimeOffset.UtcNow - lastSuccessfulAtUtc >= NatAssessmentStaleAfter)
                            {
                                NatAssessmentCommitted?.Invoke(assessment);
                                lastType = null;
                                lastSuccessfulAtUtc = null;
                            }
                        }

                        nextDelay = NatDetectionInterval;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException) when (generation.CancellationToken.IsCancellationRequested)
                    {
                        lastType = null;
                        nextDelay = TimeSpan.Zero;
                    }
                    catch (Exception exception)
                    {
                        if (lastType is null
                            || DateTimeOffset.UtcNow - lastSuccessfulAtUtc >= NatAssessmentStaleAfter)
                        {
                            NatAssessmentCommitted?.Invoke(
                                new NatAssessment(
                                    "N/A",
                                    $"NAT diagnostic failed: {exception.GetType().Name}",
                                    DateTimeOffset.UtcNow));
                            lastType = null;
                            lastSuccessfulAtUtc = null;
                        }

                        nextDelay = NatDetectionInterval;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (lastType is null)
                {
                    NatAssessmentCommitted?.Invoke(
                        new NatAssessment(
                            "N/A",
                            $"NAT diagnostic failed: {exception.GetType().Name}",
                            DateTimeOffset.UtcNow));
                }
            }
        }

        private static NatAssessment CreatePendingNatAssessment() =>
            new(
                "N/A",
                "NAT diagnostic is starting with three fixed STUN targets and Google fallback.",
                DateTimeOffset.UtcNow);

        private static NatAssessment CreateStoppedNatAssessment() =>
            new(
                "N/A",
                "NAT diagnostic is stopped.",
                DateTimeOffset.UtcNow);

        private void InitializeNetworkGeneration()
        {
            List<CancellationTokenSource> sourcesToDispose;
            lock (_networkGenerationGate)
            {
                sourcesToDispose = [.. _retiredNetworkGenerationSources];
                _retiredNetworkGenerationSources.Clear();
                if (_networkGenerationCancellation is not null)
                {
                    sourcesToDispose.Add(_networkGenerationCancellation);
                }

                _networkGenerationCancellation = new CancellationTokenSource();
                _networkGeneration = 0;
                _pendingNetworkGeneration = -1;
                _pendingNetworkGenerationLines = null;
                _networkGenerationCompletion = null;
                _networkGenerationReady = TaskCompletionSourceExtensions.Completed();
            }

            foreach (CancellationTokenSource source in sourcesToDispose)
            {
                source.Dispose();
            }
        }

        private NetworkGenerationSnapshot CaptureNetworkGeneration()
        {
            lock (_networkGenerationGate)
            {
                CancellationTokenSource source = _networkGenerationCancellation
                    ?? throw new InvalidOperationException("The network generation is not initialized.");
                return new NetworkGenerationSnapshot(
                    _networkGeneration,
                    source.Token,
                    _networkGenerationReady?.Task ?? Task.CompletedTask);
            }
        }

        private bool IsCurrentNetworkGeneration(long generation)
        {
            lock (_networkGenerationGate)
            {
                return generation == _networkGeneration;
            }
        }

        private void AcknowledgeNetworkGeneration(string lineId, long generation)
        {
            TaskCompletionSource? completed = null;
            lock (_networkGenerationGate)
            {
                if (generation != _pendingNetworkGeneration
                    || _pendingNetworkGenerationLines is null
                    || !_pendingNetworkGenerationLines.Remove(lineId))
                {
                    return;
                }

                if (_pendingNetworkGenerationLines.Count == 0)
                {
                    completed = _networkGenerationCompletion;
                    _pendingNetworkGeneration = -1;
                    _pendingNetworkGenerationLines = null;
                    _networkGenerationCompletion = null;
                }
            }

            completed?.TrySetResult();
        }

        private void ClearNetworkGeneration()
        {
            List<CancellationTokenSource> sourcesToDispose;
            TaskCompletionSource? pendingCompletion;
            TaskCompletionSource? pendingReady;
            lock (_networkGenerationGate)
            {
                sourcesToDispose = [.. _retiredNetworkGenerationSources];
                _retiredNetworkGenerationSources.Clear();
                if (_networkGenerationCancellation is not null)
                {
                    sourcesToDispose.Add(_networkGenerationCancellation);
                    _networkGenerationCancellation = null;
                }

                pendingCompletion = _networkGenerationCompletion;
                pendingReady = _networkGenerationReady;
                _pendingNetworkGeneration = -1;
                _pendingNetworkGenerationLines = null;
                _networkGenerationCompletion = null;
                _networkGenerationReady = null;
            }

            pendingCompletion?.TrySetCanceled();
            pendingReady?.TrySetResult();
            foreach (CancellationTokenSource source in sourcesToDispose)
            {
                source.Dispose();
            }
        }

        private long GetSessionElapsedMilliseconds() =>
            (long)Stopwatch.GetElapsedTime(_sessionStartedTimestamp).TotalMilliseconds;

        private void SetState(MonitorRunState state)
        {
            State = state;
            StateChanged?.Invoke(state);
        }

        private void EnsureStopped(string operation)
        {
            if (State != MonitorRunState.Stopped)
            {
                throw new InvalidOperationException($"Cannot {operation} while the coordinator is {State}.");
            }
        }

        private void ClearSessionResources()
        {
            _runCancellation?.Dispose();
            _runCancellation = null;
            _resultSlots?.Dispose();
            _resultSlots = null;
            _resultChannel = null;
            foreach (LineRunControl control in _lineControls.Values)
            {
                control.Dispose();
            }

            _lineControls = new Dictionary<string, LineRunControl>(StringComparer.Ordinal);
            _workers = [];
            _writer = null;
            _natWorker = null;
            _runningLines = new Dictionary<string, ProbeLineDefinition>(StringComparer.Ordinal);
            ClearNetworkGeneration();
        }

        private readonly record struct NetworkGenerationSnapshot(
            long Generation,
            CancellationToken CancellationToken,
            Task Ready);

        private readonly record struct WriterCommand(
            ProbeResult? Result,
            TaskCompletionSource? Barrier);

        private sealed class LineRunControl(bool isEnabled) : IDisposable
        {
            private readonly SemaphoreSlim _enabledSignal = new(0);
            private int _isEnabled = isEnabled ? 1 : 0;

            public bool IsEnabled => Volatile.Read(ref _isEnabled) != 0;

            public void SetEnabled(bool isEnabled)
            {
                int next = isEnabled ? 1 : 0;
                int previous = Interlocked.Exchange(ref _isEnabled, next);
                if (previous == 0 && next == 1)
                {
                    _enabledSignal.Release();
                }
            }

            public async Task WaitUntilEnabledAsync(CancellationToken cancellationToken)
            {
                while (!IsEnabled)
                {
                    await _enabledSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            public void Dispose() => _enabledSignal.Dispose();
        }
    }

    internal static class TaskCompletionSourceExtensions
    {
        public static TaskCompletionSource Completed()
        {
            TaskCompletionSource source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }
}
