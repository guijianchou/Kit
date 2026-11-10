namespace Kit.AiHub.Storage;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Kit.AiHub.Serialization;

/// <summary>
/// Coordinates the small AI Hub data files across instances and processes.
/// </summary>
internal sealed class AiHubStorageFiles
{
    internal const int MaxDataFileBytes = 1024 * 1024;
    internal const string ServiceSettingsFileName = "service-settings.json";
    private const int MaxJournalBytes = 6 * MaxDataFileBytes;
    private const string JournalFileName = ".settings-transaction.dat";
    private readonly string _mutexName;
    private readonly bool _readOnly;

    internal string DataDirectory { get; }

    internal AiHubStorageFiles(string dataDirectory, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = NormalizeDirectory(dataDirectory);
        _readOnly = readOnly;
        byte[] identity = Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant());
        _mutexName = @"Global\Kit.AiHub." + Convert.ToHexString(SHA256.HashData(identity));
        Run(static () => { });
    }

    internal static string NormalizeDirectory(string directory)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception)
        {
            throw new ArgumentException("The AI Hub directory is invalid.", nameof(directory));
        }
    }

    internal string GetPath(string fileName) => Path.Combine(DataDirectory, fileName);

    internal void Run(Action action, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            action();
            return true;
        }, cancellationToken);

    internal T Run<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        try
        {
            using var mutex = new Mutex(initiallyOwned: false, _mutexName);
            bool acquired = false;
            try
            {
                long deadline = Environment.TickCount64 + 10_000;
                while (!acquired)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        acquired = mutex.WaitOne(100);
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }

                    if (!acquired && Environment.TickCount64 >= deadline)
                    {
                        throw new IOException("Timed out waiting for AI Hub storage.");
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                EnsureNoReparsePoints(DataDirectory);
                if (_readOnly)
                {
                    EnsureNoReparsePoints(GetPath(JournalFileName));
                    if (File.Exists(GetPath(JournalFileName)))
                    {
                        throw new IOException("An unfinished AI Hub transaction must be recovered before a backup can be read.");
                    }
                }
                else
                {
                    Directory.CreateDirectory(DataDirectory);
                    EnsureNoReparsePoints(DataDirectory);
                    RecoverTransaction();
                }

                return action();
            }
            finally
            {
                if (acquired)
                {
                    mutex.ReleaseMutex();
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("The AI Hub storage operation was cancelled.", cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or DecoderFallbackException or EncoderFallbackException)
        {
            throw new InvalidDataException("The AI Hub stored data is invalid.");
        }
        catch (Exception)
        {
            // Paths, JSON parser errors, and cryptographic errors may contain private data.
            throw new IOException("The AI Hub storage operation could not be completed.");
        }
    }

    internal static void EnsureNoReparsePoints(string fullPath)
    {
        string? component = Path.GetFullPath(fullPath);
        while (!string.IsNullOrEmpty(component))
        {
            // Most probes target files that do not exist yet (the transaction journal, first-run
            // data). FileInfo reports a missing entry as -1 instead of throwing, and like
            // File.GetAttributes it reads the entry itself, so links are still seen, not followed.
            var attributes = new FileInfo(component).Attributes;
            if ((int)attributes != -1 && (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("AI Hub paths must not contain reparse points.");
            }

            component = Path.GetDirectoryName(component);
        }
    }

    internal static byte[]? ReadOptionalFile(string path, int maximumBytes)
    {
        EnsureNoReparsePoints(path);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximumBytes)
            {
                throw new InvalidDataException("An AI Hub data file exceeds its size limit.");
            }

            byte[] content = new byte[checked((int)stream.Length)];
            stream.ReadExactly(content);
            return content;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal void WriteAtomic(string relativePath, byte[] content, int maximumBytes = MaxDataFileBytes)
    {
        if (_readOnly)
        {
            throw new InvalidOperationException("A read-only AI Hub snapshot cannot write storage.");
        }

        if (content.Length > maximumBytes || string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException("The AI Hub data file is invalid.");
        }

        string destination = Path.GetFullPath(GetPath(relativePath));
        string rootPrefix = Path.EndsInDirectorySeparator(DataDirectory) ? DataDirectory : DataDirectory + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The AI Hub data file path is invalid.");
        }

        string? parent = Path.GetDirectoryName(destination);
        if (parent is not null)
        {
            EnsureNoReparsePoints(parent);
            Directory.CreateDirectory(parent);
            EnsureNoReparsePoints(parent);
        }

        string temporaryPath = Path.Combine(parent ?? DataDirectory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        bool temporaryCreated = false;
        try
        {
            using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                temporaryCreated = true;
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            EnsureNoReparsePoints(destination);
            File.Move(temporaryPath, destination, overwrite: true);
            temporaryCreated = false;
            AiHubSettingsStore.BumpWriteVersion();
        }
        finally
        {
            if (temporaryCreated)
            {
                EnsureNoReparsePoints(temporaryPath);
                File.Delete(temporaryPath);
            }
        }
    }

    internal void CommitSettingsAndSecrets(byte[] settings, byte[] secrets, string? sanitizeLegacyStorageId = null)
    {
        if (settings.Length > MaxDataFileBytes || secrets.Length > MaxDataFileBytes)
        {
            throw new InvalidDataException("The AI Hub settings exceed their size limit.");
        }

        using FileStream? legacyStream = sanitizeLegacyStorageId is null ? null : OpenLegacyForUpdate();
        byte[]? originalLegacy = legacyStream is null ? null : ReadPinnedFile(legacyStream);
        byte[]? sanitizedLegacySettings = null;
        try
        {
            if (originalLegacy is not null && JsonNode.Parse(originalLegacy) is JsonObject legacy && legacy.ContainsKey("targets"))
            {
                legacy.Remove("targets");
                legacy["targetStorageVersion"] = 1;
                legacy["targetStorageId"] = sanitizeLegacyStorageId;
                sanitizedLegacySettings = Encoding.UTF8.GetBytes(legacy.ToJsonString());
            }

            CommitSettingsAndSecretsCore(settings, secrets, legacyStream, originalLegacy, sanitizedLegacySettings);
        }
        finally
        {
            if (originalLegacy is not null)
            {
                CryptographicOperations.ZeroMemory(originalLegacy);
            }

            if (sanitizedLegacySettings is not null)
            {
                CryptographicOperations.ZeroMemory(sanitizedLegacySettings);
            }
        }
    }

    private void CommitSettingsAndSecretsCore(byte[] settings, byte[] secrets, FileStream? legacyStream, byte[]? originalLegacy, byte[]? sanitizedLegacySettings)
    {
        if (sanitizedLegacySettings?.Length > MaxDataFileBytes)
        {
            throw new InvalidDataException("The AI Hub settings exceed their size limit.");
        }

        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = sanitizedLegacySettings is null ? "2" : "3",
        };
        AddSnapshot(snapshot, "settings", ServiceSettingsFileName);
        AddSnapshot(snapshot, "secrets", "secrets.dat");
        if (sanitizedLegacySettings is not null)
        {
            snapshot.Add("legacy", Convert.ToBase64String(originalLegacy!));
        }

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(snapshot, AiHubJsonContext.Default.DictionaryStringString);
        byte[] journal;
        try
        {
            // The rollback copy can contain legacy settings, so protect the entire journal.
            journal = ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        WriteAtomic(JournalFileName, journal, MaxJournalBytes);
        try
        {
            WriteAtomic("secrets.dat", secrets);
            WriteAtomic(ServiceSettingsFileName, settings);
            if (sanitizedLegacySettings is not null)
            {
                WritePinnedFile(legacyStream!, sanitizedLegacySettings);
            }

            DeleteJournal();
        }
        catch (Exception)
        {
            // Retain a failed recovery journal so the next reader also fails closed.
            RecoverTransaction(legacyStream);
            throw new IOException("The AI Hub settings could not be committed.");
        }
    }

    private FileStream? OpenLegacyForUpdate()
    {
        string path = GetPath("settings.json");
        EnsureNoReparsePoints(path);
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static byte[] ReadPinnedFile(FileStream stream)
    {
        if (stream.Length > MaxDataFileBytes)
        {
            throw new InvalidDataException("An AI Hub data file exceeds its size limit.");
        }

        stream.Position = 0;
        byte[] content = new byte[checked((int)stream.Length)];
        stream.ReadExactly(content);
        return content;
    }

    private static void WritePinnedFile(FileStream stream, byte[] content)
    {
        stream.Position = 0;
        stream.Write(content);
        stream.SetLength(content.Length);
        stream.Flush(flushToDisk: true);
        AiHubSettingsStore.BumpWriteVersion();
    }

    private void AddSnapshot(Dictionary<string, string> snapshot, string key, string fileName)
    {
        byte[]? bytes = ReadOptionalFile(GetPath(fileName), MaxDataFileBytes);
        if (bytes is not null)
        {
            try
            {
                snapshot.Add(key, Convert.ToBase64String(bytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private void RecoverTransaction(FileStream? lockedLegacy = null)
    {
        byte[]? journal = ReadOptionalFile(GetPath(JournalFileName), MaxJournalBytes);
        if (journal is null)
        {
            return;
        }

        byte[] plaintext = ProtectedData.Unprotect(journal, optionalEntropy: null, DataProtectionScope.CurrentUser);
        try
        {
            var snapshot = JsonSerializer.Deserialize(plaintext, AiHubJsonContext.Default.DictionaryStringString)
                ?? throw new InvalidDataException("The AI Hub recovery journal is invalid.");
            if (!snapshot.TryGetValue("version", out string? version) || version is not ("1" or "2" or "3") || snapshot.Count > (version == "3" ? 4 : 3))
            {
                throw new InvalidDataException("The AI Hub recovery journal is invalid.");
            }

            foreach (string key in snapshot.Keys)
            {
                if (key is not ("version" or "settings" or "secrets") && !(version == "3" && key == "legacy"))
                {
                    throw new InvalidDataException("The AI Hub recovery journal is invalid.");
                }
            }

            byte[]? settings = ReadSnapshot(snapshot, "settings");
            byte[]? secrets = null;
            byte[]? legacy = null;
            FileStream? recoveryLegacy = null;
            try
            {
                secrets = ReadSnapshot(snapshot, "secrets");
                legacy = version == "3" ? ReadSnapshot(snapshot, "legacy") : null;
                if (legacy is not null && lockedLegacy is null)
                {
                    string legacyPath = GetPath("settings.json");
                    EnsureNoReparsePoints(legacyPath);
                    recoveryLegacy = new FileStream(legacyPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
                }

                RestoreFile(version == "1" ? "settings.json" : ServiceSettingsFileName, settings);
                RestoreFile("secrets.dat", secrets);
                if (version == "3")
                {
                    if (legacy is null)
                    {
                        RestoreFile("settings.json", null);
                    }
                    else
                    {
                        RestorePinnedLegacy(lockedLegacy ?? recoveryLegacy!, legacy);
                    }
                }

                DeleteJournal();
            }
            finally
            {
                recoveryLegacy?.Dispose();
                if (settings is not null)
                {
                    CryptographicOperations.ZeroMemory(settings);
                }

                if (secrets is not null)
                {
                    CryptographicOperations.ZeroMemory(secrets);
                }

                if (legacy is not null)
                {
                    CryptographicOperations.ZeroMemory(legacy);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void RestorePinnedLegacy(FileStream stream, byte[] original)
    {
        byte[] current = ReadPinnedFile(stream);
        byte[]? merged = null;
        try
        {
            if (current.AsSpan().SequenceEqual(original))
            {
                return;
            }

            if (JsonNode.Parse(original) is JsonObject before && before.ContainsKey("targets"))
            {
                try
                {
                    if (JsonNode.Parse(current) is JsonObject latest)
                    {
                        // A plugin may have saved again after a process crash. Roll back
                        // only the three fields migration owns, retaining its newer preferences.
                        foreach (string field in new[] { "targets", "targetStorageVersion", "targetStorageId" })
                        {
                            latest.Remove(field);
                            if (before.TryGetPropertyValue(field, out var value))
                            {
                                latest[field] = value?.DeepClone();
                            }
                        }

                        merged = Encoding.UTF8.GetBytes(latest.ToJsonString());
                    }
                }
                catch (JsonException)
                {
                    // An interrupted in-place write may leave torn JSON. The protected
                    // original is then the only complete recoverable legacy snapshot.
                }
            }

            WritePinnedFile(stream, merged ?? original);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(current);
            if (merged is not null)
            {
                CryptographicOperations.ZeroMemory(merged);
            }
        }
    }

    private static byte[]? ReadSnapshot(Dictionary<string, string> snapshot, string key)
    {
        if (!snapshot.TryGetValue(key, out string? encoded))
        {
            return null;
        }

        byte[] bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > MaxDataFileBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException("The AI Hub recovery snapshot exceeds its size limit.");
        }

        return bytes;
    }

    private void RestoreFile(string fileName, byte[]? original)
    {
        string path = GetPath(fileName);
        byte[]? current = ReadOptionalFile(path, MaxDataFileBytes);
        try
        {
            if (original is null)
            {
                if (current is not null)
                {
                    EnsureNoReparsePoints(path);
                    File.Delete(path);
                }
            }
            else if (current is null || !current.AsSpan().SequenceEqual(original))
            {
                WriteAtomic(fileName, original);
            }
        }
        finally
        {
            if (current is not null)
            {
                CryptographicOperations.ZeroMemory(current);
            }
        }
    }

    private void DeleteJournal()
    {
        string path = GetPath(JournalFileName);
        EnsureNoReparsePoints(path);
        File.Delete(path);
        AiHubSettingsStore.BumpWriteVersion();
    }
}
