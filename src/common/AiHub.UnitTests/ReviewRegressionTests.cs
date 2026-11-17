namespace Kit.AiHub.UnitTests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kit.AiHub.Engine;
using Kit.AiHub.Models;
using Kit.AiHub.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class ReviewRegressionTests
{
    [TestMethod]
    public void UnreadableCredentialsRestoreOnlyPublicPreferencesWhenExplicitlyRequested()
    {
        using var fixture = new FixtureDirectory();
        var source = new AiHubSettingsStore(fixture.PathFor("source"));
        var config = AiHubConfig.CreateDefault();
        config.RetentionDays = 17;
        config.IsEnabled = true;
        config.Targets[0].ApiKey = "fixture-sensitive-value";
        source.Save(config);
        File.WriteAllBytes(Path.Combine(source.DataDirectory, "secrets.dat"), new byte[] { 1, 2, 3 });

        Assert.ThrowsExactly<InvalidDataException>(() => AiHubSettingsStore.ReadBackup(source.DataDirectory));
        var snapshot = AiHubSettingsStore.ReadBackup(source.DataDirectory, skipUnreadableCredentials: true, out bool skipped);
        Assert.IsTrue(skipped);
        Assert.IsNotNull(snapshot);

        var destination = new AiHubSettingsStore(fixture.PathFor("destination"));
        var existing = AiHubConfig.CreateDefault();
        existing.Targets[0].ApiKey = "destination-key";
        existing.Targets[0].BaseUrl = "https://destination.invalid/v1";
        existing.Targets[0].Model = "destination-model";
        destination.Save(existing);
        destination.KeyStore.SetSecret("unrelated-secret", "preserve-me");
        destination.ImportBackup(snapshot);
        var restored = destination.Load();
        Assert.AreEqual(17, restored.RetentionDays);
        Assert.IsFalse(restored.IsEnabled);
        Assert.AreEqual("destination-key", restored.Targets[0].ApiKey);
        Assert.AreEqual("https://destination.invalid/v1", restored.Targets[0].BaseUrl);
        Assert.AreEqual("destination-model", restored.Targets[0].Model);
        Assert.AreEqual("preserve-me", destination.KeyStore.GetSecret("unrelated-secret"));
    }

    [TestMethod]
    public async Task StaleRequestCleanupPreservesActiveRecentAndUnrelatedDirectories()
    {
        using var fixture = new FixtureDirectory();
        string root = fixture.PathFor("requests");
        Directory.CreateDirectory(root);
        string stale = Path.Combine(root, "request-" + Guid.NewGuid().ToString("N"));
        string active = Path.Combine(root, "request-" + Guid.NewGuid().ToString("N"));
        string recent = Path.Combine(root, "request-" + Guid.NewGuid().ToString("N"));
        string unrelated = Path.Combine(root, "request-unrelated");
        foreach (string path in new[] { stale, active, recent, unrelated })
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "prompt.txt"), "fixture prompt");
        }

        using (var lease = new FileStream(Path.Combine(active, ".active"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            foreach (string path in new[] { stale, active, unrelated })
            {
                Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
            }

            await RouteDispatcher.CleanupStaleRequestsAsync(root);
            Assert.IsFalse(Directory.Exists(stale));
            Assert.IsTrue(Directory.Exists(active));
            Assert.IsTrue(Directory.Exists(recent));
            Assert.IsTrue(Directory.Exists(unrelated));
        }
    }

    [TestMethod]
    public async Task UpdatingOnlySecretsNotifiesTheRunningEngine()
    {
        using var fixture = new FixtureDirectory();
        using var engine = AiHubEngine.Create(fixture.RootPath, fixture.PathFor("packages"));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.StateChanged += (_, _) => changed.TrySetResult();
        new SecureKeyStore(fixture.RootPath).SetSecret("fixture-key", "fixture-value");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
