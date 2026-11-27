// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Download organization is the module's write path, so its boundaries are pinned here:
/// classification, single-layer scope, hidden-file exclusion and the 500-item cap.
/// </summary>
[TestClass]
public sealed class DownloadOrganizerServiceTests
{
    [TestMethod]
    public void DownloadsPathResolvesToAnExistingLocationOrNone()
    {
        var service = new DownloadOrganizerService();
        string? path = service.GetDownloadsPath();

        // The known-folder lookup may fall back to ~/Downloads. It must never return a
        // path that does not exist, because callers enumerate it unconditionally.
        if (path is not null)
        {
            Assert.IsTrue(Directory.Exists(path), $"Resolved Downloads path must exist: {path}");
        }
    }

    [TestMethod]
    public async Task ScanIsSingleLayerAndExcludesHiddenAndSystemFiles()
    {
        var service = new DownloadOrganizerService();
        string? root = service.GetDownloadsPath();
        if (root is null)
        {
            Assert.Inconclusive("No Downloads folder is available in this environment.");
        }

        List<Kit.AIHubLib.Models.TempFileInfo> items =
            await service.ScanDownloadsAsync(CancellationToken.None);

        // Single layer: every reported file must sit directly in the Downloads root,
        // never in a subfolder (the module must not reorganize nested content).
        foreach (var item in items)
        {
            string? directory = Path.GetDirectoryName(item.FilePath);
            Assert.AreEqual(
                Path.TrimEndingDirectorySeparator(root!),
                Path.TrimEndingDirectorySeparator(directory!),
                "Scan must stay on the Downloads root and never recurse.");

            // Masking the hidden/system bits must yield zero: FileAttributes.Normal is
            // also zero, so compare against the numeric value to keep the intent clear.
            FileAttributes attributes = File.GetAttributes(item.FilePath);
            Assert.AreEqual(
                0,
                (int)(attributes & (FileAttributes.Hidden | FileAttributes.System)),
                "Hidden and system files must be excluded.");
        }

        Assert.IsTrue(items.Count <= 500, "Scan must respect the 500 item cap.");
    }

    [TestMethod]
    public async Task ClassifiedItemsCarryMoveActionAndBilingualReasons()
    {
        var service = new DownloadOrganizerService();
        List<Kit.AIHubLib.Models.TempFileInfo> items =
            await service.ScanDownloadsAsync(CancellationToken.None);

        var knownCategories = new HashSet<string>(StringComparer.Ordinal)
        {
            "Documents", "Compressed", "Programs", "Music", "Video", "Pictures", DownloadOrganizerService.OthersCategory,
        };

        foreach (var item in items)
        {
            Assert.IsTrue(knownCategories.Contains(item.Category), $"Unexpected category: {item.Category}");
            Assert.AreEqual("move", item.Action);
            Assert.AreEqual(item.Category, item.TargetRelativePath);
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.ReasonEn), "English reason is required.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.ReasonZh), "Chinese reason is required.");
            Assert.IsTrue(item.ItemId.StartsWith("item-", StringComparison.Ordinal), "Items need the host item id prefix.");
        }
    }

    [TestMethod]
    public async Task ItemIdsAreUniqueWithinAScan()
    {
        var service = new DownloadOrganizerService();
        List<Kit.AIHubLib.Models.TempFileInfo> items =
            await service.ScanDownloadsAsync(CancellationToken.None);

        if (items.Count > 1)
        {
            Assert.AreEqual(
                items.Count,
                items.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count(),
                "Item ids must be unique: the AI contract references items by id.");
        }
    }

    [TestMethod]
    public async Task PictureExtensionsAreClassifiedCaseInsensitivelyAndMovedWithoutOverwriting()
    {
        using var fixture = new FixtureDirectory();
        string root = fixture.PathFor("Downloads");
        Directory.CreateDirectory(root);
        string[] extensions = ["jpg", "jpeg", "jpe", "jfif", "png", "apng", "gif", "bmp", "dib", "tif", "tiff", "webp", "avif", "heic", "heif", "svg", "ico"];
        foreach (string extension in extensions)
        {
            foreach (string variant in new[] { extension, extension.ToUpperInvariant() })
            {
                string name = (variant == extension ? "lower." : "upper.") + variant;
                WriteOld(Path.Combine(root, name), name);
                WriteOld(Path.Combine(root, "Pictures", name), "existing " + name);
            }
        }

        var service = new DownloadOrganizerService(root);
        var items = await service.ScanAsync();
        Assert.AreEqual(extensions.Length * 2, items.Count);
        foreach (var item in items)
        {
            Assert.AreEqual("Pictures", item.Category);
            Assert.AreEqual("Pictures", item.TargetRelativePath);
            Assert.AreEqual("move", item.Action);
            var result = service.OrganizeItemDetailed(item);
            string expected = Path.Combine(root, "Pictures", Path.GetFileNameWithoutExtension(item.FileName) + "_1" + Path.GetExtension(item.FileName));
            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(expected, result.DestinationPath);
            Assert.AreEqual(item.FileName, File.ReadAllText(expected));
            Assert.AreEqual("existing " + item.FileName, File.ReadAllText(Path.Combine(root, "Pictures", item.FileName)));
            Assert.IsFalse(File.Exists(item.FilePath));
        }

        Assert.AreEqual(0, (await service.ScanAsync()).Count, "Pictures subfolders must never be scanned recursively.");
    }

    [TestMethod]
    public async Task PictureCandidatesKeepDownloadEligibilityAndExecutionRevalidation()
    {
        using var fixture = new FixtureDirectory();
        string root = fixture.PathFor("Downloads");
        foreach (string name in new[] { "ready.PnG", "hidden.jpg", "system.webp", "locked.gif", "partial.jpg.crdownload", "active.png", "unfinished.part.jpeg", "unknown.image" })
        {
            WriteOld(Path.Combine(root, name), name);
        }

        File.SetAttributes(Path.Combine(root, "hidden.jpg"), FileAttributes.Hidden);
        File.SetAttributes(Path.Combine(root, "system.webp"), FileAttributes.System);
        File.WriteAllText(Path.Combine(root, "active.png.aria2"), "downloading");
        File.WriteAllText(Path.Combine(root, "recent.heic"), "recent");
        WriteOld(Path.Combine(root, "nested", "photo.jpg"), "nested");
        using var held = File.Open(Path.Combine(root, "locked.gif"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var service = new DownloadOrganizerService(root);
        var scanned = await service.ScanAsync();
        Assert.AreEqual(2, scanned.Count);
        var item = scanned.Single(candidate => candidate.FileName == "ready.PnG");
        var fallbackItem = scanned.Single(candidate => candidate.FileName == "unknown.image");
        Assert.AreEqual("Pictures", item.Category);
        Assert.AreEqual("download-extension", item.EvidenceKind);
        Assert.AreEqual(DownloadOrganizerService.OthersCategory, fallbackItem.Category);
        Assert.AreEqual("download-fallback", fallbackItem.EvidenceKind);
        item.TargetRelativePath = "Documents";
        Assert.IsFalse(service.OrganizeItem(item));
        item.TargetRelativePath = "../Pictures";
        Assert.IsFalse(service.OrganizeItem(item));
        item.TargetRelativePath = "Pictures";
        File.AppendAllText(item.FilePath, "changed since scanning");
        Assert.IsFalse(service.OrganizeItem(item));
        Assert.IsTrue(File.Exists(item.FilePath));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "Pictures")), "Rejected execution must not create the destination.");

        fallbackItem.TargetRelativePath = "../Others";
        Assert.IsFalse(service.OrganizeItem(fallbackItem));
        fallbackItem.TargetRelativePath = DownloadOrganizerService.OthersCategory;
        var movedFallback = service.OrganizeItemDetailed(fallbackItem);
        Assert.IsTrue(movedFallback.Success, movedFallback.Error);
        Assert.AreEqual(Path.Combine(root, "Others", "unknown.image"), movedFallback.DestinationPath);
        Assert.IsTrue(File.Exists(movedFallback.DestinationPath));
    }

    private static void WriteOld(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
    }
}
