using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Kit.Settings.UI;
using Kit.Settings.UI.Controls;
using Kit.Settings.UI.Services;
using Kit.Settings.UI.ViewModels;
using Kit.Settings.UI.Views;
using LocalServerHub.Core.Models;
using LocalServerHub.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;

// The plugin suite uses the same apphost, fake IPC, typography and screenshot
// helpers as the original suite. All modules remain disabled throughout. The
// wrapper isolates Localserver catalogs before any page can recover services.
internal static partial class TypographySmoke
{
    private static readonly List<object> PluginGeometry = new();

    private static async Task PluginSettle()
    {
        // NavigationView and SettingsCardsAnimations animate beyond the first
        // composition frame. Wait for their native transitions before measuring
        // or capturing; partial frames can otherwise extend outside the window.
        await Task.Delay(500);
        await Settle();
    }

    private static async Task RunPlugins()
    {
        var shell = (FrameworkElement)App.GetSettingsWindow().Content;
        var navigation = Descendants(shell).OfType<NavigationView>().Single();
        Require(App.GetSettingsWindow().SystemBackdrop is MicaBackdrop,
            "The real Settings window must use MicaBackdrop.");
        Log("MicaBackdrop object verified. PNGs use the documented opaque substitute; dynamic desktop compositing is not exercised by RenderTargetBitmap.");
        bool brushOnly = Environment.GetEnvironmentVariable("KIT_TYPOGRAPHY_SMOKE_BRUSH_ONLY") == "1";
        await VerifyThemeBrushResources();
        if (brushOnly)
        {
            Require(TypographyFailures.Count == 0, "Brush-only failures:\n" + string.Join("\n", TypographyFailures));
            return;
        }
        var pages = new[] { typeof(AIHubPage), typeof(AwakePage), typeof(LightSwitchPage),
            typeof(LocalserverPage), typeof(NetMapPage), typeof(UDPtestPage), typeof(WingetPage) };
        var filter = (Environment.GetEnvironmentVariable("KIT_TYPOGRAPHY_SMOKE_PLUGIN_FILTER") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (filter.Length > 0)
        {
            pages = pages.Where(type => filter.Contains(type.Name.Replace("Page", ""), StringComparer.OrdinalIgnoreCase)).ToArray();
            Require(pages.Length == filter.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Plugin filter must resolve every requested page.");
        }
        Log("Plugin pages: " + string.Join(", ", pages.Select(type => type.Name)));
        foreach (var pageType in pages)
        {
            await Navigate(pageType);
            var page = (Page)NavigationService.Frame.Content;
            await PreparePluginLayout(page);
            await PluginSettle();
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                shell.RequestedTheme = theme;
                foreach (int width in new[] { 1280, 760 })
                {
                    App.GetSettingsWindow().AppWindow.Resize(new Windows.Graphics.SizeInt32(width, 1100));
                    await PluginSettle();
                    navigation.IsPaneOpen = width == 1280;
                    await PluginSettle();
                    VerifyPluginSafety(page);
                    var scrolls = Descendants(page).OfType<ScrollViewer>()
                        .Where(scroll => IsVisible(scroll, page) && scroll.ActualWidth > 100 && scroll.ActualHeight > 80 &&
                            !HasEditorAncestor(scroll, page)).ToArray();
                    foreach (var scroll in scrolls) scroll.ChangeView(null, 0, null, true);
                    await PluginSettle();
                    string label = $"{pageType.Name.Replace("Page", "").ToLowerInvariant()}-{theme}-{width}";
                    VerifyPluginGeometry(page, label);
                    await CaptureShell(label + "-top-full-shell.png");
                    if (page is NetMapPage netmap) await VerifyNetMapEvidenceFlyout(netmap, label);

                    // Capture each visible vertical region, with an overlap so
                    // card boundaries and bottom actions can be reviewed too.
                    var mainScroll = scrolls.Where(scroll => scroll.ScrollableHeight > 1)
                        .OrderByDescending(scroll => scroll.ActualWidth * scroll.ActualHeight).FirstOrDefault();
                    if (mainScroll != null)
                    {
                        double step = Math.Max(100, mainScroll.ViewportHeight * 0.8);
                        int index = 0;
                        for (double offset = Math.Min(step, mainScroll.ScrollableHeight); ; offset = Math.Min(offset + step, mainScroll.ScrollableHeight))
                        {
                            mainScroll.ChangeView(null, offset, null, true);
                            await PluginSettle();
                            VerifyPluginGeometry(page, label + "-scroll" + index);
                            bool bottom = offset >= mainScroll.ScrollableHeight - 1;
                            await CaptureShell(label + (bottom ? "-bottom" : "-scroll" + index) + "-full-shell.png");
                            if (bottom) break;
                            Require(++index < 24, "Unexpectedly unbounded plugin scrolling.");
                        }
                        mainScroll.ChangeView(null, 0, null, true);
                    }
                }
            }
            VerifyPluginSafety(page);
            if (page is AwakePage awake) await VerifyPageLinkLocalization(awake);
        }
        File.WriteAllText(Path.Combine(Output, "plugin-geometry.json"), JsonSerializer.Serialize(PluginGeometry, new JsonSerializerOptions { WriteIndented = true }));
        Require(ForbiddenMessages.Count == 0, "Plugin layout checks do not restart Settings or check/download updates.");
        EnsureNoWorker();
        Require(TypographyFailures.Count == 0, "Plugin layout/typography failures:\n" + string.Join("\n", TypographyFailures.Distinct()));
        Log($"{pages.Length} plugin pages: real native pages, Light/Dark, 1280/760, full scrolling, disabled workers, typography and geometry PASS");
    }

    private static async Task PreparePluginLayout(Page page)
    {
        switch (page)
        {
            case AIHubPage ai:
                await ai.ViewModel.AiHub.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                if (ai.ViewModel.AiHub.CanToggle)
                    await WaitUntil(() => !string.IsNullOrWhiteSpace(ai.ViewModel.AiReadinessText), "AI Hub initial cached readiness applied");
                ai.ViewModel.ActiveTabIndex = 0;
                typeof(AIHubPageViewModel).GetProperty(nameof(AIHubPageViewModel.CurrentEngineAndModelLabel))
                    .SetValue(ai.ViewModel, "Codex CLI / deepseek-v4.1-flash");
                Require(ai.ViewModel.HasActiveModelInfo, "AI Hub readiness fixture exposes the model label without enabling a worker.");
                Log("AI Hub readiness model label is an in-memory layout fixture; service enablement and settings remain unchanged.");
                break;
            case AwakePage awake:
                // Show both conditionally hidden input layouts without changing
                // the user's selected mode or enabling a power request.
                Element<FrameworkElement>(awake, "AwakeIntervalSettingsCard").Visibility = Visibility.Visible;
                Element<FrameworkElement>(awake, "AwakeExpirationSettingsExpander").Visibility = Visibility.Visible;
                Log("Awake layout fixture reveals interval and expiration panels; module and selected mode stay disabled/unchanged.");
                break;
            case LightSwitchPage light:
                foreach (string name in new[] { "Fixed_TurnOnCard", "Fixed_TurnOffCard", "SunLocation_Card", "SunOffset_Card", "TimelineCard" })
                    Element<FrameworkElement>(light, name).Visibility = Visibility.Visible;
                Element<FrameworkElement>(light, "NoScheduleCard").Visibility = Visibility.Collapsed;
                Log("LightSwitch layout fixture reveals fixed-time and sunrise panels; no location or theme operation is invoked.");
                break;
            case LocalserverPage local:
                await ((Task)typeof(LocalserverViewModel).GetField("_initializationTask", PrivateInstance).GetValue(local.ViewModel)).WaitAsync(TimeSpan.FromSeconds(15));
                Require(local.ViewModel.Lines.Count == 0, "Isolated Localserver catalog must be empty before fixture insertion.");
                foreach (var entry in new[] { ("preview-service", "Local development service", 0), ("preview-long-service", "Local inference endpoint with an intentionally longer name", 11434) })
                {
                    var definition = new ServiceDefinition
                    {
                        Id = entry.Item1, Name = entry.Item2, DefaultPort = entry.Item3 > 0 ? entry.Item3 : null,
                        Cwd = @"C:\Preview\service", Executable = "ui-preview-does-not-exist.exe",
                        BaseArgs = new[] { "--host", "127.0.0.1", "--port", entry.Item3.ToString() },
                        Description = "In-memory layout fixture; never started or persisted.",
                        Tags = new[] { "Preview", "Local" }, IsEnabled = false,
                    };
                    local.ViewModel.Lines.Add(new LineRowViewModel(new ServiceRunner(definition), local.ViewModel)
                    { IsConfigExpanded = entry.Item1 == "preview-service" });
                }
                local.ViewModel.SelectedLine = local.ViewModel.Lines[0];
                Require(local.ViewModel.Lines[0].ConfigVisibility == Visibility.Visible, "The first Localserver fixture exposes its actual service configuration editors.");
                Require(!(bool)typeof(LocalserverViewModel).GetField("_hasUnsavedChanges", PrivateInstance).GetValue(local.ViewModel), "Read-only Localserver fixtures must not dirty the catalog.");
                Log("Localserver has two inert in-memory rows, one expanded. No recovery/start/save API invoked.");
                break;
            case UDPtestPage udp:
                await ((Task)typeof(UDPtestViewModel).GetField("_initializationTask", PrivateInstance).GetValue(udp.ViewModel)).WaitAsync(TimeSpan.FromSeconds(15));
                Require(udp.ViewModel.UdpLines.Count > 0, "UDPtest default inert target rows are present.");
                udp.ViewModel.UdpLines[0].IsConfigExpanded = true;
                Require(udp.ViewModel.UdpLines[0].ConfigVisibility == Visibility.Visible, "UDPtest fixture exposes the actual target configuration editors without saving or probing.");
                break;
            case WingetPage winget:
                winget.ViewModel.ActiveTabIndex = 2;
                break;
        }
        // Expand actual settings templates to exercise the controls at the bottom
        // of each page. Expansion changes only presentation state.
        foreach (var expander in Descendants(page).OfType<CommunityToolkit.WinUI.Controls.SettingsExpander>().ToArray()) expander.IsExpanded = true;
        foreach (var expander in Descendants(page).OfType<Expander>().ToArray()) expander.IsExpanded = true;
        await PluginSettle();
        VerifyPluginSafety(page);
    }

    private static void VerifyPluginSafety(Page page)
    {
        object vm = page.GetType().GetProperty("ViewModel", BindingFlags.Public | PrivateInstance)?.GetValue(page);
        Require(vm != null && !(bool)vm.GetType().GetProperty("IsEnabled").GetValue(vm), page.GetType().Name + " remains disabled.");
        if (page is LocalserverPage local)
        {
            Require(!local.ViewModel.HasActiveServices && local.ViewModel.Lines.All(row => row.Runner.ProcessId == null && row.Runner.ProcessGeneration == 0), "Localserver never starts or adopts a service.");
            Require(!(bool)typeof(LocalserverViewModel).GetField("_hasUnsavedChanges", PrivateInstance).GetValue(local.ViewModel), "Localserver layout checks cannot schedule a save.");
        }
        if (page is NetMapPage net)
            Require(!net.ViewModel.IsDetectionOn && !net.ViewModel.IsUpdating, "NetMap probing and database download remain stopped.");
        if (page is UDPtestPage udp)
        {
            var coordinator = (UDPtestLib.Engine.ProbeCoordinator)typeof(UDPtestViewModel).GetField("_coordinator", PrivateInstance).GetValue(udp.ViewModel);
            Require(coordinator.State == UDPtestLib.Core.MonitorRunState.Stopped, "UDPtest coordinator remains stopped.");
        }
        var shell = (FrameworkElement)App.GetSettingsWindow().Content;
        var shellPage = shell as ShellPage ?? Descendants(shell).OfType<ShellPage>().Single();
        var root = Element<Grid>(shellPage, "RootGrid");
        Require(IsTransparent(root.Background), "Shell must preserve transparent Mica surface after captures.");
        var bounds = page.TransformToVisual(shell).TransformBounds(new Windows.Foundation.Rect(0, 0, page.ActualWidth, page.ActualHeight));
        Require(bounds.Left >= -2 && bounds.Right <= shell.ActualWidth + 2,
            $"{page.GetType().Name} must settle within the shell before capture: x={bounds.Left:F1}..{bounds.Right:F1}, shell={shell.ActualWidth:F1}.");
        EnsureNoWorker();
    }

    private static bool IsTransparent(Brush brush) => brush == null || brush is SolidColorBrush solid && solid.Color.A == 0;

    private static async Task VerifyPageLinkLocalization(AwakePage awake)
    {
        var control = Descendants(awake).OfType<SettingsPageControl>().Single();
        var link = control.PrimaryLinks.Single();
        var labels = Descendants(awake).OfType<TextBlock>().Where(text => text.Text == link.Text && IsVisible(text, awake)).ToArray();
        Require(labels.Length > 0, "Awake primary PageLink has an actual realized text label.");
        string initial = link.Text;
        foreach (string language in new[] { IsChinese ? "en-US" : "zh-CN", RequestedLanguage })
        {
            await Navigate(typeof(GeneralPage));
            await SwitchLanguage((GeneralPage)NavigationService.Frame.Content, language);
            await Navigate(typeof(AwakePage));
            Require(ReferenceEquals(awake, NavigationService.Frame.Content) && ReferenceEquals(link, control.PrimaryLinks.Single()), "Live language keeps the cached Awake page and same PageLink object.");
            Require(link.Text == Expected("LearnMore_Awake.Text") && labels.All(label => label.Text == link.Text), "The same PageLink and realized hyperlink label update to authored translation " + ActiveLanguage + ".");
            Require(control.SecondaryLinks.Single().Text == Expected("SecondaryLink_Awake.Text"), "Awake secondary PageLink also updates its live localized text.");
            VerifyPluginSafety(awake);
        }
        Require(link.Text == initial, "PageLink language roundtrip restores its original text.");
        Log("Awake PageLink: same page, same model and realized label pass live en/zh roundtrip.");
    }

    private static void VerifyPluginGeometry(Page page, string label)
    {
        int firstFailure = TypographyFailures.Count;
        if (page is LightSwitchPage)
            CheckTypography(!Descendants(page).OfType<FrameworkElement>().Any(element =>
                    Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(element) == "Shortcut_LightSwitch" ||
                    RuntimeLocalization.GetUid(element).StartsWith("LightSwitch_Shortcut", StringComparison.Ordinal) ||
                    RuntimeLocalization.GetUid(element) == "LightSwitch_ThemeToggle_Shortcut"),
                $"{label}: LightSwitch must not retain shortcut settings controls.");
        if (page is NetMapPage netmap) VerifyNetMapEvidenceInfo(netmap, label);
        var scrolls = Descendants(page).OfType<ScrollViewer>().Where(scroll => IsVisible(scroll, page) && scroll.ActualWidth > 100 && !HasEditorAncestor(scroll, page)).ToArray();
        foreach (var scroll in scrolls)
        {
            bool hasOverflow = scroll.ExtentWidth > scroll.ViewportWidth + 2;
            bool explicitHorizontalTable = scroll.HorizontalScrollMode != ScrollMode.Disabled &&
                scroll.HorizontalScrollBarVisibility is ScrollBarVisibility.Auto or ScrollBarVisibility.Visible &&
                scroll.VerticalScrollMode == ScrollMode.Disabled;
            CheckTypography(!hasOverflow || explicitHorizontalTable,
                $"{label}: page/hidden horizontal overflow in {scroll.Name}: extent={scroll.ExtentWidth:F1}, viewport={scroll.ViewportWidth:F1}.");
            if (hasOverflow && explicitHorizontalTable)
                Log($"{label}: explicit horizontally scrollable table: extent={scroll.ExtentWidth:F1}, viewport={scroll.ViewportWidth:F1}; page stays within viewport.");
        }

        int textCount = 0;
        foreach (var text in Descendants(page).OfType<TextBlock>())
        {
            if (!IsVisible(text, page) || text.ActualWidth <= 0 || text.ActualHeight <= 0 || string.IsNullOrWhiteSpace(text.Text) || HasEditorAncestor(text, page)) continue;
            string family = text.FontFamily.Source;
            if (family.Contains("Icons", StringComparison.OrdinalIgnoreCase) || family.Contains("MDL2", StringComparison.OrdinalIgnoreCase) || text.Text.All(character => character >= '\uE000' && character <= '\uF8FF')) continue;
            textCount++;
            double baseline = TypographyResources.GetBaseFontSize(text);
            if (baseline > 0)
                CheckTypography(Near(text.FontSize, Scaled(baseline)), $"{label}: {TextId(text)} size={text.FontSize}, expected={Scaled(baseline)}.");
            string mono = ((FontFamily)Application.Current.Resources["KitMonoFontFamily"]).Source;
            CheckTypography(family == ExpectedFamily || family == mono, $"{label}: {TextId(text)} uses unexpected family {family}.");

            // Explicit ellipsis/max-lines and editor interiors are intentional
            // truncation. Untrimmed labels and buttons must fit their layout box.
            if (text.TextTrimming != TextTrimming.None || text.MaxLines > 0) continue;
            var measured = new TextBlock
            {
                Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize,
                FontWeight = text.FontWeight, FontStyle = text.FontStyle, FontStretch = text.FontStretch,
                CharacterSpacing = text.CharacterSpacing, TextWrapping = text.TextWrapping,
                LineHeight = text.LineHeight, LineStackingStrategy = text.LineStackingStrategy,
                TextLineBounds = text.TextLineBounds,
                Padding = text.Padding,
            };
            // Layout rounds at the window's rasterization scale. Re-measuring a
            // detached label at its fractional ActualWidth can introduce one
            // spurious line; the same two-pixel allowance is used for bounds.
            measured.Measure(new Windows.Foundation.Size(text.TextWrapping == TextWrapping.NoWrap ? double.PositiveInfinity : text.ActualWidth + 2, double.PositiveInfinity));
            if (text.TextWrapping == TextWrapping.NoWrap)
                CheckTypography(measured.DesiredSize.Width <= text.ActualWidth + 2,
                    $"{label}: clipped width {TextId(text)}: needed={measured.DesiredSize.Width:F1}, actual={text.ActualWidth:F1}.");
            CheckTypography(measured.DesiredSize.Height <= text.ActualHeight + 2,
                $"{label}: clipped height {TextId(text)}: needed={measured.DesiredSize.Height:F1}, actual={text.ActualHeight:F1}.");
        }
        object readiness = page is AIHubPage ai ? VerifyAiReadinessGeometry(ai, label) : null;
        PluginGeometry.Add(new { Label = label, PageWidth = page.ActualWidth, PageHeight = page.ActualHeight,
            TextCount = textCount, Scrolls = scrolls.Select(scroll => new { scroll.Name, scroll.ExtentWidth, scroll.ViewportWidth, scroll.ExtentHeight, scroll.ViewportHeight, scroll.VerticalOffset }).ToArray(),
            Readiness = readiness,
            Failures = TypographyFailures.Skip(firstFailure).ToArray() });
        File.WriteAllText(Path.Combine(Output, "plugin-geometry.json"), JsonSerializer.Serialize(PluginGeometry, new JsonSerializerOptions { WriteIndented = true }));
        Log($"Geometry {label}: {textCount} visible text blocks, {scrolls.Length} scroll regions, {TypographyFailures.Count - firstFailure} issues.");
    }

    private static void VerifyNetMapEvidenceInfo(NetMapPage page, string label)
    {
        var button = Element<Button>(page, "NetMapEvidenceInfoButton");
        string explanation = page.ViewModel.EvidenceNote;
        CheckTypography(!string.IsNullOrWhiteSpace(explanation) && IsVisible(button, page) &&
            Near(button.ActualWidth, 32) && Near(button.ActualHeight, 32) && button.IsTabStop && button.IsEnabled,
            $"{label}: NetMap scope information remains a visible 32px keyboard-accessible button.");
        CheckTypography(button.Content is FontIcon { Glyph: "\uE946" } &&
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == explanation,
            $"{label}: NetMap scope info uses an information icon and the full accessible explanation.");
        CheckTypography(ToolTipService.GetToolTip(button) is ToolTip { Content: TextBlock tooltip } &&
            tooltip.Text == explanation && tooltip.TextWrapping == TextWrapping.Wrap && tooltip.TextTrimming == TextTrimming.None && tooltip.MaxLines == 0,
            $"{label}: NetMap scope tooltip preserves the complete wrapped explanation.");
        CheckTypography(button.Flyout is Flyout { Content: TextBlock flyoutText } &&
            flyoutText.Text == explanation && flyoutText.TextWrapping == TextWrapping.Wrap && flyoutText.TextTrimming == TextTrimming.None && flyoutText.MaxLines == 0,
            $"{label}: NetMap scope flyout preserves the complete wrapped explanation.");
        var status = Element<FrameworkElement>(page, "HeroTelemetryStatus");
        CheckTypography(!Descendants(status).OfType<TextBlock>().Any(text => text.Text == explanation),
            $"{label}: NetMap header must not repeat the long explanation as a status pill.");
    }

    private static async Task VerifyNetMapEvidenceFlyout(NetMapPage page, string label)
    {
        var button = Element<Button>(page, "NetMapEvidenceInfoButton");
        var flyout = button.Flyout as Flyout;
        Require(flyout?.Content is TextBlock, "NetMap scope explanation is available through its native flyout.");
        var text = (TextBlock)flyout.Content;
        try
        {
            flyout.ShowAt(button);
            await PluginSettle();
            VerifyNetMapEvidenceInfo(page, label + "-info-open");
            VerifyText(text, 14, label + " scope flyout");
            CheckTypography(text.IsLoaded && text.ActualWidth > 0 && text.ActualWidth <= 361 && text.ActualHeight > 0,
                $"{label}: opened NetMap scope text has bounded, nonempty geometry.");
            var measured = new TextBlock
            {
                Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize,
                FontWeight = text.FontWeight, TextWrapping = text.TextWrapping,
                TextLineBounds = text.TextLineBounds, LineHeight = text.LineHeight,
                LineStackingStrategy = text.LineStackingStrategy, Padding = text.Padding,
            };
            measured.Measure(new Windows.Foundation.Size(text.ActualWidth + 2, double.PositiveInfinity));
            CheckTypography(measured.DesiredSize.Height <= text.ActualHeight + 2,
                $"{label}: scope explanation is clipped vertically: needed={measured.DesiredSize.Height:F1}, actual={text.ActualHeight:F1}.");
            DependencyObject parent = text;
            while (parent != null && parent is not FlyoutPresenter) parent = VisualTreeHelper.GetParent(parent);
            Require(parent is FlyoutPresenter, "The native scope flyout presenter is realized.");
            var presenter = (FlyoutPresenter)parent;
            var textBounds = text.TransformToVisual(presenter).TransformBounds(new Windows.Foundation.Rect(0, 0, text.ActualWidth, text.ActualHeight));
            var shell = (FrameworkElement)App.GetSettingsWindow().Content;
            CheckTypography(presenter.ActualWidth <= shell.ActualWidth + 2 &&
                textBounds.Left >= -2 && textBounds.Right <= presenter.ActualWidth + 2 &&
                textBounds.Top >= -2 && textBounds.Bottom <= presenter.ActualHeight + 2,
                $"{label}: scope explanation stays inside its flyout and the window width.");
            foreach (var scroll in Descendants(presenter).OfType<ScrollViewer>())
                CheckTypography(scroll.ExtentWidth <= scroll.ViewportWidth + 2,
                    $"{label}: scope flyout has horizontal overflow: extent={scroll.ExtentWidth:F1}, viewport={scroll.ViewportWidth:F1}.");
            await CaptureShell(label + "-scope-info-full-shell.png");
        }
        finally
        {
            flyout.Hide();
            await PluginSettle();
        }

        VerifyPluginSafety(page);
    }

    private static object VerifyAiReadinessGeometry(AIHubPage page, string label)
    {
        var bar = Element<FrameworkElement>(page, "AiReadinessBar");
        var status = Element<TextBlock>(page, "AiReadinessText");
        var icon = Element<FrameworkElement>(page, "AiReadinessIcon");
        var actions = Element<FrameworkElement>(page, "AiReadinessActions");
        var settings = Element<FrameworkElement>(page, "AiReadinessSettingsLink");
        var settingsText = Element<TextBlock>(page, "AiReadinessSettingsText");
        var recheck = Element<FrameworkElement>(page, "AiHubRecheckButton");
        var modelInfo = Element<FrameworkElement>(page, "AiReadinessModelInfo");
        bool modelVisible = IsVisible(modelInfo, page);
        var measured = new List<object>();

        Windows.Foundation.Rect Bounds(FrameworkElement element) => element.TransformToVisual(bar)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

        void CheckCentered(FrameworkElement element, string name)
        {
            var bounds = Bounds(element);
            double centerDelta = Math.Abs(bounds.Top + bounds.Height / 2 - bar.ActualHeight / 2);
            CheckTypography(IsVisible(element, page) && bounds.Width > 0 && bounds.Height > 0,
                $"{label}: readiness {name} must remain visible and measurable.");
            CheckTypography(centerDelta <= 1,
                $"{label}: readiness {name} center differs from bar by {centerDelta:F3}px (bar height={bar.ActualHeight:F3}, bounds={bounds}).");
            CheckTypography(bounds.Left >= -1 && bounds.Right <= bar.ActualWidth + 1 && bounds.Top >= -1 && bounds.Bottom <= bar.ActualHeight + 1,
                $"{label}: readiness {name} overflows its bar: bounds={bounds}, bar={bar.ActualWidth:F3}x{bar.ActualHeight:F3}.");
            measured.Add(new { Name = name, bounds.X, bounds.Y, bounds.Width, bounds.Height, CenterDelta = centerDelta });
        }

        foreach (var entry in new[] { (icon, "icon"), (status, "status"), (actions, "actions"),
            (settings, "settings link"), (settingsText, "settings text"), (recheck, "recheck") })
            CheckCentered(entry.Item1, entry.Item2);

        CheckTypography(Bounds(icon).Right <= Bounds(status).Left + 1,
            $"{label}: readiness status overlaps its icon.");
        CheckTypography(Bounds(status).Right <= Bounds(actions).Left + 1,
            $"{label}: readiness status overlaps its actions.");
        CheckTypography(Bounds(settings).Right <= Bounds(recheck).Left + 1,
            $"{label}: readiness settings and recheck actions overlap.");

        if (modelVisible)
        {
            var modelText = Element<TextBlock>(page, "AiReadinessModelText");
            var badge = VisualTreeHelper.GetParent(modelText) as Border;
            Require(badge != null, "AI Hub model text is contained by its actual badge border.");
            CheckCentered(badge, "model badge");
            CheckCentered(modelText, "model text");
            var badgeBounds = Bounds(badge);
            var textBounds = Bounds(modelText);
            CheckTypography(Math.Abs(textBounds.Top + textBounds.Height / 2 - badgeBounds.Top - badgeBounds.Height / 2) <= 1,
                $"{label}: readiness model text must be vertically centered inside its badge.");
            CheckTypography(Bounds(status).Right <= badgeBounds.Left + 1 && badgeBounds.Right <= Bounds(actions).Left + 1,
                $"{label}: readiness model badge overlaps the status or actions.");
            CheckTypography(textBounds.Left >= badgeBounds.Left + badge.BorderThickness.Left + badge.Padding.Left - 1 &&
                textBounds.Right <= badgeBounds.Right - badge.BorderThickness.Right - badge.Padding.Right + 1 &&
                textBounds.Top >= badgeBounds.Top + badge.BorderThickness.Top + badge.Padding.Top - 1 &&
                textBounds.Bottom <= badgeBounds.Bottom - badge.BorderThickness.Bottom - badge.Padding.Bottom + 1,
                $"{label}: readiness model text exceeds its badge content bounds.");
            CheckTypography(modelText.Text == "Codex CLI / deepseek-v4.1-flash" &&
                modelText.TextTrimming == TextTrimming.CharacterEllipsis && modelText.TextWrapping == TextWrapping.NoWrap,
                $"{label}: readiness fixture retains the complete model value and truncates through one-line ellipsis.");
        }

        return new { BarWidth = bar.ActualWidth, BarHeight = bar.ActualHeight, ModelVisible = modelVisible, Elements = measured.ToArray() };
    }

    private static string TextId(TextBlock text) => string.IsNullOrEmpty(text.Name) ? text.Text.Replace('\r', ' ').Replace('\n', ' ')[..Math.Min(70, text.Text.Length)] : text.Name;

    private static bool HasEditorAncestor(DependencyObject element, DependencyObject boundary)
    {
        for (var node = VisualTreeHelper.GetParent(element); node != null && node != boundary; node = VisualTreeHelper.GetParent(node))
            if (node is TextBox or RichEditBox or PasswordBox) return true;
        return false;
    }

    private static async Task VerifyThemeBrushResources()
    {
        string[] keys = { "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "TextFillColorDisabledBrush",
            "ControlStrongStrokeColorDefaultBrush", "SystemFillColorSuccessBrush", "SystemFillColorCautionBrush",
            "SystemFillColorCriticalBrush", "SystemFillColorAttentionBrush", "BadgeInfoForegroundBrush",
            "BadgeInfoBackgroundBrush", "BadgeSuccessForegroundBrush", "BadgeSuccessBackgroundBrush", "CardBackgroundFillColorDefaultBrush" };
        var shell = (FrameworkElement)App.GetSettingsWindow().Content;
        var shellPage = shell as ShellPage ?? Descendants(shell).OfType<ShellPage>().Single();
        var root = Element<Grid>(shellPage, "RootGrid");
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            // Resolve independent native ThemeResources in a real themed element.
            // No test resource dictionary or override is injected into the app.
            var reference = (StackPanel)XamlReader.Load("<StackPanel xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                string.Concat(keys.Select(key => "<TextBlock Tag='" + key + "' Text='reference' Foreground='{ThemeResource " + key + "}'/>")) + "</StackPanel>");
            reference.RequestedTheme = theme;
            reference.Opacity = 0;
            reference.IsHitTestVisible = false;
            reference.Width = reference.Height = 1;
            reference.HorizontalAlignment = HorizontalAlignment.Left;
            reference.VerticalAlignment = VerticalAlignment.Top;
            root.Children.Add(reference);
            try
            {
                await PluginSettle();
                foreach (TextBlock text in reference.Children)
                {
                    string key = (string)text.Tag;
                    Require(text.Foreground is SolidColorBrush, "Native reference is a solid brush: " + key);
                    var expected = ((SolidColorBrush)text.Foreground).Color;
                    if (Environment.GetEnvironmentVariable("KIT_TYPOGRAPHY_SMOKE_BRUSH_ONLY") == "1")
                    {
                        var detached = (Border)XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' RequestedTheme='" + theme + "' Background='{ThemeResource " + key + "}'/>");
                        var detachedColor = (detached.Background as SolidColorBrush)?.Color;
                        Log($"Detached {theme}/{key}: expected={expected}, actual={detachedColor}, match={detachedColor?.Equals(expected)}");
                    }
                    var actual = Kit.Settings.UI.Helpers.ThemeBrushHelper.GetBrush(key, new SolidColorBrush(Microsoft.UI.Colors.Magenta), theme) as SolidColorBrush;
                    CheckTypography(actual != null && actual.Color.Equals(expected), $"ThemeBrushHelper {theme}/{key}: expected={expected}, actual={actual?.Color}.");
                }
                Log($"Native {theme} ThemeResource comparison: {keys.Length} neutral/status/Badge/card brushes inspected.");
            }
            finally { root.Children.Remove(reference); }
        }
        await PluginSettle();
    }
}
