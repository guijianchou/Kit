// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetMapLib;

namespace Kit.Settings.UI.Views
{
    /// <summary>
    /// Dashboard Settings Page.
    /// </summary>
    public sealed partial class DashboardPage : NavigablePage, IRefreshablePage
    {
        private readonly MainWindow settingsWindow;
        private bool windowSubscribed;
        private bool typographySubscribed;
        private bool isCurrentPage;
        private int typographyFontSize = -1;

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
            if (!typographySubscribed)
            {
                TypographyResources.TypographyChanged += OnTypographyChanged;
                typographySubscribed = true;
            }

            if (typographyFontSize != TypographyResources.AppliedFontSize)
            {
                ApplyTypographyLayout();
            }
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
            UnsubscribeTypography();

            // Keep the page, telemetry snapshot, and window lifetime subscription cached.
            // Only the work that needs a visible dashboard pauses between visits.
            ViewModel.SetOverviewActive(false);
            ViewModel.SetUpdateStatusActive(false);
        }

        private void OnTypographyChanged(object sender, EventArgs e) => ApplyTypographyLayout();

        private void ApplyTypographyLayout()
        {
            typographyFontSize = TypographyResources.AppliedFontSize;
            DashboardTwoColumnsTrigger.MinWidth = TypographyResources.FontSize(720);
            DashboardSingleColumnTrigger.MaxWidth = TypographyResources.FontSize(720);
            DashboardHeaderWideTrigger.MinWidth = TypographyResources.FontSize(480);
            DashboardHeaderNarrowTrigger.MaxWidth = TypographyResources.FontSize(480);
            DashboardVersionPanel.MaxWidth = TypographyResources.FontSize(340);
            UpdateDetailRows(SystemOverviewCard);
        }

        private void DetailRow_Loaded(object sender, RoutedEventArgs e) => ApplyDetailRowLayout((Grid)sender);

        private static void ApplyDetailRowLayout(Grid row)
        {
            row.ColumnDefinitions[0].Width = new GridLength(TypographyResources.FontSize(92));
            if (row.FindName("UsageRow") is Grid usageRow)
            {
                usageRow.ColumnDefinitions[0].Width = new GridLength(TypographyResources.FontSize(144));
            }
        }

        private static void UpdateDetailRows(DependencyObject root)
        {
            if (root is Grid { Name: "DetailRow" } row)
            {
                ApplyDetailRowLayout(row);
                return;
            }

            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                UpdateDetailRows(VisualTreeHelper.GetChild(root, index));
            }
        }

        private void UnsubscribeTypography()
        {
            if (typographySubscribed)
            {
                TypographyResources.TypographyChanged -= OnTypographyChanged;
                typographySubscribed = false;
            }
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            isCurrentPage = true;
            RefreshEnabledState();
            UpdateVisibility();
        }

        protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            isCurrentPage = false;
            ViewModel.SetOverviewActive(false);
            ViewModel.SetUpdateStatusActive(false);
            base.OnNavigatedFrom(e);
        }

        private void Window_Changed(AppWindow sender, AppWindowChangedEventArgs args) => UpdateVisibility();

        private void UpdateVisibility()
        {
            bool visible = isCurrentPage && IsLoaded && settingsWindow.AppWindow.IsVisible &&
                !(settingsWindow.AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized);
            ViewModel.SetOverviewActive(visible);
            ViewModel.SetUpdateStatusActive(visible);
        }

        private void UpdateNotice_Click(object sender, RoutedEventArgs e) =>
            NavigationService.Navigate(typeof(GeneralPage), new NavigationParams("VersionAndUpdateGroup"));

        /// <summary>Maps an update status kind to its themed icon badge style.</summary>
        public Style UpdateBadgeStyle(DashboardUpdateStatusKind kind) => (Style)((ResourceDictionary)Application.Current.Resources["KitUpdateBadgeStyles"])[kind switch
        {
            DashboardUpdateStatusKind.Attention => "AttentionBadge",
            DashboardUpdateStatusKind.Error => "ErrorBadge",
            _ => "NormalBadge",
        }];

        /// <summary>Maps an update status kind to its themed vector glyph style.</summary>
        public Style UpdateGlyphStyle(DashboardUpdateStatusKind kind) => (Style)((ResourceDictionary)Application.Current.Resources["KitUpdateGlyphStyles"])[kind switch
        {
            DashboardUpdateStatusKind.Attention => "AttentionGlyph",
            DashboardUpdateStatusKind.Error => "ErrorGlyph",
            _ => "NormalGlyph",
        }];

        /// <summary>Maps an update status kind to its themed subtitle text style.</summary>
        public Style UpdateStatusTextStyle(DashboardUpdateStatusKind kind) => (Style)((ResourceDictionary)Application.Current.Resources["KitUpdateStatusTextStyles"])[kind switch
        {
            DashboardUpdateStatusKind.Attention => "AttentionText",
            DashboardUpdateStatusKind.Error => "ErrorText",
            _ => "NormalText",
        }];

        /// <summary>Maps an egress health state to the shared text style (app-level KitEgressStatusStyles).</summary>
        public Style EgressTextStyle(ProbeHealth health) => (Style)((ResourceDictionary)Application.Current.Resources["KitEgressStatusStyles"])[health switch
        {
            ProbeHealth.Good => "GoodText",
            ProbeHealth.Error => "ErrorText",
            _ => "UnknownText",
        }];

        public Visibility EgressStatusVisibility(NetMapCard card) =>
            string.IsNullOrWhiteSpace(DashboardViewModel.EgressStatus(card)) ? Visibility.Collapsed : Visibility.Visible;

        private void Window_Closed(object sender, WindowEventArgs args)
        {
            if (args.Handled)
            {
                // A rejected close can leave the window visible; a tray close hides it.
                UpdateVisibility();
                return;
            }

            ViewModel.SetOverviewActive(false);
            ViewModel.SetUpdateStatusActive(false);
            UnsubscribeTypography();
            UnsubscribeWindow();
            ViewModel.Dispose();
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
        }

        private void SortByStatus_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.DashboardSortOrder = DashboardSortOrder.ByStatus;
        }
    }
}
