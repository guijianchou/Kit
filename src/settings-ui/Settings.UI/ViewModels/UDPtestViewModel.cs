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
    public sealed class UDPtestViewModel : Observable, IDisposable
    {
        private static readonly Uri NetworkTraceUri = new("https://www.cloudflare.com/cdn-cgi/trace");
        private static readonly TimeSpan NetworkIdentityRefreshInterval = TimeSpan.FromSeconds(15);

        private readonly SettingsUtils _settingsUtils;
        private readonly TargetSettingsStore _targetStore = new();
        private readonly ProbeCoordinator _coordinator = new();
        private readonly HttpClient _identityClient = CreateIdentityClient();
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly DispatcherTimer _sessionTimer;
        private readonly CancellationTokenSource _lifecycleCancellation = new();
        private readonly List<(string LineId, DateTimeOffset Timestamp, double Rtt, bool IsSuccess)> _chartSamples = [];

        private UDPtestSettings _settings;
        private GpoRuleConfigured _gpoConfiguration;
        private bool _isEnabled;
        private long _sessionStartedTimestamp;

        private static bool IsChinese => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        private MonitorRunState _state = MonitorRunState.Stopped;
        private string _runStateText = IsChinese ? "已停止" : "Stopped";
        private Brush _runStateBrush;
        private string _sessionDurationText = "00:00:00";
        private bool _canStart = true;
        private bool _canStop;
        private bool _canClear;
        private int _refreshInterval = 1000;

        private UDPtestLineRowViewModel? _selectedLine;
        private string _healthStateText = "IDLE";
        private Brush _healthRingBrush;
        private string _healthConditionText = IsChinese ? "空闲" : "Idle";
        private string _qualityText = "--";
        private string _avgRttText = "--";
        private string _peakRttText = "--";
        private string _periodAvgText = "--";
        private string _healthDetailText = IsChinese ? "请选择线路" : "Select a line";
        private int _selectedPeriodSeconds = 60;

        private string _udpAssessmentText = IsChinese ? "未启动" : "Not started";
        private string _udpDetailText = IsChinese ? "4/4 已启用" : "4/4 enabled";
        private string _tcpAssessmentText = IsChinese ? "未启动" : "Not started";
        private string _tcpDetailText = IsChinese ? "2/2 已启用" : "2/2 enabled";
        private string _pathSummaryText = IsChinese ? "未启动" : "Not started";
        private string _nodeVerificationText = IsChinese ? "节点未验证" : "Node Unverified";
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

        public UDPtestViewModel(SettingsUtils? settingsUtils = null)
        {
            _settingsUtils = settingsUtils ?? SettingsUtils.Default;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread() ?? DispatcherQueue.GetForCurrentThread();

            _settings = _settingsUtils.GetSettingsOrDefault<UDPtestSettings>(UDPtestSettings.ModuleName);
            _gpoConfiguration = ModuleGpoHelper.GetModuleGpoConfiguration(ModuleType.UDPtest);
            _isEnabled = _settingsUtils.GetSettingsOrDefault<GeneralSettings>().Enabled.UDPtest;

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
                    _isEnabled = value;
                    var generalSettings = _settingsUtils.GetSettingsOrDefault<GeneralSettings>();
                    generalSettings.Enabled.UDPtest = value;
                    _settingsUtils.SaveSettings(generalSettings.ToJsonString());
                    OnPropertyChanged();

                    if (!value)
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
                        RunStateText = IsChinese ? "已停止" : "Stopped";
                        RunStateBrush = ThemeBrushHelper.SecondaryTextBrush;
                    }
                    else
                    {
                        CanStart = _state == MonitorRunState.Stopped;
                        CanStop = _state == MonitorRunState.Running;
                        CanClear = _state == MonitorRunState.Stopped;
                    }

                    var outgoing = new OutGoingGeneralSettings(generalSettings);
                    Kit.Settings.UI.Views.ShellPage.SendDefaultIPCMessage(outgoing.ToString());
                }
            }
        }

        public bool IsEnabledGpoConfigured => _gpoConfiguration is GpoRuleConfigured.Enabled or GpoRuleConfigured.Disabled;

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
                    ShowError("Please enable at least one probe line before starting.", InfoBarSeverity.Warning);
                    return;
                }

                CanStart = false;
                await _coordinator.StartAsync(definitions, RefreshInterval, _lifecycleCancellation.Token);
                _sessionStartedTimestamp = Stopwatch.GetTimestamp();
                _sessionTimer.Start();
                CanStop = true;
                CanClear = false;
            }
            catch (Exception ex)
            {
                ShowError($"Failed to start: {ex.Message}", InfoBarSeverity.Error);
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
                ShowError($"Failed to stop: {ex.Message}", InfoBarSeverity.Error);
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
                ShowError($"Failed to clear: {ex.Message}", InfoBarSeverity.Error);
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
                ShowError($"Initialization error: {ex.Message}", InfoBarSeverity.Error);
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
                ShowError("Target updated. Stop and restart monitoring to apply target endpoint changes.", InfoBarSeverity.Informational);
            }
        }

        private void OnRowDeleteRequested(UDPtestLineRowViewModel row)
        {
            if (UdpLines.Count <= 1)
            {
                ShowError("At least one UDP line must be kept.", InfoBarSeverity.Warning);
                return;
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
                    ShowError($"Failed to toggle line: {ex.Message}", InfoBarSeverity.Warning);
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
                ShowError($"Failed to save targets: {ex.Message}", InfoBarSeverity.Error);
            }
        }

        private void UpdateLineCounts()
        {
            int tcpEnabled = TcpLines.Count(l => l.IsEnabled);
            int udpEnabled = UdpLines.Count(l => l.IsEnabled);
            TcpDetailText = IsChinese ? $"{tcpEnabled}/{TcpLines.Count} 已启用" : $"{tcpEnabled}/{TcpLines.Count} enabled";
            UdpDetailText = IsChinese ? $"{udpEnabled}/{UdpLines.Count} 已启用" : $"{udpEnabled}/{UdpLines.Count} enabled";
            TcpLinksSummaryText = IsChinese ? $"{TcpLines.Count} 条链路" : $"{TcpLines.Count} LINKS";
            UdpLinksSummaryText = IsChinese ? $"{UdpLines.Count} 条链路" : $"{UdpLines.Count} LINKS";
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
                    MonitorRunState.Running => IsChinese ? "运行中" : "Running",
                    MonitorRunState.Starting => IsChinese ? "正在启动" : "Starting",
                    MonitorRunState.Stopping => IsChinese ? "正在停止" : "Stopping",
                    _ => IsChinese ? "已停止" : "Stopped",
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

        private void OnCoordinatorUdpAssessmentCommitted(UdpAssessment assessment)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (IsChinese)
                {
                    UdpAssessmentText = assessment.State switch
                    {
                        "Supported / Stable" => "支持且稳定",
                        "Supported / Unstable" => "支持但偶发丢包",
                        "Supported / Degraded" => "支持但严重劣化",
                        "Unsupported" => "不支持 UDP 出站",
                        "Assessing" => "评估中",
                        _ => assessment.State,
                    };
                    UdpDetailText = assessment.Detail;
                }
                else
                {
                    UdpAssessmentText = assessment.State;
                    UdpDetailText = assessment.Detail;
                }

                UpdateAssessmentPresentation();
            });
        }

        private void OnCoordinatorNatAssessmentCommitted(NatAssessment assessment)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                NatTypeText = IsChinese ? assessment.Type switch
                {
                    "Endpoint-Independent Mapping" => "完全圆锥型 (EIM)",
                    "Address-Dependent Mapping" => "受限圆锥型 (ADM)",
                    "Address and Port-Dependent Mapping" => "端口受限圆锥型 (APDM)",
                    "Symmetric" => "对称型 NAT",
                    "Open Internet" => "公网开放",
                    "Blocked" => "受阻断",
                    _ => assessment.Type,
                } : assessment.Type;
            });
        }

        private void OnCoordinatorError(Exception exception)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                ShowError($"Monitoring error: {exception.Message}", InfoBarSeverity.Error);
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
                HealthStateText = IsChinese ? "空闲" : "IDLE";
                HealthStateFontSize = 15;
                HealthRingBrush = ThemeBrushHelper.StrokeDefaultBrush;
                HealthConditionText = IsChinese ? "空闲" : "Idle";
                QualityText = "--";
                AvgRttText = "--";
                PeakRttText = "--";
                PeriodAvgText = "--";
                HealthDetailText = IsChinese ? "请选择线路" : "Select a line";
                return;
            }

            HealthDetailText = IsChinese
                ? $"{SelectedLine.Name} | {SelectedLine.Target}:{SelectedLine.Port} | {SelectedLine.Counts} 次探测"
                : $"{SelectedLine.Name} | {SelectedLine.Target}:{SelectedLine.Port} | {SelectedLine.Counts} probes";
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
                "Stopped" or "Disabled" => IsChinese ? "已停止" : "IDLE",
                "Warming" => IsChinese ? "预热中" : "WARMING",
                "Internal Error" => IsChinese ? "异常" : "ERROR",
                "OK" => IsChinese ? "正常" : "OK",
                "Degraded" => IsChinese ? "劣化" : "DEGRADED",
                "Offline" => IsChinese ? "离线" : "OFFLINE",
                _ => SelectedLine.Status,
            };
            HealthStateText = displayState;
            HealthStateFontSize = GetHealthStateFontSize(displayState);
            HealthRingBrush = SelectedLine.StatusBrush;
            HealthConditionText = SelectedLine.Status switch
            {
                "Stopped" or "Disabled" => IsChinese ? "空闲" : "Idle",
                "Warming" => IsChinese ? "预热中" : "Warming",
                "Internal Error" => IsChinese ? "异常" : "Error",
                "OK" => IsChinese ? "正常" : "Normal",
                "Degraded" => IsChinese ? "劣化" : "Degraded",
                "Offline" => IsChinese ? "离线" : "Offline",
                _ => displayState,
            };
        }

        private void UpdateAssessmentPresentation()
        {
            var enabledTcp = TcpLines.Where(l => l.IsEnabled).ToList();
            TcpDetailText = IsChinese ? $"{enabledTcp.Count}/{TcpLines.Count} 已启用" : $"{enabledTcp.Count}/{TcpLines.Count} enabled";

            var enabledUdp = UdpLines.Where(l => l.IsEnabled).ToList();
            UdpDetailText = IsChinese ? $"{enabledUdp.Count}/{UdpLines.Count} 已启用" : $"{enabledUdp.Count}/{UdpLines.Count} enabled";

            if (_coordinator.State != MonitorRunState.Running)
            {
                if (UdpAssessmentText is "Not started" or "未启动")
                {
                    TcpAssessmentText = IsChinese ? "未启动" : "Not started";
                    PathSummaryText = IsChinese ? "未启动" : "Not started";
                }

                return;
            }

            string tcpAssessment;
            if (enabledTcp.Count == 0)
            {
                tcpAssessment = IsChinese ? "未监控" : "Not Monitored";
            }
            else if (enabledTcp.All(l => l.Status == "OK"))
            {
                tcpAssessment = enabledTcp.Count > 1
                    ? (IsChinese ? "可用且稳定" : "Available / Stable")
                    : (IsChinese ? "单目标稳定" : "Available / Single-Target Stable");
            }
            else if (enabledTcp.Any(l => l.Status is "OK" or "DEGRADED"))
            {
                tcpAssessment = IsChinese ? "部分不稳定" : "Available / Partial or Unstable";
            }
            else if (enabledTcp.All(l => l.Status == "DOWN"))
            {
                tcpAssessment = IsChinese ? "不可用" : "Unavailable";
            }
            else if (enabledTcp.Any(l => l.Status == "Internal Error"))
            {
                tcpAssessment = IsChinese ? "内部错误" : "Unknown / Internal Error";
            }
            else
            {
                tcpAssessment = IsChinese ? "预热中" : "Warming";
            }

            TcpAssessmentText = tcpAssessment;
            bool tcpAvailable = tcpAssessment.StartsWith("Available", StringComparison.Ordinal) || tcpAssessment.Contains("可用") || tcpAssessment.Contains("稳定");
            string udpAssessment = UdpAssessmentText;
            bool udpAvailable = udpAssessment.StartsWith("Supported", StringComparison.Ordinal) || udpAssessment.Contains("支持") || udpAssessment.Contains("稳定");
            PathSummaryText = (tcpAvailable, udpAvailable) switch
            {
                (true, true) when (tcpAssessment == "Available / Stable" || tcpAssessment == "可用且稳定")
                    && (udpAssessment == "Supported / Stable" || udpAssessment == "支持且稳定") => IsChinese ? "TCP 与 UDP 稳定" : "TCP + UDP Stable",
                (true, true) => IsChinese ? "TCP 与 UDP 可用" : "TCP + UDP Available",
                (true, false) when udpAssessment.Contains("Unsupported") || udpAssessment.Contains("不支持") => IsChinese ? "TCP 可用，UDP 不受支持" : "TCP Available / UDP Unsupported",
                (true, false) => IsChinese ? "TCP 可用，UDP 评估中" : "TCP Available / UDP Assessing",
                (false, true) when tcpAssessment.Contains("Unavailable") || tcpAssessment.Contains("不可用") => IsChinese ? "UDP 可用，TCP 不可用" : "UDP Available / TCP Unavailable",
                _ when tcpAssessment.Contains("Unknown") || tcpAssessment.Contains("内部错误")
                    || udpAssessment.Contains("Unknown") => IsChinese ? "链路未知" : "Path Unknown",
                _ => IsChinese ? "评估中" : "Assessing",
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
                ? (IsChinese ? "节点已验证" : "Node Verified")
                : (IsChinese ? "节点未验证" : "Node Unverified");
            NodeVerificationIcon = verified ? "\uE73E" : "\uE946";
            NodeVerificationBrush = verified
                ? ThemeBrushHelper.SuccessBrush
                : ThemeBrushHelper.SecondaryTextBrush;
        }

        private async Task PollNetworkIdentityAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!IsEnabled)
                {
                    try
                    {
                        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
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

        public void Dispose()
        {
            _lifecycleCancellation.Cancel();
            _sessionTimer.Stop();
            _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _identityClient.Dispose();
            _lifecycleCancellation.Dispose();
        }
    }
}
