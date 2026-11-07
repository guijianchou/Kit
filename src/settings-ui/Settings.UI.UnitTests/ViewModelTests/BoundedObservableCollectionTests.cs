// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Specialized;
using System.Linq;
using Kit.Settings.UI.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ViewModelTests
{
    [TestClass]
    public class BoundedObservableCollectionTests
    {
        private static readonly int[] LatestThree = [2, 3, 4];
        private static readonly int[] LatestTwo = [3, 4];

        [TestMethod]
        public void AppendingBacklogKeepsNewestSamplesWithOneNotification()
        {
            var collection = new BoundedObservableCollection<int>();
            collection.AppendRange(Enumerable.Range(0, 240), 240);
            int notifications = 0;
            collection.CollectionChanged += (_, args) =>
            {
                notifications++;
                Assert.AreEqual(NotifyCollectionChangedAction.Reset, args.Action);
                Assert.AreEqual(240, collection.Count);
            };

            collection.AppendRange(Enumerable.Range(240, 500), 240);

            Assert.AreEqual(1, notifications);
            CollectionAssert.AreEqual(Enumerable.Range(500, 240).ToArray(), collection.ToArray());
        }

        [TestMethod]
        public void SingleAppendAtCapacityPublishesOnlyFinalBoundedState()
        {
            var collection = new BoundedObservableCollection<int>();
            collection.AppendRange([1, 2, 3], 3);
            int notifications = 0;
            collection.CollectionChanged += (_, _) =>
            {
                notifications++;
                CollectionAssert.AreEqual(LatestThree, collection.ToArray());
            };

            collection.Append(4, 3);

            Assert.AreEqual(1, notifications);
        }

        [TestMethod]
        public void EmptyBacklogAndUnchangedCapacityDoNotNotify()
        {
            var collection = new BoundedObservableCollection<int>();
            collection.AppendRange([1, 2, 3], 3);
            int notifications = 0;
            collection.CollectionChanged += (_, _) => notifications++;

            collection.AppendRange([], 3);
            collection.Trim(3);
            collection.Trim(4);

            Assert.AreEqual(0, notifications);
        }

        [TestMethod]
        public void ReducingCapacityDropsOldestSamplesOnce()
        {
            var collection = new BoundedObservableCollection<int>();
            collection.AppendRange([1, 2, 3, 4], 4);
            int notifications = 0;
            collection.CollectionChanged += (_, _) => notifications++;

            collection.Trim(2);

            Assert.AreEqual(1, notifications);
            CollectionAssert.AreEqual(LatestTwo, collection.ToArray());
        }
    }
}
