namespace Kit.AiHub.Engine;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kit.AiHub.Models;

/// <summary>
/// Manages verified local Codex and Pi kernels and coordinates installation with execution.
/// </summary>
public sealed partial class KernelManagerService : IDisposable
{
    private const long MaximumArchiveBytes = 512L * 1024 * 1024;
    private const int MaximumArchiveEntries = 10000;
    private static readonly HttpClient DefaultClient = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, (long Length, long Modified, string Version)> VersionCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex VersionPattern = new(
        @"(?<![A-Za-z0-9.+-])(?:rust-v|v)?(?<version>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)(?![A-Za-z0-9.+-])",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly string _kernelRoot;
    private readonly string _bundledRoot;
    private readonly HttpClient _client;
    private readonly Architecture _architecture;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<string>>? _versionProbe;
    private readonly SemaphoreSlim _installGate = new(1, 1);

    public KernelManagerService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit", "AiHub", "kernels"))
    {
    }

    public KernelManagerService(string kernelRoot, HttpClient? client = null)
        : this(kernelRoot, client, AppContext.BaseDirectory, null, RuntimeInformation.ProcessArchitecture)
    {
    }

    internal KernelManagerService(
        string kernelRoot,
        HttpClient? client,
        string bundledRoot,
        Func<ProcessStartInfo, CancellationToken, Task<string>>? versionProbe,
        Architecture architecture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kernelRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledRoot);
        _kernelRoot = Path.GetFullPath(kernelRoot);
        _bundledRoot = Path.GetFullPath(bundledRoot);
        _client = client ?? DefaultClient;
        _versionProbe = versionProbe;
        _architecture = architecture;
    }

    public string KernelRoot => _kernelRoot;

    internal string KernelRootDirectory => _kernelRoot;

    public AiKernelStatus GetStatus(string? kernel)
    {
        string normalized = AiKernelCatalog.Normalize(kernel);
        string executable = ExecutableName(normalized);
        string[] candidates =
        {
            Path.Combine(_kernelRoot, normalized, executable),
            Path.Combine(AppContext.BaseDirectory, executable),
            Path.Combine(AppContext.BaseDirectory, "kernels", executable),
        };
        string path = candidates.FirstOrDefault(candidate => IsCompleteInstallation(normalized, candidate)) ?? candidates[0];
        bool installed = IsCompleteInstallation(normalized, path);
        return new AiKernelStatus(normalized, installed, installed ? ReadInstalledVersion(path) : string.Empty, path);
    }

    public async Task<AiKernelStatus> GetStatusAsync(string? kernel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AiKernelStatus status = GetStatus(kernel);
        if (!status.Installed)
        {
            return status;
        }

        using IDisposable lease = await AcquireExecutionLeaseAsync(status.Kernel, cancellationToken);
        return await InspectInstalledVersionAsync(status.Kernel, cancellationToken);
    }

    public string RequireExecutable(string? kernel)
    {
        AiKernelStatus status = GetStatus(kernel);
        if (!status.Installed)
        {
            throw new FileNotFoundException("The required AI kernel is not installed. Download it in AI Hub settings.");
        }

        return status.Path;
    }

    public async Task<string> CheckLatestVersionAsync(string? kernel, CancellationToken cancellationToken = default)
    {
        using JsonDocument document = JsonDocument.Parse(await ReadHttpBytesAsync(
            LatestReleaseUrl(AiKernelCatalog.Normalize(kernel)), 4 * 1024 * 1024, cancellationToken));
        return ReleaseVersion(document.RootElement);
    }

    public async Task<AiKernelUpdateResult> DownloadOrUpdateAsync(
        string? kernel,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _installGate.WaitAsync(cancellationToken);
        try
        {
            return await InstallIfNewerAsync(AiKernelCatalog.Normalize(kernel), progress, cancellationToken);
        }
        finally
        {
            _installGate.Release();
        }
    }

    internal Task<IDisposable> AcquireExecutionLeaseAsync(string kernel, CancellationToken cancellationToken) =>
        AcquireLeaseAsync(AiKernelCatalog.Normalize(kernel), false, cancellationToken);

    private async Task<IDisposable> AcquireLeaseAsync(string kernel, bool exclusive, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(_kernelRoot, ".locks");
        EnsureManagedPath(directory);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, kernel + ".lock");
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureManagedPath(path);
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate,
                    exclusive ? FileAccess.ReadWrite : FileAccess.Read,
                    exclusive ? FileShare.None : FileShare.Read);
            }
            catch (IOException exception) when (IsSharingViolation(exception) && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private async Task<AiKernelUpdateResult> InstallIfNewerAsync(
        string kernel, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        AiKernelStatus installed = await GetStatusAsync(kernel, cancellationToken);
        EnsureNoReparsePoints(_kernelRoot);
        Directory.CreateDirectory(_kernelRoot);
        string temporaryZip = Path.Combine(_kernelRoot, "." + kernel + "-" + Guid.NewGuid().ToString("N") + ".zip");
        string staging = Path.Combine(_kernelRoot, "." + kernel + "-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(_kernelRoot, kernel);
        string backup = Path.Combine(_kernelRoot, "." + kernel + "-backup-" + Guid.NewGuid().ToString("N"));
        string releaseVersion = string.Empty;
        string[] expectedDigests;
        bool bundled = false;
        try
        {
            try
            {
                var release = await GetReleaseAssetAsync(kernel, cancellationToken);
                releaseVersion = release.Version;
                if (installed.Installed && CompareVersions(installed.Version, releaseVersion) >= 0)
                {
                    return new AiKernelUpdateResult(installed, releaseVersion, Changed: false);
                }

                expectedDigests = new[] { release.Digest };
                EnsureManagedPath(temporaryZip);
                await using var destination = new FileStream(temporaryZip, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
                progress?.Report(0);
                await CopyHttpContentAsync(release.DownloadUrl, destination, MaximumArchiveBytes, TimeSpan.FromMinutes(5), progress, cancellationToken);
            }
            catch (HttpRequestException) when (!installed.Installed && File.Exists(Path.Combine(_bundledRoot, ArchiveName(kernel))))
            {
                bundled = true;
                releaseVersion = string.Empty;
                expectedDigests = BundledDigests(kernel);
                string bundledPath = Path.Combine(_bundledRoot, ArchiveName(kernel));
                EnsureNoReparsePoints(bundledPath);
                EnsureManagedPath(temporaryZip);
                TryDeleteFile(temporaryZip);
                await using var source = new FileStream(bundledPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
                if (source.Length > MaximumArchiveBytes)
                {
                    throw new InvalidDataException("The bundled AI kernel archive exceeds the supported size.");
                }

                await using var destination = new FileStream(temporaryZip, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
                await source.CopyToAsync(destination, cancellationToken);
            }

            string digest = await ComputeSha256Async(temporaryZip, cancellationToken);
            if (!expectedDigests.Contains(digest, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The AI kernel archive does not match its trusted SHA-256 digest.");
            }

            string stagedExecutable = await ExtractArchiveAsync(kernel, temporaryZip, staging, cancellationToken);
            string version = await VerifyExecutableVersionAsync(stagedExecutable, cancellationToken);
            if (!bundled && CompareVersions(version, releaseVersion) != 0)
            {
                throw new InvalidDataException("The AI kernel executable version does not match its release.");
            }

            await WriteVersionManifestAsync(stagedExecutable, version, cancellationToken);
            using IDisposable lease = await AcquireLeaseAsync(kernel, true, cancellationToken);
            // Another installer may have committed while this archive was being downloaded.
            AiKernelStatus current = await InspectInstalledVersionAsync(kernel, cancellationToken);
            if (current.Installed && CompareVersions(current.Version, version) >= 0)
            {
                return new AiKernelUpdateResult(current, releaseVersion, Changed: false, UsedBundledArchive: bundled);
            }

            EnsureManagedPath(target);
            EnsureSafeTree(staging);
            bool hadInstallation = Directory.Exists(target);
            if (hadInstallation)
            {
                EnsureSafeTree(target);
                await MoveDirectoryAsync(target, backup, cancellationToken);
            }

            try
            {
                await MoveDirectoryAsync(staging, target, cancellationToken);
            }
            catch
            {
                if (hadInstallation)
                {
                    if (Directory.Exists(target) || File.Exists(target))
                    {
                        throw new IOException("The kernel update failed; the previous installation is preserved in its backup directory.");
                    }

                    try
                    {
                        await MoveDirectoryAsync(backup, target, CancellationToken.None);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        throw new IOException("The kernel update could not be rolled back; the previous installation is preserved in its backup directory.");
                    }
                }

                throw;
            }

            string executable = Path.Combine(target, ExecutableName(kernel));
            CacheVersion(executable, version);
            TryDeleteDirectory(backup);
            progress?.Report(1);
            return new AiKernelUpdateResult(new AiKernelStatus(kernel, true, version, executable), releaseVersion, Changed: true, UsedBundledArchive: bundled);
        }
        finally
        {
            TryDeleteFile(temporaryZip);
            TryDeleteDirectory(staging);
        }
    }

    private async Task<AiKernelStatus> InspectInstalledVersionAsync(string kernel, CancellationToken cancellationToken)
    {
        AiKernelStatus status = GetStatus(kernel);
        if (!status.Installed || status.Version.Length != 0)
        {
            return status;
        }

        try
        {
            string version = await VerifyExecutableVersionAsync(status.Path, cancellationToken);
            CacheVersion(status.Path, version);
            return status with { Version = version };
        }
        catch (InvalidDataException)
        {
            return status with { Installed = false, Version = string.Empty };
        }
    }

    private async Task<(string Version, string DownloadUrl, string Digest)> GetReleaseAssetAsync(
        string kernel, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(await ReadHttpBytesAsync(LatestReleaseUrl(kernel), 4 * 1024 * 1024, cancellationToken));
        string version = ReleaseVersion(document.RootElement);
        if (!document.RootElement.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The official kernel release has no assets.");
        }

        string archiveName = ArchiveName(kernel);
        JsonElement[] matches = assets.EnumerateArray()
            .Where(asset => string.Equals(StringProperty(asset, "name"), archiveName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("The official kernel release has no unique archive for this architecture.");
        }

        string url = ValidateDownloadUrl(StringProperty(matches[0], "browser_download_url"));
        string digest = StringProperty(matches[0], "digest");
        if (digest.Length != 0)
        {
            return (version, url, NormalizeDigest(digest));
        }

        string[] checksumNames = { archiveName + ".sha256", archiveName + ".sha256sum", "SHA256SUMS", "SHA256SUMS.txt", "checksums.sha256", "codex-package_SHA256SUMS" };
        foreach (string checksumName in checksumNames)
        {
            JsonElement[] checksums = assets.EnumerateArray()
                .Where(asset => string.Equals(StringProperty(asset, "name"), checksumName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (checksums.Length != 1)
            {
                continue;
            }

            string checksumText = Encoding.UTF8.GetString(await ReadHttpBytesAsync(
                ValidateDownloadUrl(StringProperty(checksums[0], "browser_download_url")), 1024 * 1024, cancellationToken));
            bool dedicated = checksumName.StartsWith(archiveName + ".", StringComparison.OrdinalIgnoreCase);
            foreach (string record in checksumText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (dedicated && record.Length == 64)
                {
                    return (version, url, NormalizeDigest(record));
                }

                string bsdPrefix = "SHA256 (" + archiveName + ") = ";
                if (record.StartsWith(bsdPrefix, StringComparison.Ordinal))
                {
                    return (version, url, NormalizeDigest(record[bsdPrefix.Length..]));
                }

                if (record.Length > 65 && char.IsWhiteSpace(record[64])
                    && string.Equals(record[65..].TrimStart().TrimStart('*'), archiveName, StringComparison.Ordinal))
                {
                    return (version, url, NormalizeDigest(record[..64]));
                }
            }
        }

        throw new InvalidDataException("The official kernel archive has no matching SHA-256 digest.");
    }

    private async Task<byte[]> ReadHttpBytesAsync(string url, int limit, CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        await CopyHttpContentAsync(url, destination, limit, TimeSpan.FromSeconds(30), null, cancellationToken);
        return destination.ToArray();
    }

    private async Task CopyHttpContentAsync(
        string url, Stream destination, long limit, TimeSpan timeout, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ValidateDownloadUrl(url));
            request.Headers.UserAgent.ParseAdd("Kit-AiHub/1.0");
            using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            response.EnsureSuccessStatusCode();
            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength > limit)
            {
                throw new InvalidDataException("The kernel download exceeds the supported size.");
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
            byte[] buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                int read;
                try
                {
                    read = await source.ReadAsync(buffer.AsMemory(), timeoutSource.Token);
                }
                catch (IOException exception)
                {
                    throw new HttpRequestException("The kernel download was interrupted.", exception);
                }

                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > limit)
                {
                    throw new InvalidDataException("The kernel download exceeds the supported size.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), timeoutSource.Token);
                if (contentLength is > 0)
                {
                    progress?.Report(Math.Clamp(total / (double)contentLength.Value, 0, 1));
                }
            }

            if (contentLength.HasValue && total != contentLength.Value)
            {
                throw new HttpRequestException("The kernel download was incomplete.");
            }

            await destination.FlushAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The kernel download timed out.", exception);
        }
    }

    private async Task<string> ExtractArchiveAsync(string kernel, string zipPath, string staging, CancellationToken cancellationToken)
    {
        EnsureManagedPath(staging);
        Directory.CreateDirectory(staging);
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > MaximumArchiveEntries)
        {
            throw new InvalidDataException("The kernel archive contains too many entries.");
        }

        var entries = new List<(ZipArchiveEntry Entry, string Name)>();
        var archivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = ValidateArchivePath(entry);
            if (!archivePaths.Add(name.TrimEnd('/')) || entry.Length > MaximumArchiveBytes - declaredBytes)
            {
                throw new InvalidDataException("The kernel archive contains duplicate paths or exceeds the supported size.");
            }

            declaredBytes += entry.Length;
            entries.Add((entry, name));
        }

        string platformExecutable = kernel == AiKernelCatalog.Codex ? ArchiveName(kernel)[..^4] : "pi.exe";
        var candidates = entries.Where(item => string.Equals(Path.GetFileName(item.Name), platformExecutable, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0 && kernel == AiKernelCatalog.Codex)
        {
            candidates = entries.Where(item => string.Equals(Path.GetFileName(item.Name), "codex.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        if (candidates.Length != 1 || candidates[0].Entry.Length < 16 * 1024)
        {
            throw new InvalidDataException("The kernel archive contains no unique supported Windows executable.");
        }

        var executable = candidates[0];
        string prefix = executable.Name[..(executable.Name.LastIndexOf('/') + 1)];
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long extractedBytes = 0;
        byte[] buffer = new byte[64 * 1024];
        foreach (var item in entries)
        {
            if (item.Name.EndsWith('/') || (kernel == AiKernelCatalog.Codex && item.Entry != executable.Entry)
                || (kernel == AiKernelCatalog.Pi && !item.Name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            string relative = item.Entry == executable.Entry ? ExecutableName(kernel) : item.Name[prefix.Length..];
            string path = Path.GetFullPath(Path.Combine(staging, relative));
            EnsureManagedPath(path);
            if (!path.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !destinations.Add(path))
            {
                throw new InvalidDataException("The kernel archive contains an unsafe destination.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using Stream source = item.Entry.Open();
            await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
            long entryBytes = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
            {
                extractedBytes += read;
                entryBytes += read;
                if (extractedBytes > MaximumArchiveBytes || entryBytes > item.Entry.Length)
                {
                    throw new InvalidDataException("The extracted kernel exceeds the supported size.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (entryBytes != item.Entry.Length)
            {
                throw new InvalidDataException("The kernel archive contains an incomplete entry.");
            }
        }

        string stagedExecutable = Path.Combine(staging, ExecutableName(kernel));
        if (!IsCompleteInstallation(kernel, stagedExecutable))
        {
            throw new InvalidDataException("The kernel archive is missing required runtime resources.");
        }

        return stagedExecutable;
    }

    private static string ValidateArchivePath(ZipArchiveEntry entry)
    {
        string name = entry.FullName.Replace('\\', '/');
        int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
        if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || unixType is not (0 or 0x8000 or 0x4000)
            || name.Length == 0 || name.StartsWith('/'))
        {
            throw new InvalidDataException("The kernel archive contains an unsafe entry.");
        }

        foreach (string component in name.TrimEnd('/').Split('/'))
        {
            if (component.Length == 0 || component is "." or ".." || component.EndsWith('.') || component.EndsWith(' ')
                || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException("The kernel archive contains an unsafe path.");
            }

            string stem = component.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3'))
            {
                throw new InvalidDataException("The kernel archive contains a reserved Windows path.");
            }
        }

        return name;
    }

    private async Task<string> VerifyExecutableVersionAsync(string executable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoReparsePoints(executable);
        string probeDirectory = Path.Combine(_kernelRoot, ".probe-" + Guid.NewGuid().ToString("N"));
        EnsureManagedPath(probeDirectory);
        Directory.CreateDirectory(probeDirectory);
        try
        {
            var startInfo = new ProcessStartInfo { FileName = executable, WorkingDirectory = probeDirectory };
            startInfo.Environment.Clear();
            foreach (string name in new[] { "SystemRoot", "WINDIR", "PATH", "PATHEXT", "COMSPEC" })
            {
                string? value = Environment.GetEnvironmentVariable(name);
                if (value != null)
                {
                    startInfo.Environment[name] = value;
                }
            }

            foreach (string name in new[] { "TEMP", "TMP", "USERPROFILE", "HOME", "APPDATA", "LOCALAPPDATA", "CODEX_HOME", "PI_CODING_AGENT_DIR" })
            {
                startInfo.Environment[name] = probeDirectory;
            }

            startInfo.Environment["PI_OFFLINE"] = "1";
            startInfo.Environment["PI_SKIP_VERSION_CHECK"] = "1";
            startInfo.Environment["PI_TELEMETRY"] = "0";
            startInfo.ArgumentList.Add("--version");
            string output;
            if (_versionProbe != null)
            {
                output = await _versionProbe(startInfo, cancellationToken);
            }
            else
            {
                KernelProcessResult result = await KernelProcessRunner.RunAsync(startInfo, null, TimeSpan.FromSeconds(20), cancellationToken, 64 * 1024, 64 * 1024);
                if (result.Failure == KernelProcessFailure.Cancelled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (result.Failure != KernelProcessFailure.None || result.ExitCode != 0)
                {
                    throw new InvalidDataException("The AI kernel did not pass its version check.");
                }

                output = result.StandardOutput;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return ParseVersion(output);
        }
        finally
        {
            TryDeleteDirectory(probeDirectory);
        }
    }

    private static string ReadInstalledVersion(string executable)
    {
        try
        {
            var file = new FileInfo(executable);
            if (VersionCache.TryGetValue(executable, out var cached) && cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc.Ticks)
            {
                return cached.Version;
            }

            string manifest = Path.Combine(file.DirectoryName!, "kernel-version.json");
            EnsureNoReparsePoints(manifest);
            using var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 16 * 1024)
            {
                return string.Empty;
            }

            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("Length", out JsonElement length) && length.ValueKind == JsonValueKind.Number
                && length.TryGetInt64(out long savedLength) && savedLength == file.Length
                && root.TryGetProperty("Modified", out JsonElement modified) && modified.ValueKind == JsonValueKind.Number
                && modified.TryGetInt64(out long ticks) && ticks == file.LastWriteTimeUtc.Ticks)
            {
                return ParseVersion(StringProperty(root, "Version"));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return string.Empty;
        }

        return string.Empty;
    }

    private static async Task WriteVersionManifestAsync(string executable, string version, CancellationToken cancellationToken)
    {
        var file = new FileInfo(executable);
        string path = Path.Combine(file.DirectoryName!, "kernel-version.json");
        EnsureNoReparsePoints(path);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("Version", version);
        writer.WriteNumber("Length", file.Length);
        writer.WriteNumber("Modified", file.LastWriteTimeUtc.Ticks);
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
    }

    private static void CacheVersion(string executable, string version)
    {
        var file = new FileInfo(executable);
        VersionCache[executable] = (file.Length, file.LastWriteTimeUtc.Ticks, version);
    }

    private static bool IsCompleteInstallation(string kernel, string executable)
    {
        EnsureNoReparsePoints(executable);
        if (!File.Exists(executable))
        {
            return false;
        }

        if (kernel != AiKernelCatalog.Pi)
        {
            return true;
        }

        foreach (string relative in new[] { "package.json", "theme/dark.json", "theme/light.json" })
        {
            string path = Path.Combine(Path.GetDirectoryName(executable)!, relative);
            EnsureNoReparsePoints(path);
            if (!File.Exists(path))
            {
                return false;
            }
        }

        return true;
    }

    private void EnsureManagedPath(string path)
    {
        string relative = Path.GetRelativePath(_kernelRoot, Path.GetFullPath(path));
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The kernel operation would leave its installation directory.");
        }

        EnsureNoReparsePoints(path);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The kernel operation cannot use a reparse point.");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private void EnsureSafeTree(string path)
    {
        EnsureManagedPath(path);
        var pending = new Stack<string>();
        pending.Push(path);
        int count = 0;
        while (pending.Count != 0)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++count > MaximumArchiveEntries)
                {
                    throw new InvalidDataException("The kernel directory contains too many entries.");
                }

                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The kernel directory contains a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private async Task MoveDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureManagedPath(source);
            EnsureManagedPath(destination);
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException exception) when (IsSharingViolation(exception) && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception) => (exception.HResult & 0xffff) is 32 or 33;

    private void TryDeleteFile(string path)
    {
        try
        {
            EnsureManagedPath(path);
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A failed cleanup must not hide the installation failure.
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            EnsureManagedPath(path);
            if (Directory.Exists(path))
            {
                EnsureSafeTree(path);
                Directory.Delete(path, true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Preserve directories that cannot be safely removed.
        }
    }

    private static string ParseVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 * 1024)
        {
            throw new InvalidDataException("The AI kernel version could not be determined.");
        }

        Match match = VersionPattern.Match(value);
        if (!match.Success)
        {
            throw new InvalidDataException("The AI kernel version could not be determined.");
        }

        string version = match.Groups["version"].Value;
        string[] parts = version.Split('+')[0].Split('-', 2);
        if (parts.Length == 2 && parts[1].Split('.').Any(part => part.Length > 1 && part[0] == '0' && part.All(char.IsAsciiDigit)))
        {
            throw new InvalidDataException("The AI kernel version has an invalid prerelease identifier.");
        }

        return version;
    }

    public static int CompareVersions(string a, string b)
    {
        string[] left = ParseVersion(a).Split('+')[0].Split('-', 2);
        string[] right = ParseVersion(b).Split('+')[0].Split('-', 2);
        string[] leftCore = left[0].Split('.');
        string[] rightCore = right[0].Split('.');
        for (int i = 0; i < leftCore.Length; i++)
        {
            int comparison = CompareNumericIdentifier(leftCore[i], rightCore[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        if (left.Length == 1 || right.Length == 1)
        {
            return right.Length.CompareTo(left.Length);
        }

        string[] leftParts = left[1].Split('.');
        string[] rightParts = right[1].Split('.');
        for (int i = 0; i < Math.Min(leftParts.Length, rightParts.Length); i++)
        {
            bool leftNumeric = leftParts[i].All(char.IsAsciiDigit);
            bool rightNumeric = rightParts[i].All(char.IsAsciiDigit);
            int comparison = leftNumeric && rightNumeric
                ? CompareNumericIdentifier(leftParts[i], rightParts[i])
                : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : string.CompareOrdinal(leftParts[i], rightParts[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static int CompareNumericIdentifier(string left, string right) =>
        left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);

    private static string StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static string ReleaseVersion(JsonElement release) => ParseVersion(StringProperty(release, "tag_name"));

    private static string NormalizeDigest(string digest)
    {
        string value = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : digest;
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("The official kernel archive has an invalid SHA-256 digest.");
        }

        return value;
    }

    private static string ValidateDownloadUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
            ? uri.AbsoluteUri : throw new InvalidDataException("The official kernel release has an invalid download URL.");

    private string ArchiveName(string kernel) => (kernel, _architecture) switch
    {
        (AiKernelCatalog.Codex, Architecture.X64) => "codex-x86_64-pc-windows-msvc.exe.zip",
        (AiKernelCatalog.Codex, Architecture.Arm64) => "codex-aarch64-pc-windows-msvc.exe.zip",
        (AiKernelCatalog.Pi, Architecture.X64) => "pi-windows-x64.zip",
        (AiKernelCatalog.Pi, Architecture.Arm64) => "pi-windows-arm64.zip",
        _ => throw new PlatformNotSupportedException("The AI kernel is unavailable for this process architecture."),
    };

    private string[] BundledDigests(string kernel) => (kernel, _architecture) switch
    {
        // These digests are pinned independently from the bundled archive bytes.
        (AiKernelCatalog.Codex, Architecture.X64) => new[]
        {
            "c016b0e6968b78586919c720d2685a03712f6d5f11bcd9d6f92c91eb8c41ba16",
            "53685f9f6bd171d4bd7d6c2be724fc04f6737a4001eb8471ec59824e5adc8042",
        },
        (AiKernelCatalog.Codex, Architecture.Arm64) => new[] { "5da0e4b828d125bb72ecefc882bd068443625df32d56269e687afb7026b2a65d" },
        (AiKernelCatalog.Pi, Architecture.X64) => new[] { "002fa95b90d521245b9985d8f168caebc237ad56e7e30b319807dee1b2e17e1c" },
        (AiKernelCatalog.Pi, Architecture.Arm64) => new[] { "b25e96fe64c9f41f75a924c0d36f395abb98d6c6fec0b78aaa0b86926f938bb4" },
        _ => throw new PlatformNotSupportedException("The AI kernel is unavailable for this process architecture."),
    };

    private static string ExecutableName(string kernel) => kernel == AiKernelCatalog.Pi ? "pi.exe" : "codex.exe";

    private static string LatestReleaseUrl(string kernel) => kernel == AiKernelCatalog.Pi
        ? "https://api.github.com/repos/earendil-works/pi/releases/latest"
        : "https://api.github.com/repos/openai/codex/releases/latest";

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(30),
    })
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    public void Dispose()
    {
        _installGate.Dispose();
    }
}
