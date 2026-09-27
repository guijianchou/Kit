// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using System.Threading.Tasks;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Library;
using Kit.Settings.UI.Library.Helpers;
using Kit.Settings.UI.Views;
using ManagedCommon;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Data.Json;
using WinRT.Interop;
using WinUIEx;

namespace Kit.Settings.UI
{
    public sealed partial class MainWindow : WindowEx
    {
        private bool _closePending;
        private bool _closeApproved;
        private DispatcherTimer _placementDebounceTimer;

        internal Func<Task<bool>> FlushPendingSettingsAsync { get; set; }

        internal Func<bool> KeepAliveForBackgroundWork { get; set; }

        public MainWindow(bool createHidden = false)
        {
            this.Activated += Window_Activated_SetIcon;

            App.ThemeService.ThemeChanged += OnThemeChanged;
            App.ThemeService.ApplyTheme();

            this.ExtendsContentIntoTitleBar = true;

            ShellPage.SetElevationStatus(App.IsElevated);
            ShellPage.SetIsUserAnAdmin(App.IsUserAnAdmin);

            var hWnd = WindowNative.GetWindowHandle(this);
            var placement = WindowHelper.DeserializePlacementOrDefault(hWnd);
            if (createHidden)
            {
                placement.ShowCmd = NativeMethods.SW_HIDE;
            }

            // Restore the last known placement on the first activation
            this.Activated += Window_Activated;
            NativeMethods.SetWindowPlacement(hWnd, ref placement);
            this.AppWindow.Changed += OnAppWindowChanged;

            var loader = ResourceLoaderInstance.ResourceLoader;
            Title = App.IsElevated ? loader.GetString("SettingsWindow_AdminTitle") : loader.GetString("SettingsWindow_Title");

            // send IPC Message
            ShellPage.SetDefaultSndMessageCallback(msg =>
            {
                // IPC Manager is null when launching runner directly
                App.GetTwoWayIPCManager()?.Send(msg);
            });

            // send IPC Message
            ShellPage.SetRestartAdminSndMessageCallback(msg =>
            {
                var ipcManager = App.GetTwoWayIPCManager();
                if (ipcManager != null)
                {
                    ipcManager.Send(msg);

                    // Send queues asynchronously. Keep the IPC process alive until
                    // the runner accepts the request and closes the old Settings instance.
                    return;
                }
                else
                {
                    var processPath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(processPath))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = processPath,
                            UseShellExecute = true,
                        });
                    }
                }

                Environment.Exit(0); // Standalone mode has no runner to own shutdown.
            });

            // send IPC Message
            ShellPage.SetCheckForUpdatesMessageCallback(msg =>
            {
                App.GetTwoWayIPCManager()?.Send(msg);
            });

            // open main window
            ShellPage.SetOpenMainWindowCallback(type =>
            {
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
                     App.OpenSettingsWindow(type));
            });

            // open main window
            ShellPage.SetUpdatingGeneralSettingsCallback((ModuleType moduleType, bool isEnabled) =>
            {
                SettingsRepository<GeneralSettings> repository = SettingsRepository<GeneralSettings>.GetInstance(SettingsUtils.Default);
                GeneralSettings generalSettingsConfig = repository.SettingsConfig;
                bool needToUpdate = ModuleHelper.GetIsModuleEnabled(generalSettingsConfig, moduleType) != isEnabled;

                if (needToUpdate)
                {
                    ModuleHelper.SetIsModuleEnabled(generalSettingsConfig, moduleType, isEnabled);
                    var outgoing = new OutGoingGeneralSettings(generalSettingsConfig);

                    // Save settings to file
                    SettingsUtils.Default.SaveSettings(generalSettingsConfig.ToJsonString());

                    // Send IPC message asynchronously to avoid blocking UI and potential recursive calls
                    Task.Run(() =>
                    {
                        ShellPage.SendDefaultIPCMessage(outgoing.ToString());
                    });

                    ShellPage.ShellHandler?.SignalGeneralDataUpdate();
                }

                return needToUpdate;
            });

            this.InitializeComponent();
            SetTitleBar();

            // receive IPC Message
            App.IPCMessageReceivedCallback = (string msg) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (App.GetSettingsWindow() != this || ShellPage.ShellHandler?.IPCResponseHandleList == null)
                    {
                        return;
                    }

                    var success = JsonObject.TryParse(msg, out JsonObject json);
                    if (success)
                    {
                        foreach (Action<JsonObject> handle in ShellPage.ShellHandler.IPCResponseHandleList.ToArray())
                        {
                            handle(json);
                        }
                    }
                    else
                    {
                        Logger.LogError("Failed to parse JSON from IPC message.");
                    }
                });
            };
        }

        private void SetTitleBar()
        {
            // We need to assign the window here so it can configure the custom title bar area correctly.
            shellPage.TitleBar.Window = this;
            this.ExtendsContentIntoTitleBar = true;
            WindowHelpers.ForceTopBorder1PixelInsetOnWindows10(WindowNative.GetWindowHandle(this));
        }

        public void NavigateToSection(Type type)
        {
            ShellPage.Navigate(type);
        }

        public void CloseHiddenWindow()
        {
            var hWnd = WindowNative.GetWindowHandle(this);
            if (!NativeMethods.IsWindowVisible(hWnd))
            {
                Close();
            }
        }

        private const string KitTrayIconWindowClass = "KitTrayIconWindow";

        private void Window_Closed(object sender, WindowEventArgs args)
        {
            if (!_closeApproved && FlushPendingSettingsAsync is { } flush)
            {
                args.Handled = true;
                if (!_closePending)
                {
                    _ = FlushSettingsAndCloseAsync(flush);
                }

                return;
            }

            _placementDebounceTimer?.Stop();
            try
            {
                this.AppWindow.Changed -= OnAppWindowChanged;
            }
            catch (Exception)
            {
            }

            var hWnd = WindowNative.GetWindowHandle(this);
            WindowHelper.SerializePlacement(hWnd);

            if (!App.IsSecondaryWindowOpen() && KeepAliveForBackgroundWork?.Invoke() != true)
            {
                App.ThemeService.ThemeChanged -= OnThemeChanged;
                App.IPCMessageReceivedCallback = null;
                shellPage.Dispose();
                App.ClearSettingsWindow();
            }
            else
            {
                args.Handled = true;
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_HIDE);
            }
        }

        private async Task FlushSettingsAndCloseAsync(Func<Task<bool>> flush)
        {
            _closePending = true;
            try
            {
                if (await flush() && DispatcherQueue.TryEnqueue(() =>
                {
                    _closeApproved = true;
                    try
                    {
                        Close();
                    }
                    finally
                    {
                        _closeApproved = false;
                        _closePending = false;
                    }
                }))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to save settings before closing: {ex.GetType().Name}");
            }

            _closePending = false;
        }

        private void Window_Activated_SetIcon(object sender, WindowActivatedEventArgs args)
        {
            // Set window icon
            this.SetIcon("Assets\\Settings\\icon.ico");
        }

        private void Window_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                this.Activated -= Window_Activated;
                var hWnd = WindowNative.GetWindowHandle(this);
                var placement = WindowHelper.DeserializePlacementOrDefault(hWnd);
                NativeMethods.SetWindowPlacement(hWnd, ref placement);
            }
        }

        private void OnThemeChanged(object sender, ElementTheme theme)
        {
            WindowHelper.SetTheme(this, theme);
        }

        internal void EnsurePageIsSelected()
        {
            ShellPage.EnsurePageIsSelected();
        }

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidPositionChange || args.DidSizeChange)
            {
                var hWnd = WindowNative.GetWindowHandle(this);
                if (NativeMethods.IsIconic(hWnd))
                {
                    return;
                }

                if (_placementDebounceTimer == null)
                {
                    _placementDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    _placementDebounceTimer.Tick += (s, e) =>
                    {
                        _placementDebounceTimer.Stop();
                        var currentHWnd = WindowNative.GetWindowHandle(this);
                        if (!NativeMethods.IsIconic(currentHWnd))
                        {
                            WindowHelper.SerializePlacement(currentHWnd);
                        }
                    };
                }

                _placementDebounceTimer.Stop();
                _placementDebounceTimer.Start();
            }
        }
    }
}
