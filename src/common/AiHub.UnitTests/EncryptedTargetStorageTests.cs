namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kit.AiHub.Models;
using Kit.AiHub.Serialization;
using Kit.AiHub.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class EncryptedTargetStorageTests
{
    [TestMethod]
    public void BothCompleteProfilesAreEncryptedAndSurviveReopeningAndKeyUpdates()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = CreateConfig();
        store.Save(config);

        foreach (string path in Directory.GetFiles(store.DataDirectory))
        {
            string bytesAsText = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            foreach (var target in config.Targets)
            {
                Assert.IsFalse(bytesAsText.Contains(target.BaseUrl, StringComparison.Ordinal));
                Assert.IsFalse(bytesAsText.Contains(target.Model, StringComparison.Ordinal));
                Assert.IsFalse(bytesAsText.Contains(target.ApiKey, StringComparison.Ordinal));
                Assert.IsFalse(bytesAsText.Contains(target.Effort, StringComparison.Ordinal));
            }
        }

        var reopened = new AiHubSettingsStore(store.DataDirectory);
        reopened.KeyStore.SetSecret("target_key_Main", "fixture-key-replaced");
        config.Targets[0].ApiKey = "fixture-key-replaced";
        AssertConfig(config, reopened.Load());
        reopened.KeyStore.SetSecret("fixture-unrelated", "keep-me");
        reopened.Update(current => current.RetentionDays = 14);
        Assert.AreEqual("keep-me", reopened.KeyStore.GetSecret("fixture-unrelated"));
        AssertConfig(config, new AiHubSettingsStore(store.DataDirectory).Load());
    }

    [TestMethod]
    public void PlaintextServiceAndStaleLegacyCopyMigrateWithoutLeakingEitherProfile()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = CreateConfig();
        store.KeyStore.SetSecret("target_key_Main", config.Targets[0].ApiKey);
        store.KeyStore.SetSecret("target_key_Fallback", config.Targets[1].ApiKey);
        byte[] legacy = JsonSerializer.SerializeToUtf8Bytes(config, AiHubJsonContext.Default.AiHubConfig);
        File.WriteAllBytes(store.SettingsFilePath, legacy);
        File.WriteAllBytes(Path.Combine(store.DataDirectory, "settings.json"), legacy);

        AssertConfig(config, store.Load());
        AssertConfig(config, new AiHubSettingsStore(store.DataDirectory).Load());
        foreach (string file in Directory.GetFiles(store.DataDirectory, "*.json"))
        {
            Assert.IsFalse(File.ReadAllText(file).Contains("fixture-main-model", StringComparison.Ordinal));
            Assert.IsFalse(File.ReadAllText(file).Contains("fixture-fallback-model", StringComparison.Ordinal));
            Assert.IsFalse(File.ReadAllText(file).Contains("\"targets\"", StringComparison.Ordinal));
        }

        Assert.IsFalse(File.Exists(Path.Combine(store.DataDirectory, ".settings-transaction.dat")));
    }

    [TestMethod]
    public void FailedLegacySanitizationRollsBackPublicSettingsAndEncryptedProfiles()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = CreateConfig();
        store.KeyStore.SetSecret("target_key_Main", config.Targets[0].ApiKey);
        byte[] legacy = JsonSerializer.SerializeToUtf8Bytes(config, AiHubJsonContext.Default.AiHubConfig);
        string legacyPath = Path.Combine(store.DataDirectory, "settings.json");
        File.WriteAllBytes(legacyPath, legacy);
        File.WriteAllBytes(store.SettingsFilePath, legacy);
        byte[] previousSecrets = File.ReadAllBytes(Path.Combine(store.DataDirectory, "secrets.dat"));
        File.SetAttributes(legacyPath, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsExactly<IOException>(() => store.Load());
        }
        finally
        {
            File.SetAttributes(legacyPath, FileAttributes.Normal);
        }

        CollectionAssert.AreEqual(legacy, File.ReadAllBytes(store.SettingsFilePath));
        CollectionAssert.AreEqual(legacy, File.ReadAllBytes(legacyPath));
        CollectionAssert.AreEqual(previousSecrets, File.ReadAllBytes(Path.Combine(store.DataDirectory, "secrets.dat")));
        Assert.IsFalse(File.Exists(Path.Combine(store.DataDirectory, ".settings-transaction.dat")));
    }

    [TestMethod]
    public void LegacySanitizationReReadsLatestPluginPropertiesWhileDenyingOtherWriters()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = CreateConfig();
        store.Save(config);
        var protectedSnapshot = store.ExportBackup()!;
        using var preferences = JsonDocument.Parse(protectedSnapshot.SettingsJson);
        string storageId = preferences.RootElement.GetProperty("targetStorageId").GetString()!;
        var legacy = JsonNode.Parse(JsonSerializer.Serialize(config, AiHubJsonContext.Default.AiHubConfig))!.AsObject();
        legacy["properties"] = JsonNode.Parse("{\"activeTabIndex\":{\"value\":0}}");
        string legacyPath = Path.Combine(store.DataDirectory, "settings.json");
        File.WriteAllText(legacyPath, legacy.ToJsonString());
        var files = new AiHubStorageFiles(store.DataDirectory);

        // A plugin writer updates its own properties after migration was scheduled.
        // The commit receives no stale legacy bytes and must re-read the latest file.
        legacy["properties"] = JsonNode.Parse("{\"activeTabIndex\":{\"value\":2},\"fixtureNewPreference\":true}");
        byte[] latest = Encoding.UTF8.GetBytes(legacy.ToJsonString());
        File.WriteAllBytes(legacyPath, latest);
        using (var pluginWriter = new FileStream(legacyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.ThrowsExactly<IOException>(() => files.Run(() => files.CommitSettingsAndSecrets(protectedSnapshot.SettingsJson, protectedSnapshot.EncryptedSecrets!, storageId)));
            Assert.IsFalse(File.Exists(Path.Combine(store.DataDirectory, ".settings-transaction.dat")));
        }

        CollectionAssert.AreEqual(latest, File.ReadAllBytes(legacyPath));
        files.Run(() => files.CommitSettingsAndSecrets(protectedSnapshot.SettingsJson, protectedSnapshot.EncryptedSecrets!, storageId));
        var sanitized = JsonNode.Parse(File.ReadAllText(legacyPath))!.AsObject();
        Assert.IsFalse(sanitized.ContainsKey("targets"));
        Assert.AreEqual(2, sanitized["properties"]!["activeTabIndex"]!["value"]!.GetValue<int>());
        Assert.IsTrue(sanitized["properties"]!["fixtureNewPreference"]!.GetValue<bool>());
        Assert.AreEqual(storageId, sanitized["targetStorageId"]!.GetValue<string>());
        AssertConfig(config, store.Load());
    }

    [TestMethod]
    public void OrdinaryCommitFailureDoesNotReadOrRestoreAnUnrelatedPluginFile()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        store.Save(CreateConfig());
        var previous = store.ExportBackup()!;
        var files = new AiHubStorageFiles(store.DataDirectory);
        string pluginPath = Path.Combine(store.DataDirectory, "settings.json");
        const string plugin = "{\"name\":\"AIHub\",\"properties\":{\"activeTabIndex\":{\"value\":2}}}";
        File.WriteAllText(pluginPath, plugin);
        File.SetAttributes(store.SettingsFilePath, FileAttributes.ReadOnly);
        try
        {
            // The independently owned plugin settings are unavailable throughout both
            // commit and rollback. Neither operation needs to read or restore them.
            using var unrelatedWriter = new FileStream(pluginPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            string failure = files.Run(() => Assert.ThrowsExactly<IOException>(() => files.CommitSettingsAndSecrets(previous.SettingsJson, previous.EncryptedSecrets!)).Message);
            Assert.AreEqual("The AI Hub settings could not be committed.", failure, "The failure must occur during the service commit, after its rollback journal was written.");
            Assert.IsFalse(File.Exists(Path.Combine(store.DataDirectory, ".settings-transaction.dat")));
        }
        finally
        {
            File.SetAttributes(store.SettingsFilePath, FileAttributes.Normal);
        }

        Assert.AreEqual(plugin, File.ReadAllText(pluginPath));
        CollectionAssert.AreEqual(previous.SettingsJson, File.ReadAllBytes(store.SettingsFilePath));
        CollectionAssert.AreEqual(previous.EncryptedSecrets!, File.ReadAllBytes(Path.Combine(store.DataDirectory, "secrets.dat")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InterruptedCommitRollsBackOnlyFilesThatParticipatedInTheTransaction(bool includesLegacy)
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        store.Save(CreateConfig());
        var previous = store.ExportBackup()!;
        string pluginPath = Path.Combine(store.DataDirectory, "settings.json");
        const string previousPlugin = "{\"name\":\"AIHub\",\"properties\":{\"activeTabIndex\":{\"value\":0}}}";
        const string latestPlugin = "{\"name\":\"AIHub\",\"properties\":{\"activeTabIndex\":{\"value\":2}}}";
        var journal = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = includesLegacy ? "3" : "2",
            ["settings"] = Convert.ToBase64String(previous.SettingsJson),
            ["secrets"] = Convert.ToBase64String(previous.EncryptedSecrets!),
        };
        if (includesLegacy)
        {
            journal["legacy"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(previousPlugin));
        }

        store.Update(current => current.Targets[0].Model = "fixture-interrupted-model");

        // This write occurs after the rollback snapshot was captured. A two-file
        // service transaction must preserve the newer independent plugin setting.
        File.WriteAllText(pluginPath, latestPlugin);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(journal, AiHubJsonContext.Default.DictionaryStringString);
        try
        {
            File.WriteAllBytes(Path.Combine(store.DataDirectory, ".settings-transaction.dat"), ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var recovered = new AiHubSettingsStore(store.DataDirectory).Load();
        Assert.AreEqual("fixture-main-model", recovered.Targets[0].Model);
        Assert.AreEqual(includesLegacy ? previousPlugin : latestPlugin, File.ReadAllText(pluginPath));
        Assert.IsFalse(File.Exists(Path.Combine(store.DataDirectory, ".settings-transaction.dat")));
    }

    [TestMethod]
    public void InterruptedLegacySanitizationRetainsPluginPreferencesSavedAfterTheCrash()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = CreateConfig();
        store.Save(config);
        var previous = store.ExportBackup()!;
        var originalLegacy = JsonNode.Parse(JsonSerializer.Serialize(config, AiHubJsonContext.Default.AiHubConfig))!.AsObject();
        originalLegacy["properties"] = JsonNode.Parse("{\"activeTabIndex\":{\"value\":0}}");
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = "3",
            ["settings"] = Convert.ToBase64String(previous.SettingsJson),
            ["secrets"] = Convert.ToBase64String(previous.EncryptedSecrets!),
            ["legacy"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(originalLegacy.ToJsonString())),
        };
        string legacyPath = Path.Combine(store.DataDirectory, "settings.json");
        File.WriteAllText(legacyPath, "{\"name\":\"AIHub\",\"properties\":{\"activeTabIndex\":{\"value\":2},\"fixtureAfterCrash\":true}}");
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(snapshot, AiHubJsonContext.Default.DictionaryStringString);
        try
        {
            File.WriteAllBytes(Path.Combine(store.DataDirectory, ".settings-transaction.dat"), ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var recoveredStore = new AiHubSettingsStore(store.DataDirectory);
        var recoveredLegacy = JsonNode.Parse(File.ReadAllText(legacyPath))!.AsObject();
        Assert.IsTrue(recoveredLegacy.ContainsKey("targets"));
        Assert.AreEqual(2, recoveredLegacy["properties"]!["activeTabIndex"]!["value"]!.GetValue<int>());
        Assert.IsTrue(recoveredLegacy["properties"]!["fixtureAfterCrash"]!.GetValue<bool>());
        AssertConfig(config, recoveredStore.Load());
        var migratedAgain = JsonNode.Parse(File.ReadAllText(legacyPath))!.AsObject();
        Assert.IsFalse(migratedAgain.ContainsKey("targets"));
        Assert.AreEqual(2, migratedAgain["properties"]!["activeTabIndex"]!["value"]!.GetValue<int>());
    }

    [TestMethod]
    public void MissingOrMalformedProtectedProfilesCannotBeReplacedByPreferenceUpdates()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = CreateConfig();
        store.Save(config);
        byte[] previousSettings = File.ReadAllBytes(store.SettingsFilePath);
        string secretsPath = Path.Combine(store.DataDirectory, "secrets.dat");
        File.Delete(secretsPath);
        Assert.ThrowsExactly<InvalidDataException>(() => store.Load());
        Assert.ThrowsExactly<InvalidDataException>(() => store.Save(config));
        Assert.IsFalse(File.Exists(secretsPath));
        CollectionAssert.AreEqual(previousSettings, File.ReadAllBytes(store.SettingsFilePath));

        var snapshotStore = new AiHubSettingsStore(fixture.PathFor("valid"));
        snapshotStore.Save(config);
        var snapshot = snapshotStore.ExportBackup()!;
        store.ImportBackup(snapshot);
        byte[] plaintext = ProtectedData.Unprotect(File.ReadAllBytes(secretsPath), null, DataProtectionScope.CurrentUser);
        var secrets = JsonSerializer.Deserialize(plaintext, AiHubJsonContext.Default.DictionaryStringString)!;
        CryptographicOperations.ZeroMemory(plaintext);
        secrets["target_profiles_v1"] = "not valid JSON";
        byte[] corruptedPlaintext = JsonSerializer.SerializeToUtf8Bytes(secrets, AiHubJsonContext.Default.DictionaryStringString);
        byte[] corrupted = ProtectedData.Protect(corruptedPlaintext, null, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(corruptedPlaintext);
        File.WriteAllBytes(secretsPath, corrupted);
        Assert.ThrowsExactly<InvalidDataException>(() => store.Load());
        Assert.ThrowsExactly<InvalidDataException>(() => store.Update(current => current.RetentionDays = 7));
        Assert.ThrowsExactly<InvalidDataException>(() => store.Save(config));
        CollectionAssert.AreEqual(corrupted, File.ReadAllBytes(secretsPath));
    }

    [TestMethod]
    public void MissingServicePreferencesDoNotTurnProtectedProfilesIntoDefaults()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = CreateConfig();
        store.Save(config);
        byte[] secrets = File.ReadAllBytes(Path.Combine(store.DataDirectory, "secrets.dat"));
        File.Delete(store.SettingsFilePath);
        Assert.ThrowsExactly<InvalidDataException>(() => store.Load());
        Assert.ThrowsExactly<InvalidDataException>(() => store.Save(AiHubConfig.CreateDefault()));
        Assert.ThrowsExactly<InvalidDataException>(() => store.ExportBackup());
        Assert.IsFalse(File.Exists(store.SettingsFilePath));
        CollectionAssert.AreEqual(secrets, File.ReadAllBytes(Path.Combine(store.DataDirectory, "secrets.dat")));
    }

    [TestMethod]
    public void BackupComparisonIgnoresDpapiRandomnessAndRestoreReplacesNewerProfiles()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = CreateConfig();
        store.Save(config);
        var first = store.ExportBackup()!;
        store.Save(config);
        var second = store.ExportBackup()!;
        Assert.IsFalse(first.EncryptedSecrets!.SequenceEqual(second.EncryptedSecrets!));
        Assert.IsTrue(AiHubSettingsStore.BackupContentEquals(first, second));
        store.Update(current => current.Targets[0].Model = "fixture-newer-model");
        Assert.IsFalse(AiHubSettingsStore.BackupContentEquals(first, store.ExportBackup()));
        store.ImportBackup(first);
        AssertConfig(config, store.Load());
    }

    [TestMethod]
    public void ArchiveCannotMixPreferencesAndEncryptedProfilesFromDifferentSnapshots()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        store.Save(CreateConfig());
        var first = store.ExportBackup()!;
        store.Update(current => current.Targets[0].Model = "fixture-second-model");
        var second = store.ExportBackup()!;
        string archive = fixture.PathFor("archive");
        Directory.CreateDirectory(archive);
        File.WriteAllBytes(Path.Combine(archive, "service-settings.json"), first.SettingsJson);
        File.WriteAllBytes(Path.Combine(archive, "secrets.dat"), second.EncryptedSecrets!);
        Assert.ThrowsExactly<InvalidDataException>(() => AiHubSettingsStore.ReadBackup(archive));
    }

    private static AiHubConfig CreateConfig()
    {
        var config = AiHubConfig.CreateDefault();
        config.IsEnabled = true;
        config.Targets[0].BaseUrl = "https://fixture-main.invalid/v1";
        config.Targets[0].Model = "fixture-main-model";
        config.Targets[0].ApiKey = "fixture-main-key";
        config.Targets[0].Effort = "fixture-main-effort";
        config.Targets[0].ContextWindowTokens = 512000;
        config.Targets[1].IsActive = true;
        config.Targets[1].BaseUrl = "https://fixture-fallback.invalid/v1";
        config.Targets[1].Model = "fixture-fallback-model";
        config.Targets[1].ApiKey = "fixture-fallback-key";
        config.Targets[1].Effort = "fixture-fallback-effort";
        config.Targets[1].ContextWindowTokens = 128000;
        return config;
    }

    private static void AssertConfig(AiHubConfig expected, AiHubConfig actual)
    {
        Assert.AreEqual(expected.IsEnabled, actual.IsEnabled);
        for (int index = 0; index < expected.Targets.Count; index++)
        {
            var left = expected.Targets[index];
            var right = actual.Targets[index];
            Assert.AreEqual(JsonSerializer.Serialize(left, AiHubJsonContext.Default.AiTargetSettings), JsonSerializer.Serialize(right, AiHubJsonContext.Default.AiTargetSettings));
            Assert.AreEqual(left.ApiKey, right.ApiKey);
        }
    }
}
