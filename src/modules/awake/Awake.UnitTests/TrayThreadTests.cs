// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using System.Threading.Tasks;
using Awake.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Awake.UnitTests;

[TestClass]
public class TrayThreadTests
{
    [TestMethod]
    [TestCategory("Desktop")]
    public async Task QueuedWorkRunsOnWindowThreadAndShutdownDestroysWindow()
    {
        await TrayHelper.InitializeTray(TrayHelper.DefaultAwakeIcon, "Kit Awake tray regression");
        try
        {
            var completions = Enumerable.Range(0, 24)
                .Select(_ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();
            await Task.WhenAll(completions.Select(completion => Task.Run(() =>
                TrayHelper.RunOnMainThread(() => completion.SetResult(Environment.CurrentManagedThreadId)))));
            var threads = await Task.WhenAll(completions.Select(completion => completion.Task))
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.AreEqual(1, threads.Distinct().Count(), "All native tray mutations must share the window thread.");
            Assert.AreNotEqual(IntPtr.Zero, TrayHelper.WindowHandle);
        }
        finally
        {
            await Task.Run(TrayHelper.Shutdown);
        }

        Assert.AreEqual(IntPtr.Zero, TrayHelper.WindowHandle, "Shutdown from a worker must destroy the owning thread's window.");
    }
}
