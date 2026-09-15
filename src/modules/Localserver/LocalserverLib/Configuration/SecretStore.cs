using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalServerHub.Core.Configuration;

/// <summary>
/// DPAPI-encrypted storage for secret-type parameter values (plan.md §585).
/// Values are encrypted per-user via Windows Data Protection API and persisted
/// to secrets.dat in the data directory. Services.json holds only ${secret.NAME}
/// references, never plaintext secrets.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecretStore
{
    private readonly string _storePath;
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public SecretStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        _storePath = Path.Combine(dataDirectory, "secrets.dat");
    }

    /// <summary>
    /// Loads encrypted secrets from disk. Missing file is not an error (first run).
    /// </summary>
    public void Load()
    {
        if (!File.Exists(_storePath))
        {
            return;
        }

        try
        {
            byte[] ciphertext = File.ReadAllBytes(_storePath);
            byte[] plaintext = ProtectedData.Unprotect(
                ciphertext,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);

            string json = Encoding.UTF8.GetString(plaintext);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict is not null)
            {
                _secrets.Clear();
                foreach ((string key, string value) in dict)
                {
                    _secrets[key] = value;
                }
            }
        }
        catch (CryptographicException)
        {
            // Data encrypted by a different user or machine — cannot decrypt.
            // Leave the store empty; secrets will be unresolved until re-entered.
        }
        catch (JsonException)
        {
            // Decrypted to something that is not the expected map. Same handling:
            // an unreadable store must not take the app down on startup.
        }
        catch (IOException)
        {
            // Locked or vanished between the Exists check and the read.
        }
    }

    /// <summary>
    /// Persists all secrets to disk, DPAPI-encrypted for the current user.
    /// </summary>
    public void Save()
    {
        Dictionary<string, string> snapshot = new(_secrets, StringComparer.Ordinal);
        string json = JsonSerializer.Serialize(snapshot);
        byte[] plaintext = Encoding.UTF8.GetBytes(json);
        byte[] ciphertext = ProtectedData.Protect(
            plaintext,
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);

        // Atomic, for the same reason the catalog is: DPAPI ciphertext cannot be
        // partially decrypted, so a truncated write loses every secret at once -
        // and by then the catalogs already reference them.
        string tempPath = $"{_storePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tempPath, ciphertext);
            File.Move(tempPath, _storePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Retrieves a decrypted secret by name. Returns null if not found.
    /// </summary>
    public string? Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _secrets.TryGetValue(name, out string? value);
        return value;
    }

    /// <summary>
    /// Stores a secret. The value is held in memory and encrypted only on Save().
    /// </summary>
    public void Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _secrets[name] = value;
    }

    /// <summary>
    /// Removes a secret from the store. Call Save() to persist the deletion.
    /// </summary>
    public void Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _secrets.TryRemove(name, out _);
    }

    /// <summary>
    /// Returns all secret names currently in the store (for migration/audit).
    /// </summary>
    public IReadOnlyCollection<string> GetAllNames() => _secrets.Keys.ToArray();
}
