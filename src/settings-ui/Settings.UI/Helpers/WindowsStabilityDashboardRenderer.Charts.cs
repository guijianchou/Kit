// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;
using System.Linq;
using Kit.AIHubLib.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Kit.Settings.UI.Helpers;

public static partial class WindowsStabilityDashboardRenderer
{
    private static void DailyChart(Canvas canvas, WindowsStabilityReportData data)
    {
        var days = data.Daily.OrderBy(day => day.Start).TakeLast(64).ToArray();
        Card(canvas, 438, 420, Blue, L(canvas, $"01  每日事件量 · {days.Length} 个 24 小时观察桶与应用崩溃强度", $"01  Daily events · {days.Length} rolling 24-hour buckets and application crashes"),
            L(canvas, "堆叠柱：信息 / 警告 / 错误 / 严重；紫线：崩溃次数（右轴）。灰色缺口 = 未知；≥ = 已观察下界。",
                "Stacked bars: information / warning / error / critical. Purple line: crashes (right axis). Grey gaps = unknown; ≥ = observed lower bound."), 17);
        const double left = 122, right = 1518, top = 551, bottom = 776;
        if (days.Length == 0)
        {
            Empty(canvas, left, top + 45, right - left, L(canvas, "没有可用的每日事件证据", "No daily event evidence is available"));
            return;
        }
        double maximum = Math.Max(1, days.Max(Total)) * 1.12;
        double crashMaximum = Math.Max(1, days.Where(day => day.Crashes.HasValue).Select(day => (double)day.Crashes!.Value).DefaultIfEmpty(0).Max()) * 1.18;
        for (int i = 0; i <= 4; i++)
        {
            double y = bottom - (bottom - top) * i / 4;
            Rule(canvas, left, y, right, y);
            Text(canvas, Compact(maximum * i / 4), Margin + 14, y - 11, left - Margin - 24, 14, Muted, height: 25, align: TextAlignment.Right);
            Text(canvas, Math.Round(crashMaximum * i / 4).ToString(CultureInfo.InvariantCulture), right + 6, y - 11, 35, 13, Purple, height: 23);
        }
        double slot = (right - left) / days.Length;
        double width = Math.Max(5, Math.Min(70, slot * .7));
        Point? previousPoint = null;
        bool previousPeriod = false;
        int firstCurrent = Array.FindIndex(days, day => day.Current);
        if (firstCurrent > 0)
        {
            double separator = left + firstCurrent * slot;
            Rule(canvas, separator, top - 13, separator, bottom + 48, 0xFF9DAFC6, 2);
            Text(canvas, L(canvas, "上一期", "Previous period"), left, top - 32, separator - left, 17, Muted, true, 28, align: TextAlignment.Center);
            Text(canvas, L(canvas, "本期", "Current period"), separator + 6, top - 32, right - separator - 6, 17, Blue, true, 28, align: TextAlignment.Center);
        }
        for (int index = 0; index < days.Length; index++)
        {
            var day = days[index];
            double center = left + (index + .5) * slot;
            double bottomOfStack = bottom;
            bool complete = day.Critical.HasValue && day.Error.HasValue && day.Warning.HasValue && day.Information.HasValue
                && (day.Current ? data.CurrentComplete : data.PreviousComplete);
            (int? Count, uint Color)[] segments = [(day.Information, day.Current ? 0xFFB9C8DD : 0xFFD6DFEB), (day.Warning, 0xFFDDA82D), (day.Error, Red), (day.Critical, 0xFF7E2431)];
            foreach (var segment in segments)
            {
                if (segment.Count is not { } amount || amount <= 0)
                {
                    continue;
                }
                double height = (bottom - top) * amount / maximum;
                Box(canvas, center - width / 2, bottomOfStack - height, width, Math.Max(1, height), segment.Color);
                bottomOfStack -= height;
            }
            if (!complete)
            {
                double stripeY = Math.Min(bottom - 21, bottomOfStack - 21);
                Hatch(canvas, center - width / 2, stripeY, width, 21);
                Text(canvas, Total(day) > 0 ? "≥" : "?", center - 20, stripeY - 23, 40, 17, Muted, true, 24, align: TextAlignment.Center);
            }
            else if (day.Current && days.Length <= 16)
            {
                Text(canvas, Compact(Total(day)), center - slot / 2, bottomOfStack - 24, slot, 14, Ink, true, 24, align: TextAlignment.Center);
            }

            if (day.Crashes is { } crashes)
            {
                var point = new Point(center, bottom - (bottom - top) * crashes / crashMaximum);
                uint color = day.Current ? Purple : 0xFFB6A1DB;
                if (previousPoint is { } old && previousPeriod == day.Current)
                {
                    Rule(canvas, old.X, old.Y, point.X, point.Y, color, 3);
                }
                Dot(canvas, point.X, point.Y, day.Current ? 6 : 4, color);
                if (day.Current && days.Length <= 16)
                {
                    bool nearEventTotal = Math.Abs(point.Y - bottomOfStack) < 42;
                    double labelY = nearEventTotal ? point.Y + 8 : point.Y - 28;
                    double labelX = center - 25;
                    if (labelY > bottom - 25)
                    {
                        labelY = point.Y - 28;
                        labelX = center + 12;
                    }
                    Text(canvas, Count(canvas, crashes), labelX, labelY, 50, 15, Purple, true, 25, align: TextAlignment.Center);
                }
                previousPoint = point;
                previousPeriod = day.Current;
            }
            else
            {
                previousPoint = null;
            }
            int stride = days.Length > 32 ? 4 : days.Length > 16 ? 2 : 1;
            bool lastTooClose = index < days.Length - 1 && days.Length - 1 - index < stride;
            if ((index % stride == 0 && !lastTooClose) || index == days.Length - 1)
            {
                Text(canvas, day.Start.ToString("MM/dd", CultureInfo.InvariantCulture), center - 35, bottom + 10, 70, 14, Muted, height: 23, align: TextAlignment.Center);
            }
        }
        Legend(canvas, 1566, 552, L(canvas, "信息", "Information"), 0xFFB9C8DD);
        Legend(canvas, 1566, 585, L(canvas, "警告", "Warning"), 0xFFDDA82D);
        Legend(canvas, 1566, 618, L(canvas, "错误", "Error"), Red);
        Legend(canvas, 1566, 651, L(canvas, "严重", "Critical"), 0xFF7E2431);
        Rule(canvas, 1566, 698, 1590, 698, Purple, 3);
        Dot(canvas, 1578, 698, 5, Purple);
        Text(canvas, L(canvas, "崩溃 / 右轴", "Crashes / right"), 1600, 684, 130, 16, Muted, height: 30);
        Text(canvas, L(canvas, "起点 ", "Starts ") + days[0].Start.ToString("HH:mm", CultureInfo.InvariantCulture) + L(canvas, "\n每桶连续 24 小时", "\n24h per bucket"), 1566, 728, 162, 15, Muted, height: 57, maxLines: 2);
        string note = data.PreviousComplete && data.CurrentComplete
            ? L(canvas, "本期与上期均有完整日志覆盖，可按同口径比较。", "Both periods have complete log coverage and can be compared on the same basis.")
            : L(canvas, "存在未完整覆盖区间：本图保留观察值，不据此断言系统好转或恶化。", "Some intervals lack complete coverage. Observations alone do not establish improvement or deterioration.");
        Text(canvas, note, Margin + 25, 820, ContentWidth - 50, 16, Muted, height: 27);
    }

    private static void TimelineChart(Canvas canvas, WindowsStabilityReportData data)
    {
        var events = data.Timeline.OrderBy(item => item.Time).TakeLast(600).ToArray();
        Card(canvas, 882, 360, Teal, L(canvas, "02  掉电 / 睡眠 / 变更时间线", "02  Power / sleep / change timeline"),
            L(canvas, "实心为本期、浅色为上期；节点表示真实发生时间。相邻出现不代表因果。", "Solid marks: current period. Pale marks: previous period. Nodes use actual event times; proximity does not establish causation."), 18);
        const double left = 135, right = 1708, startY = 1007;
        DateTimeOffset from = data.PreviousStart;
        double seconds = Math.Max(1, (data.End - from).TotalSeconds);
        double X(DateTimeOffset time) => Math.Clamp(left + (time - from).TotalSeconds / seconds * (right - left), left, right);
        string[] lanes = [L(canvas, "电源 / 内核", "Power / kernel"), L(canvas, "睡眠 / 唤醒", "Sleep / wake"), L(canvas, "安装 / 变更", "Install / change")];
        for (int i = 0; i < lanes.Length; i++)
        {
            double y = startY + i * 57;
            Text(canvas, lanes[i], Margin + 19, y - 15, 72, 14, Muted, height: 44, maxLines: 2);
            Rule(canvas, left, y, right, y);
        }
        double boundary = X(data.Start);
        Box(canvas, left, startY - 22, Math.Max(0, boundary - left), 151, 0x20CBD5E1);
        Rule(canvas, boundary, startY - 24, boundary, startY + 135, 0xFF9DAFC6, 2);
        for (int i = 0; i <= 14; i++)
        {
            double x = left + (right - left) * i / 14;
            Rule(canvas, x, startY - 22, x, startY + 130, 0xFFE8EEF5);
            if (i % 2 == 0)
            {
                Text(canvas, from.AddSeconds(seconds * i / 14).ToString("MM/dd", CultureInfo.InvariantCulture), x - 32, startY + 138, 65, 14, Muted, height: 24, align: TextAlignment.Center);
            }
        }
        foreach (var item in events)
        {
            int lane = item.Kind is "sleep" or "wake" ? 1 : item.Kind == "change" ? 2 : 0;
            uint color = lane == 1 ? Green : lane == 2 ? Orange : item.Kind == "kernel" ? Purple : Red;
            if (!item.Current)
            {
                color = Fade(color);
            }
            double x = X(item.Time), y = startY + lane * 57;
            if (lane == 2)
            {
                Rule(canvas, x, y - 11, x, y + 11, color, 3);
            }
            else if (lane == 1)
            {
                var shape = new Polygon { Fill = ThemeBrush(canvas, color), Points = new PointCollection { new(x, y - 6), new(x + 5, y), new(x, y + 6), new(x - 5, y) } };
                canvas.Children.Add(shape);
            }
            else
            {
                Dot(canvas, x, y, item.Kind == "kernel" ? 6 : 5, color);
                Rule(canvas, x, y - 5, x, y - 17, color, 2);
            }
        }
        if (events.Length == 0)
        {
            Text(canvas, L(canvas, "未观察到时间线事件；请结合通道覆盖判断。", "No timeline events observed; interpret this alongside channel coverage."), left + 180, startY + 25, right - left - 360, 20, Muted, height: 50, align: TextAlignment.Center);
        }
        var highlights = events.Where(item => item.Current && (item.Kind is "power" or "kernel" or "change")).OrderByDescending(item => item.Time).Take(3).ToArray();
        for (int i = 0; i < highlights.Length; i++)
        {
            var item = highlights[i];
            Text(canvas, $"{item.Time:MM/dd HH:mm}  {item.Label}", left + i * 520, 1195, 500, 15, item.Kind == "change" ? Orange : Red, height: 28);
        }
        if (data.Timeline.Count > events.Length)
        {
            Text(canvas, L(canvas, $"仅绘制最近 {events.Length} / {data.Timeline.Count} 个节点；完整时间线见 Markdown。",
                $"Showing the latest {events.Length} / {data.Timeline.Count} nodes; the full timeline is in Markdown."), left, 1220, right - left, 12, Muted, height: 20);
        }
    }

    private static void CrashChart(Canvas canvas, WindowsStabilityReportData data)
    {
        var rows = data.CrashClusters.OrderByDescending(item => item.Current).ThenByDescending(item => item.Previous).Take(10).ToArray();
        Card(canvas, 1266, 435, Purple, L(canvas, "03  应用崩溃 · 进程 × 故障模块", "03  Application crashes · Process × faulting module"),
            L(canvas, "按进程、模块、异常码与偏移聚类。实条为本期，细灰条为上期；签名不完整时明确标注。",
                "Grouped by process, module, exception code and offset. Solid bars: current; thin grey bars: previous. Incomplete signatures are marked."), 18);
        const double processX = 69, moduleX = 386, barX = 778, barWidth = 740, countX = 1538;
        Text(canvas, L(canvas, "进程", "Process"), processX, 1368, 290, 15, Muted, true, 24);
        Text(canvas, L(canvas, "模块 / 异常码", "Module / exception code"), moduleX, 1368, 370, 15, Muted, true, 24);
        Text(canvas, L(canvas, "观察次数", "Observed count"), barX, 1368, barWidth, 15, Muted, true, 24);
        Text(canvas, L(canvas, "本期 / 上期", "Current / previous"), countX, 1368, 180, 15, Muted, true, 24);
        if (rows.Length == 0)
        {
            Empty(canvas, processX, 1440, ContentWidth - 48, L(canvas, "没有可列出的应用崩溃签名；未完整采集不等同于零崩溃。",
                "No application crash signatures are available; incomplete collection does not mean zero crashes."));
            return;
        }
        bool comparable = data.PreviousComplete && data.CurrentComplete && rows.All(item => item.CurrentKnown && item.PreviousKnown);
        double maximum = Math.Max(1, rows.Max(item => Math.Max(item.CurrentKnown ? item.Current : 0, data.PreviousComplete && item.PreviousKnown ? item.Previous : 0)));
        for (int index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            double y = 1404 + index * 25;
            uint color = data.CurrentComplete && data.PreviousComplete && row.CurrentKnown && row.PreviousKnown && row.Current > row.Previous ? Red : Purple;
            Text(canvas, row.Process, processX, y - 4, 295, 16, Ink, height: 24);
            string detail = row.Module + (row.CompleteSignature ? string.Empty : L(canvas, " · 签名缺失", " · incomplete signature"));
            if (!string.IsNullOrWhiteSpace(row.ExceptionCode))
            {
                detail += " / " + row.ExceptionCode;
            }
            Text(canvas, detail, moduleX, y - 4, 370, 14, Muted, height: 24);
            if (row.CurrentKnown && row.Current > 0)
            {
                Box(canvas, barX, y, Math.Max(2, row.Current / maximum * barWidth), 12, color, 4);
            }
            if (data.PreviousComplete && row.PreviousKnown && row.Previous > 0)
            {
                Box(canvas, barX, y + 14, Math.Max(2, row.Previous / maximum * barWidth), 4, 0xFFCBD3DF, 2);
            }
            string current = row.CurrentKnown ? Observed(canvas, row.Current, data.CurrentComplete) : L(canvas, "未知", "Unknown");
            string previous = row.PreviousKnown ? Observed(canvas, row.Previous, data.PreviousComplete) : L(canvas, "未知", "Unknown");
            Text(canvas, current + " / " + previous, countX, y - 5, 180, 16, color, true, 26);
        }
        string comparisonNote = L(canvas, $"展示 {rows.Length} / {data.CrashClusters.Count} 个签名组。", $"Showing {rows.Length} / {data.CrashClusters.Count} signature groups. ")
            + (comparable ? L(canvas, "红色仅表示同口径观察次数增加，并非根因判断。", "Red means more observations on a comparable basis, not a root-cause finding.")
                : L(canvas, "覆盖不足：观察次数按下界展示，不标新增 / 消失 / 恶化。", "Limited coverage: counts are lower bounds; no new / resolved / worse classification."));
        Text(canvas, comparisonNote, processX, 1666, ContentWidth - 48, 15, Muted, height: 25);
    }

    private static void HeatmapAndNoise(Canvas canvas, WindowsStabilityReportData data)
    {
        Card(canvas, 1725, 425, Gold, L(canvas, "04  警告 / 错误热力图与噪音 Top 10", "04  Warning / error heatmap and Top 10 noise sources"),
            L(canvas, "按窗口起点对齐 24 小时桶，列为每桶起始时刻。灰纹为未知，无色底格表示已覆盖且未观察到；红色表示观察密度。",
                "24-hour buckets align with the window start. Columns show bucket start times. Grey hatch: unknown. Base fill: covered, none observed. Red: density."), 17);
        const double left = 131, top = 1868, gridWidth = 1044, gridHeight = 232;
        var dates = data.Heatmap.Select(cell => cell.Day.Date).Distinct().Order().TakeLast(7).ToArray();
        var maximum = Math.Max(1, data.Heatmap.Where(cell => dates.Contains(cell.Day.Date)).MaxOrDefault(cell => cell.Count ?? 0));
        double cellWidth = gridWidth / 24, cellHeight = gridHeight / Math.Max(1, dates.Length);
        for (int hour = 0; hour < 24; hour++)
        {
            Text(canvas, data.Start.AddHours(hour).ToString("HH:mm", CultureInfo.InvariantCulture), left + hour * cellWidth, top - 27, cellWidth, 11, Muted, height: 23, align: TextAlignment.Center);
        }
        if (dates.Length == 0)
        {
            Empty(canvas, left, top + 60, gridWidth, L(canvas, "没有逐小时覆盖证据", "No hourly coverage evidence is available"));
        }
        for (int day = 0; day < dates.Length; day++)
        {
            Text(canvas, dates[day].ToString("MM/dd", CultureInfo.InvariantCulture), Margin + 17, top + day * cellHeight + 7, 62, 14, Muted, height: 25, align: TextAlignment.Right);
            for (int hour = 0; hour < 24; hour++)
            {
                var cell = data.Heatmap.FirstOrDefault(value => value.Day.Date == dates[day] && value.Hour == hour);
                double x = left + hour * cellWidth, y = top + day * cellHeight;
                if (cell?.Count is not { } count)
                {
                    Hatch(canvas, x + 1, y + 1, cellWidth - 2, cellHeight - 2);
                }
                else
                {
                    double strength = count == 0 ? 0 : .16 + .84 * Math.Sqrt((double)count / maximum);
                    Box(canvas, x + 1, y + 1, cellWidth - 2, cellHeight - 2, HeatColor(canvas, strength));
                    if (count > 0)
                    {
                        Text(canvas, count > 999 ? Compact(count) : Count(canvas, count), x, y + Math.Max(1, (cellHeight - 22) / 2), cellWidth, 12, IsDark(canvas) || strength > .62 ? White : 0xFF803D43, true, 23, align: TextAlignment.Center);
                    }
                }
            }
        }
        double legendX = left;
        Text(canvas, L(canvas, "低", "Low"), legendX, 2111, 30, 13, Muted, height: 22);
        for (int i = 0; i < 6; i++)
        {
            Box(canvas, legendX + 30 + i * 23, 2113, 20, 12, HeatColor(canvas, i / 5.0));
        }
        Text(canvas, L(canvas, "高", "High"), legendX + 174, 2111, 35, 13, Muted, height: 22);
        string heatmapNote = data.Heatmap.Select(cell => cell.Day.Date).Distinct().Count() > 7
            ? L(canvas, "仅展示本期最近 7 个 24 小时桶；其余见 Markdown。", "Latest 7 current-period buckets shown; the rest are in Markdown.")
            : data.CurrentComplete ? L(canvas, "每格是观测计数，不是系统负载或故障概率。", "Cells show observations, not system load or failure probability.")
                : L(canvas, "覆盖不完整：非零值可能仅为下界，零仅用于已覆盖小时。", "Limited coverage: positive counts may be lower bounds; zero requires coverage.");
        Text(canvas, heatmapNote, legendX + 230, 2111, gridWidth - 230, 13, Muted, height: 22);

        const double noiseLeft = 1230, noiseBarX = 1480, noiseBarWidth = 175;
        Text(canvas, L(canvas, "本期噪音 / 重复事件", "Current noise / repeated events"), noiseLeft, 1837, 480, 21, Gold, true, 32);
        var noise = data.Noise.OrderByDescending(item => item.Current).Take(10).ToArray();
        double noiseMaximum = Math.Max(1, noise.Select(item => item.Current).DefaultIfEmpty(0).Max());
        if (noise.Length == 0)
        {
            Text(canvas, L(canvas, "没有可用的噪音分组。", "No noise groups are available."), noiseLeft, 1887, 470, 18, Muted, height: 50);
        }
        for (int index = 0; index < noise.Length; index++)
        {
            var row = noise[index];
            double y = 1881 + index * 23;
            string provider = row.Provider.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase)
                ? row.Provider["Microsoft-Windows-".Length..] : row.Provider;
            Text(canvas, provider, noiseLeft, y - 4, 178, 13, Muted, height: 23);
            Text(canvas, row.EventId.ToString(CultureInfo.InvariantCulture), noiseLeft + 181, y - 4, 57, 13, Muted, height: 23, align: TextAlignment.Right);
            if (row.CurrentKnown && row.Current > 0)
            {
                Box(canvas, noiseBarX, y, Math.Max(2, row.Current / noiseMaximum * noiseBarWidth), 12, 0xFFBA931C, 4);
            }
            Text(canvas, row.CurrentKnown ? Observed(canvas, row.Current, data.CurrentComplete) : L(canvas, "未知", "Unknown"), noiseBarX + noiseBarWidth + 8, y - 5, 65, 14, Gold, true, 23);
        }
        Text(canvas, L(canvas, "高频 ≠ 高危；按来源分类，保留进一步核实。", "Frequent ≠ dangerous; grouped by source for further review."), noiseLeft, 2111, 490, 13, Muted, height: 23);
    }

    private static double Total(WindowsStabilityDaily day) => (long)(day.Information ?? 0) + (day.Warning ?? 0) + (day.Error ?? 0) + (day.Critical ?? 0);
    private static string Compact(double value) => value >= 10000 ? (value / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k" : Math.Round(value).ToString("N0", CultureInfo.InvariantCulture);
    private static void Empty(Canvas canvas, double x, double y, double width, string message) => Text(canvas, message, x, y, width, 25, Muted, height: 85, maxLines: 2, align: TextAlignment.Center);
    private static void Legend(Canvas canvas, double x, double y, string text, uint color)
    {
        Box(canvas, x, y + 4, 20, 14, color);
        Text(canvas, text, x + 33, y, 135, 16, Muted, height: 28);
    }
    private static void Hatch(Canvas canvas, double x, double y, double width, double height)
    {
        Box(canvas, x, y, width, height, 0xFFEEF1F5);
        for (double offset = 0; offset < width; offset += 12)
        {
            Rule(canvas, x + offset, y + height, x + Math.Min(width, offset + Math.Min(12, height)), y + Math.Max(0, height - 12), 0xFFD3DBE5, 1);
        }
    }
    private static uint Fade(uint color)
    {
        byte Mix(int shift) => (byte)((((color >> shift) & 255) * .38) + 255 * .62);
        return 0xFF000000u | ((uint)Mix(16) << 16) | ((uint)Mix(8) << 8) | Mix(0);
    }
    private static uint HeatColor(Canvas canvas, double strength)
    {
        strength = Math.Clamp(strength, 0, 1);
        bool dark = IsDark(canvas);
        int redBase = dark ? 39 : 248, greenBase = dark ? 51 : 250, blueBase = dark ? 70 : 253;
        byte redChannel = (byte)(redBase + (193 - redBase) * strength);
        byte greenChannel = (byte)(greenBase + (48 - greenBase) * strength);
        byte blueChannel = (byte)(blueBase + (55 - blueBase) * strength);
        return 0xFF000000u | ((uint)redChannel << 16) | ((uint)greenChannel << 8) | blueChannel;
    }

    private static int MaxOrDefault<T>(this System.Collections.Generic.IEnumerable<T> source, Func<T, int> selector)
        => source.Select(selector).DefaultIfEmpty(0).Max();
}
