using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalServerHub.Windows.Native;

namespace LocalServerHub.Windows;

/// <summary>
/// Durable identity for a process tree started by LocalServerHub. The named
/// Job Object is the kernel ownership boundary; this record adds PID reuse and
/// service-definition checks when a later Hub instance considers recovery.
/// </summary>
internal sealed record ServiceProcessRecord(
    int ProcessId,
    int ParentProcessId,
    long ProcessStartTimeUtcFileTime,
    string? ProcessImagePath = null);

internal sealed record ServiceOwnershipRecord(
    string ServiceId,
    string OwnerTag,
    string JobName,
    int ProcessId,
    long ProcessStartTimeUtcFileTime,
    DateTimeOffset StartedAtUtc,
    int? Port,
    string ExecutablePath,
    string WorkingDirectory,
    string? ProcessImagePath = null)
{
    /// <summary>
    /// The last locally observed members of this service's ownership chain. The
    /// Job Object remains authoritative for current membership; this snapshot
    /// supplies PID-reuse checks without requiring a machine-wide process scan.
    /// </summary>
    public IReadOnlyList<ServiceProcessRecord> ProcessChain { get; init; } = [];
}

internal static class ServiceOwnershipStore
{
    internal const string OwnerTagEnvironmentVariable = "LOCALSERVERHUB_OWNER_TAG";
    internal const string OwnerWorkingDirectoryEnvironmentVariable = "LOCALSERVERHUB_OWNER_CWD";
    internal const string OwnerExecutableEnvironmentVariable = "LOCALSERVERHUB_OWNER_EXECUTABLE";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static string RuntimeDirectory => LocalserverLib.Common.LocalserverPathHelper.StateDirectory;

    internal static string CreateOwnerTag(string serviceId) =>
        $"LocalServerHub/{serviceId}/{Guid.NewGuid():N}";

    internal static bool IsOwnerTagForService(string? ownerTag, string serviceId)
    {
        if (string.IsNullOrWhiteSpace(ownerTag) || string.IsNullOrWhiteSpace(serviceId))
        {
            return false;
        }

        string prefix = $"LocalServerHub/{serviceId}/";
        return ownerTag.StartsWith(prefix, StringComparison.Ordinal)
            && ownerTag.Length > prefix.Length;
    }

    internal static string CreateJobName(string serviceId, string ownerTag)
        => JobObject.GetServiceJobName(serviceId, ownerTag);

    internal static ServiceOwnershipRecord? Load(string serviceId)
    {
        string path = GetPath(serviceId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ServiceOwnershipRecord>(
                File.ReadAllText(path),
                SerializerOptions);
        }
        catch (JsonException)
        {
            // A malformed record is never authorization to touch a process.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void Save(ServiceOwnershipRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        Directory.CreateDirectory(RuntimeDirectory);
        string path = GetPath(record.ServiceId);
        string tempPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(record, SerializerOptions),
                Encoding.UTF8);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void Delete(string serviceId, string? expectedOwnerTag = null)
    {
        string path = GetPath(serviceId);
        if (expectedOwnerTag is not null)
        {
            ServiceOwnershipRecord? current = Load(serviceId);
            if (current is not null
                && !string.Equals(current.OwnerTag, expectedOwnerTag, StringComparison.Ordinal))
            {
                return;
            }
        }

        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GetPath(string serviceId)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(serviceId));
        return Path.Combine(RuntimeDirectory, $"service-{Convert.ToHexString(digest[..16])}.json");
    }
}
