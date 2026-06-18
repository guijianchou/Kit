// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

using Microsoft.PowerToys.Settings.UI.Helpers;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Library.Helpers;
using Microsoft.UI.Xaml.Media;

using MonitorScanStatus = Microsoft.PowerToys.Monitor.MonitorScanStatus;
using MonitorStatusDay = Microsoft.PowerToys.Monitor.MonitorStatusDay;
using MonitorStatusRange = Microsoft.PowerToys.Monitor.MonitorStatusRange;
using MonitorStatusSummary = Microsoft.PowerToys.Monitor.MonitorStatusSummary;

namespace Microsoft.PowerToys.Settings.UI.ViewModels
{
    public partial class MonitorViewModel : Observable
    {
        public MonitorViewModel()
        {
            ModuleSettings = new MonitorSettings();
        }

        public MonitorSettings ModuleSettings
        {
            get => _moduleSettings;
            set
            {
                if (_moduleSettings != value)
                {
                    _moduleSettings = value;
                    RefreshModuleSettings();
                    RefreshEnabledState();
                }
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    RefreshEnabledState();
                    NotifyPropertyChanged();
                    OnPropertyChanged(nameof(CanStartManualScan));
                }
            }
        }

        public bool IsEnabledGpoConfigured => false;

        public string DownloadsPath
        {
            get => ModuleSettings.Properties.DownloadsPath.Value;
            set
            {
                if (ModuleSettings.Properties.DownloadsPath.Value != value)
                {
                    ModuleSettings.Properties.DownloadsPath.Value = value;
                    NotifyPropertyChanged();
                    OnPropertyChanged(nameof(DownloadsPathDisplay));
                }
            }
        }

        public string DownloadsPathDisplay
        {
            get
            {
                string path = string.IsNullOrWhiteSpace(DownloadsPath) ? MonitorProperties.DefaultDownloadsPath : DownloadsPath;
                return Environment.ExpandEnvironmentVariables(path);
            }
        }

        public string CsvFileName
        {
            get => ModuleSettings.Properties.CsvFileName.Value;
            set
            {
                if (ModuleSettings.Properties.CsvFileName.Value != value)
                {
                    ModuleSettings.Properties.CsvFileName.Value = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public IReadOnlyList<MonitorScanIntervalOption> ScanIntervalOptions { get; } = new[]
        {
            new MonitorScanIntervalOption("1h", 3600),
            new MonitorScanIntervalOption("2h", 7200),
            new MonitorScanIntervalOption("6h", 21600),
            new MonitorScanIntervalOption("12h", 43200),
            new MonitorScanIntervalOption("24h", 86400),
        };

        public MonitorStatusRange SelectedStatusRange
        {
            get => _selectedStatusRange;
            set
            {
                if (_selectedStatusRange != value)
                {
                    _selectedStatusRange = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (_statusText != value)
                {
                    _statusText = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string StatusDescription
        {
            get => _statusDescription;
            private set
            {
                if (_statusDescription != value)
                {
                    _statusDescription = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string RunCountText
        {
            get => _runCountText;
            private set
            {
                if (_runCountText != value)
                {
                    _runCountText = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string SuccessRateText
        {
            get => _successRateText;
            private set
            {
                if (_successRateText != value)
                {
                    _successRateText = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string IssueCountText
        {
            get => _issueCountText;
            private set
            {
                if (_issueCountText != value)
                {
                    _issueCountText = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string LastStatusMessage
        {
            get => _lastStatusMessage;
            private set
            {
                if (_lastStatusMessage != value)
                {
                    _lastStatusMessage = value;
                    NotifyPropertyChanged();
                    OnPropertyChanged(nameof(IsLastStatusMessageVisible));
                }
            }
        }

        public bool IsLastStatusMessageVisible => !string.IsNullOrWhiteSpace(LastStatusMessage);

        public IReadOnlyList<MonitorStatusDayViewModel> StatusDays
        {
            get => _statusDays;
            private set
            {
                _statusDays = value;
                NotifyPropertyChanged();
            }
        }

        public int ScanIntervalSeconds
        {
            get => ModuleSettings.Properties.ScanIntervalSeconds.Value;
            set
            {
                if (ModuleSettings.Properties.ScanIntervalSeconds.Value != value)
                {
                    ModuleSettings.Properties.ScanIntervalSeconds.Value = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public int MaxFileSizeMegabytes
        {
            get => ModuleSettings.Properties.MaxFileSizeMegabytes.Value;
            set
            {
                if (ModuleSettings.Properties.MaxFileSizeMegabytes.Value != value)
                {
                    ModuleSettings.Properties.MaxFileSizeMegabytes.Value = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public string HashAlgorithm
        {
            get => ModuleSettings.Properties.HashAlgorithm.Value;
            set
            {
                string normalizedValue = NormalizeHashAlgorithm(value);
                if (ModuleSettings.Properties.HashAlgorithm.Value != normalizedValue)
                {
                    ModuleSettings.Properties.HashAlgorithm.Value = normalizedValue;
                    NotifyPropertyChanged();
                }
            }
        }

        public bool IsManualScanProgressVisible
        {
            get => _isManualScanProgressVisible;
            set
            {
                if (_isManualScanProgressVisible != value)
                {
                    _isManualScanProgressVisible = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public bool IsManualScanProgressIndeterminate
        {
            get => _isManualScanProgressIndeterminate;
            set
            {
                if (_isManualScanProgressIndeterminate != value)
                {
                    _isManualScanProgressIndeterminate = value;
                    NotifyPropertyChanged();
                    OnPropertyChanged(nameof(ManualScanProgressText));
                }
            }
        }

        public double ManualScanProgressValue
        {
            get => _manualScanProgressValue;
            set
            {
                double boundedValue = Math.Clamp(value, 1, 100);
                if (Math.Abs(_manualScanProgressValue - boundedValue) > double.Epsilon)
                {
                    _manualScanProgressValue = boundedValue;
                    NotifyPropertyChanged();
                    OnPropertyChanged(nameof(ManualScanProgressText));
                }
            }
        }

        public string ManualScanProgressText => IsManualScanProgressIndeterminate ? string.Empty : Math.Round(ManualScanProgressValue).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";

        public string ManualScanProgressDetail
        {
            get => _manualScanProgressDetail;
            set
            {
                if (_manualScanProgressDetail != value)
                {
                    _manualScanProgressDetail = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public bool IsManualScanRunning
        {
            get => _isManualScanRunning;
            set
            {
                if (_isManualScanRunning != value)
                {
                    _isManualScanRunning = value;
                    NotifyPropertyChanged();
                    OnPropertyChanged(nameof(CanStartManualScan));
                }
            }
        }

        public bool CanStartManualScan => IsEnabled && !IsManualScanRunning;

        public bool UseIncrementalHashing
        {
            get => ModuleSettings.Properties.UseIncrementalHashing.Value;
            set
            {
                if (ModuleSettings.Properties.UseIncrementalHashing.Value != value)
                {
                    ModuleSettings.Properties.UseIncrementalHashing.Value = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public bool RunInBackground
        {
            get => ModuleSettings.Properties.RunInBackground.Value;
            set
            {
                if (ModuleSettings.Properties.RunInBackground.Value != value)
                {
                    ModuleSettings.Properties.RunInBackground.Value = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public bool OrganizeDownloads
        {
            get => ModuleSettings.Properties.OrganizeDownloads.Value;
            set
            {
                if (ModuleSettings.Properties.OrganizeDownloads.Value != value)
                {
                    ModuleSettings.Properties.OrganizeDownloads.Value = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public bool CleanInstallers
        {
            get => ModuleSettings.Properties.CleanInstallers.Value;
            set
            {
                if (ModuleSettings.Properties.CleanInstallers.Value != value)
                {
                    ModuleSettings.Properties.CleanInstallers.Value = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public void ApplyStatusSummary(MonitorStatusSummary summary)
        {
            ArgumentNullException.ThrowIfNull(summary);

            StatusText = FormatStatus(summary.OverallStatus);
            StatusDescription = summary.TotalRuns == 0 ? GetResourceString("Monitor_StatusNoScans", "No scans yet") : FormatStatusDescription(summary);
            RunCountText = summary.TotalRuns.ToString(CultureInfo.InvariantCulture);
            SuccessRateText = summary.TotalRuns == 0
                ? "--"
                : Math.Round((double)summary.SuccessRuns / summary.TotalRuns * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
            IssueCountText = (summary.WarningRuns + summary.FailedRuns).ToString(CultureInfo.InvariantCulture);
            LastStatusMessage = summary.LastMessage ?? string.Empty;
            StatusDays = summary.Days.Select(CreateDayViewModel).ToArray();
        }

        public void ApplyStatusUnavailable()
        {
            StatusText = GetResourceString("Monitor_StatusUnavailable", "Unavailable");
            StatusDescription = GetResourceString("Monitor_StatusDatabaseUnavailable", "Status database could not be read");
            RunCountText = "--";
            SuccessRateText = "--";
            IssueCountText = "--";
            LastStatusMessage = string.Empty;
            StatusDays = Array.Empty<MonitorStatusDayViewModel>();
        }

        public void RefreshEnabledState()
        {
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(CanStartManualScan));
        }

        public void RefreshModuleSettings()
        {
            OnPropertyChanged(nameof(DownloadsPath));
            OnPropertyChanged(nameof(DownloadsPathDisplay));
            OnPropertyChanged(nameof(CsvFileName));
            OnPropertyChanged(nameof(ScanIntervalSeconds));
            OnPropertyChanged(nameof(MaxFileSizeMegabytes));
            OnPropertyChanged(nameof(HashAlgorithm));
            OnPropertyChanged(nameof(UseIncrementalHashing));
            OnPropertyChanged(nameof(RunInBackground));
            OnPropertyChanged(nameof(OrganizeDownloads));
            OnPropertyChanged(nameof(CleanInstallers));
        }

        private static string NormalizeHashAlgorithm(string value)
        {
            return value?.ToUpperInvariant() switch
            {
                "MD5" => "MD5",
                "SHA256" => "SHA256",
                "SHA512" => "SHA512",
                _ => MonitorProperties.DefaultHashAlgorithm,
            };
        }

        private static MonitorStatusDayViewModel CreateDayViewModel(MonitorStatusDay day)
        {
            string status = FormatStatus(day.Status);
            string toolTip = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ": " + status;
            if (day.TotalRuns > 0)
            {
                toolTip += " (" + day.TotalRuns.ToString(CultureInfo.InvariantCulture) + ")";
            }

            return new MonitorStatusDayViewModel(GetBrush(day.Status), toolTip);
        }

        private static Brush GetBrush(MonitorScanStatus? status)
        {
            return status switch
            {
                MonitorScanStatus.Success => MonitorStatusBrushes.Success,
                MonitorScanStatus.Warning or MonitorScanStatus.Running => MonitorStatusBrushes.Warning,
                MonitorScanStatus.Failed => MonitorStatusBrushes.Failed,
                _ => MonitorStatusBrushes.Empty,
            };
        }

        private static string FormatStatus(MonitorScanStatus? status)
        {
            return status switch
            {
                MonitorScanStatus.Success => GetResourceString("Monitor_StatusHealthy", "Healthy"),
                MonitorScanStatus.Warning => GetResourceString("Monitor_StatusWarning", "Warning"),
                MonitorScanStatus.Failed => GetResourceString("Monitor_StatusFailed", "Failed"),
                MonitorScanStatus.Running => GetResourceString("Monitor_StatusRunning", "Running"),
                _ => GetResourceString("Monitor_StatusNoData", "No data"),
            };
        }

        private static string FormatStatusDescription(MonitorStatusSummary summary)
        {
            return summary.OverallStatus switch
            {
                MonitorScanStatus.Success => GetResourceString("Monitor_StatusLatestSuccess", "Latest scan completed successfully"),
                MonitorScanStatus.Warning => GetResourceString("Monitor_StatusLatestWarning", "Latest scan completed with warnings"),
                MonitorScanStatus.Failed => GetResourceString("Monitor_StatusLatestFailed", "Latest scan failed"),
                MonitorScanStatus.Running => GetResourceString("Monitor_StatusLatestRunning", "Scan is running"),
                _ => GetResourceString("Monitor_StatusNoScans", "No scans yet"),
            };
        }

        private static string GetResourceString(string resourceKey, string fallback)
        {
            string value = ResourceLoaderInstance.ResourceLoader.GetString(resourceKey);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = null)
        {
            OnPropertyChanged(propertyName);
        }

        private MonitorSettings _moduleSettings;
        private bool _isEnabled;
        private MonitorStatusRange _selectedStatusRange = MonitorStatusRange.All;
        private string _statusText = GetResourceString("Monitor_StatusNoData", "No data");
        private string _statusDescription = GetResourceString("Monitor_StatusNoScans", "No scans yet");
        private string _runCountText = "--";
        private string _successRateText = "--";
        private string _issueCountText = "--";
        private string _lastStatusMessage = string.Empty;
        private IReadOnlyList<MonitorStatusDayViewModel> _statusDays = Array.Empty<MonitorStatusDayViewModel>();
        private bool _isManualScanProgressVisible;
        private bool _isManualScanProgressIndeterminate;
        private bool _isManualScanRunning;
        private double _manualScanProgressValue = 1;
        private string _manualScanProgressDetail = string.Empty;
    }
}
