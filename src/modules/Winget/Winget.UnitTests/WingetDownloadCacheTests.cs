// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Kit.WingetLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winget.UnitTests;

[TestClass]
public sealed class WingetDownloadCacheTests
{
    [TestMethod]
    public void CleanupRemovesOnlyExpiredOwnedOperationDirectories()
    {
        WithCache((root, cache) =>
        {
            var expired = cache.CreateOperationDirectory();
            File.WriteAllText(Path.Combine(expired, "installer.exe"), "test");
            File.SetLastWriteTimeUtc(Path.Combine(expired, WingetDownloadCache.OwnerFileName), DateTime.UtcNow.AddDays(-8));
            var foreign = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(foreign);
            File.WriteAllText(Path.Combine(foreign, "keep.txt"), "keep");
            cache.Cleanup(DateTimeOffset.UtcNow);
            Assert.IsFalse(Directory.Exists(expired));
            Assert.IsTrue(File.Exists(Path.Combine(foreign, "keep.txt")));
        });
    }

    [TestMethod]
    public void CleanupKeepsTheActiveOperationAndRejectsPathsOutsideRoot()
    {
        WithCache((root, cache) =>
        {
            var active = cache.CreateOperationDirectory();
            File.SetLastWriteTimeUtc(Path.Combine(active, WingetDownloadCache.OwnerFileName), DateTime.UtcNow.AddDays(-8));
            cache.Cleanup(DateTimeOffset.UtcNow, active);
            Assert.IsTrue(Directory.Exists(active));
            Assert.IsFalse(cache.TryRemoveOperationDirectory(Path.GetDirectoryName(root)!));
            Assert.IsTrue(Directory.Exists(root));
        });
    }

    [TestMethod]
    public void DownloadResultOmitsOwnerMarkerAndIncludesNestedFiles()
    {
        WithCache((_, cache) =>
        {
            var directory = cache.CreateOperationDirectory();
            var child = Directory.CreateDirectory(Path.Combine(directory, "nested")).FullName;
            var file = Path.Combine(child, "installer.msix");
            File.WriteAllText(file, "test");
            CollectionAssert.AreEqual(new[] { file }, cache.GetDownloadedFiles(directory).ToArray());
        });
    }

    [TestMethod]
    public void ReparsePointIsNeverTraversedOrDeleted()
    {
        WithCache((root, cache) =>
        {
            var operation = cache.CreateOperationDirectory();
            var outside = Directory.CreateDirectory(Path.Combine(root, "foreign")).FullName;
            var sentinel = Path.Combine(outside, "keep.txt");
            File.WriteAllText(sentinel, "keep");
            var link = Path.Combine(operation, "linked");
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var argument in new[] { "/c", "mklink", "/J", link, outside })
                {
                    process.StartInfo.ArgumentList.Add(argument);
                }

                Assert.IsTrue(process.Start());
                Assert.IsTrue(process.WaitForExit(5000));
                Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
            }

            try
            {
                Assert.IsFalse(cache.TryRemoveOperationDirectory(operation));
                Assert.ThrowsExactly<IOException>(() => cache.GetDownloadedFiles(operation));
                Assert.IsTrue(File.Exists(sentinel));
                Assert.IsTrue(Directory.Exists(link));
            }
            finally
            {
                Directory.Delete(link);
            }
        });
    }

    private static void WithCache(Action<string, WingetDownloadCache> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "Kit.Winget.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            action(root, new WingetDownloadCache(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
