// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Kit.AIHubLib.Storage;

using System;
using System.IO;
using System.Text.Json;

/// <summary>
/// The audit schedule owned by the AI Hub plugin settings file, which the Settings page
/// writes and the headless worker reads. Missing or malformed values disable scheduling.
/// </summary>
public sealed record PluginScheduleSettings(int ScanIntervalHours, int AuditModeIndex)
{
    /// <summary>File name of the plugin settings inside the AI Hub data directory.</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>
    /// Reads the schedule from the plugin settings file inside the given AI Hub data directory.
    /// </summary>
    public static PluginScheduleSettings Read(string dataDirectory)
    {
        try
        {
            string path = Path.Combine(dataDirectory, SettingsFileName);
            if (!File.Exists(path))
            {
                return new(0, 0);
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("properties", out JsonElement properties))
            {
                return new(0, 0);
            }

            return new(
                ReadNumber(properties, "scanIntervalHours"),
                ReadNumber(properties, "auditMode"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(0, 0);
        }
    }

    private static int ReadNumber(JsonElement properties, string name)
    {
        return properties.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Object
            && property.TryGetProperty("value", out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            ? number
            : 0;
    }
}
