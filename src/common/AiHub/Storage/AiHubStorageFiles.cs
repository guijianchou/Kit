namespace Kit.AiHub.Storage;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Kit.AiHub.Serialization;

/// <summary>
/// Coordinates the small AI Hub data files across instances and processes.
/// </summary>
internal sealed class AiHubStorageFiles
{
    internal const int MaxDataFileBytes = 1024 * 1024;
    private const int MaxJournalBytes = 4 * MaxDataFileBytes;
    private const string JournalFileName = ".settings-transaction.dat";
    private readonly string _mutexName;

    internal string DataDirectory { get; }

    internal AiHubStorageFiles(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = NormalizeDirectory(dataDirectory);
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
                Directory.CreateDirectory(DataDirectory);
                EnsureNoReparsePoints(DataDirectory);
                RecoverTransaction();
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
            try
            {
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("AI Hub paths must not contain reparse points.");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }

            component = Path.GetDirectoryName(component);
        }
    }

    internal static byte[]? ReadOptionalFile(string path, int maximumBytes)
    {
        EnsureNoReparsePoints(path);
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

    internal void WriteAtomic(string fileName, byte[] content, int maximumBytes = MaxDataFileBytes)
    {
        if (content.Length > maximumBytes || Path.GetFileName(fileName) != fileName)
        {
            throw new InvalidDataException("The AI Hub data file is invalid.");
        }

        string destination = GetPath(fileName);
        EnsureNoReparsePoints(destination);
        string temporaryPath = GetPath($".{fileName}.{Guid.NewGuid():N}.tmp");
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

    internal void CommitSettingsAndSecrets(byte[] settings, byte[] secrets)
    {
        if (settings.Length > MaxDataFileBytes || secrets.Length > MaxDataFileBytes)
        {
            throw new InvalidDataException("The AI Hub settings exceed their size limit.");
        }

        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = "1",
        };
        AddSnapshot(snapshot, "settings", "settings.json");
        AddSnapshot(snapshot, "secrets", "secrets.dat");

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
            WriteAtomic("settings.json", settings);
            DeleteJournal();
        }
        catch (Exception)
        {
            // Retain a failed recovery journal so the next reader also fails closed.
            RecoverTransaction();
            throw new IOException("The AI Hub settings could not be committed.");
        }
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

    private void RecoverTransaction()
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
            if (!snapshot.TryGetValue("version", out string? version) || version != "1" || snapshot.Count > 3)
            {
                throw new InvalidDataException("The AI Hub recovery journal is invalid.");
            }

            foreach (string key in snapshot.Keys)
            {
                if (key is not ("version" or "settings" or "secrets"))
                {
                    throw new InvalidDataException("The AI Hub recovery journal is invalid.");
                }
            }

            byte[]? settings = ReadSnapshot(snapshot, "settings");
            byte[]? secrets = null;
            try
            {
                secrets = ReadSnapshot(snapshot, "secrets");
                RestoreFile("settings.json", settings);
                RestoreFile("secrets.dat", secrets);
                DeleteJournal();
            }
            finally
            {
                if (settings is not null)
                {
                    CryptographicOperations.ZeroMemory(settings);
                }

                if (secrets is not null)
                {
                    CryptographicOperations.ZeroMemory(secrets);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
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
    }
}
