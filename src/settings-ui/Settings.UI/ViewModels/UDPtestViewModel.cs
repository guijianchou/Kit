// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Common.UI;
using Kit.GPOWrapper;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.Library.Interfaces;
using ManagedCommon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UDPtestLib.Common;
using UDPtestLib.Core;
using UDPtestLib.Engine;
using UDPtestLib.Logging;
using UDPtestLib.Probes;
using UDPtestLib.Storage;

namespace Kit.Settings.UI.ViewModels
{
    public sealed partial class UDPtestViewModel : PageViewModelBase, IDisposable
    {
        protected override string ModuleName => UDPtestSettings.ModuleName;

        private static readonly Uri NetworkTraceUri = new("https://www.cloudflare.com/cdn-cgi/trace");
        private static readonly TimeSpan NetworkIdentityRefreshInterval = TimeSpan.FromSeconds(15);

        private readonly SettingsUtils _settingsUtils;
        private readonly ISettingsRepository<GeneralSettings> _generalSettingsRepository;
        private readonly ISettingsRepository<UDPtestSettings> _moduleSettingsRepository;
        private readonly Func<string, int> _sendConfigMsg;
        private readonly TargetSettingsStore _targetStore = new();
        private readonly ProbeCoordinator _coordinator = new();
        private readonly HttpClient _identityClient = CreateIdentityClient();
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly DispatcherTimer _sessionTimer;
        private readonly CancellationTokenSource _lifecycleCancellation = new();
        private readonly SemaphoreSlim _pollSignal = new(0, 1);
        private readonly List<(string LineId, DateTimeOffset Timestamp, double Rtt, bool IsSuccess)> _chartSamples = [];

        private UDPtestSettings _settings;
        private GpoRuleConfigured _gpoConfiguration;
        private bool _isEnabled;
        private volatile bool _isPageActive;
        private bool _disposed;
        private long _sessionStartedTimestamp;

        private MonitorRunState _state = MonitorRunState.Stopped;
        private string _runStateText = "UDPtest_RunStateStopped".GetLocalizedOrFallback("Stopped");
        private Brush _runStateBrush;
        private string _sessionDurationText = "00:00:00";
        private bool _canStart = true;
        private bool _canStop;
        private bool _canClear;
        private int _refreshInterval = 1000;

        private UDPtestLineRowViewModel? _selectedLine;
        private string _healthStateText = "UDPtest_HealthIdleState".GetLocalizedOrFallback("IDLE");
        private Brush _healthRingBrush;
        private string _healthConditionText = "UDPtest_HealthIdleCondition".GetLocalizedOrFallback("Idle");
        private string _qualityText = "--";
        private string _avgRttText = "--";
        private string _peakRttText = "--";
        private string _periodAvgText = "--";
        private string _healthDetailText = "UDPtest_HealthSelectLine".GetLocalizedOrFallback("Select a line");
        private int _selectedPeriodSeconds = 60;

        private string _udpAssessmentText = "UDPtest_AssessmentNotStarted".GetLocalizedOrFallback("Not started");
        private string _udpDetailText = "UDPtest_EnabledCountFormat".FormatLocalized("4/4 enabled", 4, 4);
        private string _tcpAssessmentText = "UDPtest_AssessmentNotStarted".GetLocalizedOrFallback("Not started");
        private string _tcpDetailText = "UDPtest_EnabledCountFormat".FormatLocalized("2/2 enabled", 2, 2);
        private string _pathSummaryText = "UDPtest_AssessmentNotStarted".GetLocalizedOrFallback("Not started");
        private string _nodeVerificationText = "UDPtest_NodeUnverified".GetLocalizedOrFallback("Node Unverified");
        private string _nodeVerificationIcon = "\uE946";
        private Brush _nodeVerificationBrush;

        private string _publicIpText = "--";
        private string _locationText = "--";
        private string _natTypeText = "N/A";
        private string _localRouteText = "--";

        private bool _isErrorOpen;
        private string _errorMessage = string.Empty;
        private InfoBarSeverity _messageSeverity = InfoBarSeverity.Informational;

        public event Action? HealthChartNeedsRedraw;

        public UDPtestViewModel(
            ISettingsRepository<GeneralSettings>? generalSettingsRepository = null,
            ISettingsRepository<UDPtestSettings>? moduleSettingsRepository = null,
            Func<string, int>? ipcMsgCallback = null)
        {
            _settingsUtils = SettingsUtils.Default;
            _generalSettingsRepository = generalSettingsRepository ?? SettingsRepository<GeneralSettings>.GetInstance(_settingsUtils);
            _moduleSettingsRepository = moduleSettingsRepository ?? SettingsRepository<UDPtestSettings>.GetInstance(_settingsUtils);
            _sendConfigMsg = ipcMsgCallback ?? (_ => 0);
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread() ?? DispatcherQueue.GetForCurrentThread();

            _settings = _moduleSettingsRepository.SettingsConfig ?? _settingsUtils.GetSettingsOrDefault<UDPtestSettings>(UDPtestSettings.ModuleName);
            int configuredInterval = _settings.Properties.ProbeIntervalMilliseconds.Value;
            if (configuredInterval is 500 or 1000 or 2000 or 5000)
            {
                _refreshInterval = configuredInterval;
            }

            _gpoConfiguration = ModuleGpoHelper.GetModuleGpoConfiguration(ModuleType.UDPtest);
            _isEnabled = _generalSettingsRepository.SettingsConfig.Enabled.UDPtest;

            _runStateBrush = ThemeBrushHelper.SecondaryTextBrush;
            _healthRingBrush = ThemeBrushHelper.StrokeDefaultBrush;
            _nodeVerificationBrush = ThemeBrushHelper.SecondaryTextBrush;

            _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _sessionTimer.Tick += OnSessionTimerTick;

            _coordinator.StateChanged += OnCoordinatorStateChanged;
            _coordinator.SnapshotCommitted += OnCoordinatorSnapshotCommitted;
            _coordinator.UdpAssessmentCommitted += OnCoordinatorUdpAssessmentCommitted;
            _coordinator.NatAssessmentCommitted += OnCoordinatorNatAssessmentCommitted;
            _coordinator.ErrorOccurred += OnCoordinatorError;
            _generalSettingsRepository.SettingsChanged += OnGeneralSettingsChanged;

            _ = InitializeAsync();
        }

        public bool IsEnabled
        {
            get
            {
                if (_gpoConfiguration == GpoRuleConfigured.Enabled)
                {
                    return true;
                }

                if (_gpoConfiguration == GpoRuleConfigured.Disabled)
                {
                    return false;
                }

                return _isEnabled;
            }
            set
            {
                if (IsEnabledGpoConfigured)
                {
                    return;
                }

                if (_isEnabled != value)
                {
                    var generalSettings = _generalSettingsRepository.SettingsConfig;
                    generalSettings.Enabled.UDPtest = value;
                    RefreshEnabledState();

                    var outgoing = new OutGoingGeneralSettings(generalSettings);
                    _sendConfigMsg(outgoing.ToString());
                }
            }
        }

        public bool IsEnabledGpoConfigured => _gpoConfiguration is GpoRuleConfigured.Enabled or GpoRuleConfigured.Disabled;

        private void OnGeneralSettingsChanged(GeneralSettings settings)
        {
            _dispatcherQueue.TryEnqueue(RefreshEnabledState);
        }

        public void RefreshEnabledState()
        {
            _isEnabled = _generalSettingsRepository.SettingsConfig.Enabled.UDPtest;
            OnPropertyChanged(nameof(IsEnabled));

            if (!IsEnabled)
            {
                if (_state != MonitorRunState.Stopped)
                {
                    _ = StopAsync();
                }
                else
                {
                    _sessionTimer.Stop();
                }

                CanStart = false;
                CanStop = false;
                CanClear = false;
                RunStateText = "UDPtest_RunStateStopped".GetLocalizedOrFallback("Stopped");
                RunStateBrush = ThemeBrushHelper.SecondaryTextBrush;
            }
            else
            {
                CanStart = _state == MonitorRunState.Stopped;
                CanStop = _state == MonitorRunState.Running;
                CanClear = _state == MonitorRunState.Stopped;
                if (_isPageActive)
                {
                    SignalPoll();
                }
            }
        }

        public void SetPageActive(bool active)
        {
            if (_disposed)
            {
                return;
            }

            _isPageActive = active;
            if (active && IsEnabled)
            {
                SignalPoll();
            }
        }

        private void SignalPoll()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (_pollSignal.CurrentCount == 0)
                {
                    _pollSignal.Release();
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SemaphoreFullException)
            {
            }
        }

        public ObservableCollection<UDPtestLineRowViewModel> Lines { get; } = [];
        public ObservableCollection<UDPtestLineRowViewModel> TcpLines { get; } = [];
        public ObservableCollection<UDPtestLineRowViewModel> UdpLines { get; } = [];

        private string _tcpLinksSummaryText = string.Empty;
        public string TcpLinksSummaryText
        {
            get => _tcpLinksSummaryText;
            private set => Set(ref _tcpLinksSummaryText, value);
        }

        private string _udpLinksSummaryText = string.Empty;
        public string UdpLinksSummaryText
        {
            get => _udpLinksSummaryText;
            private set => Set(ref _udpLinksSummaryText, value);
        }

        public string RunStateText
        {
            get => _runStateText;
            private set => Set(ref _runStateText, value);
        }

        public Brush RunStateBrush
        {
            get => _runStateBrush;
            private set => Set(ref _runStateBrush, value);
        }

        public string SessionDurationText
        {
            get => _sessionDurationText;
            private set => Set(ref _sessionDurationText, value);
        }

        public bool CanStart
        {
            get => _canStart;
            private set => Set(ref _canStart, value);
        }

        public bool CanStop
        {
            get => _canStop;
            private set => Set(ref _canStop, value);
        }

        public bool CanClear
        {
            get => _canClear;
            private set => Set(ref _canClear, value);
        }

        public int RefreshInterval
        {
            get => _refreshInterval;
            set
            {
                if (Set(ref _refreshInterval, value))
                {
                    _settings.Properties.ProbeIntervalMilliseconds.Value = value;
                    _settingsUtils.SaveSettings(_settings.ToJsonString(), UDPtestSettings.ModuleName);
                    _sendConfigMsg(new SndModuleSettings<SndUDPtestSettings>(new SndUDPtestSettings(_settings)).ToJsonString());
                    if (_coordinator.State == MonitorRunState.Running)
                    {
                        ShowError("UDPtest_TargetUpdatedRestartRequired".GetLocalizedOrFallback("Target updated. Stop and restart monitoring to apply target endpoint changes."), InfoBarSeverity.Informational);
                    }
                }
            }
        }

        public UDPtestLineRowViewModel? SelectedLine
        {
            get => _selectedLine;
            set
            {
                if (Set(ref _selectedLine, value))
                {
                    UpdateHealthCard();
                    HealthChartNeedsRedraw?.Invoke();
                }
            }
        }

        public string HealthStateText
        {
            get => _healthStateText;
            private set => Set(ref _healthStateText, value);
        }

        private double _healthStateFontSize = 15;
        public double HealthStateFontSize
        {
            get => _healthStateFontSize;
            private set => Set(ref _healthStateFontSize, value);
        }

        public Brush HealthRingBrush
        {
            get => _healthRingBrush;
            private set => Set(ref _healthRingBrush, value);
        }

        public string HealthConditionText
        {
            get => _healthConditionText;
            private set => Set(ref _healthConditionText, value);
        }

        public string QualityText
        {
            get => _qualityText;
            private set => Set(ref _qualityText, value);
        }

        public string AvgRttText
        {
            get => _avgRttText;
            private set => Set(ref _avgRttText, value);
        }

        public string PeakRttText
        {
            get => _peakRttText;
            private set => Set(ref _peakRttText, value);
        }

        public string PeriodAvgText
        {
            get => _periodAvgText;
            private set => Set(ref _periodAvgText, value);
        }

        public string HealthDetailText
        {
            get => _healthDetailText;
            private set => Set(ref _healthDetailText, value);
        }

        public int SelectedPeriodSeconds
        {
            get => _selectedPeriodSeconds;
            set
            {
                if (Set(ref _selectedPeriodSeconds, value))
                {
                    UpdateHealthCard();
                    HealthChartNeedsRedraw?.Invoke();
                }
            }
        }

        public string UdpAssessmentText
        {
            get => _udpAssessmentText;
            private set => Set(ref _udpAssessmentText, value);
        }

        public string UdpDetailText
        {
            get => _udpDetailText;
            private set => Set(ref _udpDetailText, value);
        }

        public string TcpAssessmentText
        {
            get => _tcpAssessmentText;
            private set => Set(ref _tcpAssessmentText, value);
        }

        public string TcpDetailText
        {
            get => _tcpDetailText;
            private set => Set(ref _tcpDetailText, value);
        }

        public string PathSummaryText
        {
            get => _pathSummaryText;
            private set => Set(ref _pathSummaryText, value);
        }

        public string NodeVerificationText
        {
            get => _nodeVerificationText;
            private set => Set(ref _nodeVerificationText, value);
        }

        public string NodeVerificationIcon
        {
            get => _nodeVerificationIcon;
            private set => Set(ref _nodeVerificationIcon, value);
        }

        public Brush NodeVerificationBrush
        {
            get => _nodeVerificationBrush;
            private set => Set(ref _nodeVerificationBrush, value);
        }

        public string PublicIpText
        {
            get => _publicIpText;
            private set => Set(ref _publicIpText, value);
        }

        public string LocationText
        {
            get => _locationText;
            private set => Set(ref _locationText, value);
        }

        public string NatTypeText
        {
            get => _natTypeText;
            private set => Set(ref _natTypeText, value);
        }

        public string LocalRouteText
        {
            get => _localRouteText;
            private set => Set(ref _localRouteText, value);
        }

        public bool IsErrorOpen
        {
            get => _isErrorOpen;
            set => Set(ref _isErrorOpen, value);
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set => Set(ref _errorMessage, value);
        }

        public InfoBarSeverity MessageSeverity
        {
            get => _messageSeverity;
            set => Set(ref _messageSeverity, value);
        }

        public IReadOnlyList<(DateTimeOffset Timestamp, double Rtt, bool IsSuccess)> GetCurrentChartPoints()
        {
            if (SelectedLine is null)
            {
                return Array.Empty<(DateTimeOffset, double, bool)>();
            }

            DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddSeconds(-SelectedPeriodSeconds);
            lock (_chartSamples)
            {
                return _chartSamples
                    .Where(s => s.LineId == SelectedLine.Id && s.Timestamp >= cutoff)
                    .Select(s => (s.Timestamp, s.Rtt, s.IsSuccess))
                    .ToList();
            }
        }

        public async Task StartAsync()
        {
            if (!IsEnabled)
            {
                return;
            }

            try
            {
                var definitions = Lines.Select(l => l.Definition).ToList();
                if (!definitions.Any(d => d.IsEnabled))
                {
                    ShowError("UDPtest_EnableProbeLineRequired".GetLocalizedOrFallback("Please enable at least one probe line before starting."), InfoBarSeverity.Warning);
                    return;
                }

                CanStart = false;
                await _coordinator.StartAsync(definitions, RefreshInterval, _lifecycleCancellation.Token);
                _sessionStartedTimestamp = Stopwatch.GetTimestamp();
                _sessionTimer.Start();
                CanStop = true;
                CanClear = false;
                SignalPoll();
            }
            catch (Exception ex)
            {
                ShowError("UDPtest_StartFailedFormat".FormatLocalized("Failed to start: {0}", ex.Message), InfoBarSeverity.Error);
                CanStart = true;
            }
        }

        public async Task StopAsync()
        {
            try
            {
                CanStop = false;
                _sessionTimer.Stop();
                await _coordinator.StopAsync(_lifecycleCancellation.Token);
                CanStart = IsEnabled;
                CanClear = IsEnabled;
            }
            catch (Exception ex)
            {
                ShowError("UDPtest_StopFailedFormat".FormatLocalized("Failed to stop: {0}", ex.Message), InfoBarSeverity.Error);
            }
        }

        public async Task ClearHistoryAsync()
        {
            try
            {
                await _coordinator.ClearHistoryAsync(_lifecycleCancellation.Token);
                lock (_chartSamples)
                {
                    _chartSamples.Clear();
                }

                foreach (var line in Lines)
                {
                    line.ResetMetrics();
                }

                SessionDurationText = "00:00:00";
                UpdateHealthCard();
                UpdateLineCounts();
                HealthChartNeedsRedraw?.Invoke();
            }
            catch (Exception ex)
            {
                ShowError("UDPtest_ClearFailedFormat".FormatLocalized("Failed to clear: {0}", ex.Message), InfoBarSeverity.Error);
            }
        }

        public void AddUdpLine()
        {
            int nextIndex = UdpLines.Count + 1;
            string id = $"udp_{Guid.NewGuid():N[..8]}";
            var def = new ProbeLineDefinition(
                id,
                $"UDP {nextIndex}",
                ProbeProtocol.Udp,
                ProbeKind.StunBinding,
                "stun.cloudflare.com",
                3478,
                IsEnabled: true,
                TargetLabel: $"Target {nextIndex}");

            var row = CreateLineRow(def);
            UdpLines.Add(row);
            Lines.Add(row);
            row.IsConfigExpanded = true;
            _ = SaveTargetsAsync();
            UpdateLineCounts();
        }

        private async Task InitializeAsync()
        {
            try
            {
                TargetSettings targets = await _targetStore.LoadAsync(_lifecycleCancellation.Token);
                PopulateLines(targets);

                LocalRouteText = CaptureLocalRoute();
                _ = PollNetworkIdentityAsync(_lifecycleCancellation.Token);

                SelectedLine = Lines.FirstOrDefault();
                UpdateLineCounts();
            }
            catch (Exception ex)
            {
                ShowError("UDPtest_InitFailedFormat".FormatLocalized("Initialization error: {0}", ex.Message), InfoBarSeverity.Error);
            }
        }

        private void PopulateLines(TargetSettings targets)
        {
            Lines.Clear();
            TcpLines.Clear();
            UdpLines.Clear();

            int tcpIndex = 1;
            foreach (var tcp in targets.TcpSettings)
            {
                _ = TcpTargetParser.TryParse(tcp.Endpoint, out _, out int port, out _);
                var def = new ProbeLineDefinition(
                    $"tcp_{tcpIndex}",
                    $"TCP {tcpIndex}",
                    ProbeProtocol.Tcp,
                    ProbeKind.Https204,
                    tcp.Endpoint,
                    port > 0 ? port : 443,
                    ExpectedHttpStatus: 204,
                    TargetLabel: tcp.Name);
                var row = CreateLineRow(def);
                TcpLines.Add(row);
                Lines.Add(row);
                tcpIndex++;
            }

            int udpIndex = 1;
            foreach (var udp in targets.UdpSettings)
            {
                _ = UdpTargetParser.TryParse(udp.Endpoint, udp.Kind, out string host, out int port, out _, out _);
                var def = new ProbeLineDefinition(
                    $"udp_{udpIndex}",
                    $"UDP {udpIndex}",
                    ProbeProtocol.Udp,
                    udp.Kind,
                    !string.IsNullOrEmpty(host) ? host : "stun.cloudflare.com",
                    port > 0 ? port : 3478,
                    TargetLabel: udp.Name);
                var row = CreateLineRow(def);
                UdpLines.Add(row);
                Lines.Add(row);
                udpIndex++;
            }

            UpdateLineCounts();
            UpdateAssessmentPresentation();
        }

        private UDPtestLineRowViewModel CreateLineRow(ProbeLineDefinition definition)
        {
            var row = new UDPtestLineRowViewModel(definition);
            row.SaveRequested += OnRowSaveRequested;
            row.DeleteRequested += OnRowDeleteRequested;
            row.ToggleRequested += OnRowToggleRequested;
            return row;
        }

        private void OnRowSaveRequested(UDPtestLineRowViewModel row)
        {
            _ = SaveTargetsAsync();
            UpdateLineCounts();
            UpdateAssessmentPresentation();
            if (_coordinator.State == MonitorRunState.Running)
            {
                ShowError("UDPtest_TargetUpdatedRestartRequired".GetLocalizedOrFallback("Target updated. Stop and restart monitoring to apply target endpoint changes."), InfoBarSeverity.Informational);
            }
        }

        private void OnRowDeleteRequested(UDPtestLineRowViewModel row)
        {
            if (UdpLines.Count <= 1)
            {
                ShowError("UDPtest_KeepOneLineRequired".GetLocalizedOrFallback("At least one UDP line must be kept."), InfoBarSeverity.Warning);
                return;
            }

            if (_coordinator.State == MonitorRunState.Running)
            {
                _ = _coordinator.SetLineEnabledAsync(row.Id, false, _lifecycleCancellation.Token);
            }

            UdpLines.Remove(row);
            Lines.Remove(row);
            if (SelectedLine == row)
            {
                SelectedLine = Lines.FirstOrDefault();
            }

            _ = SaveTargetsAsync();
            UpdateLineCounts();
            UpdateAssessmentPresentation();
        }

        private async void OnRowToggleRequested(UDPtestLineRowViewModel row, bool isEnabled)
        {
            if (_coordinator.State == MonitorRunState.Running)
            {
                try
                {
                    await _coordinator.SetLineEnabledAsync(row.Id, isEnabled, _lifecycleCancellation.Token);
                }
                catch (Exception ex)
                {
                    row.SetEnabledSilently(!isEnabled);
                    ShowError("UDPtest_ToggleLineFailedFormat".FormatLocalized("Failed to toggle line: {0}", ex.Message), InfoBarSeverity.Warning);
                }
            }

            UpdateLineCounts();
            UpdateNodeVerification();
            UpdateAssessmentPresentation();
        }

        private async Task SaveTargetsAsync()
        {
            try
            {
                TargetSettings settings = new()
                {
                    TcpSettings = TcpLines.Select(l => new TcpEndpointSetting(l.Name, l.Definition.Target, l.Definition.Kind)).ToList(),
                    UdpSettings = UdpLines.Select(l => new UdpEndpointSetting(l.Name, UdpTargetParser.Format(l.Definition.Target, l.Definition.Port), l.Definition.Kind)).ToList(),
                };
                await _targetStore.SaveAsync(settings, _lifecycleCancellation.Token);
            }
            catch (Exception ex)
            {
                ShowError("UDPtest_SaveTargetsFailedFormat".FormatLocalized("Failed to save targets: {0}", ex.Message), InfoBarSeverity.Error);
            }
        }

        private void UpdateLineCounts()
        {
            int tcpEnabled = TcpLines.Count(l => l.IsEnabled);
            int udpEnabled = UdpLines.Count(l => l.IsEnabled);
            TcpDetailText = "UDPtest_EnabledCountFormat".FormatLocalized("{0}/{1} enabled", tcpEnabled, TcpLines.Count);
            UdpDetailText = "UDPtest_EnabledCountFormat".FormatLocalized("{0}/{1} enabled", udpEnabled, UdpLines.Count);
            TcpLinksSummaryText = "UDPtest_LinksSummaryFormat".FormatLocalized("{0} LINKS", TcpLines.Count);
            UdpLinksSummaryText = "UDPtest_LinksSummaryFormat".FormatLocalized("{0} LINKS", UdpLines.Count);
        }

        private void OnSessionTimerTick(object? sender, object e)
        {
            long elapsed = (long)Stopwatch.GetElapsedTime(_sessionStartedTimestamp).TotalSeconds;
            TimeSpan span = TimeSpan.FromSeconds(elapsed);
            SessionDurationText = string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2}", (int)span.TotalHours, span.Minutes, span.Seconds);
        }

        private void OnCoordinatorStateChanged(MonitorRunState state)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                _state = state;
                RunStateText = state switch
                {
                    MonitorRunState.Running => "UDPtest_RunStateRunning".GetLocalizedOrFallback("Running"),
                    MonitorRunState.Starting => "UDPtest_RunStateStarting".GetLocalizedOrFallback("Starting"),
                    MonitorRunState.Stopping => "UDPtest_RunStateStopping".GetLocalizedOrFallback("Stopping"),
                    _ => "UDPtest_RunStateStopped".GetLocalizedOrFallback("Stopped"),
                };

                RunStateBrush = state switch
                {
                    MonitorRunState.Running => ThemeBrushHelper.SuccessBrush,
                    MonitorRunState.Starting or MonitorRunState.Stopping => ThemeBrushHelper.CautionBrush,
                    _ => ThemeBrushHelper.SecondaryTextBrush,
                };

                CanStart = IsEnabled && state == MonitorRunState.Stopped;
                CanStop = IsEnabled && state == MonitorRunState.Running;
                CanClear = IsEnabled && state == MonitorRunState.Stopped;

                foreach (var line in Lines)
                {
                    line.StatusVisibility = state == MonitorRunState.Running ? Visibility.Visible : Visibility.Collapsed;
                }

                UpdateNodeVerification();
                UpdateAssessmentPresentation();
            });
        }

        private void OnCoordinatorSnapshotCommitted(LineMetricSnapshot snapshot)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                var line = Lines.FirstOrDefault(l => l.Id == snapshot.LineId);
                if (line is not null)
                {
                    line.UpdateFromSnapshot(snapshot);
                    if (snapshot.LastOutcome is { } outcome && snapshot.LastStartedAtUtc is { } started && snapshot.LastDurationMilliseconds is { } duration)
                    {
                        line.AddResultBar(outcome, duration, snapshot.LastError);
                        lock (_chartSamples)
                        {
                            _chartSamples.Add((line.Id, started, duration, outcome == ProbeOutcome.Success));
                            DateTimeOffset prune = DateTimeOffset.UtcNow.AddHours(-1.5);
                            _chartSamples.RemoveAll(s => s.Timestamp < prune);
                        }
                    }

                    if (SelectedLine == line)
                    {
                        UpdateHealthCard();
                        HealthChartNeedsRedraw?.Invoke();
                    }
                }

                UpdateNodeVerification();
                UpdateAssessmentPresentation();
            });
        }

        private static string LocalizedUdpAssessment(string state) => state switch
        {
            "Supported / Stable" => "UDPtest_UdpAssessmentSupportedStable".GetLocalizedOrFallback(state),
            "Supported / Unstable" => "UDPtest_UdpAssessmentSupportedUnstable".GetLocalizedOrFallback(state),
            "Supported / Degraded" => "UDPtest_UdpAssessmentSupportedDegraded".GetLocalizedOrFallback(state),
            "Unsupported" => "UDPtest_UdpAssessmentUnsupported".GetLocalizedOrFallback(state),
            "Assessing" => "UDPtest_UdpAssessmentAssessing".GetLocalizedOrFallback(state),
            _ => state,
        };

        private void OnCoordinatorUdpAssessmentCommitted(UdpAssessment assessment)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                UdpAssessmentText = LocalizedUdpAssessment(assessment.State);
                UdpDetailText = assessment.Detail;
                UpdateAssessmentPresentation();
            });
        }

        private static string LocalizedNatType(string type) => type switch
        {
            "Endpoint-Independent Mapping" => "UDPtest_NatEim".GetLocalizedOrFallback(type),
            "Address-Dependent Mapping" => "UDPtest_NatAdm".GetLocalizedOrFallback(type),
            "Address and Port-Dependent Mapping" => "UDPtest_NatApdm".GetLocalizedOrFallback(type),
            "Symmetric" => "UDPtest_NatSymmetric".GetLocalizedOrFallback(type),
            "Open Internet" => "UDPtest_NatOpen".GetLocalizedOrFallback(type),
            "Blocked" => "UDPtest_NatBlocked".GetLocalizedOrFallback(type),
            _ => type,
        };

        private void OnCoordinatorNatAssessmentCommitted(NatAssessment assessment)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                NatTypeText = LocalizedNatType(assessment.Type);
            });
        }

        private void OnCoordinatorError(Exception exception)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                _sessionTimer.Stop();
                CanStart = IsEnabled && _coordinator.State == MonitorRunState.Stopped;
                CanStop = false;
                CanClear = IsEnabled && _coordinator.State == MonitorRunState.Stopped;
                ShowError("UDPtest_MonitoringErrorFormat".FormatLocalized("Monitoring error: {0}", exception.Message), InfoBarSeverity.Error);
            });
        }

        private static double GetHealthStateFontSize(string displayState) => displayState.Length switch
        {
            >= 8 => 10,
            >= 6 => 11,
            >= 5 => 12,
            _ => 15,
        };

        private void UpdateHealthCard()
        {
            if (SelectedLine is null)
            {
                HealthStateText = "UDPtest_HealthIdleState".GetLocalizedOrFallback("IDLE");
                HealthStateFontSize = 15;
                HealthRingBrush = ThemeBrushHelper.StrokeDefaultBrush;
                HealthConditionText = "UDPtest_HealthIdleCondition".GetLocalizedOrFallback("Idle");
                QualityText = "--";
                AvgRttText = "--";
                PeakRttText = "--";
                PeriodAvgText = "--";
                HealthDetailText = "UDPtest_HealthSelectLine".GetLocalizedOrFallback("Select a line");
                return;
            }

            HealthDetailText = "UDPtest_HealthDetailFormat".FormatLocalized(
                "{0} | {1}:{2} | {3} probes",
                SelectedLine.Name,
                SelectedLine.Target,
                SelectedLine.Port,
                SelectedLine.Counts);
            QualityText = SelectedLine.Quality;
            AvgRttText = SelectedLine.AverageRtt;

            DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddSeconds(-SelectedPeriodSeconds);
            List<(DateTimeOffset Timestamp, double Rtt, bool IsSuccess)> points;
            lock (_chartSamples)
            {
                points = _chartSamples
                    .Where(s => s.LineId == SelectedLine.Id && s.Timestamp >= cutoff && s.IsSuccess)
                    .Select(s => (s.Timestamp, s.Rtt, s.IsSuccess))
                    .ToList();
            }

            if (points.Count > 0)
            {
                double peak = points.Max(p => p.Rtt);
                double avg = points.Average(p => p.Rtt);
                PeakRttText = $"{Math.Round(peak, MidpointRounding.AwayFromZero):0} ms";
                PeriodAvgText = $"{Math.Round(avg, MidpointRounding.AwayFromZero):0} ms";
            }
            else
            {
                PeakRttText = "--";
                PeriodAvgText = "--";
            }

            string displayState = SelectedLine.Status switch
            {
                "Stopped" or "Disabled" or "Idle" => "UDPtest_StateIdle".GetLocalizedOrFallback("IDLE"),
                "Warming" => "UDPtest_StateWarming".GetLocalizedOrFallback("WARMING"),
                "Internal Error" => "UDPtest_StateError".GetLocalizedOrFallback("ERROR"),
                "OK" => "UDPtest_StateOk".GetLocalizedOrFallback("OK"),
                "DEGRADED" or "Degraded" => "UDPtest_StateDegraded".GetLocalizedOrFallback("DEGRADED"),
                "DOWN" or "Offline" => "UDPtest_StateOffline".GetLocalizedOrFallback("OFFLINE"),
                _ => SelectedLine.Status,
            };
            HealthStateText = displayState;
            HealthStateFontSize = GetHealthStateFontSize(displayState);
            HealthRingBrush = SelectedLine.StatusBrush;
            HealthConditionText = SelectedLine.Status switch
            {
                "Stopped" or "Disabled" or "Idle" => "UDPtest_ConditionIdle".GetLocalizedOrFallback("Idle"),
                "Warming" => "UDPtest_ConditionWarming".GetLocalizedOrFallback("Warming"),
                "Internal Error" => "UDPtest_ConditionError".GetLocalizedOrFallback("Error"),
                "OK" => "UDPtest_ConditionNormal".GetLocalizedOrFallback("Normal"),
                "DEGRADED" or "Degraded" => "UDPtest_ConditionDegraded".GetLocalizedOrFallback("Degraded"),
                "DOWN" or "Offline" => "UDPtest_ConditionOffline".GetLocalizedOrFallback("Offline"),
                _ => displayState,
            };
        }

        private void UpdateAssessmentPresentation()
        {
            var enabledTcp = TcpLines.Where(l => l.IsEnabled).ToList();
            TcpDetailText = "UDPtest_EnabledCountFormat".FormatLocalized("{0}/{1} enabled", enabledTcp.Count, TcpLines.Count);

            var enabledUdp = UdpLines.Where(l => l.IsEnabled).ToList();
            UdpDetailText = "UDPtest_EnabledCountFormat".FormatLocalized("{0}/{1} enabled", enabledUdp.Count, UdpLines.Count);

            if (_coordinator.State != MonitorRunState.Running)
            {
                if (UdpAssessmentText is "Not started" or "未启动")
                {
                    TcpAssessmentText = "UDPtest_AssessmentNotStarted".GetLocalizedOrFallback("Not started");
                    PathSummaryText = "UDPtest_AssessmentNotStarted".GetLocalizedOrFallback("Not started");
                }

                return;
            }

            string tcpAssessment;
            if (enabledTcp.Count == 0)
            {
                tcpAssessment = "UDPtest_TcpAssessmentNotMonitored".GetLocalizedOrFallback("Not Monitored");
            }
            else if (enabledTcp.All(l => l.Status == "OK"))
            {
                tcpAssessment = enabledTcp.Count > 1
                    ? "UDPtest_TcpAssessmentStable".GetLocalizedOrFallback("Available / Stable")
                    : "UDPtest_TcpAssessmentSingleStable".GetLocalizedOrFallback("Available / Single-Target Stable");
            }
            else if (enabledTcp.Any(l => l.Status is "OK" or "DEGRADED"))
            {
                tcpAssessment = "UDPtest_TcpAssessmentPartial".GetLocalizedOrFallback("Available / Partial or Unstable");
            }
            else if (enabledTcp.All(l => l.Status == "DOWN"))
            {
                tcpAssessment = "UDPtest_TcpAssessmentUnavailable".GetLocalizedOrFallback("Unavailable");
            }
            else if (enabledTcp.Any(l => l.Status == "Internal Error"))
            {
                tcpAssessment = "UDPtest_TcpAssessmentInternalError".GetLocalizedOrFallback("Unknown / Internal Error");
            }
            else
            {
                tcpAssessment = "UDPtest_TcpAssessmentWarming".GetLocalizedOrFallback("Warming");
            }

            TcpAssessmentText = tcpAssessment;
            bool tcpAvailable = tcpAssessment.StartsWith("Available", StringComparison.Ordinal) || tcpAssessment.Contains("可用") || tcpAssessment.Contains("稳定");
            string udpAssessment = UdpAssessmentText;
            bool udpAvailable = udpAssessment.StartsWith("Supported", StringComparison.Ordinal) || udpAssessment.Contains("支持") || udpAssessment.Contains("稳定");
            PathSummaryText = (tcpAvailable, udpAvailable) switch
            {
                (true, true) when (tcpAssessment == "Available / Stable" || tcpAssessment == "可用且稳定")
                    && (udpAssessment == "Supported / Stable" || udpAssessment == "支持且稳定") => "UDPtest_PathStable".GetLocalizedOrFallback("TCP + UDP Stable"),
                (true, true) => "UDPtest_PathAvailable".GetLocalizedOrFallback("TCP + UDP Available"),
                (true, false) when udpAssessment.Contains("Unsupported") || udpAssessment.Contains("不支持") => "UDPtest_PathUdpUnsupported".GetLocalizedOrFallback("TCP Available / UDP Unsupported"),
                (true, false) => "UDPtest_PathUdpAssessing".GetLocalizedOrFallback("TCP Available / UDP Assessing"),
                (false, true) when tcpAssessment.Contains("Unavailable") || tcpAssessment.Contains("不可用") => "UDPtest_PathTcpUnavailable".GetLocalizedOrFallback("UDP Available / TCP Unavailable"),
                _ when tcpAssessment.Contains("Unknown") || tcpAssessment.Contains("内部错误")
                    || udpAssessment.Contains("Unknown") => "UDPtest_PathUnknown".GetLocalizedOrFallback("Path Unknown"),
                _ => "UDPtest_PathAssessing".GetLocalizedOrFallback("Assessing"),
            };
        }

        private void UpdateNodeVerification()
        {
            bool verified = _coordinator.State == MonitorRunState.Running
                && NodeVerificationEvaluator.IsVerified(
                    PublicIpText,
                    LocationText,
                    UdpLines
                        .Where(l => l.Definition.Kind == ProbeKind.StunBinding)
                        .Select(l => new StunEgressEvidence(l.IsEnabled, l.IpAddress)));

            NodeVerificationText = verified
                ? "UDPtest_NodeVerified".GetLocalizedOrFallback("Node Verified")
                : "UDPtest_NodeUnverified".GetLocalizedOrFallback("Node Unverified");
            NodeVerificationIcon = verified ? "\uE73E" : "\uE946";
            NodeVerificationBrush = verified
                ? ThemeBrushHelper.SuccessBrush
                : ThemeBrushHelper.SecondaryTextBrush;
        }

        private async Task PollNetworkIdentityAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!IsEnabled || (!_isPageActive && _coordinator.State != MonitorRunState.Running))
                {
                    try
                    {
                        await _pollSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    continue;
                }

                try
                {
                    string route = CaptureLocalRoute();
                    if (route != LocalRouteText)
                    {
                        _dispatcherQueue.TryEnqueue(() => LocalRouteText = route);
                    }

                    Uri uri = new($"{NetworkTraceUri}?ts={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", UriKind.Absolute);
                    using HttpRequestMessage request = new(HttpMethod.Get, uri);
                    request.Headers.ConnectionClose = true;
                    request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store");

                    using HttpResponseMessage response = await _identityClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);

                    response.EnsureSuccessStatusCode();
                    string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    string nextIp = ParseTraceValue(payload, "ip") ?? PublicIpText;
                    string nextLocation = ParseTraceValue(payload, "loc") ?? LocationText;
                    bool publicIpChanged = PublicIpText != "--" && !string.Equals(nextIp, PublicIpText, StringComparison.OrdinalIgnoreCase);

                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        PublicIpText = nextIp;
                        LocationText = nextLocation;
                        UpdateNodeVerification();
                    });

                    if (publicIpChanged && _coordinator.State == MonitorRunState.Running)
                    {
                        await _coordinator.RecreateProbeConnectionsAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Transient trace failure; keep last valid values
                }

                try
                {
                    await Task.Delay(NetworkIdentityRefreshInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private static string? ParseTraceValue(string payload, string key)
        {
            foreach (string rawLine in payload.Split('\n'))
            {
                string line = rawLine.Trim();
                int separator = line.IndexOf('=');
                if (separator <= 0 || !line[..separator].Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = line[(separator + 1)..].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            return null;
        }

        private static string CaptureLocalRoute()
        {
            try
            {
                using Socket routeSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                routeSocket.Connect(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53));
                if (routeSocket.LocalEndPoint is not IPEndPoint localEndpoint)
                {
                    return "--";
                }

                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up
                        || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    IPInterfaceProperties properties = adapter.GetIPProperties();
                    bool ownsAddress = properties.UnicastAddresses.Any(
                        address => address.Address.Equals(localEndpoint.Address));
                    if (!ownsAddress)
                    {
                        continue;
                    }

                    IPAddress? gateway = properties.GatewayAddresses
                        .Select(static address => address.Address)
                        .FirstOrDefault(static address =>
                            address.AddressFamily == AddressFamily.InterNetwork
                            && !address.Equals(IPAddress.Any));
                    return gateway is null
                        ? $"{adapter.Name} · {localEndpoint.Address}"
                        : $"{adapter.Name} · {gateway}";
                }

                return localEndpoint.Address.ToString();
            }
            catch
            {
                return "--";
            }
        }

        private static HttpClient CreateIdentityClient()
        {
            SocketsHttpHandler handler = new()
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                Proxy = null,
            };

            HttpClient client = new(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(5),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Kit-UDPtest/1.0");
            return client;
        }

        private void ShowError(string message, InfoBarSeverity severity)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                ErrorMessage = message;
                MessageSeverity = severity;
                IsErrorOpen = true;
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _isPageActive = false;
                _generalSettingsRepository.SettingsChanged -= OnGeneralSettingsChanged;
                _lifecycleCancellation.Cancel();
                _sessionTimer.Stop();
                _ = _coordinator.DisposeAsync().AsTask();
                _identityClient.Dispose();
                _pollSignal.Dispose();
                _lifecycleCancellation.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
