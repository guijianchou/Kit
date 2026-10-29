// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using System;
using System.IO;
using Kit.AIHubLib.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// The background worker reads the audit cadence from the plugin settings file that the
/// Settings page owns; these tests pin the tolerant parsing contract.
/// </summary>
[TestClass]
public sealed class PluginScheduleSettingsTests
{
    private string _dataDirectory = string.Empty;

    [TestInitialize]
    public void CreateIsolatedDataDirectory()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "KitPluginScheduleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
    }

    [TestCleanup]
    public void RemoveIsolatedDataDirectory()
    {
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    [TestMethod]
    public void ReadParsesThePluginSettingsFile()
    {
        File.WriteAllText(
            Path.Combine(_dataDirectory, "settings.json"),
            """{"properties":{"retentionDays":{"value":30},"auditMode":{"value":1},"scanIntervalHours":{"value":8}},"name":"AIHub","version":"2.3.4.0"}""");

        var schedule = PluginScheduleSettings.Read(_dataDirectory);

        Assert.AreEqual(8, schedule.ScanIntervalHours);
        Assert.AreEqual(1, schedule.AuditModeIndex);
    }

    [TestMethod]
    public void MissingFileDisablesScheduling()
    {
        var schedule = PluginScheduleSettings.Read(_dataDirectory);

        Assert.AreEqual(0, schedule.ScanIntervalHours);
        Assert.AreEqual(0, schedule.AuditModeIndex);
    }

    [TestMethod]
    public void MalformedJsonFallsBackToDefaults()
    {
        File.WriteAllText(Path.Combine(_dataDirectory, "settings.json"), "{ not valid json");

        var schedule = PluginScheduleSettings.Read(_dataDirectory);

        Assert.AreEqual(0, schedule.ScanIntervalHours);
        Assert.AreEqual(0, schedule.AuditModeIndex);
    }

    [TestMethod]
    public void NonNumericValuesFallBackToZero()
    {
        File.WriteAllText(
            Path.Combine(_dataDirectory, "settings.json"),
            """{"properties":{"auditMode":{"value":"full"},"scanIntervalHours":{"value":8}}}""");

        var schedule = PluginScheduleSettings.Read(_dataDirectory);

        Assert.AreEqual(8, schedule.ScanIntervalHours);
        Assert.AreEqual(0, schedule.AuditModeIndex);
    }
}
