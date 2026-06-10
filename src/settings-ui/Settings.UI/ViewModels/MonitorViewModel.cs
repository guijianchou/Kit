// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.CompilerServices;

using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Library.Helpers;

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

        public void RefreshEnabledState()
        {
            OnPropertyChanged(nameof(IsEnabled));
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

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = null)
        {
            OnPropertyChanged(propertyName);
        }

        private MonitorSettings _moduleSettings;
        private bool _isEnabled;
        private bool _isManualScanProgressVisible;
        private bool _isManualScanProgressIndeterminate;
        private double _manualScanProgressValue = 1;
        private string _manualScanProgressDetail = string.Empty;
    }
}
