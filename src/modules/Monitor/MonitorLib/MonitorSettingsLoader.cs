// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;

namespace Microsoft.PowerToys.Monitor;

/// <summary>
/// Loads Monitor settings saved by the Kit Settings UI.
/// </summary>
public static class MonitorSettingsLoader
{
    /// <summary>
    /// Loads Monitor settings from a PowerToys-style module settings file.
    /// </summary>
    /// <param name="settingsPath">Path to the Monitor settings JSON file.</param>
    /// <returns>Loaded settings, or defaults when the file is unavailable or invalid.</returns>
    public static MonitorSettings LoadOrDefault(string? settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
        {
            return MonitorSettings.CreateDefault();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(settingsPath).Trim('\0'));
            if (!document.RootElement.TryGetProperty("properties", out JsonElement properties))
            {
                return MonitorSettings.CreateDefault();
            }

            return MonitorSettings.Create(
                downloadsPath: ReadString(properties, "downloadsPath"),
                csvPath: ReadString(properties, "csvFileName"),
                intervalSeconds: ReadInt(properties, "scanIntervalSeconds"),
                maxFileSizeForHashMb: ReadInt(properties, "maxFileSizeMegabytes"),
                hashAlgorithm: ReadString(properties, "hashAlgorithm"),
                incrementalScan: ReadBool(properties, "useIncrementalHashing"),
                runInBackground: ReadBool(properties, "runInBackground"),
                autoOrganize: ReadBool(properties, "organizeDownloads"),
                autoCleanInstallers: ReadBool(properties, "cleanInstallers"));
        }
        catch (IOException)
        {
            return MonitorSettings.CreateDefault();
        }
        catch (UnauthorizedAccessException)
        {
            return MonitorSettings.CreateDefault();
        }
        catch (JsonException)
        {
            return MonitorSettings.CreateDefault();
        }
    }

    private static string? ReadString(JsonElement properties, string propertyName)
    {
        if (!TryGetValue(properties, propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static int? ReadInt(JsonElement properties, string propertyName)
    {
        if (!TryGetValue(properties, propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt32(out int result) ? result : null;
    }

    private static bool? ReadBool(JsonElement properties, string propertyName)
    {
        if (!TryGetValue(properties, propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static bool TryGetValue(JsonElement properties, string propertyName, out JsonElement value)
    {
        value = default;
        if (!properties.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.Object ||
            !property.TryGetProperty("value", out value))
        {
            return false;
        }

        return true;
    }
}
