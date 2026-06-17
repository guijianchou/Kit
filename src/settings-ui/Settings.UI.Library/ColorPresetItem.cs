// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Microsoft.PowerToys.Settings.UI.Library
{
    public partial class ColorPresetItem : INotifyPropertyChanged
    {
        private int _vcpValue;
        private string _displayName = string.Empty;

        public ColorPresetItem()
        {
        }

        public ColorPresetItem(int vcpValue, string displayName)
        {
            _vcpValue = vcpValue;
            _displayName = displayName;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [JsonPropertyName("vcpValue")]
        public int VcpValue
        {
            get => _vcpValue;
            set
            {
                if (_vcpValue != value)
                {
                    _vcpValue = value;
                    OnPropertyChanged();
                }
            }
        }

        [JsonPropertyName("displayName")]
        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName != value)
                {
                    _displayName = value;
                    OnPropertyChanged();
                }
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
