// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Kit.WingetLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winget.UnitTests;

[TestClass]
public sealed class WingetBoundedOperationTests
{
    [TestMethod]
    public async Task TimedOutSearchRetainsAdmissionUntilUncooperativeWorkerActuallyExits()
    {
        var gate = new WingetBoundedOperation(TimeSpan.FromMilliseconds(50));
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var search = gate.RunAsync(
            token =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult(token);
                return release.Task;
            },
            CancellationToken.None);
        var workerToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var error = await Assert.ThrowsExactlyAsync<WingetOperationException>(() => search);
            StringAssert.Contains(error.Message, "timed out");
            Assert.IsTrue(workerToken.IsCancellationRequested);
            for (int attempt = 0; attempt < 10; attempt++)
            {
                // All client operations use the same gate, including operations
                // which deliberately have no search deadline (details/check/etc.).
                var blocked = gate.RunAsync(
                    _ =>
                    {
                        Interlocked.Increment(ref calls);
                        return Task.FromResult(2);
                    },
                    CancellationToken.None,
                    bounded: attempt % 2 == 0);
                await Assert.ThrowsExactlyAsync<WingetOperationException>(() => blocked);
            }

            Assert.AreEqual(1, calls, "Retries must not spawn more native workers.");
        }
        finally
        {
            release.TrySetException(new InvalidOperationException("Late native failure"));
        }

        Assert.AreEqual(7, await RunAfterDrainingAsync(gate));
    }

    [TestMethod]
    public async Task CancellationKeepsSlotAndObservesFailingNativeCancellationCallback()
    {
        var gate = new WingetBoundedOperation(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = gate.RunAsync(
            async token =>
            {
                using var registration = token.Register(() =>
                {
                    cancelled.TrySetResult();
                    throw new InvalidOperationException("Native cancellation failed");
                });
                started.TrySetResult();
                return await release.Task;
            },
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => search);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.ThrowsExactlyAsync<WingetOperationException>(() => gate.RunAsync(_ => Task.FromResult(2), CancellationToken.None, bounded: false));
        }
        finally
        {
            release.TrySetResult(1);
        }

        Assert.AreEqual(7, await RunAfterDrainingAsync(gate));
    }

    [TestMethod]
    public async Task NonSearchOperationWaitsForItsRealCompletionAfterCallerCancellation()
    {
        var gate = new WingetBoundedOperation(TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = gate.RunAsync(
            _ =>
            {
                started.TrySetResult();
                return release.Task;
            },
            cancellation.Token,
            bounded: false);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await cancellation.CancelAsync();
        Assert.IsFalse(operation.IsCompleted, "Search deadlines must not abandon installer-related operations.");
        release.TrySetResult(5);
        Assert.AreEqual(5, await operation);
    }

    private static async Task<int> RunAfterDrainingAsync(WingetBoundedOperation gate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                return await gate.RunAsync(_ => Task.FromResult(7), timeout.Token);
            }
            catch (WingetOperationException)
            {
                await Task.Delay(10, timeout.Token);
            }
        }
    }
}
