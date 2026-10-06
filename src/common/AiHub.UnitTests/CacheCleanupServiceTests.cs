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
        using var fixture = new FixtureDirectory();
        var service = CreateService(fixture);

        List<TempFileInfo> items = await service.ScanAsync(CancellationToken.None);

        Assert.IsNotNull(items);
        CollectionAssert.AreEquivalent(new[] { "first.tmp", "second.temp" }, items.Select(item => item.FileName).ToArray());
        Assert.IsTrue(items.Count <= 200000, "The scan must stay bounded.");

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
        using var fixture = new FixtureDirectory();
        var service = CreateService(fixture);

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
    public async Task CacheCandidatesUseOnlyTheDeleteAction()
    {
        using var fixture = new FixtureDirectory();
        var service = CreateService(fixture);

        List<TempFileInfo> items = await service.ScanAsync(CancellationToken.None);

        // Downloads moves must never enter the cache cleanup pipeline.
        Assert.IsTrue(
            items.All(item => string.Equals(item.Action, "delete", StringComparison.OrdinalIgnoreCase)),
            "Every cache candidate must use the 'delete' action.");
    }

    [TestMethod]
    public async Task CancellationSurfacesAsTaskCanceledRatherThanAPartialResult()
    {
        using var fixture = new FixtureDirectory();
        var service = CreateService(fixture);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The scan propagates cancellation instead of quietly returning a partial list;
        // callers must handle it so a cancelled scan is never mistaken for a complete one.
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => service.ScanAsync(cts.Token));
    }

    [TestMethod]
    public async Task CleaningNothingIsANoOp()
    {
        using var fixture = new FixtureDirectory();
        var service = CreateService(fixture);

        (int succeeded, int failed) = await service.CleanupSelectedAsync(new List<TempFileInfo>());

        Assert.AreEqual(0, succeeded);
        Assert.AreEqual(0, failed);
    }

    [TestMethod]
    public async Task ItemsCarryAnOpaqueIdentifierForTheAiContract()
    {
        using var fixture = new FixtureDirectory();
        var service = CreateService(fixture);

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

    private static CacheCleanupService CreateService(FixtureDirectory fixture)
    {
        string local = fixture.PathFor("Local");
        string temp = Path.Combine(local, "Temp");
        Directory.CreateDirectory(temp);
        foreach (string name in new[] { "first.tmp", "second.temp", "keep.docx" })
        {
            string path = Path.Combine(temp, name);
            File.WriteAllText(path, "fixture");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        }

        return new CacheCleanupService(local, temp, _ => throw new AssertFailedException("No deletion expected."));
    }
}
