namespace Kit.AiHub.UnitTests;

using System.Linq;
using Kit.AiHub.Engine;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class DynamicBatcherTests
{
    [TestMethod]
    [DataRow("low", 40, 3)]
    [DataRow("high", 20, 6)]
    [DataRow("max", 15, 7)]
    public void EffortsKeepLargeAuditsWithinBatchLimitsWithoutDroppingRecords(string effort, int maximumBatchSize, int expectedBatchCount)
    {
        int[] records = Enumerable.Range(0, 102).ToArray();
        var batches = DynamicBatcher.CreateBatches(records, DynamicBatcher.CalculateBatchSize(records.Length, effort)).ToArray();

        Assert.AreEqual(expectedBatchCount, batches.Length);
        Assert.IsTrue(batches.All(batch => batch.Count <= maximumBatchSize));
        CollectionAssert.AreEqual(records, batches.SelectMany(batch => batch).ToArray());
    }
}
