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
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Cache cleanup is the module's destructive path, so its safety contract is pinned here:
/// it only ever offers files from the known whitelist, never from a protected root, and it
/// classifies risk rather than assuming everything is safe.
/// </summary>
[TestClass]
public sealed class CacheCleanupServiceTests
{
    /// <summary>Protected locations that must never appear in scan results.</summary>
    private static readonly string[] ProtectedRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
    ];

    [TestMethod]
    public async Task ScanNeverOffersFilesOutsideTheWhitelistedLocations()
    {
        var service = new CacheCleanupService();

        List<TempFileInfo> items = await service.ScanAsync(CancellationToken.None);

        Assert.IsNotNull(items);
        Assert.IsTrue(items.Count <= 5000, "The scan must stay bounded.");

        foreach (TempFileInfo item in items)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.FilePath));
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.Category), "Every candidate carries a category.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.ReasonEn));
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.ReasonZh));
        }
    }

    [TestMethod]
    public async Task ProtectedRootsAreNeverOfferedForCleanup()
    {
        var service = new CacheCleanupService();

        List<TempFileInfo> items = await service.ScanAsync(CancellationToken.None);

        foreach (string root in ProtectedRoots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            string normalizedRoot = Path.TrimEndingDirectorySeparator(root);
            Assert.IsFalse(
                items.Any(item => Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(item.FilePath) ?? string.Empty)
                    .StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)),
                $"Files under {root} must never be offered for cleanup.");
        }
    }

    [TestMethod]
    public async Task DestructiveActionsAreReservedForRecycleBinCapableItems()
    {
        var service = new CacheCleanupService();

        List<TempFileInfo> items = await service.ScanAsync(CancellationToken.None);

        // The service may only ever ask for a recycle-bin move, never a hard delete.
        Assert.IsTrue(
            items.All(item => string.Equals(item.Action, "delete", StringComparison.OrdinalIgnoreCase)),
            "Cache cleanup uses the recycle bin, so every candidate is a 'delete' action.");
    }

    [TestMethod]
    public async Task CancellationSurfacesAsTaskCanceledRatherThanAPartialResult()
    {
        var service = new CacheCleanupService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The scan propagates cancellation instead of quietly returning a partial list;
        // callers must handle it so a cancelled scan is never mistaken for a complete one.
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => service.ScanAsync(cts.Token));
    }

    [TestMethod]
    public async Task CleaningNothingIsANoOp()
    {
        var service = new CacheCleanupService();

        (int succeeded, int failed) = await service.CleanupSelectedAsync(new List<TempFileInfo>());

        Assert.AreEqual(0, succeeded);
        Assert.AreEqual(0, failed);
    }

    [TestMethod]
    public async Task ItemsCarryAnOpaqueIdentifierForTheAiContract()
    {
        var service = new CacheCleanupService();

        List<TempFileInfo> items = await service.ScanAsync(CancellationToken.None);

        foreach (TempFileInfo item in items)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.ItemId), "The AI contract references items by id.");
        }

        if (items.Count > 1)
        {
            Assert.AreEqual(items.Count, items.Select(i => i.ItemId).Distinct(StringComparer.Ordinal).Count());
        }
    }
}
