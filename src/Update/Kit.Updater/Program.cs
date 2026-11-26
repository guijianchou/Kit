namespace Kit.Updater;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[2], out var runnerPid) || args[0] is not ("check" or "download" or "apply" or "commit" or "status" or "recover"))
        {
            UpdateFailure.WriteStandardError(new ArgumentException("Expected an update command, installation root and runner process ID."));
            return 2;
        }

        UpdateStorage? storage = null;
        Mutex? operation = null;
        var operationAcquired = false;
        var acquiringOperation = false;

        try
        {
            operation = new Mutex(false, UpdateStorage.OperationMutex);
            acquiringOperation = true;

            // Commit is launched by a short-lived installed helper that still holds this mutex.
            operationAcquired = UpdateStorage.Acquire(operation, args[0] == "commit" ? 30000 : args[0] == "status" ? 0 : 5000);
            acquiringOperation = false;
            if (!operationAcquired)
            {
                return 3;
            }

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
            return UpdateFailure.Report(error, storage, args[0], operationBusy: acquiringOperation && error is TimeoutException);
        }
        finally
        {
            try
            {
                if (operationAcquired)
                {
                    operation!.ReleaseMutex();
                }
            }
            catch (Exception error)
            {
                UpdateFailure.WriteDiagnostics(error, storage);
            }
            finally
            {
                try
                {
                    operation?.Dispose();
                }
                catch (Exception error)
                {
                    UpdateFailure.WriteDiagnostics(error, storage);
                }
            }
        }
    }
}
