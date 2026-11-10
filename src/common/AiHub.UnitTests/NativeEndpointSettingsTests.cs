namespace Kit.AiHub.UnitTests;

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kit.AiHub.Models;
using Kit.AiHub.Serialization;
using Kit.AiHub.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class NativeEndpointSettingsTests
{
    [TestMethod]
    public void ProfilesWithoutContextWindowsKeepTheExistingDefault()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        config.Targets[0].ApiKey = "fixture-main-key";
        store.Save(config);
        var document = JsonNode.Parse(JsonSerializer.Serialize(config, AiHubJsonContext.Default.AiHubConfig))!.AsObject();
        foreach (var target in document["targets"]!.AsArray())
        {
            target!.AsObject().Remove("contextWindowTokens");
        }

        File.WriteAllText(store.SettingsFilePath, document.ToJsonString());

        var loaded = store.Load();

        Assert.AreEqual(256000, loaded.Targets[0].ContextWindowTokens);
        Assert.AreEqual(256000, loaded.Targets[1].ContextWindowTokens);
        Assert.AreEqual("fixture-main-key", loaded.Targets[0].ApiKey);
        Assert.IsFalse(File.ReadAllText(store.SettingsFilePath).Contains("targets", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IndependentContextWindowsSurviveCloneSaveAndUnrelatedUpdates()
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        config.Targets[0].ContextWindowTokens = 512000;
        config.Targets[1].ContextWindowTokens = 128000;
        var mainClone = config.Targets[0].Clone();
        Assert.AreEqual(512000, mainClone.ContextWindowTokens);
        mainClone.ContextWindowTokens = 64000;
        Assert.AreEqual(512000, config.Targets[0].ContextWindowTokens);
        store.Save(config);
        config.Targets[0].ContextWindowTokens = 32000;

        var reopened = new AiHubSettingsStore(store.DataDirectory);
        reopened.Update(current => current.RetentionDays = 14);
        var loaded = reopened.Load();

        Assert.AreEqual(512000, loaded.Targets[0].ContextWindowTokens);
        Assert.AreEqual(128000, loaded.Targets[1].ContextWindowTokens);
        var persisted = new AiHubSettingsStore(store.DataDirectory).Load();
        Assert.AreEqual(512000, persisted.Targets[0].ContextWindowTokens);
        Assert.AreEqual(128000, persisted.Targets[1].ContextWindowTokens);
    }

    [TestMethod]
    [DataRow("openai-responses", "xhigh")]
    [DataRow("anthropic-messages", "medium")]
    [DataRow("future_api.v2", "native-effort.v2")]
    public void NativeIdentifiersSurviveBothRoutesWithoutCapabilityCoercion(string mode, string effort)
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        foreach (var target in config.Targets)
        {
            target.Mode = mode;
            target.Effort = effort;
        }

        store.Save(config);
        store.Update(current => current.SelectedKernel = AiKernelCatalog.Pi);
        var loaded = store.Load();

        foreach (var target in loaded.Targets)
        {
            Assert.AreEqual(mode, target.Mode);
            Assert.AreEqual(effort, target.Effort);
        }
    }

    [TestMethod]
    [DataRow(1024)]
    [DataRow(int.MaxValue)]
    public void ContextWindowDoesNotImposeModelSpecificLimits(int tokens)
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        config.Targets[0].ContextWindowTokens = tokens;
        store.Save(config);
        Assert.AreEqual(tokens, store.Load().Targets[0].ContextWindowTokens);
    }

    [TestMethod]
    [DataRow("context-negative")]
    [DataRow("context-zero")]
    [DataRow("context-too-small")]
    [DataRow("mode-empty")]
    [DataRow("mode-leading-option")]
    [DataRow("mode-newline")]
    [DataRow("mode-long")]
    [DataRow("effort-space")]
    [DataRow("effort-long")]
    public void InvalidNativeSettingsDoNotOverwriteSettingsOrCredentials(string scenario)
    {
        using var fixture = new FixtureDirectory();
        var store = new AiHubSettingsStore(fixture.PathFor("data"));
        var config = AiHubConfig.CreateDefault();
        config.Targets[0].ApiKey = "fixture-main-key";
        store.Save(config);
        byte[] settings = File.ReadAllBytes(store.SettingsFilePath);
        string secretsPath = Path.Combine(store.DataDirectory, "secrets.dat");
        byte[] secrets = File.ReadAllBytes(secretsPath);
        var target = config.Targets[0];
        switch (scenario)
        {
            case "context-negative":
                target.ContextWindowTokens = -1;
                break;
            case "context-zero":
                target.ContextWindowTokens = 0;
                break;
            case "context-too-small":
                target.ContextWindowTokens = 1023;
                break;
            case "mode-empty":
                target.Mode = string.Empty;
                break;
            case "mode-leading-option":
                target.Mode = "--override";
                break;
            case "mode-newline":
                target.Mode = "responses\nother";
                break;
            case "mode-long":
                target.Mode = new string('a', 129);
                break;
            case "effort-space":
                target.Effort = "high other";
                break;
            case "effort-long":
                target.Effort = new string('a', 129);
                break;
            default:
                throw new ArgumentException("Unknown fixture scenario.", nameof(scenario));
        }

        Assert.ThrowsExactly<InvalidDataException>(() => store.Save(config));
        CollectionAssert.AreEqual(settings, File.ReadAllBytes(store.SettingsFilePath));
        CollectionAssert.AreEqual(secrets, File.ReadAllBytes(secretsPath));
        Assert.AreEqual("fixture-main-key", store.Load().Targets[0].ApiKey);
    }
}
