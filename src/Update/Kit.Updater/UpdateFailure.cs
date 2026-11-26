namespace Kit.Updater;

internal static class UpdateFailure
{
    private const int MaximumLogBytes = 1024 * 1024;
    private const int MaximumEntryCharacters = 16384;

    // These exit codes let the runner report failures even when this process cannot write UpdateState.json.
    public static int Report(Exception error, UpdateStorage? storage, string action, bool operationBusy = false, string? localApplicationData = null, TextWriter? standardError = null)
    {
        WriteDiagnostics(error, storage, localApplicationData, standardError);
        var code = operationBusy ? "helperBusy" : (error as UpdateException)?.Code ?? (error is UnauthorizedAccessException ? "accessDenied" : "updateFailed");
        if (storage != null && !operationBusy)
        {
            try
            {
                var state = storage.Read();
                if (action != "check" || state.State != UpdateStatus.ReadyToInstall)
                {
                    state.State = action == "check" ? UpdateStatus.NetworkError : UpdateStatus.ErrorDownloading;
                }

                state.ErrorCode = code;
                storage.Checked(state);
                storage.Save(state);
            }
            catch (Exception stateError)
            {
                // Keep the original failure and exit code when the same permissions also block state reporting.
                WriteDiagnostics(stateError, storage, localApplicationData, standardError);
            }
        }

        return code == "accessDenied" ? 5 : operationBusy ? 3 : 1;
    }

    public static void WriteDiagnostics(Exception error, UpdateStorage? storage = null, string? localApplicationData = null, TextWriter? standardError = null)
    {
        WriteStandardError(error, standardError);
        if (storage != null)
        {
            try
            {
                var path = Path.Combine(storage.Cache, "updater.log");
                Paths.EnsureNoLinks(path);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {error}\n");
                return;
            }
            catch (Exception logError)
            {
                WriteStandardError(logError, standardError);
            }
        }

        try
        {
            // Use a fixed per-user location, never the unvalidated installation argument.
            var directory = localApplicationData != null
                ? Path.Combine(localApplicationData, "Kit")
                : storage?.DataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit");
            if (!Path.IsPathFullyQualified(directory))
            {
                return;
            }

            var path = Path.Combine(directory, "updater-startup.log");
            Paths.EnsureNoLinks(directory);
            Directory.CreateDirectory(directory);
            Paths.EnsureNoLinks(path);
            var entry = $"{DateTimeOffset.Now:O} {error}\n";
            if (entry.Length > MaximumEntryCharacters)
            {
                entry = entry[..MaximumEntryCharacters] + "\n[truncated]\n";
            }

            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var bytes = System.Text.Encoding.UTF8.GetBytes(entry);
            if (stream.Length + bytes.Length <= MaximumLogBytes)
            {
                stream.Write(bytes);
            }
        }
        catch (Exception logError)
        {
            // Low integrity or a full disk can also block the fallback. Stderr and the exit code remain independent.
            WriteStandardError(logError, standardError);
        }
    }

    public static void WriteStandardError(Exception error, TextWriter? standardError = null)
    {
        try
        {
            (standardError ?? Console.Error).WriteLine(error);
        }
        catch
        {
            // A missing or closed stderr handle must never replace the failure being reported.
        }
    }
}
