// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using UDPtestLib.Core;

namespace Kit.Settings.UI.ViewModels
{
    public sealed class UDPtestLineRowViewModel : INotifyPropertyChanged
    {
        private int _recentBarCapacity = 240;

        private ProbeLineDefinition _definition;
        private bool _isEnabled;
        private bool _isToggleEnabled = true;
        private string _averageRtt = "--";
        private string _jitter = "--";
        private string _quality = "--";
        private string _counts = "--";
        private string _ipAddress = "--";
        private string _ipAddressDescription = string.Empty;
        private string _lastError = "--";
        private string _lastErrorDisplay = "Last error: --";
        private string _status = "Idle";
        private Brush _statusBrush;
        private Visibility _statusVisibility = Visibility.Collapsed;

        // Folded config fields
        private bool _isConfigExpanded;
        private string _editName = string.Empty;
        private int _editKindIndex; // 0 = STUN, 1 = ECHO
        private string _editEndpoint = string.Empty;
        private string _editPortText = "--";
        private string _editCapabilityText = string.Empty;
        private string _validationErrorMessage = string.Empty;
        private bool _canSave = true;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action<UDPtestLineRowViewModel>? SaveRequested;
        public event Action<UDPtestLineRowViewModel>? DeleteRequested;
        public event Action<UDPtestLineRowViewModel, bool>? ToggleRequested;

        public UDPtestLineRowViewModel(ProbeLineDefinition definition)
        {
            _definition = definition;
            _isEnabled = definition.IsEnabled;
            _statusBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

            ResetEditFields();
        }

        public ProbeLineDefinition Definition => _definition;

        public string Id => _definition.Id;

        public string Name => _definition.Name;

        public ProbeProtocol Protocol => _definition.Protocol;

        public ProbeKind Kind => _definition.Kind;

        public string ProbeKindLabel => _definition.Kind switch
        {
            ProbeKind.Https204 => "HTTPS",
            ProbeKind.StunBinding => "STUN",
            ProbeKind.UdpEcho => "ECHO",
            _ => _definition.Kind.ToString().ToUpperInvariant(),
        };

        public string Target => _definition.TargetLabel ?? _definition.DisplayTarget;

        public string DisplayTarget => _definition.DisplayTarget;

        public string Port => _definition.Port.ToString(CultureInfo.InvariantCulture);

        public string FullTarget => _definition.Protocol == ProbeProtocol.Tcp
            ? _definition.Target
            : $"{_definition.Target}:{_definition.Port}";

        public bool IsUdp => _definition.Protocol == ProbeProtocol.Udp;

        public bool CanExpandConfig => IsUdp;

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged();
                    ToggleRequested?.Invoke(this, value);
                }
            }
        }

        public void SetEnabledSilently(bool isEnabled)
        {
            if (_isEnabled != isEnabled)
            {
                _isEnabled = isEnabled;
                OnPropertyChanged(nameof(IsEnabled));
            }
        }

        public bool IsToggleEnabled
        {
            get => _isToggleEnabled;
            set
            {
                if (_isToggleEnabled != value)
                {
                    _isToggleEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ToggleLabel => $"Toggle {Name}";

        public string AverageRtt
        {
            get => _averageRtt;
            private set
            {
                _averageRtt = value;
                OnPropertyChanged();
            }
        }

        public string Jitter
        {
            get => _jitter;
            private set
            {
                _jitter = value;
                OnPropertyChanged();
            }
        }

        public string Quality
        {
            get => _quality;
            private set
            {
                _quality = value;
                OnPropertyChanged();
            }
        }

        public string Counts
        {
            get => _counts;
            private set
            {
                _counts = value;
                OnPropertyChanged();
            }
        }

        public string IpAddress
        {
            get => _ipAddress;
            private set
            {
                _ipAddress = value;
                OnPropertyChanged();
            }
        }

        public string IpAddressDescription
        {
            get => _ipAddressDescription;
            private set
            {
                _ipAddressDescription = value;
                OnPropertyChanged();
            }
        }

        public string LastError
        {
            get => _lastError;
            private set
            {
                _lastError = value;
                OnPropertyChanged();
            }
        }

        public string LastErrorDisplay
        {
            get => _lastErrorDisplay;
            private set
            {
                _lastErrorDisplay = value;
                OnPropertyChanged();
            }
        }

        public string Status
        {
            get => _status;
            private set
            {
                _status = value;
                OnPropertyChanged();
            }
        }

        public Brush StatusBrush
        {
            get => _statusBrush;
            private set
            {
                _statusBrush = value;
                OnPropertyChanged();
            }
        }

        public Visibility StatusVisibility
        {
            get => _statusVisibility;
            set
            {
                _statusVisibility = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<RecentProbeBarViewModel> RecentBars { get; } = [];

        // Folded Configuration Properties
        public bool IsConfigExpanded
        {
            get => _isConfigExpanded;
            set
            {
                if (_isConfigExpanded != value)
                {
                    _isConfigExpanded = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ConfigVisibility));
                    OnPropertyChanged(nameof(ExpandGlyph));
                    if (value)
                    {
                        ResetEditFields();
                    }
                }
            }
        }

        public Visibility ConfigVisibility => (_isConfigExpanded && IsUdp) ? Visibility.Visible : Visibility.Collapsed;

        public string ExpandGlyph => _isConfigExpanded ? "\uE70E" : "\uE70D";

        public string EditName
        {
            get => _editName;
            set
            {
                if (_editName != value)
                {
                    _editName = value;
                    OnPropertyChanged();
                    ValidateEdit();
                }
            }
        }

        public int EditKindIndex
        {
            get => _editKindIndex;
            set
            {
                if (_editKindIndex != value)
                {
                    _editKindIndex = value;
                    OnPropertyChanged();
                    UpdateEditCapability();
                    ValidateEdit();
                }
            }
        }

        public string EditEndpoint
        {
            get => _editEndpoint;
            set
            {
                if (_editEndpoint != value)
                {
                    _editEndpoint = value;
                    OnPropertyChanged();
                    ValidateEdit();
                }
            }
        }

        public string EditPortText
        {
            get => _editPortText;
            private set
            {
                _editPortText = value;
                OnPropertyChanged();
            }
        }

        public string EditCapabilityText
        {
            get => _editCapabilityText;
            private set
            {
                _editCapabilityText = value;
                OnPropertyChanged();
            }
        }

        public string ValidationErrorMessage
        {
            get => _validationErrorMessage;
            private set
            {
                _validationErrorMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasValidationError));
                OnPropertyChanged(nameof(ValidationErrorVisibility));
            }
        }

        public bool HasValidationError => !string.IsNullOrEmpty(_validationErrorMessage);

        public Visibility ValidationErrorVisibility => HasValidationError ? Visibility.Visible : Visibility.Collapsed;

        public bool CanSave
        {
            get => _canSave;
            private set
            {
                _canSave = value;
                OnPropertyChanged();
            }
        }

        public void ToggleConfigExpanded()
        {
            IsConfigExpanded = !IsConfigExpanded;
        }

        public void ResetEditFields()
        {
            _editName = Target;
            _editKindIndex = _definition.Kind == ProbeKind.UdpEcho ? 1 : 0;
            _editEndpoint = UdpTargetParser.Format(_definition.Target, _definition.Port);
            _validationErrorMessage = string.Empty;
            _canSave = true;

            OnPropertyChanged(nameof(EditName));
            OnPropertyChanged(nameof(EditKindIndex));
            OnPropertyChanged(nameof(EditEndpoint));
            OnPropertyChanged(nameof(ValidationErrorMessage));
            OnPropertyChanged(nameof(HasValidationError));
            OnPropertyChanged(nameof(ValidationErrorVisibility));
            OnPropertyChanged(nameof(CanSave));

            UpdateEditCapability();
            ValidateEdit();
        }

        private void UpdateEditCapability()
        {
            EditCapabilityText = _editKindIndex == 1
                ? "Outbound echo reflection"
                : "NAT evaluation + Public IP detection";
        }

        private void ValidateEdit()
        {
            ProbeKind kind = _editKindIndex == 1 ? ProbeKind.UdpEcho : ProbeKind.StunBinding;
            if (string.IsNullOrWhiteSpace(_editName))
            {
                ValidationErrorMessage = "Target name cannot be empty.";
                CanSave = false;
                EditPortText = "--";
                return;
            }

            if (!UdpTargetParser.TryParse(_editEndpoint, kind, out string host, out int port, out string canonical, out string error))
            {
                ValidationErrorMessage = error;
                CanSave = false;
                EditPortText = "Invalid port";
                return;
            }

            ValidationErrorMessage = string.Empty;
            EditPortText = port.ToString(CultureInfo.InvariantCulture);
            CanSave = true;
        }

        public void SaveConfig()
        {
            ValidateEdit();
            if (!CanSave)
            {
                return;
            }

            ProbeKind kind = _editKindIndex == 1 ? ProbeKind.UdpEcho : ProbeKind.StunBinding;
            UdpTargetParser.TryParse(_editEndpoint, kind, out string host, out int port, out _, out _);

            string targetName = _editName.Trim();
            _definition = new ProbeLineDefinition(
                _definition.Id,
                targetName,
                ProbeProtocol.Udp,
                kind,
                host,
                port,
                IsEnabled: _isEnabled,
                TargetLabel: targetName);

            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Target));
            OnPropertyChanged(nameof(DisplayTarget));
            OnPropertyChanged(nameof(Port));
            OnPropertyChanged(nameof(FullTarget));
            OnPropertyChanged(nameof(Kind));
            OnPropertyChanged(nameof(ProbeKindLabel));

            IsConfigExpanded = false;
            SaveRequested?.Invoke(this);
        }

        public void DeleteConfig()
        {
            DeleteRequested?.Invoke(this);
        }

        public void UpdateFromSnapshot(LineMetricSnapshot snapshot)
        {
            AverageRtt = snapshot.AverageRttMilliseconds is int rtt && rtt >= 0
                ? $"{rtt} ms"
                : "--";
            Jitter = snapshot.JitterMilliseconds is int jitter && jitter >= 0
                ? $"{jitter} ms"
                : "--";
            Quality = snapshot.EligibleWindowCount > 0
                ? $"{snapshot.QualityPercent}%"
                : "--";
            Counts = $"{snapshot.SuccessCount} / {snapshot.FailureCount}";

            string? ip = snapshot.RemoteIpv4;
            IpAddress = !string.IsNullOrEmpty(ip) ? ip : "--";
            IpAddressDescription = !string.IsNullOrEmpty(ip) ? $"Remote endpoint: {ip}" : "Not resolved";

            string? error = snapshot.LastError;
            LastError = !string.IsNullOrEmpty(error) ? error : "--";
            LastErrorDisplay = string.IsNullOrEmpty(error) || error == "--" ? "Last error: --" : $"Last error: {FormatErrorDisplay(error)}";

            Status = snapshot.State;

            StatusBrush = snapshot.State switch
            {
                "OK" => (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
                "DEGRADED" => (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
                "DOWN" => (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                "Warming" => (Brush)Application.Current.Resources["SystemFillColorAttentionBrush"],
                _ => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
        }

        public void AddResultBar(ProbeResult result)
        {
            AddResultBar(result.Outcome, result.DurationMilliseconds, result.FailureCode);
        }

        public void AddResultBar(ProbeOutcome outcome, double durationMilliseconds, string? error = null)
        {
            bool success = outcome == ProbeOutcome.Success;
            string description = success
                ? $"{durationMilliseconds:F1} ms"
                : $"Failed: {error ?? "timeout"}";

            RecentBars.Add(new RecentProbeBarViewModel(success, durationMilliseconds, description));
            while (RecentBars.Count > _recentBarCapacity)
            {
                RecentBars.RemoveAt(0);
            }
        }

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

        public void ResetMetrics()
        {
            AverageRtt = "--";
            Jitter = "--";
            Quality = "--";
            Counts = "--";
            IpAddress = "--";
            IpAddressDescription = string.Empty;
            LastError = "--";
            LastErrorDisplay = "Last error: --";
            Status = "Idle";
            StatusBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            RecentBars.Clear();
        }

        private static string FormatErrorDisplay(string error)
        {
            if (error.StartsWith("http_status_", StringComparison.OrdinalIgnoreCase))
            {
                return $"HTTP {error["http_status_".Length..]}";
            }

            return error switch
            {
                "tcp_timeout" or "stun_timeout" or "udp_echo_timeout" => "Timeout",
                "tcp_dns" or "udp_dns" => "DNS Fail",
                "tcp_connect" => "Connect Fail",
                "tcp_tls" => "TLS Fail",
                "stun_error_response" => "STUN Error",
                "stun_invalid_response" => "Invalid STUN",
                "cancelled" => "Cancelled",
                _ => error.Length > 12 ? error[..10] + "…" : error,
            };
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
