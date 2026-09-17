// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kit.GPOWrapper;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Interfaces;
using LocalServerHub.Core.Commands;
using LocalServerHub.Core.Configuration;
using LocalServerHub.Core.Logging;
using LocalServerHub.Core.Models;
using LocalServerHub.Windows;
using LocalServerHub.Windows.Native;
using LocalserverLib.Common;
using ManagedCommon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Kit.Settings.UI.ViewModels
{
    public sealed class EnvironmentCheckChipModel
    {
        public string Label { get; }
        public Brush Brush { get; }
        public string Tooltip { get; }
        public EnvironmentCheckFix? Fix { get; }
        public bool HasCommand => !string.IsNullOrWhiteSpace(Fix?.Command);
        public bool CanReleasePort => Fix is { Port: not null, ProcessId: not null, ProcessStartTimeUtcFileTime: not null };
        public Visibility FixVisibility => HasCommand || CanReleasePort ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FixCommandVisibility => HasCommand ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ReleasePortVisibility => CanReleasePort ? Visibility.Visible : Visibility.Collapsed;

        public EnvironmentCheckChipModel(EnvironmentCheckItem item, Brush brush)
        {
            ArgumentNullException.ThrowIfNull(item);
            string title = item.Name switch
            {
                "Working directory" => "Localserver_CheckWorkingDirectory".GetLocalized(),
                "Executable" => "Localserver_CheckExecutable".GetLocalized(),
                "Service health" => "Localserver_CheckServiceHealth".GetLocalized(),
                "Health probe" => "Localserver_CheckHealthProbe".GetLocalized(),
                "Port" => "Localserver_CheckPort".GetLocalized(),
                _ => item.Name,
            };
            Label = title;
            if (item.Name.StartsWith("Runtime: ", StringComparison.Ordinal))
            {
                Label = item.Name["Runtime: ".Length..];
                title = LocalserverViewModel.FormatMessage("Localserver_CheckRuntimeTitle", "Runtime: {0}", Label);
            }

            Brush = brush;
            Fix = item.Fix;

            string tip = string.IsNullOrWhiteSpace(item.Summary) ? title : $"{title}\n{item.Summary}";
            if (item.Fix is { } fix)
            {
                if (!string.IsNullOrWhiteSpace(fix.Description))
                {
                    tip += $"\n\n{fix.Description}";
                }
                if (!string.IsNullOrWhiteSpace(fix.Command))
                {
                    tip += $"\n{fix.Command}";
                }
            }

            Tooltip = tip;
        }
    }

    public sealed class RecentBarViewModel
    {
        public Brush Brush { get; }
        public double BarHeight { get; }
        public string Description { get; }

        public RecentBarViewModel(ServiceState state, bool? healthProbePassed = null)
        {
            var severity = state.ToSeverity(healthProbePassed);
            Brush = (Brush)Application.Current.Resources[severity switch
            {
                ServiceSeverity.Success => "SystemFillColorSuccessBrush",
                ServiceSeverity.Caution => "SystemFillColorCautionBrush",
                ServiceSeverity.Critical => "SystemFillColorCriticalBrush",
                _ => "ControlStrongStrokeColorDefaultBrush",
            }];
            BarHeight = severity switch
            {
                ServiceSeverity.Success => 18d,
                ServiceSeverity.Caution => 12d,
                ServiceSeverity.Critical => 9d,
                _ => 6d,
            };
            Description = LocalserverViewModel.GetStateLabel(state, healthProbePassed);
        }
    }

    public sealed class LineRowViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly ServiceRunner _runner;
        private readonly LocalserverViewModel _parent;
        private readonly SecretStore? _secretStore;
        private string? _selectedPresetName;
        private bool _isExpanded;
        private bool _isConfigExpanded;
        private int _recentBarCapacity = 60;
        private ProcessResourceSample? _lastResourceSample;
        private DateTimeOffset? _lastResourceSampleAtUtc;
        private long _lastResourceGeneration;
        private bool _disposed;

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<RecentBarViewModel> RecentBars { get; } = new();

        public bool IsConfigExpanded
        {
            get => _isConfigExpanded;
            set
            {
                if (_isConfigExpanded != value)
                {
                    bool wasExpanded = _isConfigExpanded;
                    _isConfigExpanded = value;
                    OnPropertyChanged(nameof(IsConfigExpanded));
                    OnPropertyChanged(nameof(ExpandGlyph));
                    OnPropertyChanged(nameof(ConfigVisibility));
                    if (wasExpanded && !value)
                    {
                        _ = _parent.FlushPendingSaveAsync();
                    }
                }
            }
        }

        public string ExpandGlyph => _isConfigExpanded ? "\uE70E" : "\uE70D";
        public Visibility ConfigVisibility => _isConfigExpanded ? Visibility.Visible : Visibility.Collapsed;
        public Visibility StatusVisibility => State.IsActive() || State.IsFaulted() ? Visibility.Visible : Visibility.Collapsed;
        public Brush UptimeBrush => (Brush)Application.Current.Resources[State.IsActive() ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush"];
        public string LockTooltip => "Localserver_LineLockedRunning".GetLocalized();
        public string PortHintText => PortValue > 0 ? string.Empty : "Localserver_PortNoneHint".GetLocalized();

        public void ToggleConfigExpanded() => IsConfigExpanded = !IsConfigExpanded;

        public void SetRecentBarCapacity(int capacity)
        {
            if (capacity < 1)
            {
                return;
            }

            _recentBarCapacity = capacity;
            while (RecentBars.Count > _recentBarCapacity)
            {
                RecentBars.RemoveAt(0);
            }
        }

        public string? SelectedPresetName
        {
            get => _selectedPresetName;
            set
            {
                if (_selectedPresetName != value)
                {
                    _selectedPresetName = value;
                    OnPropertyChanged(nameof(SelectedPresetName));
                    OnPropertyChanged(nameof(ResolvedCommandText));
                }
            }
        }

        public ServiceRunner Runner => _runner;
        public ServiceDefinition Definition => _runner.Definition;
        public string Id => Definition.Id;

        public string Name
        {
            get => Definition.Name;
            set
            {
                if (Definition.Name != value)
                {
                    var updated = Definition with { Name = value };
                    _runner.UpdateDefinition(updated);
                    OnPropertyChanged(nameof(Name));
                    OnPropertyChanged(nameof(ResolvedCommandText));
                    _parent.ScheduleAutoSave();
                }
            }
        }

        public string Executable
        {
            get => Definition.Executable;
            set
            {
                if (Definition.Executable != value)
                {
                    var updated = Definition with { Executable = value };
                    _runner.UpdateDefinition(updated);
                    OnPropertyChanged(nameof(Executable));
                    OnPropertyChanged(nameof(ResolvedCommandText));
                    _parent.ScheduleAutoSave();
                }
            }
        }

        public string Cwd
        {
            get => Definition.Cwd;
            set
            {
                if (Definition.Cwd != value)
                {
                    var updated = Definition with { Cwd = value };
                    _runner.UpdateDefinition(updated);
                    OnPropertyChanged(nameof(Cwd));
                    OnPropertyChanged(nameof(ResolvedCommandText));
                    _parent.ScheduleAutoSave();
                }
            }
        }

        public string BaseArgsText
        {
            get => ArgumentText.Format(Definition.BaseArgs);
            set
            {
                var parsed = ArgumentText.Parse(value);
                var updated = Definition with { BaseArgs = parsed };
                _runner.UpdateDefinition(updated);
                OnPropertyChanged(nameof(BaseArgsText));
                OnPropertyChanged(nameof(ResolvedCommandText));
                _parent.ScheduleAutoSave();
            }
        }

        public double PortValue
        {
            get => Definition.DefaultPort ?? 0;
            set
            {
                int? port = value > 0 ? (int)value : null;
                var updated = Definition with { DefaultPort = port };
                _runner.UpdateDefinition(updated);
                OnPropertyChanged(nameof(PortValue));
                OnPropertyChanged(nameof(PortText));
                OnPropertyChanged(nameof(PortHintText));
                OnPropertyChanged(nameof(ResolvedCommandText));
                _parent.ScheduleAutoSave();
            }
        }

        public string TagLabel => Definition.Tags.Count > 0 ? Definition.Tags[0].ToUpperInvariant() : string.Empty;
        public Visibility TagVisibility => Definition.Tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public bool IsAdopted => _runner.IsRecovered && State.IsActive();
        public Visibility AdoptedVisibility => IsAdopted ? Visibility.Visible : Visibility.Collapsed;
        public string AdoptedTooltip => "Localserver_AdoptedTooltip".GetLocalized();

        public Brush TagBrush => (Brush)Application.Current.Resources[Definition.Tags.Count > 0
            ? Definition.Tags[0].ToLowerInvariant() switch
            {
                "web" => "AccentFillColorDefaultBrush",
                "ai" or "gpu" => "SystemFillColorCautionBrush",
                "db" or "database" => "SystemFillColorSuccessBrush",
                _ => "TextFillColorSecondaryBrush",
            }
            : "TextFillColorSecondaryBrush"];

        public string PortText => (_runner.Port ?? Definition.DefaultPort)?.ToString(CultureInfo.InvariantCulture) ?? "—";

        public ServiceState State => _runner.State;
        public string StateLabel => LocalserverViewModel.GetStateLabel(State, _runner.HealthProbePassed);

        public Brush StateBrush => (Brush)Application.Current.Resources[State.ToSeverity(_runner.HealthProbePassed) switch
        {
            ServiceSeverity.Success => "SystemFillColorSuccessBrush",
            ServiceSeverity.Caution => "SystemFillColorCautionBrush",
            ServiceSeverity.Critical => "SystemFillColorCriticalBrush",
            _ => "ControlStrongStrokeColorDefaultBrush",
        }];

        public string UptimeText
        {
            get
            {
                if (_runner.StartedAtUtc is not { } startedAt || !State.IsActive())
                {
                    return "—";
                }

                TimeSpan elapsed = DateTimeOffset.UtcNow - startedAt;
                return elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            }
        }

        public string CpuText { get; private set; } = "—";
        public string MemoryText { get; private set; } = "—";
        public string PidText => _runner.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "—";

        public bool IsRunning => State.IsActive();
        public bool IsEditable => _parent.IsEnabled && !_parent.IsCatalogBusy && State.AllowsEditing();
        public Visibility LockVisibility => IsRunning ? Visibility.Visible : Visibility.Collapsed;
        public bool CanToggle => _parent.IsEnabled && !_parent.IsCatalogBusy && !State.IsTransitional() && (State.CanStop() || State.CanStart());
        public bool CanRestart => _parent.IsEnabled && !_parent.IsCatalogBusy && !State.IsTransitional() && (State.CanStart() || State.CanStop());
        public bool CanForceKill => _parent.IsEnabled && !_parent.IsCatalogBusy && State.IsActive();

        public bool IsOn
        {
            get => State is ServiceState.Running or ServiceState.Starting or ServiceState.Preflight or ServiceState.Backoff;
            set
            {
                if (!CanToggle)
                {
                    return;
                }

                if (value && State.CanStart())
                {
                    _ = _parent.StartLineAsync(this);
                }
                else if (!value && State.CanStop())
                {
                    _ = _parent.StopLineAsync(this);
                }
            }
        }

        public Visibility OpenUrlVisibility =>
            State == ServiceState.Running && OpenUrl != null
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Uri? OpenUrl
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Definition.OpenUrl))
                {
                    return null;
                }

                var expander = VariableExpander.ForService(Definition, _runner.Port ?? Definition.DefaultPort, _secretStore);
                string expanded = expander.Expand(Definition.OpenUrl).Trim();
                return expander.Unresolved.Count == 0
                    && Uri.TryCreate(expanded, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    ? uri
                    : null;
            }
        }

        public string ResolvedCommandText => _runner.DescribeCommand(_selectedPresetName);

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged(nameof(IsExpanded));
                }
            }
        }

        public ObservableCollection<ParameterSettingViewModel> Parameters { get; } = new();

        public IReadOnlyList<double> CpuHistory => _cpuHistory;
        private readonly List<double> _cpuHistory = new();

        public LineRowViewModel(ServiceRunner runner, LocalserverViewModel parent, SecretStore? secretStore = null)
        {
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(parent);

            _runner = runner;
            _parent = parent;
            _secretStore = secretStore;

            foreach (var p in runner.Definition.Parameters)
            {
                Parameters.Add(new ParameterSettingViewModel(p));
            }

            _runner.StateChanged += OnRunnerStateChanged;
        }

        private void OnRunnerStateChanged(object? sender, ServiceStateChangedEventArgs e)
        {
            _parent.Dispatcher.TryEnqueue(() =>
            {
                if (!_disposed)
                {
                    RaiseAll();
                    _parent.NotifyCatalogState();
                }
            });
        }

        public void RecordCpuSample(double? cpuPercent)
        {
            _cpuHistory.Add(cpuPercent ?? 0d);
            while (_cpuHistory.Count > 60)
            {
                _cpuHistory.RemoveAt(0);
            }

            CpuText = cpuPercent.HasValue ? $"{cpuPercent.Value:F1}%" : "—";
            OnPropertyChanged(nameof(CpuText));
        }

        public void SetMemoryText(string mem)
        {
            MemoryText = mem;
            OnPropertyChanged(nameof(MemoryText));
        }

        public async Task RefreshResourceSampleAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                return;
            }

            int? processId = _runner.ProcessId;
            long processGeneration = _runner.ProcessGeneration;
            if (processId is null || !State.IsActive())
            {
                RecordCpuSample(0);
                SetMemoryText("0 MB");
                return;
            }

            try
            {
                var sample = await Task.Run(() =>
                {
                    _runner.RefreshOwnershipSnapshot();
                    return ProcessResourceInspector.Sample(processId.Value, _runner.GetManagedProcessIds());
                }, cancellationToken);

                if (_disposed)
                {
                    return;
                }

                if (sample.ReadableProcessCount > 0)
                {
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    double? cpuPercent = null;
                    if (_lastResourceSample is { } previous
                        && _lastResourceSampleAtUtc is { } previousAt
                        && previous.RootProcessId == sample.RootProcessId
                        && _lastResourceGeneration == processGeneration)
                    {
                        double elapsedMilliseconds = (now - previousAt).TotalMilliseconds;
                        double cpuMilliseconds = (sample.TotalProcessorTime - previous.TotalProcessorTime).TotalMilliseconds;
                        if (elapsedMilliseconds > 0 && cpuMilliseconds >= 0)
                        {
                            cpuPercent = Math.Clamp(
                                cpuMilliseconds / (elapsedMilliseconds * Environment.ProcessorCount) * 100d,
                                0d,
                                100d);
                        }
                    }

                    _lastResourceSample = sample;
                    _lastResourceSampleAtUtc = now;
                    _lastResourceGeneration = processGeneration;

                    RecordCpuSample(cpuPercent ?? 0);
                    double memMb = sample.WorkingSetBytes / 1024d / 1024d;
                    SetMemoryText($"{memMb:F0} MB");
                }
            }
            catch
            {
                if (!_disposed)
                {
                    RecordCpuSample(null);
                    SetMemoryText("—");
                }
            }
        }

        public void RaiseAll()
        {
            if (_disposed)
            {
                return;
            }

            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(StateBrush));
            OnPropertyChanged(nameof(PortText));
            OnPropertyChanged(nameof(UptimeText));
            OnPropertyChanged(nameof(PidText));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsEditable));
            OnPropertyChanged(nameof(LockVisibility));
            OnPropertyChanged(nameof(IsOn));
            OnPropertyChanged(nameof(CanToggle));
            OnPropertyChanged(nameof(CanRestart));
            OnPropertyChanged(nameof(CanForceKill));
            OnPropertyChanged(nameof(OpenUrlVisibility));
            OnPropertyChanged(nameof(OpenUrl));
            OnPropertyChanged(nameof(ResolvedCommandText));
            OnPropertyChanged(nameof(UptimeBrush));
            OnPropertyChanged(nameof(StatusVisibility));
            OnPropertyChanged(nameof(PortHintText));
            OnPropertyChanged(nameof(LockTooltip));
            OnPropertyChanged(nameof(IsAdopted));
            OnPropertyChanged(nameof(AdoptedVisibility));

            if (State.IsActive())
            {
                RecentBars.Add(new RecentBarViewModel(State, _runner.HealthProbePassed));
                while (RecentBars.Count > _recentBarCapacity)
                {
                    RecentBars.RemoveAt(0);
                }
            }
            else if (State == ServiceState.Stopped)
            {
                if (RecentBars.Count > 0)
                {
                    RecentBars.Clear();
                }
            }

            foreach (var param in Parameters)
            {
                param.IsLocked = !IsEditable;
            }
        }

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _runner.StateChanged -= OnRunnerStateChanged;
            _runner.Dispose();
        }
    }

    public sealed class LogLineViewModel
    {
        private static readonly string[] ErrorMarkers =
            ["error", "err:", "fatal", "exception", "traceback", "failed", "panic"];

        private static readonly string[] WarningMarkers = ["warn", "deprecated"];

        public LogLineViewModel(LogLine line)
        {
            ArgumentNullException.ThrowIfNull(line);

            Sequence = line.Sequence;
            Text = line.Text;
            Timestamp = line.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            StreamLabel = line.Stream switch
            {
                LogStream.StdOut => "out",
                LogStream.StdErr => "err",
                _ => "hub",
            };

            Brush = ResolveBrush(line);
            FontWeight = line.Stream == LogStream.Hub ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }

        public long Sequence { get; }
        public string Timestamp { get; }
        public string StreamLabel { get; }
        public string Text { get; }
        public Brush Brush { get; }
        public global::Windows.UI.Text.FontWeight FontWeight { get; }

        private static Brush ResolveBrush(LogLine line)
        {
            string key = line.Stream switch
            {
                LogStream.StdErr => "SystemFillColorCriticalBrush",
                LogStream.Hub => "TextFillColorSecondaryBrush",
                _ => ClassifyText(line.Text),
            };

            return (Brush)Application.Current.Resources[key];
        }

        private static string ClassifyText(string text)
        {
            foreach (string marker in ErrorMarkers)
            {
                if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return "SystemFillColorCriticalBrush";
                }
            }

            foreach (string marker in WarningMarkers)
            {
                if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return "SystemFillColorCautionBrush";
                }
            }

            return "TextFillColorPrimaryBrush";
        }
    }

    public sealed partial class LocalserverViewModel : PageViewModelBase, IDisposable
    {
        protected override string ModuleName => LocalserverSettings.ModuleName;

        public DispatcherQueue Dispatcher { get; }
        private readonly ISettingsRepository<GeneralSettings> _generalSettingsRepository;
        private readonly ISettingsRepository<LocalserverSettings> _moduleSettingsRepository;
        private readonly Func<string, int> _sendConfigMsg;

        private GeneralSettings _generalSettingsConfig;
        private LocalserverSettings _moduleSettings;
        private GpoRuleConfigured _enabledGpoRuleConfiguration;
        private bool _enabledStateIsGPOConfigured;
        private bool _isEnabled;
        private HubSettings _hubSettings = new();
        private ServiceCatalog? _loadedCatalog;
        private bool _canCreateInitialExample;
        private bool _isCatalogBusy;
        private bool _isErrorOpen;
        private bool _isPageActive;
        private bool _isInitialized;
        private bool _disposed;
        private int _telemetryRefreshInFlight;
        private int _environmentCheckInFlight;
        private double _chartWidth = 300;
        private double _chartHeight = 44;

        internal static string FormatMessage(string resourceKey, string fallback, params object?[] args)
        {
            string format = resourceKey.GetLocalized();
            if (string.IsNullOrWhiteSpace(format))
            {
                format = fallback;
            }

            return args.Length == 0 ? format : string.Format(CultureInfo.CurrentCulture, format, args);
        }

        internal static string GetStateLabel(ServiceState state, bool? healthProbePassed = null)
        {
            string suffix = state == ServiceState.Running && healthProbePassed == false
                ? "Unhealthy"
                : Enum.IsDefined(state) ? state.ToString() : "Unknown";
            return $"Localserver_State{suffix}".GetLocalized();
        }

        public string ErrorMessage { get; private set; } = string.Empty;
        public InfoBarSeverity MessageSeverity { get; private set; } = InfoBarSeverity.Error;

        public bool IsErrorOpen
        {
            get => _isErrorOpen;
            set
            {
                if (_isErrorOpen != value)
                {
                    _isErrorOpen = value;
                    OnPropertyChanged(nameof(IsErrorOpen));
                }
            }
        }

        private void ShowError(string resourceKey, params object?[] args)
        {
            if (_disposed)
            {
                return;
            }

            ErrorMessage = FormatMessage(resourceKey, "The operation failed.", args);
            MessageSeverity = InfoBarSeverity.Error;
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(MessageSeverity));
            IsErrorOpen = true;
        }

        private void ShowSuccess(string resourceKey, params object?[] args)
        {
            if (_disposed)
            {
                return;
            }

            ErrorMessage = FormatMessage(resourceKey, "Service configuration saved successfully.", args);
            MessageSeverity = InfoBarSeverity.Success;
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(MessageSeverity));
            IsErrorOpen = true;
        }

        public void ReportError(string resourceKey) => ShowError(resourceKey);

        public bool IsCatalogBusy
        {
            get => _isCatalogBusy;
            private set
            {
                if (_isCatalogBusy == value)
                {
                    return;
                }

                _isCatalogBusy = value;
                if (value)
                {
                    _catalogIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                else
                {
                    _catalogIdle.TrySetResult();
                }

                NotifyCatalogState();
                foreach (var row in Lines)
                {
                    row.RaiseAll();
                }
            }
        }

        public bool CanReloadCatalog => !_disposed && !IsCatalogBusy && !Lines.Any(row => row.State.IsActive() || row.State.IsTransitional());
        public bool HasActiveServices => Lines.Any(row => row.Runner.ProcessId.HasValue || row.State.IsActive() || row.State.IsTransitional());
        public Visibility EmptyLinesVisibility => Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public bool CanAddLine => IsEnabled && !IsCatalogBusy;
        public bool CanStartAll => IsEnabled && !IsCatalogBusy && Lines.Any(l => l.State.CanStart());
        public bool CanStopAll => IsEnabled && !IsCatalogBusy && Lines.Any(l => l.State.CanStop());
        public bool CanRefreshTelemetry => IsEnabled && !IsCatalogBusy;

        internal void NotifyCatalogState()
        {
            OnPropertyChanged(nameof(IsCatalogBusy));
            OnPropertyChanged(nameof(CanReloadCatalog));
            OnPropertyChanged(nameof(EmptyLinesVisibility));
            OnPropertyChanged(nameof(CanAddLine));
            OnPropertyChanged(nameof(CanStartAll));
            OnPropertyChanged(nameof(CanStopAll));
            OnPropertyChanged(nameof(CanRefreshTelemetry));
            UpdateSessionMetrics();
        }

        private void InitializeEnabledValue()
        {
            _enabledGpoRuleConfiguration = GpoRuleConfigured.Unavailable;
            _enabledStateIsGPOConfigured = false;
            _isEnabled = _generalSettingsConfig.Enabled.Localserver;
        }

        public void RefreshEnabledState()
        {
            _generalSettingsConfig = _generalSettingsRepository.SettingsConfig;
            InitializeEnabledValue();
            ApplyEnabledLifecycle();
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(CanAddLine));
            OnPropertyChanged(nameof(CanStartAll));
            OnPropertyChanged(nameof(CanStopAll));
            OnPropertyChanged(nameof(CanRefreshTelemetry));
            foreach (var row in Lines)
            {
                row.RaiseAll();
            }
        }

        private void ApplyEnabledLifecycle()
        {
            if (!_isEnabled)
            {
                _telemetryTimer.Stop();

                _ = StopAllLinesAsync();

                GpuStatusText = "Localserver_HealthDisabled".GetLocalized();
                GpuStatusDotBrush = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
                GpuUtilizationValue = 0;
                GpuUtilizationText = "—";
                GpuMemoryValue = 0;
                GpuMemoryText = "—";
                GpuTemperatureValue = 0;
                GpuTemperatureText = "—";

                SystemCpuValue = 0;
                SystemCpuText = "—";
                SystemRamValue = 0;
                SystemRamText = "—";

                EnvHealthText = "Localserver_HealthDisabled".GetLocalized();
                EnvHealthBrush = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
                EnvSummaryText = "Localserver_ServiceDisabled".GetLocalized();
                EnvSummaryBrush = (Brush)Application.Current.Resources["TextFillColorDisabledBrush"];
                EnvironmentCheckChips.Clear();

                OnPropertyChanged(nameof(GpuStatusText));
                OnPropertyChanged(nameof(GpuStatusDotBrush));
                OnPropertyChanged(nameof(GpuUtilizationValue));
                OnPropertyChanged(nameof(GpuUtilizationText));
                OnPropertyChanged(nameof(GpuMemoryValue));
                OnPropertyChanged(nameof(GpuMemoryText));
                OnPropertyChanged(nameof(GpuTemperatureValue));
                OnPropertyChanged(nameof(GpuTemperatureText));
                OnPropertyChanged(nameof(SystemCpuValue));
                OnPropertyChanged(nameof(SystemCpuText));
                OnPropertyChanged(nameof(SystemRamValue));
                OnPropertyChanged(nameof(SystemRamText));

                OnPropertyChanged(nameof(EnvHealthText));
                OnPropertyChanged(nameof(EnvHealthBrush));
                OnPropertyChanged(nameof(EnvSummaryText));
                OnPropertyChanged(nameof(EnvSummaryBrush));
                OnPropertyChanged(nameof(HealthRingBrush));
                OnPropertyChanged(nameof(HealthStateText));
                OnPropertyChanged(nameof(HealthStatusIconVisibility));
                OnPropertyChanged(nameof(SessionRunningBrush));
                OnPropertyChanged(nameof(SessionFaultedBrush));
            }
            else
            {
                if (_isPageActive && _isInitialized)
                {
                    _telemetryTimer.Start();
                    _ = RefreshTelemetryAsync();
                    RefreshEnvironmentCard();
                    UpdateSessionMetrics();
                }
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_enabledStateIsGPOConfigured)
                {
                    return;
                }

                if (value != _isEnabled)
                {
                    _isEnabled = value;
                    _generalSettingsConfig.Enabled.Localserver = value;
                    OutGoingGeneralSettings snd = new OutGoingGeneralSettings(_generalSettingsConfig);
                    _sendConfigMsg(snd.ToString());
                    OnPropertyChanged(nameof(IsEnabled));
                    OnPropertyChanged(nameof(CanAddLine));
                    OnPropertyChanged(nameof(CanStartAll));
                    OnPropertyChanged(nameof(CanStopAll));
                    OnPropertyChanged(nameof(CanRefreshTelemetry));

                    ApplyEnabledLifecycle();

                    foreach (var row in Lines)
                    {
                        row.RaiseAll();
                    }
                }
            }
        }

        public bool IsEnabledGpoConfigured => _enabledStateIsGPOConfigured;
        public GpoRuleConfigured EnabledGPOConfiguration => _enabledGpoRuleConfiguration;

        private readonly ServiceCatalogStore _catalogStore;
        private readonly HubSettingsStore _hubSettingsStore;
        private readonly SecretStore _secretStore;
        private readonly SystemResourceInspector _systemResourceInspector = new();
        private readonly DispatcherTimer _telemetryTimer = new();
        private readonly LocalserverLogSink _logSink;
        private CancellationTokenSource? _autoSaveCts;
        private bool _savePending;
        private TaskCompletionSource _catalogIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _lastSaveSucceeded = true;
        private bool _hasUnsavedChanges;

        public async Task<bool> FlushPendingSaveAsync()
        {
            _autoSaveCts?.Cancel();
            while (IsCatalogBusy)
            {
                await _catalogIdle.Task;
            }

            if (_disposed || _loadedCatalog is null || !_hasUnsavedChanges)
            {
                return true;
            }

            await SaveLinesAsync(markDirty: false);
            while (IsCatalogBusy)
            {
                await _catalogIdle.Task;
            }

            return _lastSaveSucceeded;
        }

        public void ScheduleAutoSave(int delayMs = 600)
        {
            if (_disposed)
            {
                return;
            }

            _hasUnsavedChanges = true;
            _autoSaveCts?.Cancel();
            _autoSaveCts?.Dispose();
            _autoSaveCts = new CancellationTokenSource();
            var token = _autoSaveCts.Token;

            Dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, token);
                    if (!token.IsCancellationRequested && !_disposed)
                    {
                        await SaveLinesAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        public ObservableCollection<LineRowViewModel> Lines { get; } = new();
        public ObservableCollection<EnvironmentCheckChipModel> EnvironmentCheckChips { get; } = new();

        private LineRowViewModel? _selectedLine;
        public LineRowViewModel? SelectedLine
        {
            get => _selectedLine;
            set
            {
                if (_selectedLine != value)
                {
                    _selectedLine = value;
                    OnPropertyChanged(nameof(SelectedLine));
                    EnvironmentCheckChips.Clear();
                    EnvSummaryText = value == null
                        ? "Localserver_NoLineSelected".GetLocalized()
                        : "Localserver_EnvironmentChecking".GetLocalized();
                    OnPropertyChanged(nameof(EnvSummaryText));
                    RefreshEnvironmentCard();
                    if (_isLogPanelOpen)
                    {
                        ReplayLogsForSelectedLine();
                    }
                    else
                    {
                        UpdateLogSubtitle();
                        OnPropertyChanged(nameof(LogActionsVisibility));
                    }
                }
            }
        }

        private DateTimeOffset? _sessionStartTimeUtc;
        private bool _isLogPanelOpen;
        private bool _isLogFollowEnabled = true;
        private long _lastRenderedLogSequence;
        private const int MaximumRenderedLines = 2000;

        public ObservableCollection<LogLineViewModel> LogLines { get; } = new();

        public bool IsLogPanelOpen
        {
            get => _isLogPanelOpen;
            set
            {
                if (_isLogPanelOpen != value)
                {
                    _isLogPanelOpen = value;
                    OnPropertyChanged(nameof(IsLogPanelOpen));
                    OnPropertyChanged(nameof(LogPanelVisibility));
                    OnPropertyChanged(nameof(LogToggleGlyph));
                    if (value)
                    {
                        ReplayLogsForSelectedLine();
                    }
                }
            }
        }

        public Visibility LogPanelVisibility => _isLogPanelOpen ? Visibility.Visible : Visibility.Collapsed;
        public string LogToggleGlyph => _isLogPanelOpen ? "" : "";

        public bool IsLogFollowEnabled
        {
            get => _isLogFollowEnabled;
            set
            {
                if (_isLogFollowEnabled != value)
                {
                    _isLogFollowEnabled = value;
                    OnPropertyChanged(nameof(IsLogFollowEnabled));
                }
            }
        }

        public string LogSubtitleText { get; private set; } = "Localserver_NoLineSelected".GetLocalized();
        public Visibility LogEmptyVisibility => (_isLogPanelOpen && LogLines.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LogActionsVisibility => SelectedLine != null ? Visibility.Visible : Visibility.Collapsed;

        public event Action? LogScrollRequested;

        public void ToggleLogPanel()
        {
            IsLogPanelOpen = !IsLogPanelOpen;
        }

        public void ClearLogs()
        {
            SelectedLine?.Runner.Log.Clear();
            LogLines.Clear();
            UpdateLogSubtitle();
            OnPropertyChanged(nameof(LogEmptyVisibility));
        }

        public void OpenSelectedLineLogFolder()
        {
            if (SelectedLine is null)
            {
                return;
            }

            string dir = Logger.CurrentVersionLogDirectoryPath;
            try
            {
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[LocalserverViewModel] Could not open log folder: {ex.GetType().Name}");
            }
        }

        public void ReplayLogsForSelectedLine()
        {
            LogLines.Clear();
            _lastRenderedLogSequence = 0;
            RefreshLogPanel();

            UpdateLogSubtitle();
            OnPropertyChanged(nameof(LogEmptyVisibility));
            OnPropertyChanged(nameof(LogActionsVisibility));
            LogScrollRequested?.Invoke();
        }

        private void RefreshLogPanel()
        {
            if (_disposed || !_isPageActive || !_isLogPanelOpen || SelectedLine == null)
            {
                return;
            }

            var snapshot = SelectedLine.Runner.Log.Snapshot(_lastRenderedLogSequence).TakeLast(MaximumRenderedLines).ToArray();
            if (snapshot.Length == 0)
            {
                return;
            }

            while (LogLines.Count + snapshot.Length > MaximumRenderedLines)
            {
                LogLines.RemoveAt(0);
            }

            foreach (var line in snapshot)
            {
                LogLines.Add(new LogLineViewModel(line));
            }

            _lastRenderedLogSequence = snapshot[^1].Sequence;
            UpdateLogSubtitle();
            OnPropertyChanged(nameof(LogEmptyVisibility));
            if (IsLogFollowEnabled)
            {
                LogScrollRequested?.Invoke();
            }
        }

        private void UpdateLogSubtitle()
        {
            if (SelectedLine is null)
            {
                LogSubtitleText = "Localserver_NoLineSelected".GetLocalized();
            }
            else
            {
                LogSubtitleText = FormatMessage(
                    "Localserver_LogSubtitleFormat",
                    "{0} · {1} lines shown",
                    SelectedLine.Name,
                    LogLines.Count);
            }

            OnPropertyChanged(nameof(LogSubtitleText));
        }

        public string SessionUptimeText { get; private set; } = "—";
        public string SessionRegisteredText => Lines.Count.ToString(CultureInfo.InvariantCulture);
        public string SessionRunningText => Lines.Count(l => l.State.IsActive()).ToString(CultureInfo.InvariantCulture);
        public Brush SessionRunningBrush => (Brush)Application.Current.Resources[(_isEnabled && Lines.Any(l => l.State.IsActive()))
            ? "SystemFillColorSuccessBrush"
            : "TextFillColorPrimaryBrush"];
        public string SessionFaultedText => Lines.Count(l => l.State.IsFaulted()).ToString(CultureInfo.InvariantCulture);
        public Brush SessionFaultedBrush => (Brush)Application.Current.Resources[(_isEnabled && Lines.Any(l => l.State.IsFaulted()))
            ? "SystemFillColorCriticalBrush"
            : "TextFillColorPrimaryBrush"];

        public Brush HealthRingBrush => EnvHealthBrush;
        public string HealthStateText => EnvHealthText;
        public Visibility HealthStatusIconVisibility => (_isEnabled && SelectedLine?.State == ServiceState.Running && SelectedLine?.Runner.HealthProbePassed == true)
            ? Visibility.Visible
            : Visibility.Collapsed;

        public void UpdateSessionMetrics()
        {
            if (Lines.Any(l => l.State.IsActive()))
            {
                _sessionStartTimeUtc ??= DateTimeOffset.UtcNow;
                TimeSpan elapsed = DateTimeOffset.UtcNow - _sessionStartTimeUtc.Value;
                SessionUptimeText = elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            }
            else
            {
                SessionUptimeText = "—";
            }

            OnPropertyChanged(nameof(SessionUptimeText));
            OnPropertyChanged(nameof(SessionRegisteredText));
            OnPropertyChanged(nameof(SessionRunningText));
            OnPropertyChanged(nameof(SessionRunningBrush));
            OnPropertyChanged(nameof(SessionFaultedText));
            OnPropertyChanged(nameof(SessionFaultedBrush));
            OnPropertyChanged(nameof(HealthRingBrush));
            OnPropertyChanged(nameof(HealthStateText));
            OnPropertyChanged(nameof(HealthStatusIconVisibility));
        }

        // Zone 1: Environment card bindings
        public string EnvHealthText { get; private set; } = "Localserver_HealthIdle".GetLocalized();
        public Brush EnvHealthBrush { get; private set; } = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
        public string EnvPortText => SelectedLine?.PortText ?? "—";
        public string EnvPidText => SelectedLine?.PidText ?? "—";
        public string EnvStateText => SelectedLine?.StateLabel ?? "—";
        public Brush EnvStateBrush => SelectedLine?.StateBrush ?? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        public string EnvRuntimesText { get; private set; } = "—";
        public string EnvSummaryText { get; private set; } = "Localserver_NoLineSelected".GetLocalized();
        public Brush EnvSummaryBrush { get; private set; } = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        public string EnvCommandText => SelectedLine?.ResolvedCommandText ?? "—";
        public string EnvCwdText => SelectedLine?.Cwd ?? "—";

        // Zone 1: System card bindings
        public string GpuNameText { get; private set; } = "Localserver_DetectingGpu".GetLocalized();
        public string GpuStatusText { get; private set; } = "Localserver_HealthChecking".GetLocalized();
        public Brush GpuStatusDotBrush { get; private set; } = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
        public string GpuDriverText { get; private set; } = "Localserver_DriverUnavailable".GetLocalized();
        public double GpuUtilizationValue { get; private set; } = 0;
        public string GpuUtilizationText { get; private set; } = "—";
        public double GpuMemoryValue { get; private set; } = 0;
        public string GpuMemoryText { get; private set; } = "—";
        public double GpuTemperatureValue { get; private set; } = 0;
        public string GpuTemperatureText { get; private set; } = "—";

        public string SystemProcessorText { get; private set; } = "Localserver_DetectingProcessor".GetLocalized();
        public double SystemCpuValue { get; private set; } = 0;
        public string SystemCpuText { get; private set; } = "—";
        public double SystemRamValue { get; private set; } = 0;
        public string SystemRamText { get; private set; } = "—";
        public string SystemEditionText { get; private set; } = "Localserver_DetectingWindows".GetLocalized();
        public string SystemBuildText { get; private set; } = "—";

        private string? _cachedCpuName;
        private string? _cachedEdition;
        private string? _cachedBuild;

        // Canvas CPU trend curve geometry
        public Geometry? StatusCurveGeometry { get; private set; }
        public Geometry? StatusCurveFillGeometry { get; private set; }
        public string StatusChartRangeText { get; private set; } = "Localserver_ChartRange".GetLocalized();
        public Visibility StatusChartEmptyVisibility =>
            SelectedLine == null || SelectedLine.CpuHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public event Action? ChartRedrawRequested;

        public LocalserverViewModel(
            ISettingsRepository<GeneralSettings> settingsRepository,
            ISettingsRepository<LocalserverSettings> moduleSettingsRepository,
            Func<string, int>? ipcMsgCallback = null)
        {
            ArgumentNullException.ThrowIfNull(settingsRepository);
            ArgumentNullException.ThrowIfNull(moduleSettingsRepository);

            Dispatcher = DispatcherQueue.GetForCurrentThread();
            _generalSettingsRepository = settingsRepository;
            _moduleSettingsRepository = moduleSettingsRepository;
            _generalSettingsConfig = settingsRepository.SettingsConfig;
            _moduleSettings = moduleSettingsRepository.SettingsConfig;
            _sendConfigMsg = ipcMsgCallback ?? (_ => 0);

            InitializeEnabledValue();

            string dataDir = LocalserverPathHelper.RootDataDirectory;
            _canCreateInitialExample = !Directory.Exists(dataDir);
            LocalserverPathHelper.EnsureDirectoriesExist();
            _catalogStore = new ServiceCatalogStore(dataDir);
            _hubSettingsStore = new HubSettingsStore(dataDir);
            _secretStore = new SecretStore(dataDir);
            _logSink = new LocalserverLogSink();

            _telemetryTimer.Interval = TimeSpan.FromSeconds(1);
            _telemetryTimer.Tick += OnTelemetryTimerTick;
            Lines.CollectionChanged += (_, _) => NotifyCatalogState();

            _ = InitializeAsync();
        }

        public void SetPageActive(bool active)
        {
            if (_disposed)
            {
                return;
            }

            _isPageActive = active;
            if (active && _isInitialized && _isEnabled)
            {
                _telemetryTimer.Start();
                _ = RefreshTelemetryAsync();
            }
            else
            {
                _telemetryTimer.Stop();
            }
        }

        private async Task InitializeAsync()
        {
            try
            {
                _secretStore.Load();
                _hubSettings = await _hubSettingsStore.LoadAsync();
                if (_disposed)
                {
                    return;
                }

                await LoadLinesAsync();
                _isInitialized = true;
                SetPageActive(_isPageActive);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[LocalserverViewModel] Initialization error: {ex.GetType().Name}");
                ShowError("Localserver_InitializationFailed");
            }
        }

        public async Task LoadLinesAsync()
        {
            if (!CanReloadCatalog)
            {
                return;
            }

            IsCatalogBusy = true;
            string? selectedId = SelectedLine?.Id;
            try
            {
                var catalog = await _catalogStore.LoadAndMigrateSecretsAsync(_secretStore);
                if (_disposed)
                {
                    return;
                }

                var definitions = catalog.Services;

                // Preserve an existing empty catalog. The sample is only for a newly created data directory.
                if (_canCreateInitialExample && definitions.Count == 0 && !File.Exists(_catalogStore.CatalogPath))
                {
                    definitions = [
                        new ServiceDefinition
                        {
                            Id = "loopback-ping",
                            Name = "Loopback Ping",
                            Description = "Smoke test service. Long-running, prints continuously. Verifies start, stop, log streaming.",
                            Cwd = @"C:\Windows\System32",
                            Executable = @"C:\Windows\System32\cmd.exe",
                            BaseArgs = ["/d", "/s", "/c", "ping", "-t", "127.0.0.1"],
                            Tags = ["tool"],
                            Io = ServiceIoMode.Pipe,
                            Encoding = "auto",
                            Health = new HealthCheck { Kind = HealthCheckKind.None },
                            Restart = new RestartSettings { Policy = RestartPolicy.Never },
                            StopTimeoutSec = 5,
                            IsEnabled = true,
                        }
                    ];
                    catalog = await _catalogStore.UpdateAsync(current => current with { Services = definitions }, expectedCatalog: catalog);
                    definitions = catalog.Services;
                }

                _canCreateInitialExample = false;

                if (_disposed)
                {
                    return;
                }

                foreach (var oldRow in Lines)
                {
                    oldRow.Runner.LogAppended -= OnRunnerLogAppended;
                    oldRow.Dispose();
                }

                SelectedLine = null;
                Lines.Clear();
                foreach (var def in definitions)
                {
                    var runner = new ServiceRunner(def, _hubSettings.LogRingBufferLines, FormatMessage);
                    runner.SetSecretStore(_secretStore);

                    var row = new LineRowViewModel(runner, this, _secretStore);
                    runner.LogAppended += OnRunnerLogAppended;
                    Lines.Add(row);
                }

                SelectedLine = Lines.FirstOrDefault(row => row.Id == selectedId) ?? Lines.FirstOrDefault();
                _loadedCatalog = catalog;
                _hasUnsavedChanges = false;
                _lastSaveSucceeded = true;
                if (_catalogStore.RecoverySource is { } backup)
                {
                    ErrorMessage = FormatMessage(
                        "Localserver_CatalogRecovered",
                        "Loaded backup {0} because the service configuration was invalid. Review the services before saving.",
                        Path.GetFileName(backup));
                    MessageSeverity = InfoBarSeverity.Warning;
                    OnPropertyChanged(nameof(ErrorMessage));
                    OnPropertyChanged(nameof(MessageSeverity));
                    IsErrorOpen = true;
                }
                else
                {
                    IsErrorOpen = false;
                }

                // Recover already running jobs
                var rows = Lines.ToArray();
                await Task.Run(() =>
                {
                    var snapshot = ProcessTreeSnapshot.Capture();
                    foreach (var line in rows)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        try
                        {
                            line.Runner.TryRecover(snapshot);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"[LocalserverViewModel] Recovery failed: {ex.GetType().Name}");
                            Dispatcher.TryEnqueue(() => ShowError("Localserver_RecoveryFailed", line.Name));
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.LogError($"[LocalserverViewModel] LoadLinesAsync failed: {ex.GetType().Name}");
                ShowError("Localserver_LoadCatalogFailed");
            }
            finally
            {
                IsCatalogBusy = false;
                RefreshEnvironmentCard();
            }
        }

        public async Task SaveLinesAsync(bool markDirty = true, bool showSuccess = false)
        {
            if (_disposed)
            {
                return;
            }

            _hasUnsavedChanges |= markDirty;
            if (IsCatalogBusy)
            {
                _savePending = true;
                return;
            }

            if (_loadedCatalog == null)
            {
                ShowError("Localserver_CatalogNotLoaded");
                return;
            }

            IsCatalogBusy = true;
            try
            {
                var defs = Lines.Select(l => l.Definition).ToList();
                _loadedCatalog = await _catalogStore.UpdateAsync(
                    current => current with { Services = defs },
                    expectedCatalog: _loadedCatalog);
                Logger.LogInfo("[LocalserverViewModel] Saved lines catalog successfully.");
                _lastSaveSucceeded = true;
                _hasUnsavedChanges = false;
                if (showSuccess)
                {
                    ShowSuccess("Localserver_SaveCatalogSuccess");
                }
                else
                {
                    IsErrorOpen = false;
                }
            }
            catch (InvalidOperationException)
            {
                _lastSaveSucceeded = false;
                ShowError("Localserver_CatalogSaveConflict");
            }
            catch (Exception ex)
            {
                _lastSaveSucceeded = false;
                Logger.LogError($"[LocalserverViewModel] SaveLinesAsync failed: {ex.GetType().Name}");
                ShowError("Localserver_SaveCatalogFailed");
            }
            finally
            {
                IsCatalogBusy = false;
                if (_savePending)
                {
                    _savePending = false;
                    _ = SaveLinesAsync();
                }
            }
        }

        public async Task StartLineAsync(LineRowViewModel row)
        {
            if (_disposed || IsCatalogBusy || !row.State.CanStart())
            {
                return;
            }

            if (!IsEnabled)
            {
                IsEnabled = true;
            }

            try
            {
                await row.Runner.StartAsync();
                if (row.State.IsFaulted())
                {
                    ShowError("Localserver_StartLineFailed", row.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[LocalserverViewModel] Failed to start {row.Name}: {ex.GetType().Name}");
                ShowError("Localserver_StartLineFailed", row.Name);
            }
        }

        public async Task StopLineAsync(LineRowViewModel row)
        {
            if (_disposed || IsCatalogBusy || !row.State.CanStop())
            {
                return;
            }

            try
            {
                await row.Runner.StopAsync();
                if (row.State == ServiceState.StopFailed)
                {
                    ShowError("Localserver_StopLineFailed", row.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[LocalserverViewModel] Failed to stop {row.Name}: {ex.GetType().Name}");
                ShowError("Localserver_StopLineFailed", row.Name);
            }
        }

        public async Task RestartLineAsync(LineRowViewModel row)
        {
            if (_disposed || !row.CanRestart)
            {
                return;
            }

            try
            {
                await row.Runner.RestartAsync();
                if (row.State.IsFaulted())
                {
                    ShowError("Localserver_RestartLineFailed", row.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[LocalserverViewModel] Failed to restart {row.Name}: {ex.GetType().Name}");
                ShowError("Localserver_RestartLineFailed", row.Name);
            }
        }

        public async Task ForceKillLineAsync(LineRowViewModel row)
        {
            if (_disposed || !row.CanForceKill)
            {
                return;
            }

            try
            {
                await row.Runner.ForceKillAsync();
                if (row.State == ServiceState.StopFailed)
                {
                    ShowError("Localserver_ForceKillFailed", row.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[LocalserverViewModel] Failed to force kill {row.Name}: {ex.GetType().Name}");
                ShowError("Localserver_ForceKillFailed", row.Name);
            }
        }

        public Task StartAllLinesAsync() => Task.WhenAll(Lines.Where(line => line.State.CanStart()).ToArray().Select(StartLineAsync));

        public Task StopAllLinesAsync() => Task.WhenAll(Lines.Where(line => line.State.CanStop()).ToArray().Select(StopLineAsync));

        public void AddNewLine()
        {
            if (_disposed || IsCatalogBusy)
            {
                return;
            }

            string id = $"service-{Guid.NewGuid():N}";
            var newDef = new ServiceDefinition
            {
                Id = id,
                Name = "Localserver_NewLine".GetLocalized(),
                Cwd = ".",
                Executable = string.Empty,
                BaseArgs = [],
                Tags = ["custom"],
                DefaultPort = 8000,
                StopTimeoutSec = 5,
                IsEnabled = true,
            };

            var runner = new ServiceRunner(newDef, _hubSettings.LogRingBufferLines, FormatMessage);
            runner.SetSecretStore(_secretStore);

            var row = new LineRowViewModel(runner, this, _secretStore);
            row.IsConfigExpanded = true;
            runner.LogAppended += OnRunnerLogAppended;
            Lines.Add(row);
            SelectedLine = row;
            _ = SaveLinesAsync();
        }

        public async Task DeleteLineAsync(LineRowViewModel row)
        {
            if (_disposed || IsCatalogBusy || !row.IsEditable)
            {
                return;
            }

            row.Runner.LogAppended -= OnRunnerLogAppended;
            row.Dispose();
            Lines.Remove(row);

            if (SelectedLine == row)
            {
                SelectedLine = Lines.FirstOrDefault();
            }

            await SaveLinesAsync();
        }

        public void MoveLineUp(LineRowViewModel row)
        {
            if (_disposed || IsCatalogBusy)
            {
                return;
            }

            int index = Lines.IndexOf(row);
            if (index > 0)
            {
                Lines.Move(index, index - 1);
                _ = SaveLinesAsync();
            }
        }

        public void MoveLineDown(LineRowViewModel row)
        {
            if (_disposed || IsCatalogBusy)
            {
                return;
            }

            int index = Lines.IndexOf(row);
            if (index >= 0 && index < Lines.Count - 1)
            {
                Lines.Move(index, index + 1);
                _ = SaveLinesAsync();
            }
        }

        private async void OnTelemetryTimerTick(object? sender, object e)
        {
            if (!_isEnabled || !_isPageActive || _disposed)
            {
                _telemetryTimer.Stop();
                return;
            }

            UpdateSessionMetrics();
            RefreshLogPanel();
            await RefreshTelemetryAsync();
        }

        public async Task RefreshTelemetryAsync()
        {
            if (_disposed || !_isPageActive || !_isEnabled || IsCatalogBusy || Interlocked.CompareExchange(ref _telemetryRefreshInFlight, 1, 0) != 0)
            {
                return;
            }

            try
            {
                // Refresh Host load
                var sysSample = _systemResourceInspector.Sample();
                SystemCpuValue = sysSample.CpuPercent ?? 0;
                SystemCpuText = sysSample.CpuPercent.HasValue ? $"{sysSample.CpuPercent.Value:F0}%" : "—";
                double totalGb = sysSample.TotalMemoryBytes / 1024d / 1024d / 1024d;
                double usedGb = sysSample.UsedMemoryBytes / 1024d / 1024d / 1024d;
                SystemRamValue = totalGb > 0 ? (usedGb / totalGb) * 100 : 0;
                SystemRamText = $"{usedGb:F1} / {totalGb:F1} GB";

                if (string.IsNullOrEmpty(_cachedCpuName))
                {
                    _cachedCpuName = DetectCpu();
                    _cachedEdition = DetectWindowsEdition();
                    _cachedBuild = DetectWindowsBuild();
                }

                string compactCpu = ShortenCpuName(_cachedCpuName);
                SystemProcessorText = FormatMessage("Localserver_ProcessorFormat", "{0} · {1} logical cores", compactCpu, sysSample.LogicalProcessorCount);
                SystemEditionText = ShortenWindowsEdition(_cachedEdition ?? "Windows", _cachedBuild ?? string.Empty);
                SystemBuildText = ShortenWindowsBuild(_cachedBuild ?? string.Empty);

                OnPropertyChanged(nameof(SystemCpuValue));
                OnPropertyChanged(nameof(SystemCpuText));
                OnPropertyChanged(nameof(SystemRamValue));
                OnPropertyChanged(nameof(SystemRamText));
                OnPropertyChanged(nameof(SystemProcessorText));
                OnPropertyChanged(nameof(SystemEditionText));
                OnPropertyChanged(nameof(SystemBuildText));

                // Refresh GPU
                var gpuSnapshot = await GpuInspector.SampleAsync();
                if (_disposed || !_isPageActive || IsCatalogBusy)
                {
                    return;
                }

                if (gpuSnapshot?.Primary is { } primary)
                {
                    GpuNameText = ShortenGpuName(primary.Name);
                    bool gpuActive = (primary.UtilizationPercent ?? 0) > 0;
                    GpuStatusText = (gpuActive ? "Localserver_GpuActive" : "Localserver_HealthIdle").GetLocalized();
                    GpuStatusDotBrush = gpuActive
                        ? (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"]
                        : (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
                    GpuDriverText = string.IsNullOrWhiteSpace(primary.DriverVersion)
                        ? "Localserver_DriverUnavailable".GetLocalized()
                        : FormatMessage("Localserver_DriverFormat", "Driver {0}", primary.DriverVersion);

                    GpuUtilizationValue = primary.UtilizationPercent ?? 0;
                    GpuUtilizationText = primary.UtilizationPercent.HasValue ? $"{primary.UtilizationPercent.Value}%" : "—";

                    if (primary.UsedMemoryMiB.HasValue && primary.TotalMemoryMiB.HasValue && primary.TotalMemoryMiB > 0)
                    {
                        double vramUsed = primary.UsedMemoryMiB.Value / 1024d;
                        double vramTotal = primary.TotalMemoryMiB.Value / 1024d;
                        GpuMemoryValue = (vramUsed / vramTotal) * 100;
                        GpuMemoryText = $"{vramUsed:F1} / {vramTotal:F1} GB";
                    }
                    else
                    {
                        GpuMemoryValue = 0;
                        GpuMemoryText = "—";
                    }

                    GpuTemperatureValue = primary.TemperatureCelsius ?? 0;
                    GpuTemperatureText = primary.TemperatureCelsius.HasValue ? $"{primary.TemperatureCelsius.Value} °C" : "—";
                }
                else
                {
                    GpuNameText = "Localserver_GpuNotDetected".GetLocalized();
                    GpuStatusText = "Localserver_GpuUnavailable".GetLocalized();
                    GpuStatusDotBrush = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
                    GpuDriverText = "Localserver_DriverUnavailable".GetLocalized();
                    GpuUtilizationValue = 0;
                    GpuUtilizationText = "—";
                    GpuMemoryValue = 0;
                    GpuMemoryText = "—";
                    GpuTemperatureValue = 0;
                    GpuTemperatureText = "—";
                }

                OnPropertyChanged(nameof(GpuNameText));
                OnPropertyChanged(nameof(GpuStatusText));
                OnPropertyChanged(nameof(GpuStatusDotBrush));
                OnPropertyChanged(nameof(GpuDriverText));
                OnPropertyChanged(nameof(GpuUtilizationValue));
                OnPropertyChanged(nameof(GpuUtilizationText));
                OnPropertyChanged(nameof(GpuMemoryValue));
                OnPropertyChanged(nameof(GpuMemoryText));
                OnPropertyChanged(nameof(GpuTemperatureValue));
                OnPropertyChanged(nameof(GpuTemperatureText));

                // Sample lines
                foreach (var line in Lines.ToArray())
                {
                    await line.RefreshResourceSampleAsync();
                    if (_disposed || !_isPageActive || IsCatalogBusy)
                    {
                        return;
                    }

                    if (Lines.Contains(line))
                    {
                        line.RaiseAll();
                    }
                }

                RefreshEnvironmentCard();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[LocalserverViewModel] RefreshTelemetryAsync: {ex.GetType().Name}");
                if (_isPageActive)
                {
                    ShowError("Localserver_ResourceCheckFailed");
                }
            }
            finally
            {
                Interlocked.Exchange(ref _telemetryRefreshInFlight, 0);
            }
        }

        public void RefreshEnvironmentCard()
        {
            if (_disposed)
            {
                return;
            }

            if (!_isEnabled)
            {
                EnvHealthText = "Localserver_HealthDisabled".GetLocalized();
                EnvHealthBrush = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
                EnvRuntimesText = "—";
                EnvSummaryText = "Localserver_ServiceDisabled".GetLocalized();
                EnvSummaryBrush = (Brush)Application.Current.Resources["TextFillColorDisabledBrush"];
                EnvironmentCheckChips.Clear();

                OnPropertyChanged(nameof(EnvHealthText));
                OnPropertyChanged(nameof(EnvHealthBrush));
                OnPropertyChanged(nameof(EnvPortText));
                OnPropertyChanged(nameof(EnvPidText));
                OnPropertyChanged(nameof(EnvStateText));
                OnPropertyChanged(nameof(EnvStateBrush));
                OnPropertyChanged(nameof(EnvRuntimesText));
                OnPropertyChanged(nameof(EnvSummaryText));
                OnPropertyChanged(nameof(EnvSummaryBrush));
                OnPropertyChanged(nameof(EnvCommandText));
                OnPropertyChanged(nameof(EnvCwdText));
                UpdateChartGeometry();
                return;
            }

            var line = SelectedLine;
            if (line == null)
            {
                EnvHealthText = "Localserver_HealthIdle".GetLocalized();
                EnvHealthBrush = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
                EnvRuntimesText = "—";
                EnvSummaryText = "Localserver_NoLineSelected".GetLocalized();
                EnvSummaryBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                EnvironmentCheckChips.Clear();
            }
            else
            {
                string healthKey = line.State switch
                {
                    ServiceState.Running => line.Runner.HealthProbePassed == false ? "Localserver_HealthIssue" : "Localserver_HealthHealthy",
                    ServiceState.Starting or ServiceState.Preflight => "Localserver_HealthChecking",
                    ServiceState.Stopping => "Localserver_StateStopping",
                    ServiceState.Backoff => "Localserver_StateBackoff",
                    ServiceState.Crashed or ServiceState.Failed or ServiceState.StopFailed => "Localserver_HealthFaulted",
                    _ => "Localserver_HealthStopped",
                };
                EnvHealthText = healthKey.GetLocalized();
                EnvHealthBrush = line.StateBrush;
                EnvRuntimesText = line.Definition.Runtimes.Count == 0
                    ? "Localserver_NoRuntimes".GetLocalized()
                    : string.Join(", ", line.Definition.Runtimes.Select(runtime => runtime.Kind));

                if (_isPageActive && !IsCatalogBusy && Interlocked.CompareExchange(ref _environmentCheckInFlight, 1, 0) == 0)
                {
                    _ = RefreshEnvironmentAsync(line);
                }
            }

            OnPropertyChanged(nameof(EnvHealthText));
            OnPropertyChanged(nameof(EnvHealthBrush));
            OnPropertyChanged(nameof(EnvPortText));
            OnPropertyChanged(nameof(EnvPidText));
            OnPropertyChanged(nameof(EnvStateText));
            OnPropertyChanged(nameof(EnvStateBrush));
            OnPropertyChanged(nameof(EnvRuntimesText));
            OnPropertyChanged(nameof(EnvSummaryText));
            OnPropertyChanged(nameof(EnvSummaryBrush));
            OnPropertyChanged(nameof(EnvCommandText));
            OnPropertyChanged(nameof(EnvCwdText));
            UpdateChartGeometry();
        }

        private async Task RefreshEnvironmentAsync(LineRowViewModel line)
        {
            if (_disposed || !_isPageActive || !_isEnabled)
            {
                return;
            }
            try
            {
                var snapshot = await Task.Run(() => EnvironmentInspector.CheckAsync(
                    line.Definition,
                    line.Runner.Port,
                    line.Runner.ProcessId,
                    line.Runner.GetManagedProcessIds(),
                    line.Runner.State,
                    line.Runner.LastError,
                    line.Runner.HealthProbePassed,
                    CancellationToken.None,
                    _secretStore,
                    FormatMessage));

                if (_disposed || !_isPageActive || SelectedLine != line || !Lines.Contains(line))
                {
                    return;
                }

                EnvSummaryText = string.IsNullOrWhiteSpace(snapshot.Summary)
                    ? "Localserver_EnvironmentCheckFailed".GetLocalized()
                    : snapshot.Summary;
                string summaryBrushKey = snapshot.Status switch
                {
                    EnvironmentCheckStatus.Success => "SystemFillColorSuccessBrush",
                    EnvironmentCheckStatus.Warning => "SystemFillColorCautionBrush",
                    EnvironmentCheckStatus.Error => "SystemFillColorCriticalBrush",
                    _ => "TextFillColorSecondaryBrush",
                };
                EnvSummaryBrush = (Brush)Application.Current.Resources[summaryBrushKey];
                OnPropertyChanged(nameof(EnvSummaryBrush));

                EnvironmentCheckChips.Clear();
                foreach (var item in snapshot.Items)
                {
                    string brushKey = item.Status switch
                    {
                        EnvironmentCheckStatus.Success => "SystemFillColorSuccessBrush",
                        EnvironmentCheckStatus.Warning => "SystemFillColorCautionBrush",
                        EnvironmentCheckStatus.Error => "SystemFillColorCriticalBrush",
                        _ => "ControlStrongStrokeColorDefaultBrush",
                    };
                    EnvironmentCheckChips.Add(new EnvironmentCheckChipModel(item, (Brush)Application.Current.Resources[brushKey]));
                }

                OnPropertyChanged(nameof(EnvSummaryText));
            }
            catch (Exception ex)
            {
                Logger.LogError($"[LocalserverViewModel] Environment check failed: {ex.GetType().Name}");
                if (!_disposed && _isPageActive && SelectedLine == line && Lines.Contains(line))
                {
                    EnvSummaryText = "Localserver_EnvironmentCheckFailed".GetLocalized();
                    EnvSummaryBrush = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                    EnvironmentCheckChips.Clear();
                    OnPropertyChanged(nameof(EnvSummaryText));
                    OnPropertyChanged(nameof(EnvSummaryBrush));
                }
            }
            finally
            {
                Interlocked.Exchange(ref _environmentCheckInFlight, 0);
            }
        }

        public void UpdateChartGeometry(double width = double.NaN, double height = double.NaN)
        {
            if (double.IsFinite(width) && width > 0)
            {
                _chartWidth = width;
            }

            if (double.IsFinite(height) && height > 0)
            {
                _chartHeight = height;
            }

            width = _chartWidth;
            height = _chartHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            double plotBottom = Math.Max(4, height - 4);
            double plotTop = 4;

            if (SelectedLine == null || SelectedLine.CpuHistory.Count == 0)
            {
                StatusCurveGeometry = CreateStatusBaseline(width, plotBottom);
                StatusCurveFillGeometry = null;
                StatusChartRangeText = "LAST 60S";
                OnPropertyChanged(nameof(StatusCurveGeometry));
                OnPropertyChanged(nameof(StatusCurveFillGeometry));
                OnPropertyChanged(nameof(StatusChartRangeText));
                OnPropertyChanged(nameof(StatusChartEmptyVisibility));
                return;
            }

            var samples = SelectedLine.CpuHistory;
            double peak = Math.Max(1d, samples.Max());
            double scaleMaximum = peak;

            List<Point> points = new(samples.Count);
            for (int i = 0; i < samples.Count; i++)
            {
                double x = samples.Count == 1
                    ? Math.Max(0, width - 2) / 2d
                    : (double)i / (samples.Count - 1) * Math.Max(0, width - 2);
                double norm = Math.Clamp(samples[i] / scaleMaximum, 0d, 1d);
                double y = plotBottom - norm * (plotBottom - plotTop);
                points.Add(new Point(x, y));
            }

            StatusChartRangeText = $"LAST 60S · PEAK {peak:F0}%";

            StatusCurveGeometry = CreateSmoothHealthPath(points);
            StatusCurveFillGeometry = CreateHealthFillPath(points, plotBottom);

            OnPropertyChanged(nameof(StatusCurveGeometry));
            OnPropertyChanged(nameof(StatusCurveFillGeometry));
            OnPropertyChanged(nameof(StatusChartRangeText));
            OnPropertyChanged(nameof(StatusChartEmptyVisibility));
            ChartRedrawRequested?.Invoke();
        }

        private static PathGeometry CreateStatusBaseline(double width, double y)
        {
            PathGeometry geometry = new();
            PathFigure figure = new()
            {
                StartPoint = new Point(0, y),
                IsClosed = false,
                IsFilled = false,
            };
            figure.Segments.Add(new LineSegment
            {
                Point = new Point(Math.Max(0, width), y),
            });
            geometry.Figures.Add(figure);
            return geometry;
        }

        private static PathGeometry CreateSmoothHealthPath(IReadOnlyList<Point> points)
        {
            PathGeometry geometry = new();
            if (points.Count == 0)
            {
                return geometry;
            }

            PathFigure figure = new()
            {
                StartPoint = points[0],
                IsClosed = false,
                IsFilled = false,
            };

            AddSmoothHealthSegments(figure, points);
            geometry.Figures.Add(figure);
            return geometry;
        }

        private static PathGeometry CreateHealthFillPath(IReadOnlyList<Point> points, double plotBottom)
        {
            PathGeometry geometry = new();
            if (points.Count == 0)
            {
                return geometry;
            }

            PathFigure figure = new()
            {
                StartPoint = points[0],
                IsClosed = true,
                IsFilled = true,
            };
            AddSmoothHealthSegments(figure, points);
            figure.Segments.Add(new LineSegment
            {
                Point = new Point(points[^1].X, plotBottom),
            });
            figure.Segments.Add(new LineSegment
            {
                Point = new Point(points[0].X, plotBottom),
            });
            geometry.Figures.Add(figure);
            return geometry;
        }

        private static void AddSmoothHealthSegments(PathFigure figure, IReadOnlyList<Point> points)
        {
            if (points.Count == 1)
            {
                figure.Segments.Add(new LineSegment { Point = new Point(points[0].X + 1, points[0].Y) });
            }
            else if (points.Count == 2)
            {
                figure.Segments.Add(new LineSegment { Point = points[1] });
            }
            else
            {
                for (int index = 1; index < points.Count - 1; index++)
                {
                    Point midpoint = new(
                        (points[index].X + points[index + 1].X) / 2d,
                        (points[index].Y + points[index + 1].Y) / 2d);
                    figure.Segments.Add(new QuadraticBezierSegment
                    {
                        Point1 = points[index],
                        Point2 = midpoint,
                    });
                }

                Point last = points[^1];
                figure.Segments.Add(new QuadraticBezierSegment
                {
                    Point1 = last,
                    Point2 = last,
                });
            }
        }

        private void OnRunnerLogAppended(object? sender, LogLine line)
        {
            if (!_disposed && sender is ServiceRunner runner)
            {
                _logSink.Enqueue(runner.Definition.Id, line);
            }
        }

        public async Task ReleasePortAsync(EnvironmentCheckFix fix)
        {
            if (_disposed || fix.Port is not { } port || fix.ProcessId is not { } pid || fix.ProcessStartTimeUtcFileTime is not { } startTime)
            {
                ShowError("Localserver_ReleasePortFailed");
                return;
            }

            try
            {
                var owner = new PortOwner(port, pid, fix.ProcessName ?? string.Empty, startTime);
                await Task.Run(() => PortInspector.ReleaseAsync(owner));
                IsErrorOpen = false;
                RefreshEnvironmentCard();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[LocalserverViewModel] ReleasePort failed: {ex.GetType().Name}");
                ShowError("Localserver_ReleasePortFailed");
            }
        }

        private static string DetectGpu()
        {
            try
            {
                var gpus = new List<string>();
                for (int i = 0; i < 10; i++)
                {
                    string key = $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{{4d36e968-e325-11ce-bfc1-08002be10318}}\{i:D4}";
                    var desc = Microsoft.Win32.Registry.GetValue(key, "DriverDesc", null) as string;
                    if (!string.IsNullOrWhiteSpace(desc))
                    {
                        gpus.Add(desc.Trim());
                    }
                }

                var dedicated = gpus.FirstOrDefault(g =>
                    g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                    g.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                    g.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase));

                if (dedicated != null)
                {
                    return dedicated;
                }

                if (gpus.Count > 0)
                {
                    return gpus[0];
                }
            }
            catch
            {
            }

            return "Integrated Graphics";
        }

        private static string DetectCpu()
        {
            try
            {
                var cpu = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
                if (!string.IsNullOrWhiteSpace(cpu))
                {
                    return cpu.Trim();
                }
            }
            catch
            {
            }

            return $"{Environment.ProcessorCount} Cores";
        }

        private static string ShortenGpuName(string name)
        {
            string compact = CollapseWhitespace(name)
                .Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();

            foreach (string marker in new[] { "RTX ", "GTX ", "RX ", "Arc ", "UHD ", "Iris " })
            {
                int markerIndex = compact.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex >= 0)
                {
                    compact = compact[markerIndex..];
                    break;
                }
            }

            compact = TrimDisplaySuffix(compact, " Laptop GPU", " Mobile GPU", " Graphics", " GPU");
            return compact.Length == 0 ? "GPU unavailable" : compact;
        }

        private static string ShortenCpuName(string name)
        {
            string compact = CollapseWhitespace(name)
                .Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();

            int markerIndex = compact.IndexOf("Ryzen ", StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                markerIndex = compact.IndexOf("Core ", StringComparison.OrdinalIgnoreCase);
            }

            if (markerIndex >= 0)
            {
                compact = compact[markerIndex..];
            }

            int suffixIndex = compact.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
            if (suffixIndex > 0)
            {
                compact = compact[..suffixIndex];
            }

            suffixIndex = compact.IndexOf(" w/ ", StringComparison.OrdinalIgnoreCase);
            if (suffixIndex > 0)
            {
                compact = compact[..suffixIndex];
            }

            compact = TrimDisplaySuffix(compact, " Processor", " CPU");
            return compact.Length == 0 ? $"{Environment.ProcessorCount} Cores" : compact;
        }

        private static string ShortenWindowsEdition(string edition, string build)
        {
            int? buildNumber = ExtractFirstNumber(build);
            bool isWindows11 = buildNumber is >= 22000
                || edition.Contains("Windows 11", StringComparison.OrdinalIgnoreCase);
            bool isWindows10 = buildNumber is >= 10240 and < 22000
                || edition.Contains("Windows 10", StringComparison.OrdinalIgnoreCase);
            string windows = isWindows11 ? "Windows 11" : isWindows10 ? "Windows 10" : "Windows";
            string suffix = edition.Contains("LTSC", StringComparison.OrdinalIgnoreCase)
                ? "LTSC"
                : edition.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
                    ? "Enterprise"
                    : edition.Contains("Professional", StringComparison.OrdinalIgnoreCase)
                        || edition.Contains(" Pro", StringComparison.OrdinalIgnoreCase)
                        ? "Pro"
                        : edition.Contains("Home", StringComparison.OrdinalIgnoreCase)
                            ? "Home"
                            : string.Empty;

            return string.IsNullOrEmpty(suffix) ? windows : $"{windows} {suffix}";
        }

        private static string ShortenWindowsBuild(string build) =>
            build.StartsWith("OS ", StringComparison.OrdinalIgnoreCase)
                ? build[3..]
                : build;

        private static string CollapseWhitespace(string value) =>
            Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

        private static string TrimDisplaySuffix(string value, params string[] suffixes)
        {
            foreach (string suffix in suffixes)
            {
                if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return value[..^suffix.Length].Trim();
                }
            }

            return value.Trim();
        }

        private static int? ExtractFirstNumber(string value)
        {
            Match match = Regex.Match(value ?? string.Empty, @"\d+");
            return match.Success && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
                ? result
                : null;
        }

        private static string DetectWindowsEdition()
        {
            const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            try
            {
                string productName = Microsoft.Win32.Registry.GetValue(key, "ProductName", null) as string ?? "Windows";
                string displayVersion = Microsoft.Win32.Registry.GetValue(key, "DisplayVersion", null) as string ?? string.Empty;
                string? currentBuild = Microsoft.Win32.Registry.GetValue(key, "CurrentBuild", null)?.ToString();
                if (int.TryParse(currentBuild, NumberStyles.Integer, CultureInfo.InvariantCulture, out int build)
                    && build >= 22000
                    && productName.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
                {
                    productName = $"Windows 11{productName["Windows 10".Length..]}";
                }

                return string.IsNullOrWhiteSpace(displayVersion)
                    ? productName.Trim()
                    : $"{productName.Trim()} {displayVersion.Trim()}";
            }
            catch
            {
                return "Windows";
            }
        }

        private static string DetectWindowsBuild()
        {
            const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            try
            {
                string? currentBuild = Microsoft.Win32.Registry.GetValue(key, "CurrentBuild", null)?.ToString();
                string? updateBuild = Microsoft.Win32.Registry.GetValue(key, "UBR", null)?.ToString();
                if (!string.IsNullOrWhiteSpace(currentBuild))
                {
                    return string.IsNullOrWhiteSpace(updateBuild)
                        ? $"OS Build {currentBuild}"
                        : $"OS Build {currentBuild}.{updateBuild}";
                }
            }
            catch
            {
            }

            return "OS Build —";
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _isPageActive = false;
            if (disposing)
            {
                _autoSaveCts?.Cancel();
                _autoSaveCts?.Dispose();
                _telemetryTimer.Stop();
                _telemetryTimer.Tick -= OnTelemetryTimerTick;
                foreach (var line in Lines)
                {
                    line.Runner.LogAppended -= OnRunnerLogAppended;
                    line.Dispose();
                }

                _logSink.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
