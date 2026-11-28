// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Kit.WingetLib;

/// <summary>Reads WinGet's progress display, never arbitrary percentages in installer messages.</summary>
internal sealed partial class WingetCliProgress
{
    private WingetOperationProgress _current = new("Starting", null);

    internal WingetOperationProgress Read(string snapshot)
    {
        WingetOperationProgress? observed = null;
        string stage = _current.Stage;
        foreach (string rawLine in snapshot.Split('\n'))
        {
            string line = rawLine.Trim();
            string? next = Stage(line);
            if (next is not null)
            {
                stage = next;
                observed = new(stage, null);
                continue;
            }

            var bytes = DownloadLine().Match(line);
            if (bytes.Success && TryBytes(bytes.Groups["current"].Value, bytes.Groups["currentUnit"].Value, out ulong downloaded)
                && TryBytes(bytes.Groups["total"].Value, bytes.Groups["totalUnit"].Value, out ulong required)
                && required > 0 && downloaded <= required)
            {
                stage = "Downloading";
                observed = new(stage, 100.0 * downloaded / required, downloaded, required);
                continue;
            }

            // WinGet also emits a byte counter when the server supplies no length.
            var unknownLength = DownloadCounter().Match(line);
            if (stage == "Downloading" && unknownLength.Success
                && TryBytes(unknownLength.Groups["current"].Value, unknownLength.Groups["unit"].Value, out ulong count))
            {
                observed = new(stage, null, count);
                continue;
            }

            // Only WinGet's standalone block-bar percentage, following an explicit
            // install/uninstall marker, represents a native installation percentage.
            var percentage = InstallLine().Match(line);
            if (stage is "Installing" or "Uninstalling" && percentage.Success
                && int.TryParse(percentage.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                && value is >= 0 and <= 100)
            {
                observed = new(stage, value);
            }
        }

        _current = (observed ?? _current) with { Output = snapshot };
        return _current;
    }

    private static string? Stage(string line)
    {
        if (line.StartsWith("Downloading ", StringComparison.Ordinal) || line.StartsWith("正在下载 ", StringComparison.Ordinal)
            || line.StartsWith("正在下載 ", StringComparison.Ordinal))
        {
            return "Downloading";
        }

        return line switch
        {
            "Successfully verified installer hash" or "已成功验证安装程序哈希" or "已成功驗證安裝程式雜湊" => "Verifying",
            "Starting package install..." or "正在启动程序包安装..." or "正在啟動套件安裝..." => "Installing",
            "Starting package uninstall..." or "正在启动程序包卸载..." or "正在啟動套件解除安裝..." => "Uninstalling",
            _ => null,
        };
    }

    private static bool TryBytes(string text, string unit, out ulong bytes)
    {
        bytes = 0;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal amount) || amount < 0)
        {
            return false;
        }

        // ExecutionProgress.cpp uses powers of two even for the KB/MB labels.
        // These quantities retain the precision of WinGet's displayed counters.
        int shift = unit switch { "B" => 0, "KB" or "KiB" => 10, "MB" or "MiB" => 20, "GB" or "GiB" => 30, "TB" or "TiB" => 40, _ => -1 };
        if (shift < 0 || amount > ulong.MaxValue / (decimal)(1UL << shift))
        {
            return false;
        }

        bytes = (ulong)(amount * (1UL << shift));
        return true;
    }

    [GeneratedRegex(@"^[\s\u2580-\u259f]*(?<current>[0-9]+(?:\.[0-9]+)?)\s+(?<currentUnit>B|KB|MB|GB|TB|KiB|MiB|GiB|TiB)\s+/\s+(?<total>[0-9]+(?:\.[0-9]+)?)\s+(?<totalUnit>B|KB|MB|GB|TB|KiB|MiB|GiB|TiB)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadLine();

    [GeneratedRegex(@"^(?<current>[0-9]+(?:\.[0-9]+)?)\s+(?<unit>B|KB|MB|GB|TB|KiB|MiB|GiB|TiB)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadCounter();

    [GeneratedRegex(@"^[\u2580-\u259f][\s\u2580-\u259f]{28,}\s+(?<value>[0-9]{1,3})%\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex InstallLine();
}
