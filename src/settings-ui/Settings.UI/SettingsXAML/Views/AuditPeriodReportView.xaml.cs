// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Storage;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Kit.Settings.UI.Views;

public sealed partial class AuditPeriodReportView : UserControl, ILiveLocalizable, IDisposable
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly TaskProgressClock _generationClock;
    private WindowsStabilityReportDocument _document;
    private bool _loading;
    private bool _failed;
    private bool _fit = true;
    private int _pixelWidth;
    private int _renderRequest;
    private int _loadingRevision;
    private bool _disposed;
    private string _uiLanguage;
    private WeeklyReportMode _selectedMode;
    private WeeklyReportProgress _progress;
    private string _failureDetail;
    private bool _cancelled;
    private bool _cancelling;
    private string _aiActivity;
    private bool _initialized;
    private Task _renderTail = Task.CompletedTask;

    internal string DisplayedPngPath { get; private set; }
    internal WindowsStabilityReportDocument DisplayedDocument => _document;
    internal bool IsRendering => !_renderTail.IsCompleted;
    internal Task ThemeRenderTask { get; private set; } = Task.CompletedTask;
    internal Task LanguageRenderTask => ThemeRenderTask;
    internal FrameworkElement TitleBarElement => ReportTitleBar;
    internal WeeklyReportMode SelectedMode => _selectedMode;
    internal long GenerationElapsedMilliseconds => _generationClock.ElapsedMilliseconds;

    private static string ReportLanguage => RuntimeLocalization.EffectiveLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";

    internal event EventHandler RefreshRequested;
    internal event EventHandler ModeChanged;
    internal event EventHandler CancelRequested;

    public AuditPeriodReportView()
    {
        _lifetimeToken = _lifetimeCancellation.Token;
        InitializeComponent();
        _generationClock = new TaskProgressClock(DispatcherQueue, RefreshElapsed);
        _initialized = true;
        AppearanceService.RegisterRoot(this);
        RefreshLocalization();
    }

    internal void SetLoading()
    {
        _loadingRevision++;
        _renderRequest++;
        _loading = true;
        _failed = false;
        _cancelled = false;
        _cancelling = false;
        _failureDetail = null;
        _progress = null;
        _aiActivity = null;
        _generationClock.Start();
        RefreshLocalization();
    }

    internal void SelectMode(WeeklyReportMode mode)
    {
        _selectedMode = mode;
        _loadingRevision++;
        _renderRequest++;
        _loading = false;
        _failed = false;
        _cancelled = false;
        _cancelling = false;
        _failureDetail = null;
        _progress = null;
        _aiActivity = null;
        _generationClock.Reset();
        _document = null;
        DisplayedPngPath = null;
        _pixelWidth = 0;
        ReportImage.Source = null;
        ModeSelector.SelectedIndex = mode == WeeklyReportMode.Full ? 1 : 0;
        RefreshLocalization();
    }

    internal void SetProgress(WeeklyReportProgress progress)
    {
        if (_loading)
        {
            _progress = progress;
            if (progress.Stage.Equals("ai", StringComparison.OrdinalIgnoreCase))
            {
                UpdateAiActivity(progress.Message);
            }
            RefreshLocalization();
        }
    }

    internal void SetCancelled()
    {
        _loadingRevision++;
        _renderRequest++;
        _loading = false;
        _failed = false;
        _cancelled = true;
        _cancelling = false;
        _progress = null;
        _generationClock.Stop();
        RefreshLocalization();
    }

    internal void SetCancelling()
    {
        if (_loading)
        {
            _cancelling = true;
            RefreshLocalization();
        }
    }

    internal async Task SetDocumentAsync(WindowsStabilityReportDocument document, CancellationToken cancellationToken)
    {
        if (document.Monthly)
        {
            throw new ArgumentException("The AI Hub report window supports weekly reports only.", nameof(document));
        }

        if (document.Data.WeeklySource is { } source && source.Mode != _selectedMode)
        {
            throw new ArgumentException("The report does not belong to the selected mode.", nameof(document));
        }

        Task previous = _renderTail;
        int loadingRevision = _loadingRevision;
        var completion = new TaskCompletionSource<bool>();
        _renderTail = completion.Task;
        try
        {
            await previous;
            cancellationToken.ThrowIfCancellationRequested();
            if (loadingRevision != _loadingRevision)
            {
                return;
            }
            await RenderDocumentAsync(document, cancellationToken);
        }
        finally
        {
            completion.TrySetResult(true);
        }
    }

    private async Task RenderDocumentAsync(WindowsStabilityReportDocument document, CancellationToken cancellationToken)
    {
        int request = ++_renderRequest;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lifetimeToken.ThrowIfCancellationRequested();
            bool dark = ActualTheme == ElementTheme.Dark;
            string language = ReportLanguage;
            var localized = await WindowsStabilityReportStorage.LocalizeAsync(document, language, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (request != _renderRequest)
            {
                return;
            }
            string pngPath = Path.Combine(Path.GetDirectoryName(localized.PngPath),
                $"{Path.GetFileNameWithoutExtension(localized.PngPath)}-{(dark ? "dark" : "light")}-v{WindowsStabilityDashboardRenderer.RenderRevision}.png");
            var bitmap = await LoadBitmapAsync(localized, pngPath, dark, language == "en-US", request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _lifetimeToken.ThrowIfCancellationRequested();
            if (request != _renderRequest)
            {
                return;
            }

            // A theme or language change during rendering cannot publish a stale variant.
            // Both translations share this saved version and its exact event evidence.
            if (dark != (ActualTheme == ElementTheme.Dark) || language != ReportLanguage)
            {
                continue;
            }
            _document = localized;
            DisplayedPngPath = pngPath;
            _pixelWidth = bitmap.PixelWidth;
            ReportImage.Source = bitmap;
            _loading = false;
            _failed = document.RefreshFailed;
            _cancelled = false;
            _cancelling = false;
            _failureDetail = null;
            _progress = null;
            _generationClock.Stop();
            UpdateImageWidth();
            RefreshLocalization();
            return;
        }
    }

    private async Task<BitmapImage> LoadBitmapAsync(WindowsStabilityReportDocument document, string pngPath, bool dark, bool english, int request, CancellationToken cancellationToken)
    {
        byte[] bytes = null;
        if (File.Exists(pngPath))
        {
            bytes = await File.ReadAllBytesAsync(pngPath, cancellationToken);
        }
        BitmapImage bitmap = null;
        if (bytes != null)
        {
            try
            {
                bitmap = await DecodeAsync(bytes);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ManagedCommon.Logger.LogWarning("The saved stability image is unreadable; rebuilding from its Markdown document.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (bitmap == null)
        {
            RenderCover.Visibility = Visibility.Visible;
            try
            {
                var rendered = await WindowsStabilityDashboardRenderer.RenderAsync(document.Data, RenderHost, monthly: false, dark: dark, english: english, cancellationToken: cancellationToken);
                bytes = rendered.PngBytes;
                bitmap = await DecodeAsync(bytes);
                cancellationToken.ThrowIfCancellationRequested();
                if (request != _renderRequest)
                {
                    throw new OperationCanceledException();
                }
                _lifetimeToken.ThrowIfCancellationRequested();
                string temporary = pngPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    _lifetimeToken.ThrowIfCancellationRequested();
                    File.Move(temporary, pngPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
            }
            finally
            {
                RenderCover.Visibility = Visibility.Collapsed;
            }
        }

        return bitmap;
    }

    private static async Task<BitmapImage> DecodeAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        if (bitmap.PixelWidth < 1 || bitmap.PixelHeight < 1)
        {
            throw new InvalidDataException("The report PNG has no image.");
        }
        return bitmap;
    }

    internal void SetFailure(string detail = null)
    {
        _loading = false;
        _failed = true;
        _cancelled = false;
        _cancelling = false;
        _failureDetail = detail;
        _progress = null;
        _generationClock.Stop();
        RefreshLocalization();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _renderRequest++;
        _generationClock.Dispose();
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    internal void RefreshTheme()
    {
        if (_lifetimeToken.IsCancellationRequested)
        {
            return;
        }

        // A running render already follows theme and language before it publishes its image.
        ThemeRenderTask = IsRendering ? _renderTail
            : _loading || _document == null ? Task.CompletedTask
            : RefreshThemeCoreAsync();
    }

    private async Task RefreshThemeCoreAsync()
    {
        var document = _document;
        bool failed = _failed;
        bool cancelled = _cancelled;
        string failureDetail = _failureDetail;
        int loadingRevision = _loadingRevision;
        try
        {
            await SetDocumentAsync(document, _lifetimeToken);
            if (!_loading && loadingRevision == _loadingRevision && _document?.Id == document.Id)
            {
                _failed = failed;
                _cancelled = cancelled;
                _failureDetail = failureDetail;
                RefreshLocalization();
            }
        }
        catch (OperationCanceledException)
        {
            // Closing or refreshing the document supersedes a theme-only render.
        }
        catch (Exception exception)
        {
            ManagedCommon.Logger.LogWarning($"Period report theme rendering failed ({exception.GetType().Name}).");
            if (loadingRevision == _loadingRevision && _document?.Id == document.Id)
            {
                SetFailure();
            }
        }
    }

    internal void SetTitleBarInsets(double left, double right)
        => TitleBarContent.Margin = new Thickness(Math.Max(18, left + 12), 0, Math.Max(12, right + 12), 0);

    public void RefreshLocalization()
    {
        if (_disposed)
        {
            return;
        }

        string language = ReportLanguage;
        bool languageChanged = _uiLanguage != language;
        _uiLanguage = language;
        bool english = language == "en-US";
        ReportTitle.Text = english
            ? "Kit · Windows stability report · Weekly"
            : "Kit · Windows 系统稳定性周报";
        PeriodChip.Text = english ? "Last 7 days" : "最近 7 天";
        string mode = _selectedMode.ToString();
        RefreshLabel.Text = _document == null
            ? (english ? $"Generate {mode}" : $"生成 {mode}")
            : (english ? $"Update {mode}" : $"更新 {mode}");
        CancelLabel.Text = _cancelling ? (english ? "Cancelling…" : "正在取消…") : (english ? "Cancel" : "取消");
        ModeDescription.Text = _selectedMode == WeeklyReportMode.Fast
            ? (english ? "Fast · Uses a saved 7-day scan and its AI results. No new scan or model call." : "Fast · 复用已保存的 7 天扫描及其 AI 结果，不重新扫描或调用模型。")
            : (english ? "Full · Collects 7 days of Windows events independently and requests a new AI review when you generate or update." : "Full · 点击生成或更新后，独立采集 7 天 Windows 事件并调用 AI 复核。");
        OpenImageLabel.Text = english ? "Open PNG" : "打开 PNG";
        ReportSources.Text = _document?.Data.WeeklySource is { } source
            ? (english ? $"Displayed · {source.Mode} · " : $"当前报告 · {source.Mode} · ")
                + (source.Mode == WeeklyReportMode.Fast ? (english ? "Saved 7-day scan" : "已保存的 7 天扫描") : "System / Application / Setup")
                + (string.IsNullOrWhiteSpace(source.UsedModel) ? string.Empty : $" · {source.UsedModel}")
            : (english ? "Sources · System / Application / Setup" : "事件源 · System / Application / Setup");
        ZoomHint.Text = english ? "Scroll to browse · Ctrl + wheel to zoom" : "滚轮浏览 · Ctrl + 滚轮缩放";
        ToolTipService.SetToolTip(RefreshButton, ModeDescription.Text);
        AutomationProperties.SetName(ModeSelector, english ? "Weekly report mode" : "周报模式");
        AutomationProperties.SetName(CancelButton, CancelLabel.Text);
        ToolTipService.SetToolTip(OpenImageButton, english ? "Open the complete PNG in the current language and theme" : "打开当前语言与主题的完整 PNG 图表");
        ToolTipService.SetToolTip(FitButton, english ? "Switch between fit to window and original size" : "切换适合窗口或原始大小");
        AutomationProperties.SetName(RefreshButton, RefreshLabel.Text);
        AutomationProperties.SetName(OpenImageButton, OpenImageLabel.Text);
        AutomationProperties.SetName(ReportImage, english ? "Windows system stability report charts" : "Windows 系统稳定性报告图表");
        RefreshButton.IsEnabled = !_loading;
        CancelButton.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = !_cancelling;
        OpenImageButton.IsEnabled = !string.IsNullOrEmpty(DisplayedPngPath);
        FitButton.IsEnabled = _document != null;
        FitLabel.Text = english ? (_fit ? "Original size" : "Fit to window") : (_fit ? "原始大小" : "适合窗口");
        FitIcon.Glyph = _fit ? "\uE740" : "\uE9A6";
        AutomationProperties.SetName(FitButton, FitLabel.Text);
        LoadingRing.IsActive = _loading;
        LoadingRing.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        ReportStatus.Text = _loading ? (_cancelling ? (english ? "Cancelling generation…" : "正在取消生成…") : ProgressText(english, mode))
            : _cancelled ? (english ? "Generation cancelled. Saved reports are preserved." : "已取消生成，已保存的报告仍保留。")
            : _failed ? (_document == null
                ? (_selectedMode == WeeklyReportMode.Fast
                    ? (english ? "Fast report unavailable. Run a 7-day scan first, or select Full and generate a report." : "Fast 周报不可用。请先完成 7 天扫描，或选择 Full 后生成周报。")
                    : (english ? "Generation failed. " : "生成失败。") + (_failureDetail ?? (english ? "Try again." : "请重试。")))
                : (english ? "Update failed. The previous complete report for this mode is preserved; try again." : "报告更新失败，此模式已保存的完整报告仍保留；可重试。"))
            : _document == null ? (_selectedMode == WeeklyReportMode.Fast
                ? (english ? "Run a 7-day scan first, or select Full and generate a report." : "请先完成 7 天扫描，或选择 Full 后生成周报。")
                : (english ? "Select Generate Full to start collection and AI review." : "点击“生成 Full”以开始采集和 AI 复核。"))
            : english
                ? $"{_document.Data.Start:yyyy-MM-dd HH:mm} – {_document.Data.End:yyyy-MM-dd HH:mm} · Saved {_document.GeneratedUtc.ToLocalTime():MM-dd HH:mm}"
                : $"{_document.Data.Start:yyyy-MM-dd HH:mm} – {_document.Data.End:yyyy-MM-dd HH:mm} · 保存于 {_document.GeneratedUtc.ToLocalTime():MM-dd HH:mm}";
        RefreshGenerationPanel(english, mode);
        if (languageChanged)
        {
            RefreshTheme();
        }
    }

    private string ProgressText(bool english, string mode) => _progress?.Stage.ToLowerInvariant() switch
    {
        "cache" => english ? $"{mode} · Reading the saved report…" : $"{mode} · 正在读取已保存的报告…",
        "scan" => english ? $"{mode} · Reading the saved 7-day scan…" : $"{mode} · 正在读取已保存的 7 天扫描…",
        "collect" => english ? "Full · Collecting 7 days of Windows events…" : "Full · 正在采集 7 天 Windows 事件…",
        "analyze" => english ? $"{mode} · Summarizing event evidence…" : $"{mode} · 正在汇总事件证据…",
        "review" or "ai" => english ? "Full · AI review in progress…" : "Full · AI 正在复核…",
        "save" => english ? $"{mode} · Saving both report languages…" : $"{mode} · 正在保存中英文报告…",
        "complete" or "rendering" => english ? $"{mode} · Rendering report charts…" : $"{mode} · 正在生成报告图表…",
        _ => english ? $"Preparing {mode} report…" : $"正在准备 {mode} 周报…",
    };

    private void RefreshGenerationPanel(bool english, string mode)
    {
        bool hasPreview = ReportImage.Source != null;
        GenerationPanel.Visibility = _loading || _failed || _cancelled || !hasPreview ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRowSpan(GenerationPanel, hasPreview ? 1 : 2);
        GenerationPanel.VerticalAlignment = hasPreview ? VerticalAlignment.Top : VerticalAlignment.Center;
        GenerationPanel.Margin = hasPreview ? new Thickness(16, 12, 16, 8) : new Thickness(24);
        GenerationPanel.Padding = hasPreview ? new Thickness(16) : new Thickness(24);
        GenerationPanel.MaxWidth = hasPreview ? double.PositiveInfinity : 660;
        TypographyResources.SetBaseFontSize(GenerationTitle, hasPreview ? 14 : 20);
        GenerationTitle.Text = _loading
            ? (_cancelling ? (english ? "Cancelling generation" : "正在取消生成")
                : hasPreview ? (english ? $"Updating {mode} report" : $"正在更新 {mode} 周报")
                : (english ? $"Generating {mode} report" : $"正在生成 {mode} 周报"))
            : _cancelled ? (english ? "Generation cancelled" : "已取消生成")
            : _failed ? (hasPreview ? (english ? "Update could not be completed" : "报告更新未完成") : (english ? $"{mode} report unavailable" : $"{mode} 周报未能生成"))
            : (english ? $"{mode} weekly report" : $"{mode} 每周报告");
        GenerationStateGlyph.Glyph = _loading ? "\uE9F9" : _failed ? "\uE7BA" : _cancelled ? "\uE711" : "\uE9D9";
        GenerationStage.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        GenerationStage.Text = _cancelling ? (english ? "Waiting for the current operation to stop" : "正在等待当前操作结束") : ProgressText(english, mode);
        GenerationDetail.Text = _loading ? (_cancelling
            ? (english ? "The saved report remains available while cancellation completes." : "取消完成前，已保存的报告仍可查看。")
            : ProgressDetail(english)) : ReportStatus.Text;
        RetainedPreviewNote.Visibility = hasPreview && (_loading || _failed || _cancelled) ? Visibility.Visible : Visibility.Collapsed;
        RetainedPreviewNote.Text = english ? "The previous saved report remains below until a replacement is ready." : "下方保留已保存的报告，新版本完成后才替换预览。";
        GenerationProgress.IsIndeterminate = _loading;
        GenerationProgress.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        RefreshElapsed();
    }

    private string ProgressDetail(bool english) => _progress?.Stage.ToLowerInvariant() switch
    {
        "cache" => english ? "Opening the saved document and its matching chart." : "正在打开已保存的报告与对应图表。",
        "scan" => english ? "Checking the saved seven-day evidence and its coverage." : "正在读取已保存的七天证据，并核对覆盖范围。",
        "collect" => english ? "Reading System, Application and Setup events for this seven-day window." : "正在读取本期七天的 System、Application 和 Setup 事件。",
        "analyze" => english ? "Counting observed events and checking whether a saved comparison period is available." : "正在统计已观察事件，并检查可复用的历史对比。",
        "review" => english ? "Reviewing the collected evidence and saved audit references. You can cancel while waiting." : "正在复核已采集证据与历史审计参考，等待期间可取消。",
        "ai" => AiProgressDetail(english),
        "save" => english ? "Saving and verifying the Chinese and English report documents." : "正在保存并校验中英文报告。",
        "complete" or "rendering" => english ? "Preparing the chart for the current language and theme." : "正在为当前语言与主题准备报告图表。",
        _ => _selectedMode == WeeklyReportMode.Fast
            ? (english ? "Preparing the saved scan for a local report. No new model request." : "正在准备基于已保存扫描的本地报告，不发起新的模型请求。")
            : (english ? "Preparing the seven-day report workflow." : "正在准备七天周报的生成流程。"),
    };

    private string AiProgressDetail(bool english)
    {
        return _aiActivity switch
        {
            "reasoning" => english ? "The model is reasoning over the weekly evidence." : "模型正在分析本周证据。",
            "responding" => english ? "The model is writing its assessment and verification steps." : "模型正在生成整体判断与核验步骤。",
            "retrying" => english ? "The AI service reported a retry. The report remains in progress." : "AI 服务正在重试，报告生成仍在进行。",
            "queued" => english ? "Waiting for an available AI service slot." : "正在等待 AI 服务的可用分析槽位。",
            "validating" => english ? "Checking the model response and its evidence references." : "正在校验模型结果与证据引用。",
            "preparing" => english ? "Preparing the AI review request." : "正在准备 AI 复核请求。",
            _ => english ? "Waiting for the AI service response. Elapsed time continues while no new status is available." : "正在等待 AI 服务响应；没有新状态时，已用时间仍持续更新。",
        };
    }

    private void UpdateAiActivity(string message)
    {
        // Only shared-engine markers are displayed, never transport metadata or arbitrary
        // status text. Heartbeat messages preserve the last actual observed activity.
        message ??= string.Empty;
        if (HasStatusMarker(message, "phase=reasoning"))
        {
            _aiActivity = "reasoning";
        }
        else if (HasStatusMarker(message, "phase=responding"))
        {
            _aiActivity = "responding";
        }
        else if (HasStatusMarker(message, "event=retry"))
        {
            _aiActivity = "retrying";
        }
        else if (message.Equals("Queue", StringComparison.OrdinalIgnoreCase))
        {
            _aiActivity = "queued";
        }
        else if (message.Equals("Validate", StringComparison.OrdinalIgnoreCase) || HasStatusMarker(message, "event=completed"))
        {
            _aiActivity = "validating";
        }
        else if (message.Equals("Prepare", StringComparison.OrdinalIgnoreCase))
        {
            _aiActivity = "preparing";
        }
        else if (message.Equals("Execute", StringComparison.OrdinalIgnoreCase) || HasStatusMarker(message, "phase=started"))
        {
            _aiActivity = "waiting";
        }
    }

    private static bool HasStatusMarker(string message, string marker)
    {
        foreach (string field in message.Split(','))
        {
            if (field.AsSpan().Trim().Equals(marker.AsSpan(), StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private void RefreshElapsed()
    {
        if (_disposed || _generationClock == null)
        {
            return;
        }
        GenerationElapsed.Visibility = _generationClock.HasStarted ? Visibility.Visible : Visibility.Collapsed;
        var elapsed = TimeSpan.FromMilliseconds(_generationClock.ElapsedMilliseconds);
        string value = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
        GenerationElapsed.Text = (ReportLanguage == "en-US" ? "Elapsed " : "已用时 ") + value;
    }

    private void UpdateImageWidth()
    {
        if (_pixelWidth > 0)
        {
            ReportImage.Width = _fit ? Math.Max(1, ReportScroll.ActualWidth - 18) : _pixelWidth;
        }
    }

    private void OnReportSizeChanged(object sender, SizeChangedEventArgs args) => UpdateImageWidth();
    private void OnRefreshClick(object sender, RoutedEventArgs args) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    private void OnCancelClick(object sender, RoutedEventArgs args) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private void OnModeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_initialized && ModeSelector.SelectedIndex >= 0)
        {
            var mode = ModeSelector.SelectedIndex == 1 ? WeeklyReportMode.Full : WeeklyReportMode.Fast;
            if (mode != _selectedMode)
            {
                _selectedMode = mode;
                ModeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void OnFitClick(object sender, RoutedEventArgs args)
    {
        _fit = !_fit;
        ReportScroll.ChangeView(0, 0, 1, disableAnimation: true);
        UpdateImageWidth();
        RefreshLocalization();
    }

    private async void OnOpenImageClick(object sender, RoutedEventArgs args)
    {
        if (string.IsNullOrEmpty(DisplayedPngPath))
        {
            return;
        }
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(DisplayedPngPath);
            if (!await Windows.System.Launcher.LaunchFileAsync(file))
            {
                ShowOpenFailure();
            }
        }
        catch (Exception)
        {
            ShowOpenFailure();
        }
    }

    private void ShowOpenFailure()
        => ReportStatus.Text = ReportLanguage == "en-US"
            ? "Unable to open the PNG. The chart is still saved in the report folder."
            : "无法打开 PNG，图表仍保存在报告目录。";
}
