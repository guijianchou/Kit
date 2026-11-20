using System.Text.Json;

namespace Kit.Updater;

// The flushed journal records intent before each mutation. Backups survive crashes and failed rollbacks.
internal sealed class InstallTransaction(string root, string work)
{
    private string JournalPath => Path.Combine(work, "journal.json");
    private string Backup => Path.Combine(work, "backup");

    public bool NeedsRecovery => File.Exists(JournalPath) && ReadJournal().Phase is not ("committed" or "rolledBack");

    public void Apply(string extracted, PortableManifest package, Action? start = null, Action<int>? afterReplace = null)
    {
        if (NeedsRecovery)
        {
            throw new UpdateException("recoveryRequired", "An interrupted update must be recovered first.");
        }

        if (Directory.Exists(Backup))
        {
            throw new IOException("Backup directory already exists.");
        }

        var incoming = package.Files.Select(file => file.Path).Append(PortablePackage.ManifestName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remove = new List<string>();
        if (File.Exists(Paths.Under(root, PortablePackage.ManifestName)))
        {
            foreach (var old in PortablePackage.ReadManifest(root).Files)
            {
                var oldPath = Paths.Under(root, old.Path);

                // Keep locally modified or unknown files, including files added by the user.
                if (!incoming.Contains(old.Path) && File.Exists(oldPath) && Paths.Hash(oldPath).Equals(old.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    remove.Add(old.Path);
                }
            }
        }

        var journal = new TransactionJournal { InstallRoot = Path.GetFullPath(root) };
        Directory.CreateDirectory(Backup);

        // Back up and check every destination before the first replacement.
        foreach (var relative in incoming.Concat(remove).Order(StringComparer.OrdinalIgnoreCase))
        {
            var target = Paths.Under(root, relative);
            if (Directory.Exists(target))
            {
                throw new IOException("A directory occupies an update file path.");
            }

            if (File.Exists(target))
            {
                using var unlocked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var copy = Paths.Under(Backup, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                using var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                unlocked.CopyTo(output);
                output.Flush(true);
            }
        }

        WriteJournal(journal);
        try
        {
            foreach (var relative in incoming.Concat(remove).Order(StringComparer.OrdinalIgnoreCase))
            {
                var target = Paths.Under(root, relative);
                journal.Files.Add(new JournalFile { Path = relative, Existed = File.Exists(Paths.Under(Backup, relative)) });
                WriteJournal(journal);
                if (incoming.Contains(relative))
                {
                    Replace(Paths.Under(extracted, relative), target);
                }
                else
                {
                    File.Delete(target);
                }

                afterReplace?.Invoke(journal.Files.Count);
            }

            journal.Phase = "committed";
            WriteJournal(journal);
            try
            {
                start?.Invoke();
            }
            catch
            {
                journal.Phase = "prepared";
                WriteJournal(journal);
                throw;
            }
        }
        catch
        {
            Recover();
            throw;
        }
    }

    public void Recover()
    {
        var journal = ReadJournal();
        if (journal.Phase is "committed" or "rolledBack")
        {
            return;
        }

        foreach (var file in Enumerable.Reverse(journal.Files))
        {
            var target = Paths.Under(root, file.Path);
            if (file.Existed)
            {
                Replace(Paths.Under(Backup, file.Path), target);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
        }

        journal.Phase = "rolledBack";
        WriteJournal(journal);
    }

    private TransactionJournal ReadJournal()
    {
        Paths.EnsureNoLinks(JournalPath);
        var journal = JsonSerializer.Deserialize(File.ReadAllText(JournalPath), UpdateJson.Default.TransactionJournal) ?? throw new IOException("Invalid update journal.");
        if (!string.Equals(Path.GetFullPath(journal.InstallRoot), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Update journal belongs to another installation.");
        }

        return journal;
    }

    private void WriteJournal(TransactionJournal journal) => UpdateStorage.WriteJson(JournalPath, journal, UpdateJson.Default.TransactionJournal);

    private static void Replace(string source, string target)
    {
        Paths.EnsureNoLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".kit-update-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }

            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
