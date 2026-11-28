using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Storage;
using Kit.AIHubLib.Services;
using Kit.Settings.UI;
using Kit.Settings.UI.Helpers;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Kit.Settings.UI.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

internal static class WindowsStabilityUiChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static async Task RenderSavedAsync(string path, string output)
    {
        var document = WindowsStabilityReportStorage.Read(path);
        Check(!document.Monthly, "The AI Hub report window accepts weekly documents only.");
        var owner = App.GetSettingsWindow();
        var ownerRoot = (FrameworkElement)owner.Content;
        var originalTheme = ownerRoot.RequestedTheme;
        string originalLanguage = RuntimeLocalization.AppliedLanguage;
        string previewLanguage = RuntimeLocalization.EffectiveLanguage;
        var previewDefaultTheme = ownerRoot.ActualTheme;
        var window = new AuditPeriodReportWindow(owner);
        try
        {
            var mode = document.Data.WeeklySource?.Mode ?? WeeklyReportMode.Fast;
            Control<ComboBox>(window, "ModeSelector").SelectedIndex = mode == WeeklyReportMode.Full ? 1 : 0;
            Func<WeeklyReportMode, bool, IProgress<WeeklyReportProgress>, CancellationToken, Task<WindowsStabilityReportDocument>> load = (_, _, _, _) => Task.FromResult(document);
            Func<WeeklyReportMode, CancellationToken, Task<WindowsStabilityReportDocument>> readSaved = (_, _) => Task.FromResult(document);
            await (Task)window.GetType().GetMethod("ShowWeeklyReportAsync", Flags).Invoke(window, new object[] { load, null, readSaved });
            var displayed = Property<WindowsStabilityReportDocument>(window, "LoadedDocument");
            var bitmap = Control<Image>(window, "ReportImage").Source as BitmapImage;
            string pngPath = Property<string>(window.Content, "DisplayedPngPath");
            Check(displayed?.Id == document.Id && bitmap?.PixelWidth == 1800 && File.Exists(pngPath),
                "The real collected report must render as a saved PNG from its exact Markdown version.");
            Check(WindowsStabilityReportStorage.Read(path).Markdown == document.Markdown, "Rendering must not modify the saved source report.");
            File.AppendAllText(Path.Combine(output, "aihub-ux-smoke.log"), $"LIVE DOCUMENT PNG PASS: version={document.Id}, pixels={bitmap.PixelWidth}x{bitmap.PixelHeight}, png={pngPath}; no new collection or model requests.\n");
            foreach (string language in new[] { "zh-CN", "en-US" })
            {
                await SetLanguageAsync(language, window);
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    ownerRoot.RequestedTheme = theme;
                    await Task.Delay(75);
                    await Until(() => !Property<bool>(window.Content, "IsRendering")
                        && Property<string>(window.Content, "DisplayedPngPath").EndsWith(ThemeSuffix(theme), StringComparison.Ordinal));
                    await Property<Task>(window.Content, "ThemeRenderTask");
                    string themedPng = Property<string>(window.Content, "DisplayedPngPath");
                    Check(themedPng.EndsWith(ThemeSuffix(theme), StringComparison.Ordinal), "The real report must select the matching theme image.");
                    var translated = Property<WindowsStabilityReportDocument>(window.Content, "DisplayedDocument");
                    Check(translated.Id == document.Id && translated.Language == language && File.Exists(translated.SourcePath), "Both real report languages must preserve the same evidence version and saved Markdown.");
                    File.Copy(themedPng, Path.Combine(output, $"windows-stability-live-{language}-{theme}.png"), true);
                    await CaptureWindowAsync(window, $"windows-stability-live-window-{language}-{theme}.png", theme);
                }
            }

            int previewSeconds = int.TryParse(Environment.GetEnvironmentVariable("KIT_STABILITY_PREVIEW_SECONDS"), out int requestedSeconds)
                ? Math.Clamp(requestedSeconds, 0, 120) : 0;
            if (previewSeconds > 0)
            {
                var previewTheme = Environment.GetEnvironmentVariable("KIT_STABILITY_PREVIEW_THEME")?.ToLowerInvariant() switch
                {
                    "light" => ElementTheme.Light,
                    "dark" => ElementTheme.Dark,
                    _ => previewDefaultTheme,
                };
                await SetLanguageAsync(previewLanguage, window);
                ownerRoot.RequestedTheme = previewTheme;
                await Until(() => !Property<bool>(window.Content, "IsRendering")
                    && Property<string>(window.Content, "DisplayedPngPath").EndsWith(ThemeSuffix(previewTheme), StringComparison.Ordinal));
                await Property<Task>(window.Content, "ThemeRenderTask");
                // Set the preview viewport directly; no button, generation or external-open action is invoked.
                window.Content.GetType().GetField("_fit", Flags).SetValue(window.Content, true);
                Control<ScrollViewer>(window, "ReportScroll").ChangeView(0, 0, 1, disableAnimation: true);
                window.Content.GetType().GetMethod("UpdateImageWidth", Flags).Invoke(window.Content, null);
                ((AuditPeriodReportView)window.Content).RefreshLocalization();
                window.Activate();
                ((FrameworkElement)window.Content).UpdateLayout();
                string ready = $"LIVE DOCUMENT PREVIEW READY: language={previewLanguage}, theme={previewTheme}, holdSeconds={previewSeconds}, version={document.Id}; fit to window; no automated button actions during preview.\n";
                File.AppendAllText(Path.Combine(output, "aihub-ux-smoke.log"), ready);
                Console.Write(ready);
                var previewTimer = Stopwatch.StartNew();
                while (previewTimer.Elapsed < TimeSpan.FromSeconds(previewSeconds) && !Property<bool>(window, "IsClosed"))
                {
                    await Task.Delay(250);
                }
            }
        }
        finally
        {
            window.Close();
            AppearanceService.RequestLanguage(originalLanguage);
            await AppearanceService.WhenIdleAsync();
            ownerRoot.RequestedTheme = originalTheme;
            owner.Activate();
        }
    }

    internal static async Task RunAsync(AIHubPage page, string output)
    {
        var vm = page.ViewModel;
        var storeField = typeof(AIHubPageViewModel).GetField("_weeklyReportService", Flags);
        var originalStore = storeField.GetValue(vm);
        var dateField = typeof(AIHubPageViewModel).GetField("_selectedAuditDate", Flags);
        var originalDate = dateField.GetValue(vm);
        var analyzing = typeof(AIHubPageViewModel).GetProperty(nameof(vm.IsAiAnalyzing));
        var owner = App.GetSettingsWindow();
        var ownerRoot = (FrameworkElement)owner.Content;
        var originalTheme = ownerRoot.RequestedTheme;
        string originalLanguage = RuntimeLocalization.AppliedLanguage;
        string directory = Directory.CreateTempSubdirectory("KitStabilityPng-").FullName;
        int collections = 0, reviews = 0, identities = 0;
        bool fail = false, pause = false, cancelled = false, pauseReview = false, reviewCancelled = false;
        Window window = null;
        var scanStore = new WeeklyScanSnapshotStore(Path.Combine(directory, "weekly-scans"), () => Array.Empty<AuditResult>());

        async Task<WindowsStabilityCollection> Collect(DateTimeOffset from, DateTimeOffset end, CancellationToken token)
        {
            collections++;
            if (pause)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled = true; throw; }
            }
            if (fail) throw new IOException("Injected collection failure.");
            Check(end - from == TimeSpan.FromDays(7), "Full must collect exactly seven days independently.");
            return FixtureCollection(from, end);
        }

        WindowsStabilityCollection FixtureCollection(DateTimeOffset from, DateTimeOffset end)
        {
            var events = new List<WindowsStabilityEvent>();
            long id = 0;
            void Add(string channel, string provider, int eventId, int level, DateTimeOffset time, Dictionary<string, string> fields = null)
                => events.Add(new(channel, provider, eventId, ++id, time, level, "Synthetic event used only by the isolated UI regression.", fields ?? new(), []));
            for (int day = 0; day < 14; day++)
            {
                var time = end.AddDays(-14 + day).AddHours(1);
                Add("System", "Microsoft-Windows-Kernel-Power", 42, 4, time);
                Add("System", "Microsoft-Windows-Kernel-Power", 107, 4, time.AddMinutes(30));
                for (int n = 0; n < 35 + day * 7; n++) Add("Application", "Synthetic information", 10, 4, time.AddMinutes(n * 6));
                for (int n = 0; n < 2 + day; n++) Add("System", "Microsoft-Windows-DistributedCOM", 10016, 3, time.AddMinutes(n * 36));
                for (int n = 0; n < 1 + day % 5; n++) Add("Application", "Application Error", 1000, 2, time.AddMinutes(n * 12), new()
                {
                    ["AppName"] = day % 2 == 0 ? "SyntheticAudio.exe" : "SyntheticEditor.exe",
                    ["ModuleName"] = day % 2 == 0 ? "SyntheticAudio.dll" : "SyntheticRuntime.dll",
                    ["ExceptionCode"] = "c0000005", ["FaultingOffset"] = day % 2 == 0 ? "00001234" : "00005678",
                });
            }
            Add("System", "Microsoft-Windows-Kernel-Power", 41, 1, end.AddDays(-2), new() { ["BugcheckCode"] = "0", ["SleepInProgress"] = "1" });
            Add("System", "Service Control Manager", 7045, 4, end.AddDays(-3));
            var coverage = new[] { "System", "Application", "Setup" }.Select(channel => new WindowsStabilityCoverage(
                channel, from, end, from.AddDays(-1), true, false, false, events.Count(item => item.Channel == channel), string.Empty)).ToArray();
            return new(from, end, events.Where(item => item.Time >= from).ToArray(), coverage,
                new Dictionary<string, string> { ["Computer"] = "Synthetic UI fixture", ["MinidumpTimesUtc"] = string.Empty }, ["合成测试数据，不代表本机事件。"]);
        }

        async Task<WeeklyReportReview> Review(WeeklyReportReviewInput input, IProgress<WeeklyReportProgress> progress, CancellationToken token)
        {
            reviews++;
            Check(input.Mode == WeeklyReportMode.Full && input.Data.End - input.Data.Start == TimeSpan.FromDays(7),
                "Only explicit Full generation may call the independent seven-day reviewer.");
            progress?.Report(new("ai", "batch=1/1, AI native event: request=fixture, event=activity, phase=reasoning"));
            if (pauseReview)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { reviewCancelled = true; throw; }
            }
            return new()
            {
                AssessmentZh = "合成复核结论，仅验证周报流程。",
                AssessmentEn = "Synthetic review used only to verify the weekly report flow.",
                UsedModel = "fixture-weekly-reviewer", UsedRoute = "Fixture",
            };
        }

        async Task Open(bool hasDocument = true)
        {
            Check(!Descendants(page).OfType<Button>().Any(item => AutomationProperties.GetAutomationId(item) == "AIHub_MonthlyReportButton"),
                "AI Hub must not expose a monthly report button.");
            Check(typeof(AIHubPage).GetMethod("OnMonthlyReportClick", Flags) == null
                && typeof(AIHubPageViewModel).GetProperty("MonthlyReportLabel", Flags) == null,
                "Removed monthly UI entry points must be absent, not hidden.");
            var button = Descendants(page).OfType<Button>().Single(item => AutomationProperties.GetAutomationId(item) == "AIHub_WeeklyReportButton");
            Check(button.IsEnabled, "Report actions remain usable while audit analysis runs.");
            Click(button);
            await Until(() => Field<Window>(page, "_periodReportWindow")?.Content is FrameworkElement { IsLoaded: true }
                && !Property<bool>(Field<Window>(page, "_periodReportWindow"), "IsLoading"));
            window = Field<Window>(page, "_periodReportWindow");
            Check((Property<WindowsStabilityReportDocument>(window, "LoadedDocument") != null) == hasDocument,
                "The selected mode must display only its own successfully loaded document.");
        }

        async Task SelectMode(WeeklyReportMode mode)
        {
            Control<ComboBox>(window, "ModeSelector").SelectedIndex = mode == WeeklyReportMode.Full ? 1 : 0;
            await Task.Delay(50);
            await Until(() => !Property<bool>(window, "IsLoading") && !Property<bool>(window.Content, "IsRendering"));
        }

        async Task Refresh()
        {
            Click(Control<Button>(window, "RefreshButton"));
            await Task.Delay(50);
            await Until(() => !Property<bool>(window, "IsLoading"));
        }

        WindowsStabilityReportDocument Verify()
        {
            var document = Property<WindowsStabilityReportDocument>(window.Content, "DisplayedDocument");
            Check(!document.Monthly && document.Data.End - document.Data.Start == TimeSpan.FromDays(7),
                "Every AI Hub report and refresh must retain the rolling seven-day period.");
            Check(document.Id == Property<WindowsStabilityReportDocument>(window, "LoadedDocument").Id, "Display language must retain the loaded evidence version.");
            var restored = WindowsStabilityReportStorage.Read(document.SourcePath);
            Check(restored.Id == document.Id && restored.Markdown == document.Markdown, "PNG data must come from the saved Markdown version.");
            string pngPath = Property<string>(window.Content, "DisplayedPngPath");
            Check(pngPath.Contains(document.Id, StringComparison.Ordinal) && File.Exists(pngPath), "PNG cache must be bound to its document version and theme.");
            var bytes = File.ReadAllBytes(pngPath);
            Check(bytes.Length > 20000 && bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "A real nonempty PNG must be persisted.");
            var bitmap = Control<Image>(window, "ReportImage").Source as BitmapImage;
            Check(bitmap != null && bitmap.PixelWidth == 1800 && bitmap.PixelHeight >= 2600, "The report displays the high-resolution raster image.");
            Check(Control<Grid>(window, "RenderHost").Children.Count == 0, "Temporary chart controls must detach after encoding.");
            Check(!Descendants((FrameworkElement)window.Content).OfType<Expander>().Any(), "The report's primary presentation is the PNG, without the old finding-card stack.");
            Check(Control<Button>(window, "OpenImageButton").IsEnabled, "The saved PNG can be opened externally.");
            Check(!Control<ProgressBar>(window, "GenerationProgress").IsIndeterminate,
                "A displayed report must not leave the generation animation running.");
            return document;
        }

        try
        {
            dateField.SetValue(vm, (DateTimeOffset?)DateTimeOffset.Now);
            storeField.SetValue(vm, new WeeklyReportService(storage: new WindowsStabilityReportStorage(directory), scans: scanStore,
                collect: Collect, review: Review, identity: _ => { identities++; return Task.FromResult("fixture-weekly-identity"); }));
            analyzing.SetValue(vm, true);
            await Open(hasDocument: false);
            Check(collections == 0 && reviews == 0 && identities == 0 && Control<TextBlock>(window, "ReportStatus").Text.Contains("7", StringComparison.Ordinal),
                "Fast without a retained scan must explain its prerequisite and never invoke Full, collection or AI identity.");
            var scanEnd = DateTimeOffset.Now;
            var scanEvidence = FixtureCollection(scanEnd.AddDays(-7), scanEnd);
            await scanStore.SaveAsync(new AuditResult
            {
                ScanStart = scanEnd.AddDays(-7).UtcDateTime, ScanEnd = scanEnd.UtcDateTime,
                Timestamp = scanEnd.UtcDateTime, EventsScanned = scanEvidence.Events.Count,
                CollectionProfile = "Full", UsedModel = "fixture-saved-scan", AnalysisOutcome = "ok",
                AiReportSummary = "Synthetic saved audit / 已保存的合成审计",
            }, scanEvidence.Events.Select(item => new SecurityEvent(item.EventId, item.Channel, item.Provider, item.Level, item.Time.UtcDateTime, item.Message)).ToArray());
            await Refresh();
            var fast = Verify();
            Check(fast.Data.WeeklySource?.Mode == WeeklyReportMode.Fast && collections == 0 && reviews == 0 && identities == 0,
                "Fast must build only from retained scan evidence and its saved analysis.");
            await SelectMode(WeeklyReportMode.Full);
            Check(Property<WindowsStabilityReportDocument>(window, "LoadedDocument") == null && Control<Image>(window, "ReportImage").Source == null
                && collections == 0 && reviews == 0, "Selecting Full must not spend AI calls or relabel the previous Fast image.");
            fail = true;
            await Refresh();
            Check(Property<WindowsStabilityReportDocument>(window, "LoadedDocument") == null && Control<Image>(window, "ReportImage").Source == null,
                "A first Full failure must never display the Fast document as Full.");
            Check(Control<Border>(window, "GenerationPanel").Visibility == Visibility.Visible
                && !Control<ProgressBar>(window, "GenerationProgress").IsIndeterminate,
                "A first failure must replace the blank body with a stable, retryable explanation.");
            fail = false;
            await SelectMode(WeeklyReportMode.Fast);
            Check(Verify().Id == fast.Id, "Switching back must restore the retained Fast document.");
            await SelectMode(WeeklyReportMode.Full);
            pauseReview = true;
            int firstReviewCount = reviews;
            Click(Control<Button>(window, "RefreshButton"));
            await Until(() => reviews > firstReviewCount
                && Control<TextBlock>(window, "GenerationDetail").Text.Contains(RuntimeLocalization.EffectiveLanguage == "zh-CN" ? "本周证据" : "reasoning", StringComparison.Ordinal));
            Check(Control<Image>(window, "ReportImage").Source == null
                && Control<Border>(window, "GenerationPanel").Visibility == Visibility.Visible
                && Grid.GetRowSpan(Control<Border>(window, "GenerationPanel")) == 2
                && Control<ProgressBar>(window, "GenerationProgress").IsIndeterminate,
                "First generation must fill the empty body with actual AI stage and indeterminate progress.");
            long initialElapsed = Property<long>(window.Content, "GenerationElapsedMilliseconds");
            string initialElapsedText = Control<TextBlock>(window, "GenerationElapsed").Text;
            await Task.Delay(1200);
            Check(Property<long>(window.Content, "GenerationElapsedMilliseconds") >= initialElapsed + 1000
                && Control<TextBlock>(window, "GenerationElapsed").Text != initialElapsedText,
                "Elapsed time must keep advancing when the model reports no new progress.");
            foreach (string language in new[] { "en-US", "zh-CN" })
            {
                AppearanceService.RequestLanguage(language);
                await AppearanceService.WhenIdleAsync();
                ((AuditPeriodReportView)window.Content).RefreshLocalization();
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    ownerRoot.RequestedTheme = theme;
                    window.AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 800));
                    await Task.Delay(75);
                    ((FrameworkElement)window.Content).UpdateLayout();
                    var panel = Control<Border>(window, "GenerationPanel");
                    string stage = Control<TextBlock>(window, "GenerationStage").Text;
                    string detail = Control<TextBlock>(window, "GenerationDetail").Text;
                    Check(panel.ActualHeight > 100 && panel.ActualWidth > 200
                        && panel.Background is SolidColorBrush { Color.A: > 0 and < 255 }
                        && panel.BackgroundSizing == BackgroundSizing.OuterBorderEdge,
                        "Generation must be visible at narrow widths with a rounded translucent surface over Mica.");
                    Check(stage.Contains("AI", StringComparison.Ordinal) && !stage.Contains("Preparing", StringComparison.Ordinal)
                        && detail.Contains(language == "zh-CN" ? "本周证据" : "reasoning", StringComparison.Ordinal)
                        && Control<TextBlock>(window, "GenerationElapsed").Text.StartsWith(language == "zh-CN" ? "已用时" : "Elapsed", StringComparison.Ordinal)
                        && !stage.Contains('%') && !detail.Contains('%'),
                        "The actual ai stage and elapsed label must switch language without invented percentages or transport logs.");
                    await CaptureWindowAsync(window, $"windows-stability-generating-{language}-{theme}.png", theme);
                }
            }
            Click(Control<Button>(window, "CancelButton"));
            await Until(() => reviewCancelled && !Property<bool>(window, "IsLoading"));
            long stoppedElapsed = Property<long>(window.Content, "GenerationElapsedMilliseconds");
            await Task.Delay(100);
            Check(Property<long>(window.Content, "GenerationElapsedMilliseconds") == stoppedElapsed
                && !Control<ProgressBar>(window, "GenerationProgress").IsIndeterminate
                && Control<TextBlock>(window, "GenerationTitle").Text.Contains("取消", StringComparison.Ordinal),
                "Cancellation must settle the empty-body panel and stop elapsed time and animation.");
            pauseReview = false;
            reviewCancelled = false;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 800));
            int reviewsBeforeFull = reviews;
            await Refresh();
            var first = Verify();
            Check(first.Data.WeeklySource?.Mode == WeeklyReportMode.Full && first.Id != fast.Id && reviews == reviewsBeforeFull + 1,
                "Full must independently collect and review its own seven-day evidence.");
            Check(Control<Border>(window, "GenerationPanel").Visibility == Visibility.Collapsed,
                "A successful first generation must hand the body back to the completed report.");
            Check(first.Data.CurrentComplete && !first.Data.PreviousComplete, "Seven-day Full evidence must not invent a previous-period baseline.");
            Check(first.Data.Metrics.Single(metric => metric.Key == "power").Current == 1, "Power incident count is an event count, not a saved-scan count.");
            File.Copy(Property<string>(window.Content, "DisplayedPngPath"), Path.Combine(output, "windows-stability-weekly.png"), true);
            if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
                Check(window.SystemBackdrop is MicaBackdrop { Kind: Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt }, "The image window keeps Mica BaseAlt.");

            int before = collections;
            await Open();
            Check(collections == before && Verify().Id == first.Id, "Reopening a recent report reuses its Markdown and PNG without recollecting.");
            var closedWindow = window;
            int reviewsBeforeReopen = reviews, identitiesBeforeReopen = identities;
            window.Close();
            await Until(() => Property<bool>(closedWindow, "IsClosed"));
            await Open();
            Check(!ReferenceEquals(closedWindow, window) && Verify().Id == fast.Id,
                "A new window starts in Fast and can load its persisted report after the old window closes.");
            await SelectMode(WeeklyReportMode.Full);
            Check(Verify().Id == first.Id && collections == before && reviews == reviewsBeforeReopen && identities == identitiesBeforeReopen,
                "Selecting Full in a new window must read its persisted document with zero collection, reviewer or AI identity calls.");
            foreach (string language in new[] { "en-US", "zh-CN", "en-US" })
            {
                await SetLanguageAsync(language, window);
                var translated = Verify();
                Check(translated.Language == language && translated.Id == first.Id && collections == before,
                    "Live language changes must select paired documents without collecting events or changing evidence.");
                Check(Control<TextBlock>(window, "ReportTitle").Text.Contains(language == "zh-CN" ? "稳定性周报" : "stability report", StringComparison.OrdinalIgnoreCase),
                    "Window chrome must follow the chosen report language.");
                Check(translated.Markdown.Contains(language == "zh-CN" ? "# Windows 系统稳定性周报" : "# Windows", StringComparison.Ordinal),
                    "The displayed report must own a saved Markdown document in the chosen language.");
                File.Copy(Property<string>(window.Content, "DisplayedPngPath"), Path.Combine(output, $"windows-stability-weekly-{language}.png"), true);
            }
            await SetLanguageAsync("", window);
            Check(Verify().Language == RuntimeLocalization.EffectiveLanguage && collections == before, "System-default language must resolve through the existing application localization setting.");
            await SetLanguageAsync("zh-CN", window);
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                ownerRoot.RequestedTheme = theme;
                await Task.Delay(50);
                await Until(() => !Property<bool>(window.Content, "IsRendering")
                    && Property<string>(window.Content, "DisplayedPngPath").EndsWith(ThemeSuffix(theme), StringComparison.Ordinal));
                await Property<Task>(window.Content, "ThemeRenderTask");
                Verify();
                string themedPng = Property<string>(window.Content, "DisplayedPngPath");
                Check(themedPng.EndsWith(ThemeSuffix(theme), StringComparison.Ordinal),
                    "Each theme must use its own PNG from the same document version.");
                File.Copy(themedPng, Path.Combine(output, $"windows-stability-weekly-{theme}.png"), true);
                await CheckImageThemeAsync(themedPng, theme);
                foreach (int width in new[] { 1000, 520 })
                {
                    window.AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 800));
                    await Task.Delay(100);
                    var view = (FrameworkElement)window.Content;
                    view.UpdateLayout();
                    var scroll = Control<ScrollViewer>(window, "ReportScroll");
                    var image = Control<Image>(window, "ReportImage");
                    Check(view.RequestedTheme == theme && scroll.ActualHeight > 100 && image.ActualWidth <= scroll.ActualWidth + 1,
                        "Mica host, image fit and scroll viewport must remain usable across themes and widths.");
                    Check(window.ExtendsContentIntoTitleBar, "The report title bar must be integrated into its own Mica surface.");
                    var chrome = Control<Border>(window, "ReportChrome");
                    var footer = Control<Border>(window, "ReportFooter");
                    Check(chrome.Background is SolidColorBrush { Color.A: > 0 and < 255 }
                        && footer.Background is SolidColorBrush { Color.A: > 0 and < 255 },
                        "Title and footer must retain translucent surfaces over Mica.");
                    var chromeColor = ((SolidColorBrush)chrome.Background).Color;
                    var titleColor = ((SolidColorBrush)Control<TextBlock>(window, "ReportTitle").Foreground).Color;
                    var captionColor = window.AppWindow.TitleBar.ButtonForegroundColor.Value;
                    bool light = theme == ElementTheme.Light;
                    Check(light ? chromeColor.R > 200 && titleColor.R < 60 && captionColor.R < 60
                        : chromeColor.R < 60 && titleColor.R > 200 && captionColor.R > 200,
                        "Window title, text and native captions must use the matching light or dark palette.");
                    Check(Control<Button>(window, "RefreshButton").ActualTheme == theme
                        && Control<Button>(window, "FitButton").ActualTheme == theme,
                        "Report command buttons must inherit the window theme instead of forcing dark.");
                    var title = Control<Grid>(window, "ReportTitleBar");
                    var toolbar = Control<Border>(window, "ReportToolbar");
                    var footerPosition = footer.TransformToVisual(view).TransformPoint(new Windows.Foundation.Point());
                    Check(Math.Abs(title.ActualHeight - 48) < 1 && Math.Abs(footerPosition.Y + footer.ActualHeight - view.ActualHeight) < 1
                        && toolbar.ActualHeight >= 40, "Title, wrapping commands and footer must share the report layout without overlaying its image viewport.");
                    await CaptureWindowAsync(window, $"windows-stability-window-{theme}-{width}.png", theme);
                }
            }
            Click(Control<Button>(window, "FitButton"));
            await Task.Delay(50);
            Check(Control<Image>(window, "ReportImage").Width == 1800, "Original size exposes the full PNG for detailed reading.");
            Click(Control<Button>(window, "FitButton"));

            ownerRoot.RequestedTheme = ElementTheme.Light;
            await Until(() => !Property<bool>(window.Content, "IsRendering")
                && Property<string>(window.Content, "DisplayedPngPath").EndsWith(ThemeSuffix(ElementTheme.Light), StringComparison.Ordinal));
            await Property<Task>(window.Content, "ThemeRenderTask");
            string lightPath = Property<string>(window.Content, "DisplayedPngPath");
            string darkPath = lightPath.Replace(ThemeSuffix(ElementTheme.Light), ThemeSuffix(ElementTheme.Dark), StringComparison.Ordinal);
            Check(lightPath != darkPath, "The race fixture starts from a completed light image.");
            if (File.Exists(darkPath)) File.Delete(darkPath);
            fail = true;
            ownerRoot.RequestedTheme = ElementTheme.Dark;
            await Refresh();
            await Until(() => !Property<bool>(window.Content, "IsRendering")
                && Property<string>(window.Content, "DisplayedPngPath").EndsWith(ThemeSuffix(ElementTheme.Dark), StringComparison.Ordinal));
            Check(Verify().Id == first.Id && Control<TextBlock>(window, "ReportStatus").Text.Contains("失败", StringComparison.Ordinal),
                "A failed update retains the previous complete document and visible PNG.");
            Check(Property<string>(window.Content, "DisplayedPngPath").EndsWith(ThemeSuffix(ElementTheme.Dark), StringComparison.Ordinal),
                "A failed refresh that interrupts a theme render must still restore the retained report in the current theme.");
            fail = false;
            await Refresh();
            var updated = Verify();
            Check(updated.Id != first.Id && !Control<TextBlock>(window, "ReportStatus").Text.Contains("失败", StringComparison.Ordinal),
                "Successful explicit refresh publishes a new document and matching PNG.");

            File.WriteAllText(Property<string>(window.Content, "DisplayedPngPath"), "broken image");
            before = collections;
            await Open();
            Check(Verify().Id == updated.Id && collections == before, "A damaged PNG is rebuilt from the same Markdown without reading Windows logs again.");
            bool monthlyRejected = false;
            try
            {
                await (Task)window.Content.GetType().GetMethod("SetDocumentAsync", Flags).Invoke(window.Content,
                    new object[] { updated with { Monthly = true }, CancellationToken.None });
            }
            catch (ArgumentException)
            {
                monthlyRejected = true;
            }
            Check(monthlyRejected && Verify().Id == updated.Id,
                "A monthly document cannot enter the weekly-only view or replace its complete weekly report.");

            pauseReview = true;
            int reviewsBefore = reviews;
            Click(Control<Button>(window, "RefreshButton"));
            await Until(() => reviews > reviewsBefore);
            Check(Control<Button>(window, "CancelButton").Visibility == Visibility.Visible
                && Control<TextBlock>(window, "ReportStatus").Text.Length > 0, "AI review must expose progress and cancellation.");
            Check(Control<Border>(window, "GenerationPanel").Visibility == Visibility.Visible
                && Grid.GetRowSpan(Control<Border>(window, "GenerationPanel")) == 1
                && Control<Image>(window, "ReportImage").Source != null
                && Property<WindowsStabilityReportDocument>(window.Content, "DisplayedDocument").Id == updated.Id
                && Control<TextBlock>(window, "RetainedPreviewNote").Visibility == Visibility.Visible,
                "Updating a report must keep the previous preview visible below a compact progress panel.");
            Click(Control<Button>(window, "CancelButton"));
            await Until(() => reviewCancelled && !Property<bool>(window, "IsLoading"));
            Check(Verify().Id == updated.Id && Control<TextBlock>(window, "ReportStatus").Text.Contains("取消", StringComparison.Ordinal),
                "Cancelling AI review preserves the previous Full report and reports cancellation.");
            pauseReview = false;

            pause = true;
            before = collections;
            Click(Control<Button>(window, "RefreshButton"));
            await Until(() => collections > before);
            await SelectMode(WeeklyReportMode.Fast);
            await Until(() => cancelled);
            Check(Verify().Id == fast.Id && Verify().Data.WeeklySource.Mode == WeeklyReportMode.Fast,
                "Changing modes cancels an active Full collection and cannot publish it into Fast.");
            int callsAfterSwitch = collections;
            await SelectMode(WeeklyReportMode.Full);
            Check(Verify().Id == updated.Id && collections == callsAfterSwitch,
                "Returning to Full reuses its own completed image without another collection or model request.");

            // A provider may finish despite cancellation. The window must still reject that response.
            var loaderField = window.GetType().GetField("_load", Flags);
            var originalLoader = loaderField.GetValue(window);
            var releaseLateResponse = new TaskCompletionSource<bool>();
            Task lateGeneration = null;
            try
            {
                Func<WeeklyReportMode, bool, IProgress<WeeklyReportProgress>, CancellationToken, Task<WindowsStabilityReportDocument>> lateLoader = async (_, _, _, _) =>
                {
                    await releaseLateResponse.Task;
                    return updated;
                };
                loaderField.SetValue(window, lateLoader);
                lateGeneration = (Task)window.GetType().GetMethod("ReloadAsync", Flags).Invoke(window, new object[] { true });
                await SelectMode(WeeklyReportMode.Fast);
                releaseLateResponse.TrySetResult(true);
                await lateGeneration;
                Check(Verify().Id == fast.Id && Verify().Data.WeeklySource.Mode == WeeklyReportMode.Fast,
                    "A late Full response that ignores cancellation cannot replace the selected Fast document or image.");
            }
            finally
            {
                releaseLateResponse.TrySetResult(true);
                if (lateGeneration != null) await lateGeneration;
                loaderField.SetValue(window, originalLoader);
            }
            await SelectMode(WeeklyReportMode.Full);
            Check(Verify().Id == updated.Id, "Rejecting a stale response must preserve the completed Full document.");

            before = collections;
            dateField.SetValue(vm, (DateTimeOffset?)DateTimeOffset.Now.AddDays(-1));
            await Open(hasDocument: false);
            Check(collections == before && Control<Image>(window, "ReportImage").Source == null,
                "Opening Full for a different date clears the old date's document without collecting or invoking AI.");
            dateField.SetValue(vm, (DateTimeOffset?)DateTimeOffset.Now);
            int reviewsBeforeDateReturn = reviews, identitiesBeforeDateReturn = identities;
            await Open();
            Check(collections == before && reviews == reviewsBeforeDateReturn && identities == identitiesBeforeDateReturn && Verify().Id == updated.Id,
                "Returning to a date restores its own persisted Full document with no collection or AI work.");
            cancelled = false;
            pause = true;
            before = collections;
            Click(Control<Button>(window, "RefreshButton"));
            await Until(() => collections > before);
            window.Close();
            await Until(() => cancelled && Property<bool>(window, "IsClosed"));
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Closing during generation cancels collection without publishing temporary output.");
        }
        finally
        {
            window?.Close();
            analyzing.SetValue(vm, false);
            if (storeField.GetValue(vm) is WeeklyReportService injected && !ReferenceEquals(injected, originalStore))
            {
                await injected.DisposeAsync();
            }
            storeField.SetValue(vm, originalStore);
            dateField.SetValue(vm, originalDate);
            AppearanceService.RequestLanguage(originalLanguage);
            await AppearanceService.WhenIdleAsync();
            ownerRoot.RequestedTheme = originalTheme;
            owner.Activate();
            Directory.Delete(directory, true);
        }
        File.AppendAllText(Path.Combine(output, "aihub-ux-smoke.log"), "Weekly Fast/Full: missing-scan prerequisite, Fast zero collection/model calls, explicit Full generation, visible first-generation AI progress and live elapsed time, bilingual themed Mica progress at narrow widths, retained preview during update, stopped cancellation animation, separate mode/date documents, persisted Full reopen without collection/AI, seven-day evidence, progress, review cancellation, mode-switch cancellation, stale response rejection, failure preservation, real PNG/Markdown, bilingual/theme cache, monthly rejection and close cancellation PASS. Synthetic collector/reviewer only; no real collection or model requests.\n");
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags).GetValue(target);
    private static string ThemeSuffix(ElementTheme theme) => $"-{(theme == ElementTheme.Dark ? "dark" : "light")}-v{WindowsStabilityDashboardRenderer.RenderRevision}.png";
    private static async Task CheckImageThemeAsync(string path, ElementTheme theme)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var pixel = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Straight,
            new Windows.Graphics.Imaging.BitmapTransform { Bounds = new Windows.Graphics.Imaging.BitmapBounds { X = 10, Y = 10, Width = 1, Height = 1 } },
            Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        byte[] channels = pixel.DetachPixelData();
        Check(channels[3] is > 0 and < 255 && (theme == ElementTheme.Light
            ? channels[0] > 200 && channels[1] > 200 && channels[2] > 200
            : channels[0] < 80 && channels[1] < 80 && channels[2] < 80),
            "The persisted PNG header must match its theme and preserve translucency.");
    }
    private static async Task SetLanguageAsync(string language, Window window)
    {
        AppearanceService.RequestLanguage(language);
        await AppearanceService.WhenIdleAsync();
        await Until(() => !Property<bool>(window.Content, "IsRendering")
            && Property<WindowsStabilityReportDocument>(window.Content, "DisplayedDocument")?.Language == RuntimeLocalization.EffectiveLanguage);
        await Property<Task>(window.Content, "LanguageRenderTask");
    }
    private static Task CaptureWindowAsync(Window window, string name, ElementTheme theme) => AiHubUxSmoke.CaptureElement(
        (FrameworkElement)window.Content, name, theme == ElementTheme.Dark ? Microsoft.UI.ColorHelper.FromArgb(255, 25, 34, 49) : Microsoft.UI.ColorHelper.FromArgb(255, 235, 241, 247));
    private static T Property<T>(object target, string name) => (T)target.GetType().GetProperty(name, Flags).GetValue(target);
    private static T Control<T>(Window window, string name) where T : FrameworkElement => (T)((FrameworkElement)window.Content).FindName(name);
    private static void Click(Button button) => ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready()) { if (timer.Elapsed > TimeSpan.FromSeconds(40)) throw new TimeoutException("Stability UI did not finish."); await Task.Delay(25); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
