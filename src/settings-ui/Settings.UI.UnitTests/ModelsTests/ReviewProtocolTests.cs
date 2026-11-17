// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Text.Json;
using Kit.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Settings.UI.UnitTests.ModelsTests;

[TestClass]
public sealed class ReviewProtocolTests
{
    [TestMethod]
    public void AiHubConfigurationUsesTheRunnerModuleRoute()
    {
        using var payload = JsonDocument.Parse(new SndAIHubSettings(new AIHubSettings()).ToModuleJsonString());
        Assert.IsTrue(payload.RootElement.TryGetProperty("kit", out var modules));
        Assert.IsTrue(modules.TryGetProperty("AIHub", out var settings));
        Assert.AreEqual(JsonValueKind.Object, settings.ValueKind);
    }

    [TestMethod]
    public void ModuleChangesNotifyBothPagesAndUnsubscribeIndependently()
    {
        var settings = new GeneralSettings();
        int launchCount = 0;
        int appsCount = 0;
        Action launch = () => launchCount++;
        Action apps = () => appsCount++;
        settings.AddEnabledModuleChangeNotification(launch);
        settings.AddEnabledModuleChangeNotification(apps);
        settings.Enabled.Awake = !settings.Enabled.Awake;
        Assert.AreEqual(1, launchCount);
        Assert.AreEqual(1, appsCount);

        settings.RemoveEnabledModuleChangeNotification(apps);
        settings.Enabled.Awake = !settings.Enabled.Awake;
        Assert.AreEqual(2, launchCount);
        Assert.AreEqual(1, appsCount);
    }
}
