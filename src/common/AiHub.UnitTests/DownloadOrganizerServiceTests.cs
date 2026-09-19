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
            "Documents", "Archives", "Images", "Installers", "Videos", "Audio", "Code", "Other",
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
}
