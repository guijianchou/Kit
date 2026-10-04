// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kit.Settings.UI.Views
{
    /// <summary>
    /// Dashboard Settings Page.
    /// </summary>
    public sealed partial class DashboardPage : NavigablePage, IRefreshablePage
    {
        private readonly MainWindow settingsWindow;
        private bool windowSubscribed;
        private bool isCurrentPage;

        public DashboardViewModel ViewModel { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="DashboardPage"/> class.
        /// </summary>
        public DashboardPage()
        {
            InitializeComponent();
            var settingsUtils = SettingsUtils.Default;

            ViewModel = new DashboardViewModel(
               SettingsRepository<GeneralSettings>.GetInstance(settingsUtils), ShellPage.SendDefaultIPCMessage);
            DataContext = ViewModel;
            settingsWindow = App.GetSettingsWindow();

            Loaded += Page_Loaded;
            Unloaded += Page_Unloaded;
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (!windowSubscribed)
            {
                settingsWindow.AppWindow.Changed += Window_Changed;
                settingsWindow.Closed += Window_Closed;
                windowSubscribed = true;
            }

            ViewModel.OnPageLoaded();
            UpdateVisibility();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeWindow();
            ViewModel.Dispose();
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            isCurrentPage = true;
            UpdateVisibility();
        }

        protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            isCurrentPage = false;
            ViewModel.SetOverviewActive(false);
            base.OnNavigatedFrom(e);
        }

        private void Window_Changed(AppWindow sender, AppWindowChangedEventArgs args) => UpdateVisibility();

        private void UpdateVisibility()
        {
            bool visible = isCurrentPage && IsLoaded && settingsWindow.AppWindow.IsVisible &&
                !(settingsWindow.AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized);
            ViewModel.SetOverviewActive(visible);
        }

        private void Window_Closed(object sender, WindowEventArgs args)
        {
            ViewModel.SetOverviewActive(false);
            if (!args.Handled)
            {
                UnsubscribeWindow();
                ViewModel.Dispose();
            }
        }

        private void UnsubscribeWindow()
        {
            if (windowSubscribed)
            {
                settingsWindow.AppWindow.Changed -= Window_Changed;
                settingsWindow.Closed -= Window_Closed;
                windowSubscribed = false;
            }
        }

        public void RefreshEnabledState()
        {
            ViewModel.ModuleEnabledChangedOnSettingsPage();
        }

        private void SortAlphabetical_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.DashboardSortOrder = DashboardSortOrder.Alphabetical;
            if (sender is ToggleMenuFlyoutItem item)
            {
                item.IsChecked = true;
            }
        }

        private void SortByStatus_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.DashboardSortOrder = DashboardSortOrder.ByStatus;
            if (sender is ToggleMenuFlyoutItem item)
            {
                item.IsChecked = true;
            }
        }
    }
}
