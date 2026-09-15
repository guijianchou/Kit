namespace Kit.AiHub.Storage;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Kit.AiHub.Models;
using Kit.AiHub.Serialization;

/// <summary>
/// Persists AI Hub settings and DPAPI credentials as one recoverable transaction.
/// </summary>
public sealed class AiHubSettingsStore
{
    private readonly AiHubStorageFiles _files;
    private readonly SecureKeyStore _keyStore;

    public string DataDirectory => _files.DataDirectory;
    public string SettingsDirectory => DataDirectory;
    public string SettingsFilePath => _files.GetPath("settings.json");
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
    public AiHubConfig Load() => _files.Run(LoadUnlocked);

    /// <summary>
    /// Saves the complete configuration, committing settings and encrypted keys together.
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

    private AiHubConfig LoadUnlocked()
    {
        byte[]? bytes = AiHubStorageFiles.ReadOptionalFile(SettingsFilePath, AiHubStorageFiles.MaxDataFileBytes);
        var config = bytes is null
            ? AiHubConfig.CreateDefault()
            : JsonSerializer.Deserialize(bytes, AiHubJsonContext.Default.AiHubConfig)
                ?? throw new InvalidDataException("The AI Hub settings file is invalid.");
        ValidateTargets(config);
        var secrets = _keyStore.ReadUnlocked();
        foreach (var target in config.Targets)
        {
            target.ApiKey = secrets.TryGetValue($"target_key_{target.Name}", out string? key) ? key : string.Empty;
        }

        ValidateTargets(config);
        return config;
    }

    private void SaveUnlocked(AiHubConfig config)
    {
        ValidateTargets(config);
        var clone = new AiHubConfig
        {
            IsEnabled = config.IsEnabled,
            SelectedKernel = config.SelectedKernel,
            MaxConcurrentAnalysis = config.MaxConcurrentAnalysis,
        };
        var secrets = _keyStore.ReadUnlocked();
        foreach (string key in secrets.Keys.Where(key => key.StartsWith("target_key_", StringComparison.Ordinal)).ToArray())
        {
            secrets.Remove(key);
        }

        foreach (var target in config.Targets)
        {
            var targetClone = target.Clone();
            if (!string.IsNullOrEmpty(targetClone.ApiKey))
            {
                secrets.Add($"target_key_{targetClone.Name}", targetClone.ApiKey);
            }

            targetClone.ApiKey = string.Empty;
            clone.Targets.Add(targetClone);
        }

        byte[] settings = JsonSerializer.SerializeToUtf8Bytes(clone, AiHubJsonContext.Default.AiHubConfig);
        _files.CommitSettingsAndSecrets(settings, SecureKeyStore.Encrypt(secrets));
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
                target.Mode is not ("responses" or "chat") ||
                target.Effort is not ("low" or "medium" or "high" or "xhigh" or "max"))
            {
                throw new InvalidDataException("The AI Hub target fields are invalid or exceed their size limits.");
            }
        }
    }
}
