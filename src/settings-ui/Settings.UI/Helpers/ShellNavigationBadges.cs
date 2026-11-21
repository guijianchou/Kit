// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kit.Settings.UI.Helpers
{
    /// <summary>
    /// Shared process-wide live badge state for <see cref="Microsoft.UI.Xaml.Controls.NavigationViewItem.InfoBadge"/>
    /// indicators in the shell sidebar. Modules publish active runtime status without coupling to the shell.
    /// </summary>
    public sealed class ShellNavigationBadges : INotifyPropertyChanged
    {
        public static ShellNavigationBadges Current { get; } = new();

        private bool _isAwakeActive;
        private int _runningLocalserverCount;
        private bool _isUdpMonitoring;
        private bool _isNetMapDetecting;
        private int _wingetUpdateCount;
        private bool _isKitUpdateAvailable;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsAwakeActive
        {
            get => _isAwakeActive;
            set => Set(ref _isAwakeActive, value);
        }

        public int RunningLocalserverCount
        {
            get => _runningLocalserverCount;
            set
            {
                if (Set(ref _runningLocalserverCount, value < 0 ? 0 : value))
                {
                    OnPropertyChanged(nameof(HasRunningLocalservers));
                }
            }
        }

        public bool HasRunningLocalservers => _runningLocalserverCount > 0;

        public bool IsUdpMonitoring
        {
            get => _isUdpMonitoring;
            set => Set(ref _isUdpMonitoring, value);
        }

        public bool IsNetMapDetecting
        {
            get => _isNetMapDetecting;
            set => Set(ref _isNetMapDetecting, value);
        }

        public int WingetUpdateCount
        {
            get => _wingetUpdateCount;
            set
            {
                if (Set(ref _wingetUpdateCount, value < 0 ? 0 : value))
                {
                    OnPropertyChanged(nameof(HasWingetUpdates));
                }
            }
        }

        public bool HasWingetUpdates => _wingetUpdateCount > 0;

        public bool IsKitUpdateAvailable
        {
            get => _isKitUpdateAvailable;
            set => Set(ref _isKitUpdateAvailable, value);
        }

        private bool Set<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged(string? propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
