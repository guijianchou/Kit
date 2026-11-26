// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading.Tasks;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using ManagedCommon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Kit.Settings.UI.Views
{
    /// <summary>
    /// General Settings Page.
    /// </summary>
    public sealed partial class GeneralPage : NavigablePage, IRefreshablePage, IDisposable, ILiveLocalizable
    {
        private static DateTime OkToHideBackupAndRestoreMessageTime { get; set; }

        private bool _backupStatusRefreshQueued;
        private bool _aiSettingsPresented;
        private bool _aiPresentationQueued;
        private bool _waitingForAiRender;
        private string _pendingSettingsSection;

        /// <summary>
        /// Gets or sets view model.
        /// </summary>
        public GeneralViewModel ViewModel { get; set; }

        public string UnsavedEndpointsTitle { get; private set; }

        public string UnsavedEndpointsMessage { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="GeneralPage"/> class.
        /// General Settings page constructor.
        /// </summary>
        public GeneralPage()
        {
            // Load string resources
            var loader = Helpers.ResourceLoaderInstance.ResourceLoader;
            UnsavedEndpointsTitle = loader.GetString("AiHub_UnsavedEndpointsCard/Header");
            UnsavedEndpointsMessage = loader.GetString("AiHub_UnsavedEndpointsCard/Description");
            var settingsUtils = SettingsUtils.Default;

            Action stateUpdatingAction = () =>
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    ViewModel.RefreshUpdatingState();
                    ShellNavigationBadges.Current.IsKitUpdateAvailable = ViewModel.IsUpdateAvailable;
                });
            };

            Action hideBackupAndRestoreMessageArea = () =>
            {
                this.DispatcherQueue.TryEnqueue(async () =>
                {
                    const int messageShowTimeIs = 10000;

                    // in order to keep the message for about 5 seconds after the last call
                    // and not need any lock/thread-synch, use an OK-To-Hide time, and wait just a little longer than that.
                    OkToHideBackupAndRestoreMessageTime = DateTime.UtcNow.AddMilliseconds(messageShowTimeIs - 16);
                    await System.Threading.Tasks.Task.Delay(messageShowTimeIs);
                    if (DateTime.UtcNow > OkToHideBackupAndRestoreMessageTime)
                    {
                        ViewModel.HideBackupAndRestoreMessageArea();
                    }
                });
            };

            var doRefreshBackupRestoreStatus = new Action<int>(RefreshBackupRestoreStatus);

            ViewModel = new GeneralViewModel(
                SettingsRepository<GeneralSettings>.GetInstance(settingsUtils),
                loader.GetString("GeneralSettings_RunningAsAdminText"),
                loader.GetString("GeneralSettings_RunningAsUserText"),
                ShellPage.IsElevated,
                ShellPage.IsUserAnAdmin,
                ShellPage.SendDefaultIPCMessage,
                ShellPage.SendRestartAdminIPCMessage,
                ShellPage.SendCheckForUpdatesIPCMessage,
                string.Empty,
                stateUpdatingAction,
                hideBackupAndRestoreMessageArea,
                doRefreshBackupRestoreStatus,
                PickSingleFolderDialog,
                loader,
                appliedUiFontFamily: TypographyResources.AppliedFontFamily,
                appliedUiFontSize: TypographyResources.AppliedFontSize);

            DataContext = ViewModel;
            InitializeComponent();

            // Appearance, startup and update settings are usable on the first frame.
            // Only the AI section waits for its asynchronous configuration read.
            Unloaded += OnPageUnloaded;
            Loaded += OnSettingsPageLoaded;
        }

        private async void OnSettingsPageLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                ViewModel.OnPageLoaded();
                ShellNavigationBadges.Current.IsKitUpdateAvailable = ViewModel.IsUpdateAvailable;
                AiServiceSettingsLoadingRing.IsActive = !_aiSettingsPresented;

                if (!_backupStatusRefreshQueued)
                {
                    _backupStatusRefreshQueued = true;
                    RefreshBackupRestoreStatus(500);
                }

                await ViewModel.AiServices.Initialization.ConfigureAwait(false);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!IsLoaded)
                    {
                        return;
                    }

                    if (ViewModel.AiServices.CanToggle)
                    {
                        ApplyStoredApiKeys();
                    }

                    QueueAiSettingsPresentation();
                });
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to initialize AI service settings.", ex);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (IsLoaded)
                    {
                        // Configuration failures still expose the service's retry action.
                        QueueAiSettingsPresentation();
                    }
                });
            }
        }

        private void QueueAiSettingsPresentation()
        {
            if (_aiSettingsPresented || _aiPresentationQueued || _waitingForAiRender)
            {
                return;
            }

            _aiPresentationQueued = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _aiPresentationQueued = false;
                if (!IsLoaded)
                {
                    return;
                }

                AiServiceSettingsSurface.IsEnabled = true;
                _waitingForAiRender = true;
                CompositionTarget.Rendering += OnAiSettingsRendering;
            });
        }

        private void OnAiSettingsRendering(object sender, object args)
        {
            CancelAiSettingsRendering();
            if (!IsLoaded)
            {
                return;
            }

            // The binding changes and enabled state share the next render pass;
            // never force repeated whole-page measure passes from Rendering.
            _aiSettingsPresented = true;
            AiServiceSettingsSurface.Opacity = 1;
            AiServiceSettingsSurface.IsHitTestVisible = true;
            AiServiceSettingsLoadingRing.IsActive = false;
            AiServiceSettingsLoading.Visibility = Visibility.Collapsed;
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            _pendingSettingsSection = null;
            AiServiceSettingsLoadingRing.IsActive = false;
            CancelAiSettingsRendering();
        }

        private void CancelAiSettingsRendering()
        {
            if (_waitingForAiRender)
            {
                CompositionTarget.Rendering -= OnAiSettingsRendering;
                _waitingForAiRender = false;
            }
        }

        public void Dispose()
        {
            CancelAiSettingsRendering();
            ViewModel.Dispose();
        }

        private void SettingsSection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem { Tag: string sectionName })
            {
                _pendingSettingsSection = sectionName;
            }
        }

        private void SettingsSectionMenu_Opening(object sender, object e)
        {
            if (sender is not MenuFlyout menu)
            {
                return;
            }

            foreach (var item in menu.Items)
            {
                if (item is MenuFlyoutItem { Tag: string sectionName } && FindName(sectionName) is Controls.SettingsGroup section)
                {
                    AutomationProperties.SetName(item, section.Header);
                }
            }
        }

        private void SettingsSectionMenu_Closed(object sender, object e)
        {
            var sectionName = _pendingSettingsSection;
            _pendingSettingsSection = null;
            if (sectionName == null)
            {
                return;
            }

            // The flyout restores focus to its owner when it closes. Move focus
            // afterwards, then scroll the section heading into view so the focus
            // change cannot replace the requested section position.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!IsLoaded || FindName(sectionName) is not FrameworkElement section)
                {
                    return;
                }

                Control focusTarget = sectionName switch
                {
                    nameof(AppearanceAndBehaviorGroup) => ThemeSelector,
                    nameof(StartupAndPermissionsGroup) => StartupToggle,
                    nameof(AiServicesGroup) => AiServiceToggle,
                    nameof(DiagnosticsGroup) => DiagnosticsToggle,
                    nameof(BackupAndRestoreGroup) => BackupButton,
                    nameof(VersionAndUpdateGroup) => CheckForUpdatesButton,
                    _ => null,
                };

                if (focusTarget?.Focus(FocusState.Keyboard) != true && FocusManager.FindFirstFocusableElement(section) is Control fallback)
                {
                    fallback.Focus(FocusState.Keyboard);
                }

                section.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = false,
                    VerticalAlignmentRatio = 0,
                    VerticalOffset = 8,
                    TargetRect = new Windows.Foundation.Rect(0, 0, section.ActualWidth, Math.Min(section.ActualHeight, 96)),
                });
            });
        }

        private void SettingEditorCard_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is ContentControl { Content: FrameworkElement editor } card && e.NewSize.Width > 0)
            {
                // Preserve the desktop field width, but allow nested endpoint
                // editors to fit the actual card width in narrow windows.
                editor.MaxWidth = Math.Min(280, Math.Max(0, e.NewSize.Width - card.Padding.Left - card.Padding.Right));
            }
        }

        private void OpenColorsSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Helpers.StartProcessHelper.Start(Helpers.StartProcessHelper.ColorsSettings);
            }
            catch (Exception ex)
            {
                Logger.LogError("Error while trying to open the system color settings", ex);
            }
        }

        private void RefreshBackupRestoreStatus(int delayMs = 0)
        {
            _ = Task.Run(async () =>
            {
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }

                var settingsBackupAndRestoreUtils = SettingsBackupAndRestoreUtils.Instance;
                _ = settingsBackupAndRestoreUtils.DryRunBackup();
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    ViewModel.NotifyAllBackupAndRestoreProperties();
                });
            });
        }

        private void UpdateBackupAndRestoreStatusText(object sender, RoutedEventArgs e)
        {
            RefreshBackupRestoreStatus();
        }

        private async Task<string> PickSingleFolderDialog()
        {
            // This function was changed to use the shell32 API to open folder dialog
            // as the old one (PickSingleFolderAsync) can't work when the process is elevated
            // TODO: go back PickSingleFolderAsync when it's fixed
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.GetSettingsWindow());
            string r = await Task.FromResult<string>(ShellGetFolder.GetFolderDialog(hwnd));
            return r;
        }

        public void RefreshLocalization()
        {
            var loader = ResourceLoaderInstance.ResourceLoader;
            UnsavedEndpointsTitle = loader.GetString("AiHub_UnsavedEndpointsCard/Header");
            UnsavedEndpointsMessage = loader.GetString("AiHub_UnsavedEndpointsCard/Description");
            AiUnsavedEndpointsCard.Title = UnsavedEndpointsTitle;
            AiUnsavedEndpointsCard.Message = UnsavedEndpointsMessage;
        }

        private void FontSizeSelector_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (!double.IsFinite(args.NewValue) || args.NewValue != Math.Truncate(args.NewValue))
            {
                sender.Value = ViewModel.UiFontSize;
            }
        }

        public void RefreshEnabledState()
        {
            ViewModel.RefreshSettingsOnExternalChange();
        }

        /// <summary>
        /// Pushes the stored API keys into the password boxes.
        /// </summary>
        /// <remarks>
        /// PasswordBox.Password cannot be two-way bound safely, so the value is applied on
        /// load and pushed back on change instead.
        /// </remarks>
        private void ApplyStoredApiKeys()
        {
            if (ViewModel?.AiServices?.MainEndpoint is { } main)
            {
                AiMainApiKeyBox.Password = main.ApiKey ?? string.Empty;
            }

            if (ViewModel?.AiServices?.FallbackEndpoint is { } fallback)
            {
                AiFallbackApiKeyBox.Password = fallback.ApiKey ?? string.Empty;
            }
        }

        private void AiMainApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox box && ViewModel?.AiServices?.MainEndpoint is { } endpoint)
            {
                endpoint.ApiKey = box.Password;
            }
        }

        private void AiFallbackApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox box && ViewModel?.AiServices?.FallbackEndpoint is { } endpoint)
            {
                endpoint.ApiKey = box.Password;
            }
        }
    }
}
