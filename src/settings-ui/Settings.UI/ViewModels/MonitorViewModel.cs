// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Microsoft.PowerToys.Settings.UI.Helpers;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Library.Helpers;
using Microsoft.PowerToys.Settings.UI.Services;

using MonitorStatusRange = Microsoft.PowerToys.Monitor.MonitorStatusRange;
using MonitorStatusSummary = Microsoft.PowerToys.Monitor.MonitorStatusSummary;

namespace Microsoft.PowerToys.Settings.UI.ViewModels
{
    public partial class MonitorViewModel : Observable
    {
        public MonitorViewModel()
            : this(new MonitorStatusPresentationService())
        {
        }

        public MonitorViewModel(MonitorStatusPresentationService statusPresentationService)
        {
            _statusPresentationService = statusPresentationService ?? throw new ArgumentNullException(nameof(statusPresentationService));
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

        public IReadOnlyList<MonitorStatusMetricViewModel> StatusMetrics
        {
            get => _statusMetrics;
            private set
            {
                _statusMetrics = value;
                NotifyPropertyChanged();
            }
        }

        public IReadOnlyList<MonitorStatusLegendItemViewModel> StatusLegendItems
        {
            get => _statusLegendItems;
            private set
            {
                _statusLegendItems = value;
                NotifyPropertyChanged();
            }
        }

        public string StatusChartAccessibilityName
        {
            get => _statusChartAccessibilityName;
            private set
            {
                if (_statusChartAccessibilityName != value)
                {
                    _statusChartAccessibilityName = value;
                    NotifyPropertyChanged();
                }
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

        public int InstallerCleanupConfidence
        {
            get => ModuleSettings.Properties.InstallerCleanupConfidence.Value;
            set
            {
                if (ModuleSettings.Properties.InstallerCleanupConfidence.Value != value)
                {
                    ModuleSettings.Properties.InstallerCleanupConfidence.Value = value;
                    NotifyPropertyChanged();
                }
            }
        }

        public void ApplyStatusSummary(MonitorStatusSummary summary)
        {
            ApplyStatusPresentation(_statusPresentationService.CreateSummaryPresentation(summary));
        }

        public void ApplyStatusUnavailable()
        {
            ApplyStatusPresentation(_statusPresentationService.CreateUnavailablePresentation());
        }

        public void ApplyStatusPresentation(MonitorStatusPresentation presentation)
        {
            ArgumentNullException.ThrowIfNull(presentation);

            StatusText = presentation.StatusText;
            StatusDescription = presentation.StatusDescription;
            LastStatusMessage = presentation.LastStatusMessage;
            StatusMetrics = presentation.Metrics;
            StatusDays = presentation.Days;
            StatusLegendItems = presentation.LegendItems;
            StatusChartAccessibilityName = presentation.ChartAccessibilityName;
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
            OnPropertyChanged(nameof(InstallerCleanupConfidence));
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

        private static string GetResourceString(string resourceKey, string fallback)
        {
            string value = ResourceLoaderInstance.ResourceLoader.GetString(resourceKey);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = null)
        {
            OnPropertyChanged(propertyName);
        }

        private readonly MonitorStatusPresentationService _statusPresentationService;
        private MonitorSettings _moduleSettings;
        private bool _isEnabled;
        private MonitorStatusRange _selectedStatusRange = MonitorStatusRange.All;
        private string _statusText = GetResourceString("Monitor_StatusNoData", "No data");
        private string _statusDescription = GetResourceString("Monitor_StatusNoScans", "No scans yet");
        private string _lastStatusMessage = string.Empty;
        private string _statusChartAccessibilityName = string.Empty;
        private IReadOnlyList<MonitorStatusDayViewModel> _statusDays = Array.Empty<MonitorStatusDayViewModel>();
        private IReadOnlyList<MonitorStatusMetricViewModel> _statusMetrics = Array.Empty<MonitorStatusMetricViewModel>();
        private IReadOnlyList<MonitorStatusLegendItemViewModel> _statusLegendItems = Array.Empty<MonitorStatusLegendItemViewModel>();
        private bool _isManualScanProgressVisible;
        private bool _isManualScanProgressIndeterminate;
        private bool _isManualScanRunning;
        private double _manualScanProgressValue = 1;
        private string _manualScanProgressDetail = string.Empty;
    }
}
