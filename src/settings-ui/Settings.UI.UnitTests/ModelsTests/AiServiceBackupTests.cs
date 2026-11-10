// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Kit.AiHub.Models;
using Kit.AiHub.Serialization;
using Kit.AiHub.Storage;
using Kit.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ModelsTests;

[TestClass]
[DoNotParallelize]
public class AiServiceBackupTests
{
    [TestMethod]
    public void LegacyOnlyServiceDryRunIsReadOnlyAndTheArchiveContainsOnlyEncryptedProfiles()
    {
        using var fixture = new BackupFixture();
        string dataDirectory = Path.Combine(fixture.Source, "AiHub");
        var store = new AiHubSettingsStore(dataDirectory);
        var config = AiHubConfig.CreateDefault();
        config.Targets[0].Model = "fixture-legacy-only-model";
        config.Targets[0].ApiKey = "fixture-legacy-only-key";
        store.KeyStore.SetSecret("target_key_Main", config.Targets[0].ApiKey);
        string legacyPath = Path.Combine(dataDirectory, "settings.json");
        byte[] legacy = JsonSerializer.SerializeToUtf8Bytes(config, AiHubJsonContext.Default.AiHubConfig);
        File.WriteAllBytes(legacyPath, legacy);
        byte[] secrets = File.ReadAllBytes(Path.Combine(dataDirectory, "secrets.dat"));
        var utility = SettingsBackupAndRestoreUtils.Instance;

        Assert.IsTrue(utility.BackupSettings(fixture.Source, fixture.Backups, true).Success);
        CollectionAssert.AreEqual(legacy, File.ReadAllBytes(legacyPath));
        CollectionAssert.AreEqual(secrets, File.ReadAllBytes(Path.Combine(dataDirectory, "secrets.dat")));
        Assert.IsFalse(File.Exists(store.SettingsFilePath));
        Assert.IsFalse(File.Exists(Path.Combine(dataDirectory, ".settings-transaction.dat")));

        Assert.IsTrue(utility.BackupSettings(fixture.Source, fixture.Backups, false).Success);
        CollectionAssert.AreEqual(legacy, File.ReadAllBytes(legacyPath));
        CollectionAssert.AreEqual(secrets, File.ReadAllBytes(Path.Combine(dataDirectory, "secrets.dat")));
        Assert.IsFalse(File.Exists(store.SettingsFilePath));
        using var archive = ZipFile.OpenRead(Directory.GetFiles(fixture.Backups, "*.ptb").Single());
        Assert.IsFalse(archive.Entries.Any(entry => entry.FullName.Replace('\\', '/') == "AiHub/settings.json"));
        var preferenceEntry = archive.Entries.Single(entry => entry.FullName.Replace('\\', '/') == "AiHub/service-settings.json");
        using var reader = new StreamReader(preferenceEntry.Open());
        Assert.IsFalse(reader.ReadToEnd().Contains("fixture-legacy-only", StringComparison.Ordinal));
        Assert.IsTrue(archive.Entries.Any(entry => entry.FullName.Replace('\\', '/') == "AiHub/secrets.dat"));
    }

    [TestMethod]
    public void DryRunDoesNotRecoverOrRemoveAPendingServiceTransaction()
    {
        using var fixture = new BackupFixture();
        var store = new AiHubSettingsStore(Path.Combine(fixture.Source, "AiHub"));
        store.Save(AiHubConfig.CreateDefault());
        byte[] preferences = File.ReadAllBytes(store.SettingsFilePath);
        byte[] secrets = File.ReadAllBytes(Path.Combine(store.DataDirectory, "secrets.dat"));
        string journalPath = Path.Combine(store.DataDirectory, ".settings-transaction.dat");
        byte[] journal = { 1, 2, 3, 4 };
        File.WriteAllBytes(journalPath, journal);

        Assert.AreEqual("Error", SettingsBackupAndRestoreUtils.Instance.BackupSettings(fixture.Source, fixture.Backups, true).Severity);
        CollectionAssert.AreEqual(preferences, File.ReadAllBytes(store.SettingsFilePath));
        CollectionAssert.AreEqual(secrets, File.ReadAllBytes(Path.Combine(store.DataDirectory, "secrets.dat")));
        CollectionAssert.AreEqual(journal, File.ReadAllBytes(journalPath));
    }

    [TestMethod]
    public void EncryptedProfilesAreBackedUpAndRestoredWithoutDuplicateBackupsForUnchangedContent()
    {
        using var fixture = new BackupFixture();
        var store = new AiHubSettingsStore(Path.Combine(fixture.Source, "AiHub"));
        var config = AiHubConfig.CreateDefault();
        config.Targets[0].BaseUrl = "https://fixture-backup.invalid/v1";
        config.Targets[0].Model = "fixture-backup-model";
        config.Targets[0].ApiKey = "fixture-backup-key";
        store.Save(config);
        var utility = SettingsBackupAndRestoreUtils.Instance;

        Assert.IsTrue(utility.BackupSettings(fixture.Source, fixture.Backups, false).Success);
        string archivePath = Directory.GetFiles(fixture.Backups, "*.ptb").Single();
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            Assert.IsTrue(archive.Entries.Any(entry => entry.FullName.Replace('\\', '/') == "AiHub/secrets.dat"));
            var settings = archive.Entries.Single(entry => entry.FullName.Replace('\\', '/') == "AiHub/service-settings.json");
            using var reader = new StreamReader(settings.Open());
            string json = reader.ReadToEnd();
            Assert.IsFalse(json.Contains("fixture-backup", StringComparison.Ordinal));
            Assert.IsFalse(json.Contains("\"targets\"", StringComparison.Ordinal));
        }

        store.Save(config); // DPAPI ciphertext and transaction identity both change.
        Assert.AreEqual("General_SettingsBackupAndRestore_NothingToBackup", utility.BackupSettings(fixture.Source, fixture.Backups, true).Message);
        store.Update(current => current.Targets[0].Model = "fixture-modified-model");
        Assert.IsTrue(utility.BackupSettings(fixture.Source, fixture.Backups, true).Success);

        Assert.AreEqual("Success", utility.RestoreSettings(fixture.Source, fixture.Backups).Severity);
        var restored = store.Load();
        Assert.AreEqual("fixture-backup-model", restored.Targets[0].Model);
        Assert.AreEqual("fixture-backup-key", restored.Targets[0].ApiKey);
        Assert.IsFalse(File.ReadAllText(store.SettingsFilePath).Contains("fixture-backup", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MissingEncryptedPayloadRejectsRestoreBeforeOrdinarySettingsChange()
    {
        using var fixture = new BackupFixture();
        var store = new AiHubSettingsStore(Path.Combine(fixture.Source, "AiHub"));
        store.Save(AiHubConfig.CreateDefault());
        var snapshot = store.ExportBackup();
        Directory.CreateDirectory(Path.Combine(fixture.ArchiveSource, "AiHub"));
        File.WriteAllBytes(Path.Combine(fixture.ArchiveSource, "AiHub", "service-settings.json"), snapshot.SettingsJson);
        File.WriteAllText(Path.Combine(fixture.ArchiveSource, "settings.json"), "{\"fixture\":\"archive-value\"}");
        fixture.WriteArchive();

        string currentPath = Path.Combine(fixture.Source, "settings.json");
        byte[] before = File.ReadAllBytes(currentPath);
        Assert.AreEqual("Error", SettingsBackupAndRestoreUtils.Instance.RestoreSettings(fixture.Source, fixture.Backups).Severity);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(currentPath));
    }

    [TestMethod]
    public void LegacyPlaintextBackupReplacesNewerProfilesAndIsImmediatelyEncrypted()
    {
        using var fixture = new BackupFixture();
        var store = new AiHubSettingsStore(Path.Combine(fixture.Source, "AiHub"));
        var current = AiHubConfig.CreateDefault();
        current.Targets[0].Model = "fixture-current-model";
        current.Targets[0].ApiKey = "fixture-current-key";
        store.Save(current);
        var old = AiHubConfig.CreateDefault();
        old.Targets[0].Model = "fixture-legacy-model";
        Directory.CreateDirectory(Path.Combine(fixture.ArchiveSource, "AiHub"));
        File.WriteAllBytes(Path.Combine(fixture.ArchiveSource, "AiHub", "service-settings.json"), JsonSerializer.SerializeToUtf8Bytes(old, AiHubJsonContext.Default.AiHubConfig));
        fixture.WriteArchive();

        Assert.AreEqual("Success", SettingsBackupAndRestoreUtils.Instance.RestoreSettings(fixture.Source, fixture.Backups).Severity);
        Assert.AreEqual("fixture-legacy-model", store.Load().Targets[0].Model);
        Assert.AreEqual("fixture-current-key", store.Load().Targets[0].ApiKey);
        Assert.IsFalse(File.ReadAllText(store.SettingsFilePath).Contains("fixture-legacy-model", StringComparison.Ordinal));
        Assert.AreEqual(2, store.Load().Targets.Count);
    }

    private sealed class BackupFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "KitAiServiceBackupTests_" + Guid.NewGuid().ToString("N"));
        private readonly string originalTemp = Environment.GetEnvironmentVariable("TEMP");
        private readonly string originalTmp = Environment.GetEnvironmentVariable("TMP");

        public BackupFixture()
        {
            Source = Path.Combine(root, "source");
            Backups = Path.Combine(root, "backups");
            ArchiveSource = Path.Combine(root, "archive");
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Backups);
            Directory.CreateDirectory(ArchiveSource);
            string temp = Path.Combine(root, "temp");
            Directory.CreateDirectory(temp);
            Environment.SetEnvironmentVariable("TEMP", temp);
            Environment.SetEnvironmentVariable("TMP", temp);
            File.WriteAllText(Path.Combine(Source, "settings.json"), "{\"fixture\":\"original-value\"}");
        }

        public string Source { get; }

        public string Backups { get; }

        public string ArchiveSource { get; }

        public void WriteArchive()
        {
            File.WriteAllText(Path.Combine(ArchiveSource, "manifest.json"), "{\"BackupSource\":\"fixture\"}");
            ZipFile.CreateFromDirectory(ArchiveSource, Path.Combine(Backups, "Kit_settings_20261109_120000.ptb"));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TEMP", originalTemp);
            Environment.SetEnvironmentVariable("TMP", originalTmp);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
