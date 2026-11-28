// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using WinUIEx;

namespace Kit.Settings.UI.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WinUI owns the native window lifetime. OnClosed cancels generation and disposes the report view.")]
public sealed partial class AuditPeriodReportWindow : WindowEx
{
    private readonly Window _owner;
    private readonly FrameworkElement _ownerRoot;
    private readonly AuditPeriodReportView _view;
    private readonly AccessibilitySettings _accessibility = new();
    private readonly Dictionary<WeeklyReportMode, WindowsStabilityReportDocument> _documents = new();
    private Func<WeeklyReportMode, bool, IProgress<WeeklyReportProgress>, CancellationToken, Task<WindowsStabilityReportDocument>> _load;
    private Func<WeeklyReportMode, CancellationToken, Task<WindowsStabilityReportDocument>> _readSaved;
    private CancellationTokenSource _generationCancellation;
    private bool _highContrastSubscribed;
    private int _request;
    private DateOnly? _reportDate;

    internal WindowsStabilityReportDocument LoadedDocument { get; private set; }
    internal bool IsLoading { get; private set; }
    internal bool IsClosed { get; private set; }

    public AuditPeriodReportWindow(Window owner)
    {
        _owner = owner;
        _ownerRoot = owner.Content as FrameworkElement;
        _view = new AuditPeriodReportView();
        Content = _view;
        Title = "Kit · AI Hub";
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetTitleBar(_view.TitleBarElement);
        UpdateCaptionButtons();
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            _view.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        SetOwner(WinRT.Interop.WindowNative.GetWindowHandle(this), -8, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        AppWindow.IsShownInSwitchers = false;
        var workArea = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = _ownerRoot?.XamlRoot?.RasterizationScale is > 0 ? _ownerRoot.XamlRoot.RasterizationScale : 1;
        int availableWidth = Math.Max(1, workArea.Width - 32);
        int availableHeight = Math.Max(1, workArea.Height - 32);
        int width = Math.Min((int)Math.Round(900 * scale), availableWidth);
        int height = Math.Min((int)Math.Round(780 * scale), availableHeight);
        MinWidth = Math.Min(480, availableWidth / scale);
        MinHeight = Math.Min(420, availableHeight / scale);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            workArea.X + ((workArea.Width - width) / 2), workArea.Y + ((workArea.Height - height) / 2), width, height));
        if (_ownerRoot != null)
        {
            _view.RequestedTheme = _ownerRoot.ActualTheme;
            _ownerRoot.ActualThemeChanged += OnOwnerThemeChanged;
        }
        _owner.Closed += OnOwnerClosed;
        _view.RefreshRequested += OnRefreshRequested;
        _view.ModeChanged += OnModeChanged;
        _view.CancelRequested += OnCancelRequested;
        _view.ActualThemeChanged += OnViewThemeChanged;
        _view.SizeChanged += OnViewSizeChanged;
        try
        {
            _accessibility.HighContrastChanged += OnHighContrastChanged;
            _highContrastSubscribed = true;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or NotSupportedException)
        {
            // Some desktop hosts expose the current setting without a notification
            // source. Activation and theme synchronization still read that setting.
        }

        Activated += OnActivated;
        Closed += OnClosed;
    }

    internal async Task ShowWeeklyReportAsync(
        Func<WeeklyReportMode, bool, IProgress<WeeklyReportProgress>, CancellationToken, Task<WindowsStabilityReportDocument>> load,
        DateOnly? reportDate = null,
        Func<WeeklyReportMode, CancellationToken, Task<WindowsStabilityReportDocument>> readSaved = null)
    {
        if (IsClosed)
        {
            return;
        }
        _load = load;
        _readSaved = readSaved;
        if (_reportDate != reportDate)
        {
            _reportDate = reportDate;
            _request++;
            _generationCancellation?.Cancel();
            _generationCancellation = null;
            _documents.Clear();
            LoadedDocument = null;
            IsLoading = false;
            _view.SelectMode(_view.SelectedMode);
        }
        Activate();

        // Opening never starts a Full collection or model request. Its button is explicit.
        if (_view.SelectedMode == WeeklyReportMode.Fast)
        {
            await ReloadAsync(forceRefresh: false);
        }
        else
        {
            await ShowSelectedCachedAsync();
        }
    }

    private async Task ReloadAsync(bool forceRefresh)
    {
        if (IsClosed || _load == null)
        {
            return;
        }
        int request = ++_request;
        _generationCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _generationCancellation = cancellation;
        IsLoading = true;
        var mode = _view.SelectedMode;
        var load = _load;
        bool acceptsServiceProgress = true;
        _view.SetLoading();
        try
        {
            await WaitForViewLoadedAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            var progress = new ReportProgress(value => DispatcherQueue.TryEnqueue(() =>
            {
                if (acceptsServiceProgress && !IsClosed && request == _request && !cancellation.IsCancellationRequested)
                {
                    _view.SetProgress(value);
                }
            }));
            var document = await load(mode, forceRefresh, progress, cancellation.Token);
            acceptsServiceProgress = false;
            cancellation.Token.ThrowIfCancellationRequested();
            if (IsClosed || request != _request)
            {
                return;
            }

            if (document.Data.WeeklySource is { } source && source.Mode != mode)
            {
                throw new InvalidOperationException("The report source does not match the selected mode.");
            }

            string renderingMessage = Kit.Settings.UI.Services.RuntimeLocalization.EffectiveLanguage == "zh-CN"
                ? "正在生成报告图表…" : "Rendering report charts…";
            _view.SetProgress(new WeeklyReportProgress("Rendering", renderingMessage));
            await _view.SetDocumentAsync(document, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (IsClosed || request != _request)
            {
                return;
            }
            LoadedDocument = document;
            _documents[mode] = document;
            if (document.RefreshFailed)
            {
                _view.SetFailure();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!IsClosed && request == _request)
            {
                _view.SetCancelled();
            }
        }
        catch (Exception exception)
        {
            if (!IsClosed && request == _request)
            {
                ManagedCommon.Logger.LogWarning($"Period report generation failed ({exception.GetType().Name}).");
                _view.SetFailure(mode == WeeklyReportMode.Fast && LoadedDocument == null
                    ? (Kit.Settings.UI.Services.RuntimeLocalization.EffectiveLanguage == "zh-CN"
                        ? "请先完成 7 天扫描，或选择 Full 后生成周报。"
                        : "Run a 7-day scan first, or select Full and generate a report.")
                    : null);
            }
        }
        finally
        {
            acceptsServiceProgress = false;
            if (request == _request)
            {
                IsLoading = false;
                _generationCancellation = null;
            }
        }
    }

    private sealed partial class ReportProgress(Action<WeeklyReportProgress> report) : IProgress<WeeklyReportProgress>
    {
        public void Report(WeeklyReportProgress value) => report(value);
    }

    private async void OnRefreshRequested(object sender, EventArgs args) => await ReloadAsync(forceRefresh: true);
    private void OnCancelRequested(object sender, EventArgs args)
    {
        if (_generationCancellation is not null && IsLoading)
        {
            _view.SetCancelling();
            _generationCancellation.Cancel();
        }
    }

    private async void OnModeChanged(object sender, EventArgs args) => await ShowSelectedCachedAsync();

    private async Task ShowSelectedCachedAsync()
    {
        if (IsClosed)
        {
            return;
        }
        int request = ++_request;
        _generationCancellation?.Cancel();
        _generationCancellation = null;
        IsLoading = false;
        var mode = _view.SelectedMode;
        var readSaved = _readSaved;
        LoadedDocument = null;
        _view.SelectMode(mode);
        _documents.TryGetValue(mode, out var saved);
        if (saved == null && readSaved == null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _generationCancellation = cancellation;
        IsLoading = true;
        _view.SetLoading();
        try
        {
            await WaitForViewLoadedAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _view.SetProgress(new WeeklyReportProgress("cache", string.Empty));

            // This delegate is a storage-only read. Never call the generation loader
            // for Full here: a cache miss must leave the explicit Generate button.
            if (readSaved != null)
            {
                saved = await readSaved(mode, cancellation.Token) ?? saved;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (IsClosed || request != _request)
            {
                return;
            }
            if (saved == null)
            {
                _view.SelectMode(mode);
                return;
            }
            if (saved.Data.WeeklySource?.Mode != mode)
            {
                throw new InvalidOperationException("The saved report source does not match the selected mode.");
            }
            await _view.SetDocumentAsync(saved, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsClosed && request == _request)
            {
                LoadedDocument = saved;
                _documents[mode] = saved;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!IsClosed && request == _request)
            {
                _view.SetCancelled();
            }
        }
        catch (Exception exception)
        {
            if (!IsClosed && request == _request)
            {
                ManagedCommon.Logger.LogWarning($"Saved weekly report display failed ({exception.GetType().Name}).");
                _view.SetFailure();
            }
        }
        finally
        {
            if (request == _request)
            {
                IsLoading = false;
                _generationCancellation = null;
            }
        }
    }

    private async Task WaitForViewLoadedAsync(CancellationToken cancellationToken)
    {
        if (_view.IsLoaded)
        {
            return;
        }
        var loaded = new TaskCompletionSource<bool>();
        RoutedEventHandler onLoaded = (_, _) => loaded.TrySetResult(true);
        _view.Loaded += onLoaded;
        using var registration = cancellationToken.Register(() => loaded.TrySetCanceled(cancellationToken));
        try
        {
            if (!_view.IsLoaded)
            {
                await loaded.Task;
            }
        }
        finally
        {
            _view.Loaded -= onLoaded;
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args) => UpdateCaptionButtons();
    private void OnOwnerThemeChanged(FrameworkElement sender, object args) => _view.RequestedTheme = sender.ActualTheme;

    private void OnViewThemeChanged(FrameworkElement sender, object args)
    {
        UpdateCaptionButtons();
        _view.RefreshTheme();
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs args)
    {
        double scale = _view.XamlRoot?.RasterizationScale is > 0 ? _view.XamlRoot.RasterizationScale : 1;
        _view.SetTitleBarInsets(AppWindow.TitleBar.LeftInset / scale, AppWindow.TitleBar.RightInset / scale);
    }

    private void OnHighContrastChanged(AccessibilitySettings sender, object args)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsClosed)
            {
                UpdateCaptionButtons();
            }
        });

    private void UpdateCaptionButtons()
    {
        var titleBar = AppWindow.TitleBar;
        if (_accessibility.HighContrast)
        {
            titleBar.ButtonBackgroundColor = null;
            titleBar.ButtonForegroundColor = null;
            titleBar.ButtonHoverBackgroundColor = null;
            titleBar.ButtonHoverForegroundColor = null;
            titleBar.ButtonPressedBackgroundColor = null;
            titleBar.ButtonPressedForegroundColor = null;
            titleBar.ButtonInactiveBackgroundColor = null;
            titleBar.ButtonInactiveForegroundColor = null;
            return;
        }

        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bool dark = _view.ActualTheme == ElementTheme.Dark;
        var foreground = dark ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(0xFF, 0x17, 0x22, 0x35);
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x0F, 0x17, 0x22, 0x35);
        titleBar.ButtonPressedForegroundColor = dark ? Windows.UI.Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0xC5, 0x17, 0x22, 0x35);
        titleBar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x18, 0x17, 0x22, 0x35);
        titleBar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x80, 0x17, 0x22, 0x35);
    }

    private void OnOwnerClosed(object sender, WindowEventArgs args)
    {
        if (!args.Handled && !IsClosed)
        {
            Close();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        IsClosed = true;
        IsLoading = false;
        _request++;
        _generationCancellation?.Cancel();
        _generationCancellation = null;
        _view.Dispose();
        _owner.Closed -= OnOwnerClosed;
        if (_ownerRoot != null)
        {
            _ownerRoot.ActualThemeChanged -= OnOwnerThemeChanged;
        }
        _view.RefreshRequested -= OnRefreshRequested;
        _view.ModeChanged -= OnModeChanged;
        _view.CancelRequested -= OnCancelRequested;
        _view.ActualThemeChanged -= OnViewThemeChanged;
        _view.SizeChanged -= OnViewSizeChanged;
        if (_highContrastSubscribed)
        {
            _accessibility.HighContrastChanged -= OnHighContrastChanged;
        }

        Activated -= OnActivated;
        SystemBackdrop = null;
        _load = null;
        _readSaved = null;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetOwner(nint window, int index, nint owner);
}
