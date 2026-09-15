using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalServerHub.Core.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<AppThemePreference>))]
public enum AppThemePreference
{
    System,
    Light,
    Dark,
}

public sealed record WindowPlacementSettings
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool IsMaximized { get; init; }
}

public sealed record HubSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public AppThemePreference Theme { get; init; } = AppThemePreference.System;

    public bool MinimizeToTray { get; init; }

    public int LogRingBufferLines { get; init; } = 1000;
    public int LogRotationSizeMB { get; init; } = 10;
    public int LogRetentionCopies { get; init; } = 5;
    public string LogDirectory { get; init; } = "logs";

    public WindowPlacementSettings? WindowPlacement { get; init; }

    public HubSettings Normalize() => this with
    {
        Version = CurrentVersion,
        Theme = Theme is AppThemePreference.System or AppThemePreference.Light or AppThemePreference.Dark
            ? Theme
            : AppThemePreference.System,
        LogRingBufferLines = LogRingBufferLines is > 0 and <= 100000 ? LogRingBufferLines : 1000,
        LogRotationSizeMB = LogRotationSizeMB is > 0 and <= 1000 ? LogRotationSizeMB : 10,
        LogRetentionCopies = LogRetentionCopies is > 0 and <= 100 ? LogRetentionCopies : 5,
        LogDirectory = string.IsNullOrWhiteSpace(LogDirectory) ? "logs" : LogDirectory.Trim(),
        WindowPlacement = NormalizeWindowPlacement(WindowPlacement),
    };

    private static WindowPlacementSettings? NormalizeWindowPlacement(WindowPlacementSettings? placement)
    {
        const int maximumCoordinateMagnitude = 1_000_000;
        const int maximumExtent = 100_000;
        if (placement is null
            || placement.X is < -maximumCoordinateMagnitude or > maximumCoordinateMagnitude
            || placement.Y is < -maximumCoordinateMagnitude or > maximumCoordinateMagnitude
            || placement.Width is <= 0 or > maximumExtent
            || placement.Height is <= 0 or > maximumExtent)
        {
            return null;
        }

        return placement;
    }
}

public sealed class HubSettingsStore
{
    public const string SettingsFileName = "hub.settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public HubSettingsStore(string? settingsDirectory = null)
    {
        SettingsPath = Path.Combine(
            Path.GetFullPath(settingsDirectory ?? AppContext.BaseDirectory),
            SettingsFileName);
    }

    public string SettingsPath { get; }
    public event EventHandler<HubSettings>? Changed;

    public async Task<HubSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<HubSettings> ReadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new HubSettings();
            }

            await using FileStream stream = new(
                SettingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4_096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            HubSettings? settings = await JsonSerializer.DeserializeAsync<HubSettings>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            return (settings ?? new HubSettings()).Normalize();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Settings file '{SettingsPath}' is not valid JSON.", exception);
        }
    }

    public async Task SaveAsync(HubSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteCoreAsync(settings, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<HubSettings> UpdateAsync(Func<HubSettings, HubSettings> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HubSettings current = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            HubSettings updated = update(current).Normalize();
            await WriteCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }

    private async Task WriteCoreAsync(HubSettings settings, CancellationToken cancellationToken)
    {
        HubSettings normalized = settings.Normalize();
        string temporaryPath = $"{SettingsPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4_096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    normalized,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, SettingsPath, overwrite: true);
            Changed?.Invoke(this, normalized);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

