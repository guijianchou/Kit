// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.PowerToys.Monitor.Tests;

[TestClass]
public sealed class MonitorDuplicateDetectorTests
{
    [TestMethod]
    public void FindDuplicatesGroupsMatchingSha1WithoutCollapsingRecords()
    {
        MonitorFileRecord first = CreateRecord("a.txt", "same-sha1", 10);
        MonitorFileRecord second = CreateRecord("b.txt", "same-sha1", 10);
        MonitorFileRecord skipped = CreateRecord("large.iso", MonitorHasher.Sha1SkippedTooLarge, 100);

        IReadOnlyList<MonitorDuplicateGroup> duplicateGroups = MonitorDuplicateDetector.FindDuplicates(new[] { first, second, skipped });

        Assert.AreEqual(1, duplicateGroups.Count);
        Assert.AreEqual(2, duplicateGroups[0].Files.Count);
        Assert.AreEqual(10, duplicateGroups[0].WastedBytes);
    }

    private static MonitorFileRecord CreateRecord(string fileName, string? sha1, long fileSize)
    {
        return new MonitorFileRecord("~", "~", fileName, fileName, fileName, sha1, "2026-04-25T12:00:00", fileSize, "Documents");
    }
}
