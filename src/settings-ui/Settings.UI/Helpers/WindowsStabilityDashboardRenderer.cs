// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace Kit.Settings.UI.Helpers;

public sealed record WindowsStabilityDashboardImage(byte[] PngBytes, int PixelWidth, int PixelHeight);

/// <summary>Rasterizes the evidence read back from one saved report. No collection, AI or filesystem writes occur here.</summary>
public static partial class WindowsStabilityDashboardRenderer
{
    public const int ImageWidth = 1800;
    public const int ImageHeight = 3100;
    public const int RenderRevision = 6;
    private const string PaletteResourceKey = "WindowsStabilityDashboardPalette";
    private const uint Ink = 0xFF172235;
    private const uint Muted = 0xFF63748E;
    private const uint LineColor = 0xFFDCE4F0;
    private const uint Blue = 0xFF2563B8;
    private const uint Teal = 0xFF087F8C;
    private const uint Red = 0xFFCD3941;
    private const uint Orange = 0xFFCD7819;
    private const uint Gold = 0xFFAD880F;
    private const uint Purple = 0xFF7049B8;
    private const uint Green = 0xFF15815B;
    private const uint White = 0xFFFFFFFF;
    private const double Margin = 44;
    private const double ContentWidth = ImageWidth - (Margin * 2);

    /// <summary>Call on the UI thread with a loaded host. The temporary visual remains attached until PNG encoding completes.</summary>
    public static async Task<WindowsStabilityDashboardImage> RenderAsync(
        WindowsStabilityReportData data, Panel renderHost, bool monthly = false, bool dark = false, bool english = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(renderHost);
        if (!renderHost.DispatcherQueue.HasThreadAccess || !renderHost.IsLoaded)
        {
            throw new InvalidOperationException("The report image needs a loaded host on its UI thread.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var canvas = Build(data, monthly, dark, english);
        AutomationProperties.SetAccessibilityView(canvas, AccessibilityView.Raw);
        renderHost.Children.Add(canvas);
        try
        {
            canvas.Measure(new Size(ImageWidth, ImageHeight));
            canvas.Arrange(new Rect(0, 0, ImageWidth, ImageHeight));
            canvas.UpdateLayout();
            await NextFrameAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var bitmap = new RenderTargetBitmap();

            // Explicit output dimensions keep the persisted image independent of desktop scale.
            await bitmap.RenderAsync(canvas, ImageWidth, ImageHeight);
            cancellationToken.ThrowIfCancellationRequested();
            if (bitmap.PixelWidth != ImageWidth || bitmap.PixelHeight != ImageHeight)
            {
                throw new InvalidOperationException("The report bitmap did not render at its required dimensions.");
            }
            var buffer = await bitmap.GetPixelsAsync();
            var pixels = new byte[checked((int)buffer.Length)];
            using (var reader = DataReader.FromBuffer(buffer))
            {
                reader.ReadBytes(pixels);
            }
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                ImageWidth, ImageHeight, 96, 96, pixels);
            await encoder.FlushAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var png = new byte[checked((int)stream.Size)];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync(checked((uint)png.Length));
                reader.ReadBytes(png);
            }
            return new(png, ImageWidth, ImageHeight);
        }
        finally
        {
            renderHost.Children.Remove(canvas);
        }
    }

    private static async Task NextFrameAsync(CancellationToken token)
    {
        var ready = new TaskCompletionSource<bool>();
        EventHandler<object> handler = (_, _) => ready.TrySetResult(true);
        CompositionTarget.Rendering += handler;
        try
        {
            await Task.WhenAny(ready.Task, Task.Delay(250, token));
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            CompositionTarget.Rendering -= handler;
        }
    }

    private static Canvas Build(WindowsStabilityReportData data, bool monthly, bool dark, bool english)
    {
        var canvas = new Canvas
        {
            Width = ImageWidth, Height = ImageHeight, Background = RawBrush(0x00000000),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false, UseLayoutRounding = true,
        };
        canvas.Resources[PaletteResourceKey] = new DashboardPalette(dark, english);
        Header(canvas, data, monthly);
        Metrics(canvas, data);
        DailyChart(canvas, data);
        TimelineChart(canvas, data);
        CrashChart(canvas, data);
        HeatmapAndNoise(canvas, data);
        RequiredChecks(canvas, data);
        Conclusions(canvas, data);
        Box(canvas, Margin, 3014, ContentWidth, 76, White, 12, LineColor);
        Text(canvas, L(canvas, "数据口径：按真实事件时间统计 · 缺失 / 截断明确标记 · 同一崩溃签名聚类不等于已确认根因",
            "Evidence: actual event timestamps · Missing and truncated data are marked · Matching crash signatures do not establish a root cause."), Margin + 18, 3022, ContentWidth - 36, 17, Muted, height: 27);
        string origin = data.WeeklySource is { Mode: WeeklyReportMode.Full }
            ? L(canvas, "Full · 独立日志分析，已有扫描仅作参考。", "Full · Independent log review; prior scans are references.")
            : data.WeeklySource is { Mode: WeeklyReportMode.Fast }
                ? L(canvas, "Fast · 复用七天扫描证据与已有结论，未新增 AI 调用。", "Fast · Reused seven-day scan evidence and conclusions; no new AI call.")
                : L(canvas, "本地规则摘要。", "Local rule-based summary.");
        string sourceNote = origin + L(canvas, " 缺失不等于零；无完整基线不推断趋势。依据、来源与完整建议见同版 Markdown。",
            " Missing is not zero; trends require a complete baseline. Evidence, sources and full advice are in the matching Markdown.");
        Text(canvas, sourceNote, Margin + 18, 3051, ContentWidth - 36, 17, Muted, height: 32);
        return canvas;
    }

    private static void Header(Canvas canvas, WindowsStabilityReportData data, bool monthly)
    {
        bool dark = IsDark(canvas);
        uint titleColor = dark ? White : Ink;
        uint secondaryColor = dark ? 0xFFB9C7D9 : Muted;
        uint machineColor = dark ? 0xFF91A6BF : Muted;
        uint gradeColor = dark ? 0xFFFFCB78 : 0xFF8B5709;
        Box(canvas, 0, 0, ImageWidth, 220, dark ? 0xFF172333 : 0xF2EDF3FA);
        Box(canvas, 0, 214, ImageWidth, 6, Teal);
        string title = monthly ? L(canvas, "Windows 系统稳定性月报", "Windows Stability · Monthly Report")
            : L(canvas, "Windows 系统稳定性周报", "Windows Stability · Weekly Report");
        Text(canvas, title, Margin, 28, 1260, IsEnglish(canvas) ? 42 : 48, titleColor, bold: true, height: 68);
        Text(canvas, L(canvas, $"本期 {data.Start:yyyy-MM-dd HH:mm} — {data.End:yyyy-MM-dd HH:mm}  ·  事件时间 {data.Start:zzz}",
            $"Current {data.Start:yyyy-MM-dd HH:mm} — {data.End:yyyy-MM-dd HH:mm}  ·  Event time {data.Start:zzz}"), Margin, 103, 1270, 22, secondaryColor, height: 35);
        Text(canvas, Plain(data.Machine, L(canvas, "环境信息未采集", "Environment information was not collected")), Margin, 145, 1270, 19, machineColor, height: 48, maxLines: 2);
        Box(canvas, 1390, 26, 366, 166, dark ? 0xFF26364D : 0xF2E4ECF7, 16, dark ? 0xFF435975 : 0xFFB7C9DF);
        Text(canvas, L(canvas, "稳定性结论", "Stability assessment"), 1413, 40, 320, 18, secondaryColor, height: 28);
        string grade = Plain(data.Grade, L(canvas, "未知", "Unknown"));
        string coverage = data.CurrentComplete ? L(canvas, "本期覆盖完整", "Current coverage complete") : L(canvas, "本期覆盖受限", "Current coverage limited");
        if (IsEnglish(canvas))
        {
            Text(canvas, grade, 1413, 78, 320, 26, gradeColor, bold: true, height: 38);
            Text(canvas, coverage, 1413, 119, 320, 18, titleColor, bold: true, height: 28);
        }
        else
        {
            Text(canvas, grade, 1412, 82, 98, Math.Min(56, 91.0 / grade.Length), gradeColor, bold: true, height: 66);
            Text(canvas, coverage, 1516, 77, 214, 24, titleColor, bold: true, height: 65, maxLines: 2);
        }
        Text(canvas, Plain(data.GradeBasis, L(canvas, "分级依据未提供", "No assessment basis supplied")), 1413, 151, 320, 16, secondaryColor, height: 30);
    }

    private static void Metrics(Canvas canvas, WindowsStabilityReportData data)
    {
        uint[] colors = [Blue, Red, Orange, Gold, Purple, Red, Teal, Green];
        const double gap = 12;
        double width = (ContentWidth - 7 * gap) / 8;
        for (int index = 0; index < 8; index++)
        {
            double x = Margin + index * (width + gap);
            var metric = data.Metrics.ElementAtOrDefault(index);
            uint color = colors[index];
            Box(canvas, x, 246, width, 166, White, 12, LineColor);
            Box(canvas, x, 246, 6, 166, color, 3);
            Text(canvas, metric?.Label ?? L(canvas, "指标未提供", "Metric unavailable"), x + 17, 260, width - 28, IsEnglish(canvas) ? 16 : 18, Muted, height: 43, maxLines: 2);
            string number = metric is null ? L(canvas, "未知", "Unknown") : MetricCount(canvas, metric, previous: false);
            Text(canvas, number, x + 17, 302, width - 28, number.Length > 7 ? 29 : 38, metric?.Current is null ? Muted : color, bold: true, height: 51);
            string previous = metric is null ? L(canvas, "缺少采集数据", "No collected data")
                : MetricCount(canvas, metric, previous: true) == L(canvas, "未知", "Unknown") ? L(canvas, "上期未知 · 不计算趋势", "Previous unknown")
                : L(canvas, "上期 ", "Previous ") + MetricCount(canvas, metric, previous: true) + " " + metric.Unit;
            Text(canvas, previous, x + 17, 356, width - 28, 14, Muted, height: 23);
            bool comparable = metric?.Current is not null && metric.Previous is not null
                && HasQualification(metric, "本期完整", "Current complete")
                && HasQualification(metric, "上期完整", "Previous complete");
            string trend = metric?.Qualification ?? "—";
            if (comparable && metric is not null)
            {
                long difference = (long)metric.Current!.Value - metric.Previous!.Value;
                trend = difference == 0 ? L(canvas, "→ 与上期持平", "→ No change") : (difference > 0 ? L(canvas, "↑ 增加 ", "↑ Up ") : L(canvas, "↓ 减少 ", "↓ Down "))
                    + Math.Abs(difference).ToString("N0", CultureInfo.InvariantCulture) + " " + metric.Unit;
            }
            Text(canvas, trend, x + 17, 379, width - 28, 13, Muted, height: 25);
        }
    }

    private static void RequiredChecks(Canvas canvas, WindowsStabilityReportData data)
    {
        const double y = 2174;
        Card(canvas, y, 255, Teal, L(canvas, "05  内核 / 驱动 / 设备必查清单", "05  Kernel / driver / device checks"),
            L(canvas, "每项为本期 / 上期；≥ 表示已观察下界，未知不计为零。转储文件仅是存在性观察，恢复字段不猜测单位。",
                "Current / previous; ≥ marks an observed lower bound. Unknown is not zero. Dump counts show file presence; resume units are not assumed."), 16);
        var checks = data.Metrics.Skip(8).ToArray();
        if (checks.Length == 0)
        {
            Text(canvas, L(canvas, "尚未提供必查分项证据；不能据总量推导 WHEA、蓝屏、TDR 或磁盘异常为零。",
                "Detailed checks are unavailable. Aggregate totals cannot establish zero WHEA, bugcheck, TDR or disk events."), 69, y + 109, 1660, 18, Muted, height: 36);
            return;
        }
        int columns = checks.Length <= 8 ? 4 : 6;
        int rows = (int)Math.Ceiling((double)checks.Length / columns);
        double width = (ContentWidth - 50) / columns;
        double rowHeight = 154.0 / rows;
        for (int i = 0; i < checks.Length; i++)
        {
            var metric = checks[i];
            double x = 69 + i % columns * width, rowY = y + 95 + i / columns * rowHeight;
            string value = MetricCount(canvas, metric, previous: false) + " / " + MetricCount(canvas, metric, previous: true);
            Text(canvas, metric.Label, x, rowY, width - 14, IsEnglish(canvas) ? 13 : 14, Ink, true, height: 20);
            bool adverse = metric.Key is "power41" or "power6008" or "bugcheck" or "whea" or "tdr" or "disk7" or "disk11" or "disk51" or "ntfs98" or "audio";
            Text(canvas, value + " " + metric.Unit, x, rowY + 19, width - 14, 14, metric.Current > 0 ? adverse ? Red : Blue : Muted, height: rowHeight - 19);
        }
    }

    private static void Conclusions(Canvas canvas, WindowsStabilityReportData data)
    {
        const double y = 2453, top = 2590, actionX = 68, actionWidth = 1100, rightX = 1192, rightWidth = 540;
        Box(canvas, Margin, y, ContentWidth, 548, White, 14, LineColor);
        Box(canvas, Margin, y, 7, 548, Green, 3);
        Text(canvas, L(canvas, "06  稳定性结论与下一步行动", "06  Stability assessment and next steps"), 69, y + 18, 1660, 28, Ink, true, height: 42);
        Text(canvas, data.Assessment, 69, y + 63, 1660, 20, Ink, height: 76, maxLines: 3);
        Box(canvas, actionX, top, actionWidth, 381, 0xFFF2F0FA, 11);
        Text(canvas, L(canvas, "优先行动 · 先检查，再处置，最后复测", "Priority actions · Inspect, address, then verify"), actionX + 20, top + 14, actionWidth - 40, 22, Purple, true, height: 34);
        var actions = data.Actions.OrderBy(action => action.Priority, StringComparer.Ordinal).Take(2).ToArray();
        if (actions.Length == 0)
        {
            Text(canvas, L(canvas, "没有证据支持额外处置。继续观察并补齐采集覆盖。", "No evidence supports additional intervention. Continue monitoring and complete collection coverage."), actionX + 20, top + 70, actionWidth - 40, 20, Muted, height: 100, maxLines: 3);
        }
        for (int i = 0; i < actions.Length; i++)
        {
            var action = actions[i];
            double row = top + 58 + i * 153;
            uint color = action.Priority == "P0" ? Red : action.Priority == "P1" ? Orange : Blue;
            Box(canvas, actionX + 20, row, 48, 25, color, 5);
            Text(canvas, action.Priority, actionX + 20, row, 48, 16, White, true, height: 25, align: TextAlignment.Center);
            Text(canvas, action.Title, actionX + 80, row - 2, actionWidth - 100, 20, Ink, true, height: 29);
            Text(canvas, L(canvas, "原因：", "Reason: ") + action.Reason, actionX + 20, row + 28, actionWidth - 40, 15, Muted, height: 40, maxLines: 2);
            Text(canvas, L(canvas, "检查 / 处理：", "Inspect / address: ") + action.Step, actionX + 20, row + 67, actionWidth - 40, 16, Ink, height: 43, maxLines: 2);
            Text(canvas, L(canvas, "效果验证：", "Verify: ") + action.Verification, actionX + 20, row + 111, actionWidth - 40, 15, Green, height: 40, maxLines: 2);
        }
        Box(canvas, rightX, top, rightWidth, 381, 0xFFFFF7E8, 11);
        Text(canvas, L(canvas, "维度判断与观测盲区", "Dimensions and blind spots"), rightX + 18, top + 14, rightWidth - 36, 22, Gold, true, height: 34);
        var dimensions = data.Dimensions.Take(4).ToArray();
        for (int i = 0; i < dimensions.Length; i++)
        {
            var dimension = dimensions[i];
            Text(canvas, dimension.Name + L(canvas, "：", ": ") + dimension.Status, rightX + 18, top + 58 + i * 27, rightWidth - 36, 17, Ink, true, height: 27);
        }
        Rule(canvas, rightX + 18, top + 172, rightX + rightWidth - 18, top + 172, 0xFFE9DDBF);
        var spots = data.BlindSpots.Where(value => !string.IsNullOrWhiteSpace(value)).Take(3).ToArray();
        if (spots.Length == 0)
        {
            spots = [L(canvas, "未声明额外盲区；完整日志不代表已覆盖全部系统故障。", "No additional blind spots declared; complete logs do not cover every possible system fault.")];
        }
        for (int i = 0; i < spots.Length; i++)
        {
            Text(canvas, "• " + spots[i], rightX + 18, top + 185 + i * 54, rightWidth - 36, 15, Ink, height: 48, maxLines: 2);
        }
        int capped = data.Coverage.Count(coverage => coverage.Capped || coverage.RolledOver || !coverage.ReadSucceeded);
        Text(canvas, L(canvas, $"{data.Coverage.Count} 个通道 · {capped} 个有覆盖限制", $"{data.Coverage.Count} channels · {capped} with coverage limits"), rightX + 18, top + 351, rightWidth - 36, 14, Muted, height: 23);
        if (data.Actions.Count > 2)
        {
            Text(canvas, L(canvas, $"图中展开前 2 项优先行动；其余 {data.Actions.Count - 2} 项建议及完整事件证据保留于同版报告。",
                $"The first 2 priority actions are shown. The remaining {data.Actions.Count - 2} recommendations and full event evidence are in the matching report."), 69, y + 521, 1660, 14, Muted, height: 23);
        }
    }

    private static void Card(Canvas canvas, double y, double height, uint accent, string title, string subtitle, double subtitleSize = 18)
    {
        Box(canvas, Margin, y, ContentWidth, height, White, 14, LineColor);
        Box(canvas, Margin, y, 7, height, accent, 3);
        Text(canvas, title, Margin + 25, y + 18, ContentWidth - 50, 28, Ink, true, height: 42);
        Text(canvas, subtitle, Margin + 25, y + 62, ContentWidth - 50, subtitleSize, Muted, height: 34);
    }

    private static void Box(Canvas canvas, double x, double y, double width, double height, uint fill, double radius = 0, uint? stroke = null)
    {
        var border = new Border { Width = Math.Max(0, width), Height = Math.Max(0, height), Background = ThemeBrush(canvas, fill), CornerRadius = new CornerRadius(radius) };
        if (stroke is { } value)
        {
            border.BorderBrush = ThemeBrush(canvas, value);
            border.BorderThickness = new Thickness(1.5);
        }
        Add(canvas, border, x, y);
    }

    private static TextBlock Text(Canvas canvas, string? value, double x, double y, double width, double size,
        uint color = Ink, bool bold = false, double height = 36, int maxLines = 1, TextAlignment align = TextAlignment.Left)
    {
        var text = new TextBlock
        {
            Text = Plain(value, "—"), Width = Math.Max(1, width), Height = Math.Max(1, height), FontSize = size,
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), Foreground = ThemeBrush(canvas, color, text: true),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = maxLines, TextAlignment = align,
            LineHeight = size * 1.32, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        Add(canvas, text, x, y);
        return text;
    }

    private static void Rule(Canvas canvas, double x1, double y1, double x2, double y2, uint color = LineColor, double thickness = 1)
        => canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = ThemeBrush(canvas, color), StrokeThickness = thickness });

    private static void Dot(Canvas canvas, double x, double y, double radius, uint color)
        => Add(canvas, new Ellipse { Width = radius * 2, Height = radius * 2, Fill = ThemeBrush(canvas, color) }, x - radius, y - radius);

    private static void Add(Canvas canvas, UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        canvas.Children.Add(element);
    }

    private static SolidColorBrush RawBrush(uint argb) => new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    private static SolidColorBrush ThemeBrush(Canvas canvas, uint argb, bool text = false)
        => RawBrush(((DashboardPalette)canvas.Resources[PaletteResourceKey]).Map(argb, text));

    private static bool IsDark(Canvas canvas) => ((DashboardPalette)canvas.Resources[PaletteResourceKey]).Dark;

    private static bool IsEnglish(Canvas canvas) => ((DashboardPalette)canvas.Resources[PaletteResourceKey]).English;

    private static string L(Canvas canvas, string chinese, string english) => IsEnglish(canvas) ? english : chinese;

    private sealed class DashboardPalette(bool dark, bool english)
    {
        public bool Dark { get; } = dark;

        public bool English { get; } = english;

        public uint Map(uint color, bool text)
        {
            if (text)
            {
                if (!Dark)
                {
                    return color;
                }

                return color switch
                {
                    Ink => 0xFFF1F5FC,
                    Muted => 0xFFB6C5DA,
                    Blue => 0xFF8BBEFF,
                    Teal => 0xFF6BD9DF,
                    Red => 0xFFFF9BA0,
                    Orange => 0xFFFFC27D,
                    Gold => 0xFFEACB6B,
                    Purple => 0xFFC0A5FF,
                    Green => 0xFF80DDB6,
                    _ => color,
                };
            }

            if (color == White)
            {
                return Dark ? 0xF0273346 : 0xF8FFFFFF;
            }

            if (color == 0xFF172333)
            {
                return 0xF2172333;
            }

            if (!Dark)
            {
                return color;
            }

            return color switch
            {
                LineColor => 0xFF465772,
                0xFFE8EEF5 => 0xFF344357,
                0xFF9DAFC6 => 0xFF6C819D,
                0xFFF2F0FA => 0xF0332F49,
                0xFFFFF7E8 => 0xF03C3729,
                0xFFE9DDBF => 0xFF655B40,
                0xFFEEF1F5 => 0xFF334154,
                0xFFD3DBE5 => 0xFF53677E,
                0xFFB9C8DD => 0xFF7187A7,
                0xFFD6DFEB => 0xFF4A5C75,
                0xFFCBD3DF => 0xFF77869F,
                _ => color,
            };
        }
    }
    private static string Plain(string? text, string fallback = "—") => string.IsNullOrWhiteSpace(text) ? fallback : text.Replace('\r', ' ').Replace('\n', ' ').Trim();
    private static string Count(Canvas canvas, int? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? L(canvas, "未知", "Unknown");

    private static string Observed(Canvas canvas, int? value, bool complete) => value is null ? L(canvas, "未知", "Unknown")
        : complete ? Count(canvas, value) : value > 0 ? "≥" + Count(canvas, value) : L(canvas, "未知", "Unknown");

    private static string MetricCount(Canvas canvas, WindowsStabilityMetric metric, bool previous)
    {
        int? value = previous ? metric.Previous : metric.Current;

        // File observations and original duration fields are exact observed values.
        // Only an explicit period lower-bound qualification changes their presentation.
        string lowerBound = previous ? "上期至少" : "本期至少";
        string englishLowerBound = previous ? "Previous at least" : "Current at least";
        return HasQualification(metric, lowerBound, englishLowerBound) ? Observed(canvas, value, complete: false) : Count(canvas, value);
    }

    private static bool HasQualification(WindowsStabilityMetric metric, string chinese, string english)
        => metric.Qualification.Contains(chinese, StringComparison.Ordinal) || metric.Qualification.Contains(english, StringComparison.Ordinal);
}
