using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalServerHub.Core.Models;

namespace LocalServerHub.Core.Configuration;

/// <summary>Root shape of services.json.</summary>
public sealed record ServiceCatalog
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public int Version { get; init; } = 2;

    public IReadOnlyList<ServiceDefinition> Services { get; init; } = [];
}

/// <summary>
/// Loads and saves the service catalog (plan.md §3.4).
/// </summary>
/// <remarks>
/// Writes are atomic: a temp file is flushed and then moved over the target, so a
/// crash mid-save leaves the previous catalog intact rather than a truncated one.
/// The previous version is rotated into backups/ first, keeping the last ten.
/// </remarks>
public sealed class ServiceCatalogStore
{
    private const int BackupsToKeep = 10;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            // Parameter defaults and preset values are object?; without this they
            // arrive as JsonElement and boolean switches silently never fire.
            new JsonPrimitiveConverter(),
        },
    };

    private readonly string _catalogPath;
    private readonly string _dropInDirectory;
    private readonly string _backupDirectory;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SaveGates = new(StringComparer.OrdinalIgnoreCase);

    public ServiceCatalogStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        DataDirectory = Path.GetFullPath(dataDirectory);
        _catalogPath = Path.Combine(DataDirectory, "services.json");
        _dropInDirectory = Path.Combine(DataDirectory, "services.d");
        _backupDirectory = Path.Combine(DataDirectory, "backups");
    }

    public string DataDirectory { get; }

    public string CatalogPath => _catalogPath;
    public string? RecoverySource { get; private set; }

    public static string GetChangeStamp(string catalogPath)
    {
        string dropIns = Path.Combine(Path.GetDirectoryName(catalogPath)!, "services.d");
        IEnumerable<string> paths = new[] { catalogPath };
        if (Directory.Exists(dropIns)) paths = paths.Concat(Directory.EnumerateFiles(dropIns, "*.json"));
        return string.Join("\n", paths.Order(StringComparer.Ordinal).Select(path =>
        {
            FileInfo file = new(path);
            return file.Exists ? $"{path}|{file.LastWriteTimeUtc.Ticks}|{file.Length}" : $"{path}|missing";
        }));
    }

    /// <summary>
    /// Reads services.json and merges every services.d/*.json on top of it. A
    /// drop-in file with an existing id replaces that service, which is what makes
    /// one-file-per-service sharing work.
    /// </summary>
    public async Task<ServiceCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        RecoverySource = null;
        ServiceCatalog root;
        try
        {
            root = File.Exists(_catalogPath)
                ? await ReadCatalogAsync(_catalogPath, cancellationToken).ConfigureAwait(false)
                : new ServiceCatalog();
        }
        catch (InvalidDataException)
        {
            root = await ReadBackupAsync(cancellationToken).ConfigureAwait(false);
        }

        Dictionary<string, ServiceDefinition> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach (ServiceDefinition service in root.Services)
        {
            merged[service.Id] = service;
        }

        if (Directory.Exists(_dropInDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(_dropInDirectory, "*.json").Order(StringComparer.Ordinal))
            {
                ServiceCatalog dropIn = await ReadCatalogAsync(file, cancellationToken).ConfigureAwait(false);
                foreach (ServiceDefinition service in dropIn.Services)
                {
                    merged[service.Id] = service;
                }
            }
        }

        return root with { Services = [.. merged.Values] };
    }

    private async Task<ServiceCatalog> ReadBackupAsync(CancellationToken cancellationToken)
    {
        if (Directory.Exists(_backupDirectory))
        {
            foreach (string backup in Directory.EnumerateFiles(_backupDirectory, "services.*.json").OrderDescending(StringComparer.Ordinal))
            {
                try
                {
                    ServiceCatalog catalog = await ReadCatalogAsync(backup, cancellationToken).ConfigureAwait(false);
                    RecoverySource = backup;
                    return catalog;
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException) { }
            }
        }
        throw new InvalidDataException("services.json is invalid and no valid backup is available.");
    }

    /// <summary>
    /// Loads the catalog and migrates plaintext secrets into DPAPI-encrypted storage.
    /// This is the entry point used by the Windows App layer; Core's LoadAsync stays
    /// platform-neutral.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public async Task<ServiceCatalog> LoadAndMigrateSecretsAsync(
        SecretStore secretStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        SemaphoreSlim saveGate = SaveGates.GetOrAdd(_catalogPath, static _ => new SemaphoreSlim(1, 1));
        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadAndMigrateSecretsCoreAsync(secretStore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            saveGate.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<ServiceCatalog> LoadAndMigrateSecretsCoreAsync(
        SecretStore secretStore,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(_backupDirectory))
        {
            foreach (string backup in Directory.EnumerateFiles(_backupDirectory, "services.*.json"))
            {
                try { await MigrateFileAsync(backup, secretStore, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { }
            }
        }

        await LoadAsync(cancellationToken).ConfigureAwait(false);

        // Migrate each source file in place. Migrating the merged catalog instead would
        // write an encrypted copy of a drop-in's secret into services.json and leave the
        // plaintext sitting in services.d/, which defeats the point.
        if (RecoverySource is null && File.Exists(_catalogPath))
        {
            await MigrateFileAsync(_catalogPath, secretStore, cancellationToken).ConfigureAwait(false);
        }

        if (Directory.Exists(_dropInDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(_dropInDirectory, "*.json").Order(StringComparer.Ordinal))
            {
                await MigrateFileAsync(file, secretStore, cancellationToken).ConfigureAwait(false);
            }
        }

        // Re-read so the returned catalog reflects the rewritten references.
        return await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private async Task MigrateFileAsync(
        string path,
        SecretStore secretStore,
        CancellationToken cancellationToken)
    {
        ServiceCatalog source;
        try
        {
            source = await ReadCatalogAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // A malformed drop-in is LoadAsync's problem to surface, not the
            // migration's to crash on.
            return;
        }

        if (!MigrateSecretsToStore(source, secretStore, out ServiceCatalog rewritten))
        {
            return;
        }

        // The encrypted store is durable already; never back up the plaintext source.
        await WriteCatalogAsync(path, rewritten, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(ServiceCatalog catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ServiceCatalogValidator.Validate(catalog);

        SemaphoreSlim saveGate = SaveGates.GetOrAdd(_catalogPath, static _ => new SemaphoreSlim(1, 1));
        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(catalog, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            saveGate.Release();
        }
    }

    /// <summary>
    /// Applies an update to the latest merged catalog while holding the same
    /// per-file gate used by SaveAsync. This prevents a stale load-modify-save
    /// sequence from overwriting a newer UI change.
    /// </summary>
    /// <remarks>
    /// The result is written back to the file each service came from, not wholesale
    /// into services.json. Writing the merged catalog to the root file would copy
    /// every drop-in definition into it while leaving the original in place, so the
    /// same id would then have two sources and edits would appear to be lost —
    /// the drop-in still wins the merge. This mirrors how secret migration handles
    /// each source file in place.
    /// </remarks>
    public async Task<ServiceCatalog> UpdateAsync(
        Func<ServiceCatalog, ServiceCatalog> update,
        CancellationToken cancellationToken = default,
        ServiceCatalog? expectedCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(update);

        SemaphoreSlim saveGate = SaveGates.GetOrAdd(_catalogPath, static _ => new SemaphoreSlim(1, 1));
        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ServiceCatalog current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (expectedCatalog is not null && !JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(expectedCatalog, SerializerOptions),
                    JsonSerializer.SerializeToElement(current, SerializerOptions)))
            {
                throw new InvalidOperationException("The service configuration changed since this page was loaded. Reload Targets before saving; your edits have not been written.");
            }
            ServiceCatalog updated = update(current);
            ArgumentNullException.ThrowIfNull(updated);
            ServiceCatalogValidator.Validate(updated);
            await SaveByOriginAsync(updated, cancellationToken).ConfigureAwait(false);
            // Source files determine the persisted order used by the next concurrency check.
            return await LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            saveGate.Release();
        }
    }

    /// <summary>
    /// Writes a merged catalog back to the files it came from: each drop-in keeps
    /// only the services it already owned, and everything else lands in
    /// services.json. Ids dropped from the catalog are removed from their origin.
    /// Assumes the caller holds the save gate.
    /// </summary>
    private async Task SaveByOriginAsync(ServiceCatalog catalog, CancellationToken cancellationToken)
    {
        Dictionary<string, ServiceDefinition> remaining =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (ServiceDefinition service in catalog.Services)
        {
            remaining[service.Id] = service;
        }

        if (Directory.Exists(_dropInDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(_dropInDirectory, "*.json").OrderDescending(StringComparer.Ordinal))
            {
                ServiceCatalog dropIn;
                try
                {
                    dropIn = await ReadCatalogAsync(file, cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    // A malformed drop-in is LoadAsync's problem to surface. Leaving it
                    // untouched is safer than rewriting a file we could not parse.
                    continue;
                }

                List<ServiceDefinition> owned = [];
                bool changed = false;
                foreach (ServiceDefinition original in dropIn.Services)
                {
                    if (remaining.Remove(original.Id, out ServiceDefinition? updated))
                    {
                        owned.Add(updated);
                        changed |= !updated.Equals(original);
                    }
                    else
                    {
                        // Deleted through the UI; dropping it here is what makes the
                        // deletion stick instead of reappearing on the next merge.
                        changed = true;
                    }
                }

                if (changed)
                {
                    await WriteCatalogAsync(file, dropIn with { Services = owned }, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        // Whatever no drop-in claimed belongs to the root file, in catalog order so
        // the settings page's reordering survives the round trip.
        List<ServiceDefinition> rootServices =
            [.. catalog.Services.Where(service => remaining.ContainsKey(service.Id))];
        await SaveCoreAsync(catalog with { Services = rootServices }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveCoreAsync(ServiceCatalog catalog, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DataDirectory);
        RotateBackup();

        string tempPath = $"{_catalogPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, catalog, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, _catalogPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Atomically writes a catalog to an arbitrary path. Used for services.d/*.json,
    /// which get no backup rotation of their own — they are expected to be under
    /// version control by whoever shared them.
    /// </summary>
    private static async Task WriteCatalogAsync(
        string path,
        ServiceCatalog catalog,
        CancellationToken cancellationToken)
    {
        string tempPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, catalog, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<ServiceCatalog> ReadCatalogAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.EnumerateObject().Any(p => p.Name.Equals("services", StringComparison.OrdinalIgnoreCase)
                    && p.Value.ValueKind == JsonValueKind.Array))
                throw new InvalidDataException("Catalog root must contain a services array.");
            ServiceCatalog catalog = document.RootElement.Deserialize<ServiceCatalog>(SerializerOptions)
                ?? throw new InvalidDataException("Catalog cannot be null.");
            ServiceCatalogValidator.Validate(catalog);
            return catalog;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} contains invalid JSON or unsupported fields (line {ex.LineNumber}, byte {ex.BytePositionInLine}).");
        }
    }

    private void RotateBackup()
    {
        if (!File.Exists(_catalogPath))
        {
            return;
        }

        Directory.CreateDirectory(_backupDirectory);
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        File.Copy(_catalogPath, Path.Combine(_backupDirectory, $"services.{stamp}.{Guid.NewGuid():N}.json"), overwrite: false);

        string[] backups = [.. Directory
            .EnumerateFiles(_backupDirectory, "services.*.json")
            .OrderByDescending(path => path, StringComparer.Ordinal)];

        foreach (string stale in backups.Skip(BackupsToKeep))
        {
            try
            {
                File.Delete(stale);
            }
            catch (IOException)
            {
                // A locked backup is not worth failing a save over.
            }
        }
    }

    /// <summary>
    /// Detects secret-type parameters holding plaintext values, moves them into the
    /// encrypted store, and rewrites the catalog with ${secret.NAME} references.
    /// Returns true if any migration occurred.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool MigrateSecretsToStore(
        ServiceCatalog catalog,
        SecretStore secretStore,
        out ServiceCatalog rewritten)
    {
        bool anyMigrated = false;
        List<ServiceDefinition> rewrittenServices = new(catalog.Services.Count);

        foreach (ServiceDefinition service in catalog.Services)
        {
            bool serviceMigrated = false;
            List<ParameterDefinition> rewrittenParams = new(service.Parameters.Count);

            foreach (ParameterDefinition param in service.Parameters)
            {
                if (param.Type == ParameterType.Secret
                    && param.Default is string plaintext
                    && !string.IsNullOrWhiteSpace(plaintext)
                    && !plaintext.StartsWith("${", StringComparison.Ordinal))
                {
                    // Each migrated value gets its own reference so older backups
                    // cannot overwrite the secret used by the current definition.
                    string secretName = $"{service.Id}.{param.Name}.{Guid.NewGuid():N}";
                    secretStore.Set(secretName, plaintext);

                    // Replace default value with ${secret.NAME} reference
                    rewrittenParams.Add(param with { Default = $"${{secret.{secretName}}}" });
                    serviceMigrated = true;
                    anyMigrated = true;
                }
                else
                {
                    rewrittenParams.Add(param);
                }
            }

            // Migrate preset overrides for secret-type params
            List<ServicePreset> rewrittenPresets = new(service.Presets.Count);
            foreach (ServicePreset preset in service.Presets)
            {
                bool presetMigrated = false;
                Dictionary<string, object?> rewrittenArgs = new(preset.Args, StringComparer.Ordinal);

                foreach ((string flag, object? value) in preset.Args)
                {
                    ParameterDefinition? paramDef = service.Parameters
                        .FirstOrDefault(p => string.Equals(p.Flag, flag, StringComparison.Ordinal));

                    if (paramDef?.Type == ParameterType.Secret
                        && value is string plaintext
                        && !string.IsNullOrWhiteSpace(plaintext)
                        && !plaintext.StartsWith("${", StringComparison.Ordinal))
                    {
                        string secretName = $"{service.Id}.{preset.Name}.{paramDef.Name}.{Guid.NewGuid():N}";
                        secretStore.Set(secretName, plaintext);
                        rewrittenArgs[flag] = $"${{secret.{secretName}}}";
                        presetMigrated = true;
                        serviceMigrated = true;
                        anyMigrated = true;
                    }
                }

                if (presetMigrated)
                {
                    rewrittenPresets.Add(preset with { Args = rewrittenArgs });
                }
                else
                {
                    rewrittenPresets.Add(preset);
                }
            }

            if (serviceMigrated)
            {
                rewrittenServices.Add(service with
                {
                    Parameters = [.. rewrittenParams],
                    Presets = [.. rewrittenPresets],
                });
            }
            else
            {
                rewrittenServices.Add(service);
            }
        }

        if (anyMigrated)
        {
            secretStore.Save();
            rewritten = catalog with { Services = [.. rewrittenServices] };
            return true;
        }

        rewritten = catalog;
        return false;
    }
}
