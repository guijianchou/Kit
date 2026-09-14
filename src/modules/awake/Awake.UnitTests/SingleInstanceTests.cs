// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using Awake.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Awake.UnitTests;

[TestClass]
public class SingleInstanceTests
{
    [TestMethod]
    public void KitAndPowerToysCanHoldSeparateSingleInstanceMutexes()
    {
        // A unique suffix avoids interacting with running Kit or PowerToys instances.
        string suffix = Guid.NewGuid().ToString("N");
        using var powerToysMutex = new Mutex(false, $"{Constants.AppName}.{suffix}", out bool powerToysCreated);
        using var kitMutex = new Mutex(false, $"{Constants.SingleInstanceMutexName}.{suffix}", out bool kitCreated);
        using var secondKitMutex = new Mutex(false, $"{Constants.SingleInstanceMutexName}.{suffix}", out bool secondKitCreated);

        Assert.IsTrue(powerToysCreated);
        Assert.IsTrue(kitCreated, "Kit must not share the official Awake singleton.");
        Assert.IsFalse(secondKitCreated, "Two Kit instances must still share the same singleton.");
    }
}
