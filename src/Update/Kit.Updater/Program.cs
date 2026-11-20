namespace Kit.Updater;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[2], out var runnerPid) || args[0] is not ("check" or "download" or "apply" or "commit" or "status" or "recover"))
        {
            return 2;
        }

        UpdateStorage? storage = null;
        using var operation = new Mutex(false, UpdateStorage.OperationMutex);

        // Commit is launched by a short-lived installed helper that still holds this mutex.
        if (!UpdateStorage.Acquire(operation, args[0] == "commit" ? 30000 : args[0] == "status" ? 0 : 5000))
        {
            return 3;
        }

        try
        {
            storage = new UpdateStorage(args[1]);
            var expected = args[0] is "commit" or "recover" ? Path.Combine(storage.Cache, "worker", "Kit.Updater.exe") : Path.Combine(storage.Root, "Updater", "Kit.Updater.exe");
            if (!string.Equals(Environment.ProcessPath, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("invalidInstall", "Updater must run from its installation or bound update cache.");
            }

            using var client = ReleaseClient.CreateHttpClient();
            var service = new UpdateService(storage, new ReleaseClient(client));
            switch (args[0])
            {
                case "check": service.CheckAsync(CancellationToken.None).GetAwaiter().GetResult(); break;
                case "status": service.Refresh(); break;
                case "download":
                    using (var cancellation = new CancellationTokenSource())
                    using (var signal = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateStorage.CancelEvent))
                    {
                        signal.Reset();
                        var wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => cancellation.Cancel(), null, Timeout.Infinite, true);
                        try
                        {
                            service.DownloadAsync(cancellation.Token).GetAwaiter().GetResult();
                        }
                        finally
                        {
                            wait.Unregister(null);
                        }
                    }

                    break;
                case "apply": service.StartApply(runnerPid); break;
                case "commit": service.CommitAsync(runnerPid).GetAwaiter().GetResult(); break;
                case "recover": service.RecoverOffline(); break;
            }

            return 0;
        }
        catch (Exception error)
        {
            if (storage != null)
            {
                storage.Log(error);
                var state = storage.Read();
                state.State = args[0] == "check" ? UpdateStatus.NetworkError : UpdateStatus.ErrorDownloading;
                state.ErrorCode = (error as UpdateException)?.Code ?? "updateFailed";
                storage.Checked(state);
                storage.Save(state);
            }

            return 1;
        }
        finally
        {
            operation.ReleaseMutex();
        }
    }
}
