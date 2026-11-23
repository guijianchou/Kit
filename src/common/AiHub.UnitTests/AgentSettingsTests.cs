namespace Kit.AiHub.UnitTests;

using System.IO;
using System.Text;
using Kit.AiHub.Models;
using Kit.AiHub.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AgentSettingsTests
{
    [TestMethod]
    [DataRow("codex")]
    [DataRow("pi")]
    public void WorkerPreferencesSurviveCacheUnrelatedSaveAndBackup(string kernel)
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("source"));
        var config = AiHubConfig.CreateDefault();
        config.SelectedKernel = kernel;
        config.EnableSubagents = false;
        config.MaxAgentsPerTask = 4;
        config.MaxConcurrentAnalysis = 3;
        config.Targets[0].ApiKey = "fixture-secret";
        store.Save(config);

        Assert.IsFalse(store.Load().EnableSubagents);
        Assert.AreEqual(4, store.Load().MaxAgentsPerTask);
        store.Update(saved => saved.RetentionDays = 14);
        var reopened = new AiHubSettingsStore(store.DataDirectory);
        Assert.IsFalse(reopened.Load().EnableSubagents);
        Assert.AreEqual(4, reopened.Load().MaxAgentsPerTask);

        AiHubSettingsBackup backup = reopened.ExportBackup()!;
        Assert.IsNotNull(backup);
        Assert.IsFalse(Encoding.UTF8.GetString(backup.SettingsJson).Contains("fixture-secret", System.StringComparison.Ordinal));
        var restored = new AiHubSettingsStore(fixture.PathFor("restored"));
        restored.ImportBackup(backup);
        Assert.IsFalse(restored.Load().EnableSubagents);
        Assert.AreEqual(4, restored.Load().MaxAgentsPerTask);
        Assert.AreEqual(3, restored.Load().MaxConcurrentAnalysis);
        Assert.AreEqual(kernel, restored.Load().SelectedKernel);
        Assert.AreEqual("fixture-secret", restored.Load().Targets[0].ApiKey);
    }

    [TestMethod]
    public void OlderPublicPreferencesReceiveWorkerDefaultsWithoutRewritingFiles()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        store.Save(AiHubConfig.CreateDefault());
        string json = File.ReadAllText(store.SettingsFilePath);
        var document = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        document.Remove("enableSubagents");
        document.Remove("maxAgentsPerTask");
        string legacy = document.ToJsonString();
        File.WriteAllText(store.SettingsFilePath, legacy);

        var loaded = new AiHubSettingsStore(store.DataDirectory).Load();
        Assert.IsTrue(loaded.EnableSubagents);
        Assert.AreEqual(3, loaded.MaxAgentsPerTask);
        Assert.AreEqual(2, loaded.MaxConcurrentAnalysis);
        Assert.AreEqual(legacy, File.ReadAllText(store.SettingsFilePath));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(5)]
    public void InvalidWorkerLimitsDoNotOverwriteSavedSettings(int limit)
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        store.Save(AiHubConfig.CreateDefault());
        byte[] before = File.ReadAllBytes(store.SettingsFilePath);
        Assert.ThrowsExactly<InvalidDataException>(() => store.Update(config => config.MaxAgentsPerTask = limit));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(store.SettingsFilePath));
    }
}
