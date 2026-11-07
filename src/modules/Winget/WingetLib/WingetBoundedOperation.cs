// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.WingetLib;

// A synchronous COM call cannot be interrupted by Task.WaitAsync. Keep its slot
// reserved after the caller stops waiting, so retries cannot accumulate workers.
internal sealed class WingetBoundedOperation(TimeSpan timeout)
{
    private readonly object _gate = new();
    private Task? _worker;

    internal Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken, bool bounded = true)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        Task<T> worker;
        CancellationTokenSource stop;
        lock (_gate)
        {
            if (_worker is { IsCompleted: false })
            {
                return Task.FromException<T>(new WingetOperationException("Windows Package Manager is still finishing the previous request. Wait briefly and try again."));
            }

            stop = new CancellationTokenSource();
            worker = Task.Run(() => operation(stop.Token), CancellationToken.None);
            _worker = worker;
        }

        return WaitAsync(worker, stop, bounded, cancellationToken);
    }

    private async Task<T> WaitAsync<T>(Task<T> worker, CancellationTokenSource stop, bool bounded, CancellationToken cancellationToken)
    {
        Task? cancelling = null;
        try
        {
            // Only searches release their caller on a deadline. Installer-related
            // validation retains its original lifetime and cancellation semantics.
            return bounded ? await worker.WaitAsync(timeout, cancellationToken).ConfigureAwait(false)
                : await worker.ConfigureAwait(false);
        }
        catch (TimeoutException) when (bounded)
        {
            cancelling = stop.CancelAsync();
            throw new WingetOperationException("The WinGet catalog search timed out. Previous results were kept. Windows Package Manager may still be finishing the request; try again shortly.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelling = stop.CancelAsync();
            throw;
        }
        finally
        {
            _ = ObserveCompletionAsync(worker, stop, cancelling);
        }
    }

    private static async Task ObserveCompletionAsync(Task worker, CancellationTokenSource stop, Task? cancelling)
    {
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The caller may already have received a timeout/cancellation.
        }

        try
        {
            if (cancelling is not null)
            {
                await cancelling.ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A failed native cancellation cannot replace the original result.
        }
        finally
        {
            stop.Dispose();
        }
    }
}
