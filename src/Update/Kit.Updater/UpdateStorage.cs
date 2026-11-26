using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Kit.Updater;

internal sealed class UpdateStorage
{
    public const string OperationMutex = @"Local\KitPortableUpdateOperation";
    public const string CancelEvent = @"Local\KitPortableUpdateCancel";
    private const string StateMutex = @"Local\KitRunnerUpdateStateMutex";

    public UpdateStorage(string root, string? dataRoot = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Paths.EnsureNoLinks(Root);
        if (!File.Exists(Path.Combine(Root, "Kit.exe")) || Path.GetPathRoot(Root) == Root)
        {
            throw new UpdateException("invalidInstall", "The installation must contain Kit.exe.");
        }

        CurrentVersion = FileVersionInfo.GetVersionInfo(Path.Combine(Root, "Kit.exe")) is var info
            ? new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) : new Version(0, 0, 0);
        DataRoot = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())))[..24];
        Cache = Path.Combine(DataRoot, "Updates", key);
        Directory.CreateDirectory(Cache);
        Paths.EnsureNoLinks(Cache);
    }

    public string Root { get; }
    public string DataRoot { get; }
    public string Cache { get; }
    public Version CurrentVersion { get; }
    public string Package => Path.Combine(Cache, "package.zip");
    public string Transaction => Path.Combine(Cache, "transaction");
    public string StatePath => Path.Combine(DataRoot, "UpdateState.json");

    public UpdateState Read()
    {
        using var mutex = new Mutex(false, StateMutex);
        Acquire(mutex);
        try
        {
            var state = File.Exists(StatePath) ? JsonSerializer.Deserialize(File.ReadAllText(StatePath), UpdateJson.Default.UpdateState) : null;
            return state != null && string.Equals(state.InstallRoot, Root, StringComparison.OrdinalIgnoreCase)
                ? state : new UpdateState { InstallRoot = Root, UpdateStateFileVersion = "v" + CurrentVersion };
        }
        catch (JsonException)
        {
            return new UpdateState { InstallRoot = Root };
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public void Save(UpdateState state)
    {
        using var mutex = new Mutex(false, StateMutex);
        Acquire(mutex);
        try
        {
            state.InstallRoot = Root;
            state.UpdateStateFileVersion = "v" + CurrentVersion;
            WriteJson(StatePath, state, UpdateJson.Default.UpdateState);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public void Checked(UpdateState state)
    {
        // Seconds are the legacy IPC format. Keep repeated manual checks distinguishable.
        _ = long.TryParse(state.GithubUpdateLastCheckedDate, CultureInfo.InvariantCulture, out var previous);
        state.GithubUpdateLastCheckedDate = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), previous + 1).ToString(CultureInfo.InvariantCulture);
    }

    public void Log(Exception error)
    {
        UpdateFailure.WriteDiagnostics(error, this);
    }

    public static void WriteJson<T>(string path, T value, JsonTypeInfo<T> type)
    {
        Paths.EnsureNoLinks(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        Paths.EnsureNoLinks(temporary);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, type);
            stream.Flush(true);
        }

        File.Move(temporary, path, true);
    }

    public static bool Acquire(Mutex mutex, int timeout = 30000)
    {
        try
        {
            if (!mutex.WaitOne(timeout))
            {
                if (timeout == 0)
                {
                    return false;
                }

                throw new TimeoutException("Updater state is busy.");
            }
        }
        catch (AbandonedMutexException)
        {
            // Ownership is granted after a crashed writer. Atomic state/journal writes remain readable.
        }

        return true;
    }
}

internal static class Paths
{
    public static string Relative(string path)
    {
        var normalized = path.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 220 || parts.Any(part =>
            part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
            part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDevice(part)))
        {
            throw new UpdateException("invalidPackage", "Unsafe archive path: " + path);
        }

        return normalized;
    }

    public static string Under(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, Relative(relative)));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("invalidPackage", "Path escapes update root.");
        }

        EnsureNoLinks(full);
        return full;
    }

    public static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UpdateException("unsafePath", "Linked update/install paths are not supported.");
            }
        }
    }

    private static bool IsDevice(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]));
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
