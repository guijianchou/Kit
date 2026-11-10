namespace Kit.AiHub.Storage;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Kit.AiHub.Models;
using Kit.AiHub.Serialization;

/// <summary>
/// Persists public preferences and DPAPI endpoint profiles as one recoverable transaction.
/// </summary>
public sealed class AiHubSettingsStore
{
    private static readonly ConcurrentDictionary<string, CachedSnapshot> Snapshots = new(StringComparer.OrdinalIgnoreCase);
    private static long _writeVersion;

    private readonly AiHubStorageFiles _files;
    private readonly SecureKeyStore _keyStore;

    private readonly record struct FileStamp(bool Exists, long Length, long LastWriteUtcTicks);

    private readonly record struct DirectoryStamp(
        long WriteVersion,
        FileStamp ServiceSettings,
        FileStamp LegacySettings,
        FileStamp Secrets);

    private sealed record CachedSnapshot(DirectoryStamp Stamp, AiHubConfig Config);

    internal static void BumpWriteVersion() => Interlocked.Increment(ref _writeVersion);

    public string DataDirectory => _files.DataDirectory;
    public string SettingsDirectory => DataDirectory;
    public string SettingsFilePath => _files.GetPath(AiHubStorageFiles.ServiceSettingsFileName);
    public SecureKeyStore KeyStore => _keyStore;

    public AiHubSettingsStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit", "AiHub"))
    {
    }

    public AiHubSettingsStore(string customDirectory)
    {
        _files = new AiHubStorageFiles(customDirectory);
        _keyStore = new SecureKeyStore(_files);
    }

    /// <summary>
    /// Loads current settings and credentials under the same cross-process lock.
    /// Missing settings return defaults; malformed or inaccessible files fail closed.
    /// </summary>
    public AiHubConfig Load()
    {
        if (TryCaptureStamp(out DirectoryStamp stamp) &&
            Snapshots.TryGetValue(DataDirectory, out CachedSnapshot? cached) &&
            cached.Stamp == stamp)
        {
            return CloneConfig(cached.Config);
        }

        return _files.Run(() =>
        {
            AiHubConfig loaded = LoadUnlocked();
            // Keep the snapshot and its file stamps under the same storage lock. Otherwise
            // a completed writer could lend its new stamps to this older configuration.
            if (TryCaptureStamp(out DirectoryStamp postStamp))
            {
                Snapshots[DataDirectory] = new CachedSnapshot(postStamp, CloneConfig(loaded));
            }
            else
            {
                Snapshots.TryRemove(DataDirectory, out _);
            }

            return loaded;
        });
    }

    private bool TryCaptureStamp(out DirectoryStamp stamp)
    {
        stamp = default;
        try
        {
            var journal = new FileInfo(_files.GetPath(".settings-transaction.dat"));
            if (journal.Exists)
            {
                return false;
            }

            long version = Interlocked.Read(ref _writeVersion);
            stamp = new DirectoryStamp(
                version,
                GetFileStamp(SettingsFilePath),
                GetFileStamp(_files.GetPath("settings.json")),
                GetFileStamp(_files.GetPath("secrets.dat")));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static FileStamp GetFileStamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists
            ? new FileStamp(true, info.Length, info.LastWriteTimeUtc.Ticks)
            : new FileStamp(false, 0, 0);
    }

    private static AiHubConfig CloneConfig(AiHubConfig source)
    {
        return new AiHubConfig
        {
            IsEnabled = source.IsEnabled,
            SelectedKernel = source.SelectedKernel,
            MaxConcurrentAnalysis = source.MaxConcurrentAnalysis,
            ScanIntervalHours = source.ScanIntervalHours,
            AuditModeIndex = source.AuditModeIndex,
            RetentionDays = source.RetentionDays,
            Targets = new ObservableCollection<AiTargetSettings>(source.Targets.Select(t => t.Clone())),
        };
    }

    /// <summary>
    /// Saves the complete configuration, committing public preferences and encrypted endpoints together.
    /// </summary>
    public void Save(AiHubConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _files.Run(() => SaveUnlocked(config));
    }

    /// <summary>
    /// Updates selected fields of the latest configuration without overwriting unrelated changes.
    /// </summary>
    public AiHubConfig Update(Action<AiHubConfig> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return _files.Run(() =>
        {
            var config = LoadUnlocked();
            update(config);
            SaveUnlocked(config);
            return config;
        });
    }

    /// <summary>Captures the complete encrypted service snapshot without changing the source.</summary>
    public AiHubSettingsBackup? ExportBackup() => ExportBackup(DataDirectory);

    /// <summary>Reads under the storage lock; legacy profiles are encrypted only in memory.</summary>
    public static AiHubSettingsBackup? ExportBackup(string directory)
    {
        var files = new AiHubStorageFiles(directory, readOnly: true);
        return files.Run(() =>
        {
            var snapshot = ReadBackup(files.DataDirectory);
            if (snapshot is null || UsesProtectedTargets(snapshot.SettingsJson))
            {
                return snapshot;
            }

            var (config, secrets) = DecodeBackup(snapshot);
            return CreateProtectedSnapshot(config, secrets);
        });
    }

    /// <summary>Reads and validates a backup without migrating or changing the archive.</summary>
    public static AiHubSettingsBackup? ReadBackup(string directory)
    {
        try
        {
            string root = AiHubStorageFiles.NormalizeDirectory(directory);
            byte[]? settings = AiHubStorageFiles.ReadOptionalFile(Path.Combine(root, AiHubStorageFiles.ServiceSettingsFileName), AiHubStorageFiles.MaxDataFileBytes);
            if (settings is null)
            {
                byte[]? legacy = AiHubStorageFiles.ReadOptionalFile(Path.Combine(root, "settings.json"), AiHubStorageFiles.MaxDataFileBytes);
                if (legacy is null)
                {
                    RejectOrphanedProfiles(root);
                    return null;
                }

                using var document = JsonDocument.Parse(legacy);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    (!document.RootElement.TryGetProperty("targets", out _) && !document.RootElement.TryGetProperty("targetStorageVersion", out _)))
                {
                    RejectOrphanedProfiles(root);
                    return null;
                }

                settings = legacy;
            }

            byte[]? secrets = AiHubStorageFiles.ReadOptionalFile(Path.Combine(root, "secrets.dat"), AiHubStorageFiles.MaxDataFileBytes);
            var snapshot = new AiHubSettingsBackup(settings, secrets);
            DecodeBackup(snapshot);
            return snapshot;
        }
        catch (Exception)
        {
            // A bad or foreign-user DPAPI archive must not leak settings/parser details.
            throw new InvalidDataException("The AI Hub backup is incomplete or cannot be read by this Windows user.");
        }
    }

    private static void RejectOrphanedProfiles(string root)
    {
        byte[]? ciphertext = AiHubStorageFiles.ReadOptionalFile(Path.Combine(root, "secrets.dat"), AiHubStorageFiles.MaxDataFileBytes);
        if (ciphertext is not null)
        {
            var secrets = SecureKeyStore.Decrypt(ciphertext);
            if (secrets.ContainsKey(SecureKeyStore.TargetProfilesKey) || secrets.ContainsKey(SecureKeyStore.TargetProfilesIdKey))
            {
                throw new InvalidDataException("The AI Hub service preferences are missing.");
            }
        }
    }

    /// <summary>Compares decrypted content in memory so randomized DPAPI bytes do not create duplicate backups.</summary>
    public static bool BackupContentEquals(AiHubSettingsBackup? left, AiHubSettingsBackup? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        var (leftConfig, leftSecrets) = DecodeBackup(left);
        var (rightConfig, rightSecrets) = DecodeBackup(right);
        return JsonSerializer.Serialize(leftConfig, AiHubJsonContext.Default.AiHubConfig) == JsonSerializer.Serialize(rightConfig, AiHubJsonContext.Default.AiHubConfig) &&
            leftSecrets.Count == rightSecrets.Count && leftSecrets.All(pair => pair.Key == SecureKeyStore.TargetProfilesIdKey ||
                (rightSecrets.TryGetValue(pair.Key, out string? value) && pair.Value == value));
    }

    /// <summary>Restores a validated service snapshot atomically; old archives retain the current encrypted keys.</summary>
    public void ImportBackup(AiHubSettingsBackup snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var ownedSnapshot = new AiHubSettingsBackup(snapshot.SettingsJson, snapshot.EncryptedSecrets);
        _files.Run(() =>
        {
            var (config, secrets) = DecodeBackup(ownedSnapshot);
            if (ownedSnapshot.EncryptedSecrets is null)
            {
                secrets = _keyStore.ReadUnlocked();
                foreach (var target in config.Targets)
                {
                    target.ApiKey = secrets.TryGetValue($"target_key_{target.Name}", out string? key) ? key : string.Empty;
                }
            }

            ValidateTargets(config);
            PersistUnlocked(config, secrets);
        });
    }

    private static (AiHubConfig Config, Dictionary<string, string> Secrets) DecodeBackup(AiHubSettingsBackup snapshot)
    {
        if (snapshot.SettingsJson.Length > AiHubStorageFiles.MaxDataFileBytes)
        {
            throw new InvalidDataException("The AI Hub backup exceeds its size limit.");
        }

        bool protectedTargets = UsesProtectedTargets(snapshot.SettingsJson);
        var secrets = snapshot.EncryptedSecrets is null ? new Dictionary<string, string>(StringComparer.Ordinal) : SecureKeyStore.Decrypt(snapshot.EncryptedSecrets);
        if (protectedTargets)
        {
            EnsureProtectedPair(snapshot.SettingsJson, secrets);
        }

        var config = protectedTargets
            ? JsonSerializer.Deserialize(snapshot.SettingsJson, AiHubJsonContext.Default.AiServiceSettingsSnapshot)?.ToConfig()
            : JsonSerializer.Deserialize(snapshot.SettingsJson, AiHubJsonContext.Default.AiHubConfig);
        if (config is null)
        {
            throw new InvalidDataException("The AI Hub backup is invalid.");
        }

        List<AiTargetSettings>? profiles = ReadEncryptedProfiles(secrets, required: protectedTargets);
        if (protectedTargets)
        {
            config.Targets = new ObservableCollection<AiTargetSettings>(profiles!);
        }

        ValidateTargets(config);
        foreach (var target in config.Targets)
        {
            target.ApiKey = secrets.TryGetValue($"target_key_{target.Name}", out string? key) ? key : string.Empty;
        }

        ValidateTargets(config);
        return (config, secrets);
    }

    private AiHubConfig LoadUnlocked()
    {
        byte[]? bytes = AiHubStorageFiles.ReadOptionalFile(SettingsFilePath, AiHubStorageFiles.MaxDataFileBytes);
        JsonObject? legacy = ReadLegacySettings(requireValidJson: bytes is null);
        bool migrateLegacyFile = bytes is null && legacy is not null &&
            (legacy.ContainsKey("targets") || legacy.ContainsKey("targetStorageVersion"));
        if (migrateLegacyFile)
        {
            bytes = Encoding.UTF8.GetBytes(legacy!.ToJsonString());
        }

        bool protectedTargets = bytes is not null && UsesProtectedTargets(bytes);
        var config = bytes is null ? AiHubConfig.CreateDefault() : protectedTargets
            ? JsonSerializer.Deserialize(bytes, AiHubJsonContext.Default.AiServiceSettingsSnapshot)?.ToConfig()
                ?? throw new InvalidDataException("The AI Hub settings file is invalid.")
            : JsonSerializer.Deserialize(bytes, AiHubJsonContext.Default.AiHubConfig)
                ?? throw new InvalidDataException("The AI Hub settings file is invalid.");

        var secrets = _keyStore.ReadUnlocked();
        if (protectedTargets)
        {
            EnsureProtectedPair(bytes!, secrets);
        }

        List<AiTargetSettings>? profiles = ReadEncryptedProfiles(secrets, required: protectedTargets);
        if (bytes is null && profiles is not null)
        {
            throw new InvalidDataException("The AI Hub service preferences are missing.");
        }

        if (protectedTargets)
        {
            config.Targets = new ObservableCollection<AiTargetSettings>(profiles!);
        }

        ValidateTargets(config);
        foreach (var target in config.Targets)
        {
            target.ApiKey = secrets.TryGetValue($"target_key_{target.Name}", out string? key) ? key : string.Empty;
        }

        ValidateTargets(config);
        if ((bytes is not null && !protectedTargets) || migrateLegacyFile || legacy?.ContainsKey("targets") == true)
        {
            // A successful read upgrades old endpoint metadata and removes its legacy copy.
            // All affected files are protected by the same recoverable storage transaction.
            SaveUnlocked(config);
        }

        return config;
    }

    private void SaveUnlocked(AiHubConfig config)
    {
        ValidateTargets(config);
        var secrets = _keyStore.ReadUnlocked();
        byte[]? existingSettings = AiHubStorageFiles.ReadOptionalFile(SettingsFilePath, AiHubStorageFiles.MaxDataFileBytes);
        // Missing or malformed protected profiles are never silently replaced by defaults
        // or an unrelated preference edit. Legacy plaintext settings remain importable.
        bool protectedTargets = existingSettings is not null && UsesProtectedTargets(existingSettings);
        if (protectedTargets)
        {
            EnsureProtectedPair(existingSettings!, secrets);
        }

        var profiles = ReadEncryptedProfiles(secrets, required: protectedTargets);
        if (existingSettings is null && profiles is not null)
        {
            JsonObject? legacy = ReadLegacySettings(requireValidJson: true);
            if (legacy is null || (!legacy.ContainsKey("targets") && !legacy.ContainsKey("targetStorageVersion")))
            {
                throw new InvalidDataException("The AI Hub service preferences are missing.");
            }

            byte[] legacyBytes = Encoding.UTF8.GetBytes(legacy.ToJsonString());
            if (UsesProtectedTargets(legacyBytes))
            {
                EnsureProtectedPair(legacyBytes, secrets);
            }
        }

        PersistUnlocked(config, secrets);
    }

    private void PersistUnlocked(AiHubConfig config, Dictionary<string, string> secrets)
    {
        var snapshot = CreateProtectedSnapshot(config, secrets);
        JsonObject? legacy = ReadLegacySettings(requireValidJson: false);
        // The storage layer re-reads the legacy file after denying other writers,
        // so unrelated plugin properties cannot be replaced with this earlier read.
        string? sanitizeLegacyStorageId = legacy?.ContainsKey("targets") == true ? secrets[SecureKeyStore.TargetProfilesIdKey] : null;
        _files.CommitSettingsAndSecrets(snapshot.SettingsJson, snapshot.EncryptedSecrets!, sanitizeLegacyStorageId);
        if (TryCaptureStamp(out DirectoryStamp postStamp))
        {
            Snapshots[DataDirectory] = new CachedSnapshot(postStamp, CloneConfig(config));
        }
        else
        {
            Snapshots.TryRemove(DataDirectory, out _);
        }
    }

    private static AiHubSettingsBackup CreateProtectedSnapshot(AiHubConfig config, Dictionary<string, string> secrets)
    {
        foreach (string key in secrets.Keys.Where(key => key.StartsWith("target_key_", StringComparison.Ordinal)).ToArray())
        {
            secrets.Remove(key);
        }

        var profiles = new List<AiTargetSettings>();
        foreach (var target in config.Targets)
        {
            var targetClone = target.Clone();
            if (!string.IsNullOrEmpty(targetClone.ApiKey))
            {
                secrets.Add($"target_key_{targetClone.Name}", targetClone.ApiKey);
            }

            targetClone.ApiKey = string.Empty;
            profiles.Add(targetClone);
        }

        secrets[SecureKeyStore.TargetProfilesKey] = JsonSerializer.Serialize(profiles, AiHubJsonContext.Default.ListAiTargetSettings);
        string storageId = Guid.NewGuid().ToString("N");
        secrets[SecureKeyStore.TargetProfilesIdKey] = storageId;
        byte[] settings = JsonSerializer.SerializeToUtf8Bytes(AiServiceSettingsSnapshot.FromConfig(config, storageId), AiHubJsonContext.Default.AiServiceSettingsSnapshot);
        return new AiHubSettingsBackup(settings, SecureKeyStore.Encrypt(secrets));
    }

    private static bool UsesProtectedTargets(byte[] settings)
    {
        using var document = JsonDocument.Parse(settings);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The AI Hub settings file is invalid.");
        }

        if (!document.RootElement.TryGetProperty("targetStorageVersion", out var version))
        {
            return false;
        }

        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1 ||
            document.RootElement.TryGetProperty("targets", out _))
        {
            throw new InvalidDataException("The AI Hub endpoint storage version is invalid.");
        }

        return true;
    }

    private static List<AiTargetSettings>? ReadEncryptedProfiles(Dictionary<string, string> secrets, bool required)
    {
        if (!secrets.TryGetValue(SecureKeyStore.TargetProfilesKey, out string? json))
        {
            if (required)
            {
                throw new InvalidDataException("The protected AI Hub endpoint profiles are missing.");
            }

            return null;
        }

        var profiles = JsonSerializer.Deserialize(json, AiHubJsonContext.Default.ListAiTargetSettings)
            ?? throw new InvalidDataException("The protected AI Hub endpoint profiles are invalid.");
        var validation = AiHubConfig.CreateDefault();
        validation.Targets = new ObservableCollection<AiTargetSettings>(profiles);
        ValidateTargets(validation);
        return profiles;
    }

    private static void EnsureProtectedPair(byte[] settings, Dictionary<string, string> secrets)
    {
        using var document = JsonDocument.Parse(settings);
        if (!document.RootElement.TryGetProperty("targetStorageId", out var id) || id.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(id.GetString(), "N", out _) ||
            !secrets.TryGetValue(SecureKeyStore.TargetProfilesIdKey, out string? protectedId) || id.GetString() != protectedId)
        {
            throw new InvalidDataException("The protected AI Hub endpoint snapshot is incomplete or mismatched.");
        }
    }

    private JsonObject? ReadLegacySettings(bool requireValidJson)
    {
        byte[]? bytes = AiHubStorageFiles.ReadOptionalFile(_files.GetPath("settings.json"), AiHubStorageFiles.MaxDataFileBytes);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(bytes) as JsonObject;
        }
        catch (JsonException) when (!requireValidJson)
        {
            // A malformed plugin-only settings file must not prevent reading the
            // separate service configuration, and cannot safely be rewritten here.
            return null;
        }
    }

    private static void ValidateTargets(AiHubConfig config)
    {
        if (config.SelectedKernel is not (AiKernelCatalog.Codex or AiKernelCatalog.Pi) ||
            config.MaxConcurrentAnalysis is < 1 or > 4 ||
            config.Targets is null || config.Targets.Count != 2 ||
            config.Targets[0]?.Name != "Main" || config.Targets[1]?.Name != "Fallback")
        {
            throw new InvalidDataException("The AI Hub configuration must contain valid settings and the Main/Fallback target slots.");
        }

        foreach (var target in config.Targets)
        {
            if (target.BaseUrl is null || target.BaseUrl.Length > 4096 ||
                target.ApiKey is null || target.ApiKey.Length > 8192 ||
                target.Model is null || target.Model.Length > 256 ||
                !AiTargetSettings.IsValidNativeIdentifier(target.Mode) ||
                !AiTargetSettings.IsValidNativeIdentifier(target.Effort) ||
                target.ContextWindowTokens < AiTargetSettings.MinimumContextWindowTokens)
            {
                throw new InvalidDataException("The AI Hub target fields are invalid or exceed their size limits.");
            }
        }
    }
}
