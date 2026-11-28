// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using Kit.WingetLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winget.UnitTests;

[TestClass]
public sealed class WingetCliProgressTests
{
    [DataTestMethod]
    [DataRow("Downloading https://example.invalid/a.exe", "Starting package install...")]
    [DataRow("正在下载 https://example.invalid/a.exe", "正在启动程序包安装...")]
    public void RealByteCountersUseWinGetBinaryUnitsAndResetBeforeInstallation(string download, string install)
    {
        var parser = new WingetCliProgress();
        var partial = parser.Read(download + "\n  █████▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  512 KB / 2.00 MB\n");
        Assert.AreEqual("Downloading", partial.Stage);
        Assert.AreEqual(25.0, partial.Percentage);
        Assert.AreEqual(524288UL, partial.BytesDownloaded);
        Assert.AreEqual(2097152UL, partial.BytesRequired);
        var installing = parser.Read(download + "\n  2.00 MB / 2.00 MB\n" + install + "\n");
        Assert.AreEqual("Installing", installing.Stage);
        Assert.IsNull(installing.Percentage);
        Assert.AreEqual(0UL, installing.BytesDownloaded);
        Assert.AreEqual(0UL, installing.BytesRequired);
    }

    [TestMethod]
    public void AnsiChunkBoundariesAndCarriageReturnsPublishTheLatestDownload()
    {
        var buffer = new WingetOutputBuffer();
        var parser = new WingetCliProgress();
        buffer.Append("Downloading https://example.invalid/a.exe\n\u001b[3");
        buffer.Append("2m  ███▒▒▒  1.00 MB / 4.00 MB\u001b[0m");
        Assert.AreEqual(25.0, parser.Read(buffer.Snapshot()).Percentage);
        buffer.Append("\r\u001b[2K  ██████▒  3.00 MB / 4.00 MB");
        var updated = parser.Read(buffer.Snapshot());
        Assert.AreEqual(75.0, updated.Percentage);
        Assert.AreEqual(3145728UL, updated.BytesDownloaded);
        buffer.Append("\r  0 B / 8.00 MB");
        var restarted = parser.Read(buffer.Snapshot());
        Assert.AreEqual(0.0, restarted.Percentage, "A retry or next dependency must not retain an earlier download percentage.");
        Assert.AreEqual(8388608UL, restarted.BytesRequired);
    }

    [DataTestMethod]
    [DataRow("47%")]
    [DataRow("Installation is 47% complete")]
    [DataRow("Downloading 47%")]
    [DataRow("version 1 MB / 4 MB")]
    [DataRow("-1 MB / 4 MB")]
    [DataRow("5 MB / 4 MB")]
    [DataRow("1 MB / 0 B")]
    [DataRow("NaN MB / 4 MB")]
    [DataRow("Infinity MB / 4 MB")]
    [DataRow("1,000 MB / 4,000 MB")]
    [DataRow("18446744073709551616 TB / 18446744073709551617 TB")]
    [DataRow("1 MB / 4 MB per second")]
    public void InvalidOrUnqualifiedNumbersNeverProduceProgress(string line)
    {
        var value = new WingetCliProgress().Read(line);
        Assert.IsNull(value.Percentage);
        Assert.AreEqual(0UL, value.BytesDownloaded);
        Assert.AreEqual(0UL, value.BytesRequired);
    }

    [TestMethod]
    public void UnknownDownloadLengthAndUnknownInstallerProgressStayIndeterminate()
    {
        var parser = new WingetCliProgress();
        var unknown = parser.Read("Downloading https://example.invalid/a.exe\n  1.25 MB");
        Assert.AreEqual("Downloading", unknown.Stage);
        Assert.IsNull(unknown.Percentage);
        Assert.AreEqual(1310720UL, unknown.BytesDownloaded);
        Assert.AreEqual(0UL, unknown.BytesRequired);
        var installing = parser.Read("Starting package install...\nWaiting for administrator approval\n");
        Assert.AreEqual("Installing", installing.Stage);
        Assert.IsNull(installing.Percentage);
        Assert.AreEqual(0UL, installing.BytesDownloaded);
    }

    [TestMethod]
    public void VerificationAndNextDependencyReplacePreviousDownloadMeasurements()
    {
        var parser = new WingetCliProgress();
        _ = parser.Read("Downloading https://example.invalid/a.exe\n  8 MB / 8 MB");
        var verified = parser.Read("Successfully verified installer hash\n");
        Assert.AreEqual("Verifying", verified.Stage);
        Assert.IsNull(verified.Percentage);
        Assert.AreEqual(0UL, verified.BytesRequired);
        var dependency = parser.Read("Downloading https://example.invalid/dependency.exe\n  0 B / 64 KB");
        Assert.AreEqual("Downloading", dependency.Stage);
        Assert.AreEqual(0.0, dependency.Percentage);
        Assert.AreEqual(65536UL, dependency.BytesRequired);
    }

    [DataTestMethod]
    [DataRow("Starting package install...", "Installing")]
    [DataRow("Starting package uninstall...", "Uninstalling")]
    public void NativePercentageRequiresAnExplicitInstallStageAndStandaloneBlockBar(string marker, string stage)
    {
        string bar = new string('█', 14) + new string('▒', 16) + "  47%";
        Assert.IsNull(new WingetCliProgress().Read(bar).Percentage);
        var parser = new WingetCliProgress();
        var progress = parser.Read(marker + "\n" + bar);
        Assert.AreEqual(stage, progress.Stage);
        Assert.AreEqual(47.0, progress.Percentage);
        Assert.AreEqual(0UL, progress.BytesRequired);
        Assert.AreEqual(
            48.0,
            parser.Read(bar.Replace("47%", "48%", StringComparison.Ordinal)).Percentage,
            "Progress still advances after the stage marker has rolled out of the bounded output tail.");
        Assert.IsNull(new WingetCliProgress().Read(marker + "\n" + bar.Replace("47%", "101%", StringComparison.Ordinal)).Percentage);
    }

    [TestMethod]
    public void DownloadQuantitiesAreOptionalOnTheWireAndRoundTripWithoutChangingUnits()
    {
        var legacy = JsonSerializer.Deserialize("{\"phase\":\"Downloading\",\"progressPercent\":25}", WingetServiceJsonContext.Default.WingetServiceStatus)!;
        Assert.IsNull(legacy.BytesDownloaded);
        Assert.IsNull(legacy.BytesRequired);
        var current = legacy with { BytesDownloaded = 524288, BytesRequired = 2097152 };
        string json = JsonSerializer.Serialize(current, WingetServiceJsonContext.Default.WingetServiceStatus);
        var restored = JsonSerializer.Deserialize(json, WingetServiceJsonContext.Default.WingetServiceStatus)!;
        Assert.AreEqual(current.BytesDownloaded, restored.BytesDownloaded);
        Assert.AreEqual(current.BytesRequired, restored.BytesRequired);
    }
}
