// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Windows.Input;

using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.Library.Interfaces;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.Views;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Kit.Settings.UI.ViewModels
{
    public partial class ShellViewModel : Observable
    {
        private bool isBackEnabled;
        private NavigationView navigationView;
        private NavigationViewItem selected;
        private NavigationViewItem expanding;
        private ICommand itemInvokedCommand;
        private NavigationViewItem[] _fullListOfNavViewItems;
        private NavigationViewItem[] _moduleNavViewItems;
        private GeneralSettings _generalSettingsConfig;

        public bool IsBackEnabled
        {
            get => isBackEnabled;
            set => Set(ref isBackEnabled, value);
        }

        public NavigationViewItem Selected
        {
            get => selected;
            set => Set(ref selected, value);
        }

        public NavigationViewItem Expanding
        {
            get { return expanding; }
            set { Set(ref expanding, value); }
        }

        public NavigationViewItem[] NavItems
        {
            get { return _moduleNavViewItems; }
        }

        /// <summary>
        /// Audit progress shown in the navigation pane. The audit itself runs on the AI Hub
        /// page, so both sides share this observable holder rather than the pane reaching
        /// into a page that may not be loaded.
        /// </summary>
        public Helpers.AuditProgressState AuditProgress => Helpers.AuditProgressState.Current;

        public ICommand ItemInvokedCommand => itemInvokedCommand ?? (itemInvokedCommand = new RelayCommand<NavigationViewItemInvokedEventArgs>(OnItemInvoked));

        public ShellViewModel(ISettingsRepository<GeneralSettings> settingsRepository)
        {
            _generalSettingsConfig = settingsRepository.SettingsConfig;
        }

        public void Initialize(Frame frame, NavigationView navigationView)
        {
            this.navigationView = navigationView;
            NavigationService.Frame = frame;
            NavigationService.NavigationFailed += Frame_NavigationFailed;
            NavigationService.Navigated += Frame_Navigated;
            this.navigationView.BackRequested += OnBackRequested;
            var topLevelItems = navigationView.MenuItems.OfType<NavigationViewItem>();
            _moduleNavViewItems = topLevelItems.SelectMany(menuItem => menuItem.MenuItems.OfType<NavigationViewItem>()).ToArray();
            var footerItems = navigationView.FooterMenuItems.OfType<NavigationViewItem>();
            _fullListOfNavViewItems = topLevelItems.Union(_moduleNavViewItems).Union(footerItems).ToArray();
        }

        private void OnItemInvoked(NavigationViewItemInvokedEventArgs args)
        {
            if (args.InvokedItemContainer is NavigationViewItem item)
            {
                var pageType = item.GetValue(NavHelper.NavigateToProperty) as Type;
                if (pageType != null)
                {
                    NavigationService.Navigate(pageType);
                }
            }
        }

        private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            NavigationService.GoBack();
        }

        private void Frame_NavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw e.Exception;
        }

        private void Frame_Navigated(object sender, NavigationEventArgs e)
        {
            IsBackEnabled = NavigationService.CanGoBack;
            var selectedPageType = e.SourcePageType;
            Selected = _fullListOfNavViewItems.FirstOrDefault(menuItem => IsMenuItemForPageType(menuItem, selectedPageType));
        }

        private static bool IsMenuItemForPageType(NavigationViewItem menuItem, Type sourcePageType)
        {
            var pageType = menuItem.GetValue(NavHelper.NavigateToProperty) as Type;
            return pageType == sourcePageType;
        }
    }
}
