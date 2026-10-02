namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Kit.AiHub.Models;
using Kit.AiHub.Security;
using Kit.AiHub.Serialization;
using Kit.AiHub.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class StorageAndPolicyTests
{
    [TestMethod]
    [DataRow("low")]
    [DataRow("high")]
    [DataRow("max")]
    public void EndpointModelsAndEffortsSurviveReopeningAndUnrelatedUpdates(string effort)
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        config.Targets[0].Model = "gpt-6.1-sol";
        config.Targets[0].Effort = effort;
        config.Targets[1].Model = "provider/custom-next-model";
        config.Targets[1].Effort = effort;
        store.Save(config);

        var reopened = new AiHubSettingsStore(store.DataDirectory);
        reopened.Update(current => current.RetentionDays = 14);
        var saved = reopened.Load();
        Assert.AreEqual("gpt-6.1-sol", saved.Targets[0].Model);
        Assert.AreEqual("provider/custom-next-model", saved.Targets[1].Model);
        Assert.AreEqual(effort, saved.Targets[0].Effort);
        Assert.AreEqual(effort, saved.Targets[1].Effort);
    }

    [TestMethod]
    [DataRow("none", "low")]
    [DataRow("minimal", "low")]
    [DataRow("medium", "high")]
    [DataRow("xhigh", "max")]
    [DataRow("ultra", "max")]
    public void LegacyEffortsNormalizeOnLoadWithoutWritingUntilAnUpdate(string legacyEffort, string expectedEffort)
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        config.Targets[0].Model = "gpt-5.6-luna";
        config.Targets[0].ApiKey = "fixture-main-key";
        config.Targets[1].ApiKey = "fixture-fallback-key";
        store.Save(config);
        config.Targets[0].Effort = legacyEffort;
        config.Targets[1].Effort = legacyEffort;
        byte[] legacy = JsonSerializer.SerializeToUtf8Bytes(config, AiHubJsonContext.Default.AiHubConfig);
        File.WriteAllBytes(store.SettingsFilePath, legacy);
        string secretsPath = Path.Combine(store.DataDirectory, "secrets.dat");
        byte[] secrets = File.ReadAllBytes(secretsPath);

        var loaded = store.Load();
        Assert.AreEqual(expectedEffort, loaded.Targets[0].Effort);
        Assert.AreEqual(expectedEffort, loaded.Targets[1].Effort);
        Assert.AreEqual("gpt-5.6-luna", loaded.Targets[0].Model);
        Assert.AreEqual("fixture-main-key", loaded.Targets[0].ApiKey);
        Assert.AreEqual("fixture-fallback-key", loaded.Targets[1].ApiKey);
        Assert.ThrowsExactly<InvalidDataException>(() => store.Save(config));
        CollectionAssert.AreEqual(legacy, File.ReadAllBytes(store.SettingsFilePath));
        CollectionAssert.AreEqual(secrets, File.ReadAllBytes(secretsPath));

        store.Update(current => current.RetentionDays = 14);
        var persisted = JsonSerializer.Deserialize(File.ReadAllBytes(store.SettingsFilePath), AiHubJsonContext.Default.AiHubConfig)!;
        Assert.AreEqual(expectedEffort, persisted.Targets[0].Effort);
        Assert.AreEqual(expectedEffort, persisted.Targets[1].Effort);
        var saved = store.Load();
        Assert.AreEqual("gpt-5.6-luna", saved.Targets[0].Model);
        Assert.AreEqual("fixture-main-key", saved.Targets[0].ApiKey);
        Assert.AreEqual("fixture-fallback-key", saved.Targets[1].ApiKey);
    }

    [TestMethod]
    public void UnknownPersistedEffortIsRejectedWithoutChangingSettingsOrCredentials()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        store.Save(config);
        config.Targets[0].Effort = "unknown";
        byte[] invalid = JsonSerializer.SerializeToUtf8Bytes(config, AiHubJsonContext.Default.AiHubConfig);
        File.WriteAllBytes(store.SettingsFilePath, invalid);
        string secretsPath = Path.Combine(store.DataDirectory, "secrets.dat");
        byte[] secrets = File.ReadAllBytes(secretsPath);

        Assert.ThrowsExactly<InvalidDataException>(() => store.Load());
        CollectionAssert.AreEqual(invalid, File.ReadAllBytes(store.SettingsFilePath));
        CollectionAssert.AreEqual(secrets, File.ReadAllBytes(secretsPath));
    }

    [TestMethod]
    public void LegacyServiceMigratesWithoutTouchingPluginSettingsOrCredentials()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        config.IsEnabled = true;
        config.Targets[0].Model = "fixture-model";
        config.Targets[0].ApiKey = "fixture-key";
        store.Save(config);
        string legacyPath = Path.Combine(store.DataDirectory, "settings.json");
        File.Move(store.SettingsFilePath, legacyPath);
        byte[] legacy = File.ReadAllBytes(legacyPath);
        byte[] secrets = File.ReadAllBytes(Path.Combine(store.DataDirectory, "secrets.dat"));

        var migrated = store.Load();
        Assert.IsTrue(migrated.IsEnabled);
        Assert.AreEqual("fixture-key", migrated.Targets[0].ApiKey);
        CollectionAssert.AreEqual(legacy, File.ReadAllBytes(legacyPath));
        CollectionAssert.AreEqual(secrets, File.ReadAllBytes(Path.Combine(store.DataDirectory, "secrets.dat")));

        const string plugin = "{\"name\":\"AIHub\",\"properties\":{\"activeTabIndex\":{\"value\":1}}}";
        File.WriteAllText(legacyPath, plugin);
        store.Update(current => current.MaxConcurrentAnalysis = 3);
        Assert.AreEqual(plugin, File.ReadAllText(legacyPath));
        Assert.AreEqual("fixture-model", store.Load().Targets[0].Model);
        Assert.AreEqual("fixture-key", store.Load().Targets[0].ApiKey);
    }

    [TestMethod]
    public async Task ExistingShortTaskPolicyIsPreservedWhenServiceIsRecreated()
    {
        using var fixture = new FixtureDirectory();
        var service = new SecurityPolicyService(fixture.PathFor("data"), fixture.PathFor("modules"));
        const string customPolicy = "Report findings only; never change files.";
        await service.SaveTaskPolicyAsync("security-audit", customPolicy);

        var reopened = new SecurityPolicyService(fixture.PathFor("data"), fixture.PathFor("modules"));
        Assert.AreEqual(customPolicy, await reopened.LoadTaskAgentsPolicyAsync("aihub", "security-audit"));
    }

    [TestMethod]
    public void PluginOnlyLegacyFileDoesNotBecomeServiceConfiguration()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        string legacyPath = Path.Combine(store.DataDirectory, "settings.json");
        const string plugin = "{\"name\":\"AIHub\",\"properties\":{\"activeTabIndex\":{\"value\":2}}}";
        File.WriteAllText(legacyPath, plugin);
        Assert.IsFalse(store.Load().IsEnabled);
        store.Update(current => current.IsEnabled = true);
        Assert.AreEqual(plugin, File.ReadAllText(legacyPath));
        Assert.IsTrue(store.Load().IsEnabled);
    }

    [TestMethod]
    public void ServiceUpdatesPreserveLegacyAuditPreferences()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        config.ScanIntervalHours = 8;
        config.AuditModeIndex = 1;
        config.RetentionDays = 14;
        store.Save(config);
        store.Update(current => current.IsEnabled = true);
        var saved = store.Load();
        Assert.AreEqual(8, saved.ScanIntervalHours);
        Assert.AreEqual(1, saved.AuditModeIndex);
        Assert.AreEqual(14, saved.RetentionDays);
    }

    [TestMethod]
    public void StoresReadCurrentCredentialsWithoutWritingPlaintextKeys()
    {
        using var fixture = new FixtureDirectory();
        string dataDirectory = fixture.PathFor("data");
        var first = new AiHubSettingsStore(dataDirectory);
        var second = new AiHubSettingsStore(dataDirectory);
        var config = AiHubConfig.CreateDefault();
        config.Targets[0].ApiKey = "fixture-api-key-initial";
        first.Save(config);

        Assert.AreEqual(Path.GetFullPath(dataDirectory), first.DataDirectory);
        Assert.AreEqual("fixture-api-key-initial", second.Load().Targets[0].ApiKey);
        string settingsJson = File.ReadAllText(first.SettingsFilePath);
        Assert.IsFalse(settingsJson.Contains("apiKey", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(settingsJson.Contains("fixture-api-key-initial", StringComparison.Ordinal));

        byte[] ciphertext = File.ReadAllBytes(Path.Combine(dataDirectory, "secrets.dat"));
        Assert.IsFalse(Encoding.UTF8.GetString(ciphertext).Contains("fixture-api-key-initial", StringComparison.Ordinal));
        byte[] plaintext = ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
        try
        {
            var secrets = JsonSerializer.Deserialize(plaintext, AiHubJsonContext.Default.DictionaryStringString);
            Assert.IsNotNull(secrets);
            Assert.AreEqual("fixture-api-key-initial", secrets["target_key_Main"]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        second.KeyStore.SetSecret("target_key_Main", "fixture-api-key-replaced");
        Assert.AreEqual("fixture-api-key-replaced", first.Load().Targets[0].ApiKey);
        first.KeyStore.SetSecret("target_key_Main", string.Empty);
        Assert.AreEqual(string.Empty, second.Load().Targets[0].ApiKey);
    }

    [TestMethod]
    public async Task ConcurrentUpdatesPreserveAllCommittedChanges()
    {
        using var fixture = new FixtureDirectory();
        var first = new AiHubSettingsStore(fixture.PathFor("data"));
        var second = new AiHubSettingsStore(first.DataDirectory);
        var config = AiHubConfig.CreateDefault();
        config.MaxConcurrentAnalysis = 1;
        config.Targets[0].ApiKey = "fixture-saved-key";
        config.Targets[0].Model = "fixture-saved-model";
        first.Save(config);

        var updates = Enumerable.Range(0, 12).Select(index => Task.Run(() =>
        {
            var store = index % 2 == 0 ? first : second;
            store.Update(current => current.Targets[0].Model += "x");
        }));
        await Task.WhenAll(updates);
        second.Update(current => current.IsEnabled = true);

        var latest = first.Load();
        Assert.AreEqual(1, latest.MaxConcurrentAnalysis);
        Assert.IsTrue(latest.IsEnabled);
        Assert.AreEqual("fixture-saved-key", latest.Targets[0].ApiKey);
        Assert.AreEqual("fixture-saved-model" + new string('x', 12), latest.Targets[0].Model);
    }

    [TestMethod]
    public async Task ConcurrentSecretUpdatesDoNotDiscardOtherKeys()
    {
        using var fixture = new FixtureDirectory();
        var first = new SecureKeyStore(fixture.PathFor("data"));
        var second = new SecureKeyStore(fixture.PathFor("data"));
        var updates = Enumerable.Range(0, 12).Select(index => Task.Run(() =>
            (index % 2 == 0 ? first : second).SetSecret($"fixture-key-{index}", $"fixture-value-{index}")));
        await Task.WhenAll(updates);

        for (int index = 0; index < 12; index++)
        {
            Assert.AreEqual($"fixture-value-{index}", first.GetSecret($"fixture-key-{index}"));
            Assert.AreEqual($"fixture-value-{index}", second.GetSecret($"fixture-key-{index}"));
        }
    }

    [TestMethod]
    public void UpdateUsesLatestSettingsInsteadOfAnUnsavedEndpointDraft()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        config.Targets[0].BaseUrl = "https://example.invalid/saved";
        config.Targets[0].ApiKey = "fixture-saved-key";
        store.Save(config);
        var draft = store.Load();
        draft.Targets[0].BaseUrl = "https://example.invalid/unsaved";
        draft.Targets[0].ApiKey = "fixture-unsaved-key";

        store.Update(current => current.IsEnabled = true);
        var loaded = store.Load();
        Assert.IsTrue(loaded.IsEnabled);
        Assert.AreEqual("https://example.invalid/saved", loaded.Targets[0].BaseUrl);
        Assert.AreEqual("fixture-saved-key", loaded.Targets[0].ApiKey);
    }

    [TestMethod]
    public void DuplicateTargetNamesAreRejectedBeforeChangingFiles()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        store.Save(config);
        byte[] oldSettings = File.ReadAllBytes(store.SettingsFilePath);
        string secretsPath = Path.Combine(store.DataDirectory, "secrets.dat");
        byte[] oldSecrets = File.ReadAllBytes(secretsPath);
        config.Targets.Add(config.Targets[0].Clone());

        Assert.ThrowsExactly<InvalidDataException>(() => store.Save(config));
        CollectionAssert.AreEqual(oldSettings, File.ReadAllBytes(store.SettingsFilePath));
        CollectionAssert.AreEqual(oldSecrets, File.ReadAllBytes(secretsPath));
    }

    [TestMethod]
    [DataRow("empty-targets")]
    [DataRow("missing-slot")]
    [DataRow("reordered-slots")]
    [DataRow("renamed-slot")]
    [DataRow("null-targets")]
    [DataRow("null-target")]
    [DataRow("unknown-kernel")]
    [DataRow("null-kernel")]
    [DataRow("zero-concurrency")]
    [DataRow("high-concurrency")]
    [DataRow("null-url")]
    [DataRow("long-url")]
    [DataRow("null-key")]
    [DataRow("long-key")]
    [DataRow("null-model")]
    [DataRow("long-model")]
    [DataRow("invalid-mode")]
    [DataRow("invalid-effort")]
    public void InvalidConfigurationsCannotReplaceValidPersistedState(string scenario)
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        store.Save(config);
        byte[] oldSettings = File.ReadAllBytes(store.SettingsFilePath);
        string secretsPath = Path.Combine(store.DataDirectory, "secrets.dat");
        byte[] oldSecrets = File.ReadAllBytes(secretsPath);

        switch (scenario)
        {
            case "empty-targets":
                config.Targets.Clear();
                break;
            case "missing-slot":
                config.Targets.RemoveAt(1);
                break;
            case "reordered-slots":
                config.Targets.Move(0, 1);
                break;
            case "renamed-slot":
                config.Targets[0].Name = "Synthetic";
                break;
            case "null-targets":
                config.Targets = null!;
                break;
            case "null-target":
                config.Targets[0] = null!;
                break;
            case "unknown-kernel":
                config.SelectedKernel = "unknown";
                break;
            case "null-kernel":
                config.SelectedKernel = null!;
                break;
            case "zero-concurrency":
                config.MaxConcurrentAnalysis = 0;
                break;
            case "high-concurrency":
                config.MaxConcurrentAnalysis = 5;
                break;
            case "null-url":
                config.Targets[0].BaseUrl = null!;
                break;
            case "long-url":
                config.Targets[0].BaseUrl = new string('a', 4097);
                break;
            case "null-key":
                config.Targets[0].ApiKey = null!;
                break;
            case "long-key":
                config.Targets[0].ApiKey = new string('a', 8193);
                break;
            case "null-model":
                config.Targets[0].Model = null!;
                break;
            case "long-model":
                config.Targets[0].Model = new string('a', 257);
                break;
            case "invalid-mode":
                config.Targets[0].Mode = "invalid";
                break;
            case "invalid-effort":
                config.Targets[0].Effort = "invalid";
                break;
            default:
                Assert.Fail("Unknown fixture scenario.");
                break;
        }

        var exception = Assert.ThrowsExactly<InvalidDataException>(() => store.Save(config));
        Assert.IsNull(exception.InnerException);
        CollectionAssert.AreEqual(oldSettings, File.ReadAllBytes(store.SettingsFilePath));
        CollectionAssert.AreEqual(oldSecrets, File.ReadAllBytes(secretsPath));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("null")]
    [DataRow("{\"targets\":null}")]
    [DataRow("{\"targets\":[]}")]
    [DataRow("invalid fixture JSON")]
    public void InvalidPersistedSettingsDoNotBecomeDefaultsOrOverwriteFiles(string json)
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        File.WriteAllText(store.SettingsFilePath, json);
        Assert.ThrowsExactly<InvalidDataException>(() => store.Load());
        Assert.AreEqual(json, File.ReadAllText(store.SettingsFilePath));
        Assert.IsFalse(File.Exists(Path.Combine(store.DataDirectory, "secrets.dat")));
    }

    [TestMethod]
    public void LegacyPlaintextKeysAreIgnoredAndRemovedOnTheNextSave()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        File.WriteAllText(store.SettingsFilePath, """
            {
              "isEnabled": false,
              "selectedKernel": "codex",
              "maxConcurrentAnalysis": 2,
              "targets": [
                { "name": "Main", "apiKey": "fixture-legacy-plaintext-key" },
                { "name": "Fallback" }
              ]
            }
            """);
        var config = store.Load();
        Assert.AreEqual(string.Empty, config.Targets[0].ApiKey);
        store.Save(config);
        string saved = File.ReadAllText(store.SettingsFilePath);
        Assert.IsFalse(saved.Contains("fixture-legacy-plaintext-key", StringComparison.Ordinal));
        Assert.IsFalse(saved.Contains("apiKey", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void CorruptCredentialsFailClosedWithoutOverwritingDataOrExposingErrors()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        config.Targets[0].ApiKey = "fixture-private-value";
        store.Save(config);
        byte[] oldSettings = File.ReadAllBytes(store.SettingsFilePath);
        string secretsPath = Path.Combine(store.DataDirectory, "secrets.dat");
        byte[] corrupted = [1, 2, 3, 4];
        File.WriteAllBytes(secretsPath, corrupted);

        var exception = Assert.ThrowsExactly<IOException>(() => store.Load());
        Assert.IsNull(exception.InnerException);
        Assert.IsFalse(exception.Message.Contains("fixture-private-value", StringComparison.Ordinal));
        Assert.IsFalse(exception.Message.Contains(store.DataDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.ThrowsExactly<IOException>(() => store.KeyStore.SetSecret("fixture", "fixture-value"));
        Assert.ThrowsExactly<IOException>(() => store.Save(config));
        CollectionAssert.AreEqual(oldSettings, File.ReadAllBytes(store.SettingsFilePath));
        CollectionAssert.AreEqual(corrupted, File.ReadAllBytes(secretsPath));
    }

    [TestMethod]
    public void SettingsCommitFailureRestoresPreviousEncryptedCredentials()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        config.Targets[0].ApiKey = "fixture-original-key";
        store.Save(config);
        byte[] oldSettings = File.ReadAllBytes(store.SettingsFilePath);
        string secretsPath = Path.Combine(store.DataDirectory, "secrets.dat");
        byte[] oldSecrets = File.ReadAllBytes(secretsPath);
        File.SetAttributes(store.SettingsFilePath, FileAttributes.ReadOnly);
        try
        {
            config.Targets[0].ApiKey = "fixture-uncommitted-key";
            config.IsEnabled = true;
            Assert.ThrowsExactly<IOException>(() => store.Save(config));
        }
        finally
        {
            File.SetAttributes(store.SettingsFilePath, FileAttributes.Normal);
        }

        CollectionAssert.AreEqual(oldSettings, File.ReadAllBytes(store.SettingsFilePath));
        CollectionAssert.AreEqual(oldSecrets, File.ReadAllBytes(secretsPath));
        Assert.AreEqual("fixture-original-key", store.Load().Targets[0].ApiKey);
        Assert.IsFalse(File.Exists(Path.Combine(store.DataDirectory, ".settings-transaction.dat")));
        Assert.AreEqual(0, Directory.GetFiles(store.DataDirectory, "*.tmp").Length);
    }

    [TestMethod]
    public void SettingsCommitFailureRestoresAnOriginallyMissingCredentialFile()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        store.Save(config);
        string secretsPath = Path.Combine(store.DataDirectory, "secrets.dat");
        File.Delete(secretsPath);
        File.SetAttributes(store.SettingsFilePath, FileAttributes.ReadOnly);
        try
        {
            config.Targets[0].ApiKey = "fixture-uncommitted-key";
            Assert.ThrowsExactly<IOException>(() => store.Save(config));
        }
        finally
        {
            File.SetAttributes(store.SettingsFilePath, FileAttributes.Normal);
        }

        Assert.IsFalse(File.Exists(secretsPath));
        Assert.AreEqual(string.Empty, store.Load().Targets[0].ApiKey);
    }

    [TestMethod]
    [DataRow("1")]
    [DataRow("2")]
    public void PendingEncryptedTransactionIsRecoveredBeforeAReaderReturns(string version)
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        config.Targets[0].ApiKey = "fixture-before-crash";
        store.Save(config);
        string secretsPath = Path.Combine(store.DataDirectory, "secrets.dat");
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = version,
            ["settings"] = Convert.ToBase64String(File.ReadAllBytes(store.SettingsFilePath)),
            ["secrets"] = Convert.ToBase64String(File.ReadAllBytes(secretsPath)),
        };
        config.Targets[0].ApiKey = "fixture-uncommitted";
        config.IsEnabled = true;
        store.Save(config);

        if (version == "1")
        {
            File.Move(store.SettingsFilePath, Path.Combine(store.DataDirectory, "settings.json"));
        }

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(snapshot, AiHubJsonContext.Default.DictionaryStringString);
        try
        {
            File.WriteAllBytes(
                Path.Combine(store.DataDirectory, ".settings-transaction.dat"),
                ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var recovered = new AiHubSettingsStore(store.DataDirectory).Load();
        Assert.IsFalse(recovered.IsEnabled);
        Assert.AreEqual("fixture-before-crash", recovered.Targets[0].ApiKey);
        Assert.IsFalse(File.Exists(Path.Combine(store.DataDirectory, ".settings-transaction.dat")));
        Assert.IsTrue(File.Exists(store.SettingsFilePath));
    }

    [TestMethod]
    public void CorruptJournalCannotPartiallyRestoreSettings()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        store.Save(AiHubConfig.CreateDefault());
        byte[] oldSettings = File.ReadAllBytes(store.SettingsFilePath);
        string secretsPath = Path.Combine(store.DataDirectory, "secrets.dat");
        byte[] oldSecrets = File.ReadAllBytes(secretsPath);
        File.WriteAllBytes(Path.Combine(store.DataDirectory, ".settings-transaction.dat"), [1, 2, 3, 4]);

        Assert.ThrowsExactly<IOException>(() => { _ = new AiHubSettingsStore(store.DataDirectory); });
        CollectionAssert.AreEqual(oldSettings, File.ReadAllBytes(store.SettingsFilePath));
        CollectionAssert.AreEqual(oldSecrets, File.ReadAllBytes(secretsPath));
    }

    [TestMethod]
    public async Task TaskPoliciesAreIsolatedByPluginAndNeverUseTheUserChainFallback()
    {
        using var fixture = new FixtureDirectory();
        string dataDirectory = fixture.PathFor("data");
        string packagesDirectory = fixture.PathFor("modules");
        WritePolicy(fixture.PathFor("modules", "PluginA", "Chains", "task", "AGENTS.md"), "Plugin A instructions");
        WritePolicy(fixture.PathFor("modules", "PluginA", "Chains", "task", "security.md"), "Plugin A policy");
        WritePolicy(fixture.PathFor("modules", "PluginB", "Chains", "task", "security.md"), "Plugin B policy");
        WritePolicy(fixture.PathFor("data", "chains", "task", "security.md"), "Legacy policy must never be loaded");
        WritePolicy(fixture.PathFor("data", "chains", "task", "AGENTS.md"), "Legacy instructions must never be loaded");
        var service = new SecurityPolicyService(dataDirectory, packagesDirectory);

        Assert.AreEqual("Plugin A instructions", await service.LoadTaskAgentsPolicyAsync("PluginA", "task"));
        Assert.AreEqual("Plugin A policy", await service.LoadTaskSecurityPolicyAsync("PluginA", "task"));
        Assert.AreEqual("Plugin B policy", await service.LoadTaskSecurityPolicyAsync("PluginB", "task"));
        Assert.AreEqual(string.Empty, await service.LoadTaskSecurityPolicyAsync("PluginC", "task"));
        Assert.AreEqual(string.Empty, await service.LoadTaskAgentsPolicyAsync("PluginC", "task"));
        Assert.AreEqual(packagesDirectory, service.PluginPackagesDirectory);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("..")]
    [DataRow("../outside")]
    [DataRow("nested/name")]
    [DataRow(@"nested\name")]
    [DataRow("C:device")]
    [DataRow("with.dot")]
    [DataRow("-leading")]
    [DataRow("trailing ")]
    [DataRow("CON")]
    [DataRow("LPT1")]
    [DataRow("插件")]
    public async Task TaskPoliciesRejectUnsafeIdentifiers(string identifier)
    {
        using var fixture = new FixtureDirectory();
        var service = new SecurityPolicyService(fixture.PathFor("data"), fixture.PathFor("modules"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.LoadTaskSecurityPolicyAsync(identifier, "task"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.LoadTaskAgentsPolicyAsync("Plugin", identifier));
    }

    [TestMethod]
    public async Task PoliciesEnforceNonemptyUtf8AndSizeLimitsAndCanBeReset()
    {
        using var fixture = new FixtureDirectory();
        var service = new SecurityPolicyService(fixture.PathFor("data"), fixture.PathFor("modules"));
        await service.SaveGlobalPolicyAsync("Valid policy");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.SaveGlobalPolicyAsync(" \r\n\t"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.SaveGlobalPolicyAsync(new string('a', SecurityPolicyService.MaximumPolicyBytes + 1)));
        Assert.AreEqual("Valid policy", await service.LoadGlobalPolicyAsync());

        File.WriteAllBytes(service.GlobalSecurityPolicyPath, [0xC3, 0x28]);
        service = new SecurityPolicyService(fixture.PathFor("data"), fixture.PathFor("modules"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.LoadGlobalPolicyAsync());
        service.ResetGlobalPolicy();
        Assert.AreEqual(SecurityPolicyService.GetDefaultPolicyContent(), await service.LoadGlobalPolicyAsync());
        File.WriteAllText(service.GlobalSecurityPolicyPath, "UTF-8 BOM policy", new UTF8Encoding(true));
        Assert.AreEqual("UTF-8 BOM policy", await service.LoadGlobalPolicyAsync());

        string taskPolicy = fixture.PathFor("modules", "Plugin", "Chains", "task", "security.md");
        WritePolicy(taskPolicy, string.Empty);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.LoadTaskSecurityPolicyAsync("Plugin", "task"));
        File.WriteAllBytes(taskPolicy, [0xC3, 0x28]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.LoadTaskSecurityPolicyAsync("Plugin", "task"));
        File.WriteAllText(taskPolicy, new string('a', SecurityPolicyService.MaximumPolicyBytes + 1));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.LoadTaskSecurityPolicyAsync("Plugin", "task"));
    }

    [TestMethod]
    public async Task GlobalPolicyReplacementFailurePreservesThePreviousPolicy()
    {
        using var fixture = new FixtureDirectory();
        var service = new SecurityPolicyService(fixture.PathFor("data"), fixture.PathFor("modules"));
        await service.SaveGlobalPolicyAsync("Original policy");
        File.SetAttributes(service.GlobalSecurityPolicyPath, FileAttributes.ReadOnly);
        try
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => service.SaveGlobalPolicyAsync("Uncommitted policy"));
        }
        finally
        {
            File.SetAttributes(service.GlobalSecurityPolicyPath, FileAttributes.Normal);
        }

        Assert.AreEqual("Original policy", await service.LoadGlobalPolicyAsync());
        Assert.AreEqual(0, Directory.GetFiles(fixture.PathFor("data"), "*.tmp").Length);
    }

    [TestMethod]
    public async Task ReparseDirectoriesCannotEscapeTaskOrStorageRoots()
    {
        using var fixture = new FixtureDirectory();
        string target = fixture.PathFor("outside-package");
        WritePolicy(Path.Combine(target, "Chains", "task", "security.md"), "Outside policy");
        string packageRoot = fixture.PathFor("modules");
        Directory.CreateDirectory(packageRoot);
        string link = Path.Combine(packageRoot, "Plugin");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            Assert.Inconclusive("Creating a directory link is unavailable in this test environment.");
        }

        var service = new SecurityPolicyService(fixture.PathFor("data"), packageRoot);
        await Assert.ThrowsExactlyAsync<IOException>(() => service.LoadTaskSecurityPolicyAsync("Plugin", "task"));
        Assert.ThrowsExactly<IOException>(() => { _ = new AiHubSettingsStore(link); });
        Assert.IsFalse(File.Exists(Path.Combine(target, "settings.json")));
        Assert.AreEqual("Outside policy", File.ReadAllText(Path.Combine(target, "Chains", "task", "security.md")));
    }

    [TestMethod]
    public async Task SaveTaskPolicyAsyncCanCreateAndPersistUserChains()
    {
        using var fixture = new FixtureDirectory();
        var service = new SecurityPolicyService(fixture.PathFor("data"), fixture.PathFor("modules"));

        await service.SaveTaskPolicyAsync("security-audit", "Custom security audit policy content");
        await service.SaveTaskPolicyAsync("system-optimization", "Custom system optimization policy content");

        string auditPolicy = await service.LoadTaskAgentsPolicyAsync("aihub", "security-audit");
        string optPolicy = await service.LoadTaskAgentsPolicyAsync("aihub", "system-optimization");

        Assert.AreEqual("Custom security audit policy content", auditPolicy);
        Assert.AreEqual("Custom system optimization policy content", optPolicy);
    }

    [TestMethod]
    [DataRow(false, "\n")]
    [DataRow(true, "\n")]
    [DataRow(false, "\r\n")]
    [DataRow(true, "\r\n")]
    public async Task ShippedAuditPolicyMigratesReferencesAcrossBomAndLineEndings(bool bom, string newline)
    {
        using var fixture = new FixtureDirectory();
        string path = fixture.PathFor("data", "chains", "security-audit", "AGENTS.md");
        string legacy = LegacyAuditPolicy();
        Assert.IsTrue(legacy.Contains("`event-3`", StringComparison.Ordinal));
        Assert.IsTrue(legacy.Contains("\"eventRef\":\"event-4\"", StringComparison.Ordinal));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, legacy.ReplaceLineEndings(newline), new UTF8Encoding(bom));

        var service = new SecurityPolicyService(fixture.PathFor("data"), fixture.PathFor("modules"));
        string updated = await service.LoadTaskAgentsPolicyAsync("aihub", "security-audit");
        Assert.AreEqual(TaskPolicyDefaults.DefaultSecurityAuditInstructions, updated);
        Assert.IsTrue(updated.Contains("`input.itemId`", StringComparison.Ordinal));
        Assert.IsFalse(updated.Contains("event-4", StringComparison.Ordinal));
        byte[] migrated = File.ReadAllBytes(path);
        service.EnsureDefaultPolicyExists();
        CollectionAssert.AreEqual(migrated, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AuditPolicyMigrationPreservesUserEdits(bool nearDefault)
    {
        using var fixture = new FixtureDirectory();
        string path = fixture.PathFor("data", "chains", "security-audit", "AGENTS.md");
        string custom = nearDefault
            ? LegacyAuditPolicy().Replace("Local Security Audit policy", "Custom Security Audit policy", StringComparison.Ordinal)
            : "Custom policy with event-3 and event-4 examples.";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, custom, new UTF8Encoding(true));
        byte[] original = File.ReadAllBytes(path);

        var service = new SecurityPolicyService(fixture.PathFor("data"), fixture.PathFor("modules"));
        Assert.AreEqual(custom, await service.LoadTaskAgentsPolicyAsync("aihub", "security-audit"));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
    }

    private static string LegacyAuditPolicy()
    {
        // Reconstruct the prior shipped document without duplicating the entire audit policy.
        string legacy = TaskPolicyDefaults.DefaultSecurityAuditInstructions.ReplaceLineEndings("\n")
            .Replace(
                """
                - Cite the events you used. Copy `eventRef` exactly from the primary event's
                  `input.itemId` in the current batch (for example `item-000003`). Every entry in
                  `relatedEventRefs` must also be an `input.itemId` from this same batch.
                  Never invent references, copy example IDs, use Windows event IDs as references,
                  or refer to events from another batch.
                """.ReplaceLineEndings("\n"),
                """
                - Cite the events you used. `eventRef` names the primary event, copied exactly (for
                  example `event-3`); `relatedEventRefs` lists every supplied event that supports the
                  same finding.
                """.ReplaceLineEndings("\n"),
                StringComparison.Ordinal)
            .Replace(
                "- eventRef (string): copy the primary event's `input.itemId` from the current batch\n  exactly.",
                "- eventRef (string): reference of the primary event, copied exactly from the supplied\n  data.",
                StringComparison.Ordinal)
            .Replace(
                "Example (illustrative only; copy actual IDs and evidence from the current batch):",
                "Example:",
                StringComparison.Ordinal);
        for (int index = 4; index <= 9; index++)
        {
            legacy = legacy.Replace($"item-{index:D6}", $"event-{index}", StringComparison.Ordinal);
        }

        return legacy;
    }

    private static void WritePolicy(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}
