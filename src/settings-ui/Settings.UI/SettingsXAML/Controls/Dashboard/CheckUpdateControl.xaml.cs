// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Kit.Settings.UI.Library;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.Views;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.Controls
{
    public sealed partial class CheckUpdateControl : UserControl
    {
        public bool UpdateAvailable { get; set; }

        public UpdatingSettings UpdateSettingsConfig { get; set; }

        public string LastCheckedDateFriendly { get; set; }

        public CheckUpdateControl()
        {
            InitializeComponent();
            UpdateSettingsConfig = new UpdatingSettings();
            UpdateAvailable = false;
            LastCheckedDateFriendly = string.Empty;
        }

        private void SWVersionButtonClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            NavigationService.Navigate(typeof(GeneralPage));
        }
    }
}
