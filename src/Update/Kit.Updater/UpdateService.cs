using System.Diagnostics;

namespace Kit.Updater;

internal sealed class UpdateService(UpdateStorage storage, ReleaseClient releases)
{
    public async Task CheckAsync(CancellationToken token)
    {
        var state = storage.Read();
        if (HasRecovery(state))
        {
            storage.Checked(state);
            storage.Save(state);
            return;
        }

        try
        {
            var release = await releases.LatestAsync(token);
            var preserve = state.TargetVersion == release.Version.ToString(3) &&
                File.Exists(storage.Package) && ReleaseClient.IsHash(state.PackageSha256) && state.State == UpdateStatus.ReadyToInstall;
            state.ReleasePageUrl = release.Page;
            state.TargetVersion = release.Version.ToString(3);
            state.TotalBytes = release.Size;
            state.ErrorCode = string.Empty;
            state.State = release.Version <= storage.CurrentVersion ? UpdateStatus.UpToDate : preserve ? UpdateStatus.ReadyToInstall : UpdateStatus.ReadyToDownload;
            if (!preserve)
            {
                state.DownloadedInstallerFilename = string.Empty;
                state.PackageSha256 = string.Empty;
                state.DownloadedBytes = 0;
            }
        }
        catch (Exception error)
        {
            storage.Log(error);

            // A failed recheck must not discard a previously verified, ready-to-install package.
            if (state.State != UpdateStatus.ReadyToInstall)
            {
                state.State = UpdateStatus.NetworkError;
            }

            state.ErrorCode = (error as UpdateException)?.Code ?? "checkFailed";
        }

        storage.Checked(state);
        storage.Save(state);
    }

    public async Task DownloadAsync(CancellationToken token)
    {
        var state = storage.Read();
        if (HasRecovery(state))
        {
            storage.Save(state);
            return;
        }

        var part = storage.Package + ".part";
        var validation = Path.Combine(storage.Cache, "validate-" + Guid.NewGuid().ToString("N"));
        try
        {
            state.State = UpdateStatus.Downloading;
            state.ErrorCode = string.Empty;
            state.DownloadedBytes = 0;
            state.PackageSha256 = string.Empty;
            state.DownloadedInstallerFilename = string.Empty;
            storage.Save(state);

            // Resolve current release assets again; never download a URL supplied through IPC or persisted state.
            var release = await releases.LatestAsync(token);
            storage.Checked(state);
            if (release.Version <= storage.CurrentVersion)
            {
                state.State = UpdateStatus.UpToDate;
                storage.Save(state);
                return;
            }

            state.TargetVersion = release.Version.ToString(3);
            state.ReleasePageUrl = release.Page;
            state.TotalBytes = release.Size;
            storage.Save(state);
            Paths.EnsureNoLinks(part);
            if (File.Exists(part))
            {
                File.Delete(part);
            }

            var lastProgress = Stopwatch.StartNew();
            var hash = await releases.DownloadAsync(
                release,
                part,
                count =>
                {
                    state.DownloadedBytes = count;
                    if (lastProgress.ElapsedMilliseconds >= 250)
                    {
                        storage.Save(state);
                        lastProgress.Restart();
                    }
                },
                token);
            state.State = UpdateStatus.Verifying;
            storage.Save(state);
            _ = PortablePackage.Extract(part, validation, release.Version, token);
            token.ThrowIfCancellationRequested();
            File.Move(part, storage.Package, true);
            state.PackageSha256 = hash;
            state.DownloadedInstallerFilename = release.Name;
            state.State = UpdateStatus.ReadyToInstall;
            storage.Save(state);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            state.State = UpdateStatus.ReadyToDownload;
            state.ErrorCode = "cancelled";
            storage.Save(state);
        }
        catch (Exception error)
        {
            Fail(state, error);
        }
        finally
        {
            if (File.Exists(part))
            {
                File.Delete(part);
            }

            DeleteOwnedTree(validation);
        }
    }

    public void Refresh()
    {
        var state = storage.Read();
        if (HasRecovery(state))
        {
            storage.Save(state);
            return;
        }

        if (Version.TryParse(state.TargetVersion, out var target) && target <= storage.CurrentVersion)
        {
            state.State = UpdateStatus.UpToDate;
            state.ErrorCode = string.Empty;
            state.DownloadedInstallerFilename = string.Empty;
        }
        else if (state.State is UpdateStatus.Downloading or UpdateStatus.Verifying or UpdateStatus.Installing ||
            (state.State == UpdateStatus.ReadyToInstall && !File.Exists(storage.Package)))
        {
            state.State = UpdateStatus.ErrorDownloading;
            state.ErrorCode = "interrupted";
        }

        storage.Save(state);
    }

    public void StartApply(int runnerPid)
    {
        NativeProcesses.VerifyRunner(runnerPid, storage.Root);
        var state = storage.Read();
        if (state.State != UpdateStatus.ReadyToInstall && state.ErrorCode != "recoveryRequired")
        {
            throw new UpdateException("interrupted", "No prepared update is available.");
        }

        // Copy the entire self-contained helper outside the installation before asking Kit to exit.
        var worker = Path.Combine(storage.Cache, "worker");
        Directory.CreateDirectory(worker);
        Paths.EnsureNoLinks(worker);
        var executable = Paths.Under(worker, "Kit.Updater.exe");
        File.Copy(Environment.ProcessPath!, executable, true);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = worker };
        start.ArgumentList.Add("commit");
        start.ArgumentList.Add(storage.Root);
        start.ArgumentList.Add(runnerPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new IOException("Could not start the external updater.");
    }

    public async Task CommitAsync(int runnerPid)
    {
        var state = storage.Read();
        var closed = false;
        var transaction = new InstallTransaction(storage.Root, storage.Transaction);
        try
        {
            NativeProcesses.VerifyRunner(runnerPid, storage.Root);
            if (transaction.NeedsRecovery)
            {
                state.State = UpdateStatus.Installing;
                storage.Save(state);
                await NativeProcesses.CloseAndWaitAsync(runnerPid, storage.Root);
                closed = true;
                transaction.Recover();
                state.State = UpdateStatus.ErrorDownloading;
                state.ErrorCode = "recovered";
                storage.Save(state);
                NativeProcesses.StartKit(storage.Root);
                return;
            }

            if (state.State != UpdateStatus.ReadyToInstall || !Version.TryParse(state.TargetVersion, out var version) || version <= storage.CurrentVersion ||
                !ReleaseClient.IsHash(state.PackageSha256) || !Paths.Hash(storage.Package).Equals(state.PackageSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("hashMismatch", "Prepared update is invalid; download it again.");
            }

            DeleteOwnedTree(storage.Transaction);
            Directory.CreateDirectory(storage.Transaction);
            state.State = UpdateStatus.Verifying;
            storage.Save(state);
            var extracted = Path.Combine(storage.Transaction, "payload");
            var manifest = PortablePackage.Extract(storage.Package, extracted, version, CancellationToken.None);

            // Ensure write access before closing the application. Actual backup/replacement also checks each destination.
            var probe = Paths.Under(storage.Root, ".kit-update-write-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }

            state.State = UpdateStatus.Installing;
            storage.Save(state);
            await NativeProcesses.CloseAndWaitAsync(runnerPid, storage.Root);
            closed = true;
            transaction.Apply(extracted, manifest, () =>
            {
                state.State = UpdateStatus.UpToDate;
                state.ErrorCode = string.Empty;
                state.DownloadedInstallerFilename = string.Empty;
                storage.Save(state);
                NativeProcesses.StartKit(storage.Root);
            });

            // Keep one committed backup until the next download/apply, allowing manual inspection.
        }
        catch (Exception error)
        {
            Fail(state, error, transaction.NeedsRecovery ? "recoveryRequired" : null);

            // Never relaunch a partially rolled-back installation.
            if (closed && !transaction.NeedsRecovery)
            {
                NativeProcesses.StartKit(storage.Root);
            }
        }
    }

    public void Fail(UpdateState state, Exception error, string? code = null)
    {
        storage.Log(error);
        state.State = UpdateStatus.ErrorDownloading;
        state.ErrorCode = code ?? (error as UpdateException)?.Code ?? (error is UnauthorizedAccessException ? "accessDenied" : "updateFailed");
        storage.Save(state);
    }

    public void RecoverOffline()
    {
        var transaction = new InstallTransaction(storage.Root, storage.Transaction);
        if (!transaction.NeedsRecovery)
        {
            return;
        }

        if (NativeProcesses.ProcessesInRoot(storage.Root).Count != 0)
        {
            throw new UpdateException("closeFailed", "Close Kit before recovering an interrupted update.");
        }

        transaction.Recover();
        var state = storage.Read();
        state.State = UpdateStatus.ErrorDownloading;
        state.ErrorCode = "recovered";
        storage.Save(state);
        NativeProcesses.StartKit(storage.Root);
    }

    private bool HasRecovery(UpdateState state)
    {
        if (!new InstallTransaction(storage.Root, storage.Transaction).NeedsRecovery)
        {
            return false;
        }

        state.State = UpdateStatus.ErrorDownloading;
        state.ErrorCode = "recoveryRequired";
        return true;
    }

    private static void DeleteOwnedTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Paths.EnsureNoLinks(path);
        foreach (var item in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            Paths.EnsureNoLinks(item);
        }

        Directory.Delete(path, true);
    }
}
