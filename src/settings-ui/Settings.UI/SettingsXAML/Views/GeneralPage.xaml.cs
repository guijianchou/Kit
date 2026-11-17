// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.ViewModels;
using ManagedCommon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Kit.Settings.UI.Views
{
    /// <summary>
    /// General Settings Page.
    /// </summary>
    public sealed partial class GeneralPage : NavigablePage, IRefreshablePage, IDisposable
    {
        private static DateTime OkToHideBackupAndRestoreMessageTime { get; set; }

        private readonly System.Collections.Generic.List<(Panel Panel, TransitionCollection Transitions)> _initialTransitions = new();
        private bool _backupStatusRefreshQueued;
        private bool _initialContentPresented;
        private bool _presentationQueued;
        private bool _waitingForInitialRender;
        private (double Width, double Height, double AiWidth, double AiHeight, double DiagnosticsY)? _initialLayout;

        /// <summary>
        /// Gets or sets view model.
        /// </summary>
        public GeneralViewModel ViewModel { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="GeneralPage"/> class.
        /// General Settings page constructor.
        /// </summary>
        public GeneralPage()
        {
            // Load string resources
            var loader = Helpers.ResourceLoaderInstance.ResourceLoader;
            var settingsUtils = SettingsUtils.Default;

            Action stateUpdatingAction = () =>
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    ViewModel.RefreshUpdatingState();
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
                loader);

            DataContext = ViewModel;
            InitializeComponent();

            // The first service configuration read runs off the dispatcher. Keep its
            // provisional default state out of the visible page while controls still
            // measure normally, then reveal the completed layout in one frame.
            SuppressInitialTransitions(GeneralSettingsSurface);
            Unloaded += OnInitialPresentationUnloaded;

            this.Loaded += async (s, e) =>
            {
                try
                {
                    ViewModel.OnPageLoaded();

                    if (!_backupStatusRefreshQueued)
                    {
                        _backupStatusRefreshQueued = true;
                        doRefreshBackupRestoreStatus(500);
                    }

                    SuppressInitialTransitions(GeneralSettingsSurface);
                    await ViewModel.AiServices.Initialization.ConfigureAwait(false);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (IsLoaded)
                        {
                            if (ViewModel.AiServices.CanToggle)
                            {
                                ApplyStoredApiKeys();
                            }

                            QueueInitialContentPresentation();
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError("Failed to initialize general settings.", ex);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (IsLoaded)
                        {
                            QueueInitialContentPresentation();
                        }
                    });
                }
            };
        }

        private void SuppressInitialTransitions(DependencyObject root)
        {
            if (_initialContentPresented)
            {
                return;
            }

            if (root is Panel panel && panel.ChildrenTransitions?.Count > 0)
            {
                _initialTransitions.Add((panel, panel.ChildrenTransitions));
                panel.ChildrenTransitions = null;
            }

            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                SuppressInitialTransitions(VisualTreeHelper.GetChild(root, index));
            }
        }

        private void QueueInitialContentPresentation()
        {
            if (_initialContentPresented || _presentationQueued || _waitingForInitialRender)
            {
                return;
            }

            _presentationQueued = true;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _presentationQueued = false;
                if (!IsLoaded)
                {
                    return;
                }

                // Enabling controls can change their template states and invalidate
                // measure. Apply it while hidden, before the initial render passes.
                Bindings.Update();
                GeneralSettingsSurface.IsEnabled = true;
                SuppressInitialTransitions(GeneralSettingsSurface);
                GeneralSettingsSurface.UpdateLayout();
                _initialLayout = null;
                _waitingForInitialRender = true;
                CompositionTarget.Rendering += OnInitialContentRendering;
            });
        }

        private void OnInitialContentRendering(object sender, object args)
        {
            if (!IsLoaded)
            {
                CancelInitialContentRendering();
                return;
            }

            // Template size triggers can invalidate measure after UpdateLayout.
            // Require matching layouts across actual render passes before exposing
            // the content; no timer or assumed device/render speed is involved.
            SuppressInitialTransitions(GeneralSettingsSurface);
            GeneralSettingsSurface.UpdateLayout();
            var content = (FrameworkElement)GeneralSettingsSurface.ModuleContent;
            var layout = (content.ActualWidth, content.ActualHeight,
                AiServicesGroup.ActualWidth, AiServicesGroup.ActualHeight,
                DiagnosticsGroup.TransformToVisual(content).TransformPoint(default).Y);
            if (_initialLayout == layout)
            {
                CancelInitialContentRendering();
                PresentInitialContent();
            }
            else
            {
                _initialLayout = layout;
            }
        }

        private void OnInitialPresentationUnloaded(object sender, RoutedEventArgs e) => CancelInitialContentRendering();

        private void CancelInitialContentRendering()
        {
            if (_waitingForInitialRender)
            {
                CompositionTarget.Rendering -= OnInitialContentRendering;
                _waitingForInitialRender = false;
            }

            _initialLayout = null;
        }

        private void PresentInitialContent()
        {
            _initialContentPresented = true;
            GeneralSettingsSurface.Opacity = 1;
            GeneralSettingsSurface.IsHitTestVisible = true;
            GeneralSettingsLoading.Visibility = Visibility.Collapsed;
            foreach (var (panel, transitions) in _initialTransitions)
            {
                panel.ChildrenTransitions = transitions;
            }

            _initialTransitions.Clear();
        }

        public void Dispose()
        {
            CancelInitialContentRendering();
            ViewModel.Dispose();
        }

        private void SettingsSection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string sectionName } && FindName(sectionName) is FrameworkElement section)
            {
                if (FocusManager.FindFirstFocusableElement(section) is Control control)
                {
                    control.Focus(FocusState.Programmatic);
                }

                section.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = true,
                    VerticalAlignmentRatio = 0,
                    VerticalOffset = 8,
                });
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
            Task.Run(() =>
            {
                if (delayMs > 0)
                {
                    Thread.Sleep(delayMs);
                }

                var settingsBackupAndRestoreUtils = SettingsBackupAndRestoreUtils.Instance;
                var results = settingsBackupAndRestoreUtils.DryRunBackup();
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

        private void Click_LanguageRestart(object sender, RoutedEventArgs e)
        {
            ViewModel.Restart();
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
