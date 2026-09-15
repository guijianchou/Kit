namespace Kit.AiHub.Storage;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Kit.AiHub.Serialization;

/// <summary>
/// Stores API keys encrypted for the current Windows user in secrets.dat.
/// Every operation reads the current disk snapshot while holding the shared storage mutex.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecureKeyStore
{
    private readonly AiHubStorageFiles _files;

    public SecureKeyStore(string dataDirectory)
        : this(new AiHubStorageFiles(dataDirectory))
    {
    }

    internal SecureKeyStore(AiHubStorageFiles files)
    {
        _files = files;
    }

    /// <summary>
    /// Gets a decrypted secret by key, or returns empty string if not found.
    /// </summary>
    public string GetSecret(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        return _files.Run(() =>
        {
            var secrets = ReadUnlocked();
            return secrets.TryGetValue(key, out string? secret) ? secret : string.Empty;
        });
    }

    /// <summary>
    /// Updates a secret in the latest snapshot and atomically persists the encrypted result.
    /// </summary>
    public void SetSecret(string key, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _files.Run(() =>
        {
            var secrets = ReadUnlocked();
            if (string.IsNullOrEmpty(secret))
            {
                secrets.Remove(key);
            }
            else
            {
                secrets[key] = secret;
            }

            _files.WriteAtomic("secrets.dat", Encrypt(secrets));
        });
    }

    /// <summary>
    /// Reloads and validates encrypted secrets without retaining a stale in-memory snapshot.
    /// </summary>
    public void Load() => _files.Run(() => { ReadUnlocked(); });

    /// <summary>
    /// Rewrites the latest encrypted snapshot. SetSecret already persists each change.
    /// </summary>
    public void Save() => _files.Run(() => _files.WriteAtomic("secrets.dat", Encrypt(ReadUnlocked())));

    internal Dictionary<string, string> ReadUnlocked()
    {
        byte[]? ciphertext = AiHubStorageFiles.ReadOptionalFile(_files.GetPath("secrets.dat"), AiHubStorageFiles.MaxDataFileBytes);
        if (ciphertext is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        byte[] plaintext = ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
        try
        {
            var secrets = JsonSerializer.Deserialize(plaintext, AiHubJsonContext.Default.DictionaryStringString)
                ?? throw new InvalidDataException("The AI Hub credential file is invalid.");
            foreach (var pair in secrets)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                {
                    throw new InvalidDataException("The AI Hub credential file is invalid.");
                }
            }

            return secrets;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static byte[] Encrypt(Dictionary<string, string> secrets)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(secrets, AiHubJsonContext.Default.DictionaryStringString);
        try
        {
            if (plaintext.Length > AiHubStorageFiles.MaxDataFileBytes)
            {
                throw new InvalidDataException("The AI Hub credentials exceed their size limit.");
            }

            return ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
