using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace Kit.Updater;

internal static class PortablePackage
{
    public const string ManifestName = "portable-manifest.json";
    private const long MaxExpandedBytes = 4L * 1024 * 1024 * 1024;

    public static PortableManifest Extract(string archive, string destination, Version expectedVersion, CancellationToken token)
    {
        if (Directory.Exists(destination))
        {
            throw new IOException("Extraction directory already exists.");
        }

        Paths.EnsureNoLinks(destination);
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 30000)
        {
            throw new UpdateException("invalidPackage", "Too many archive entries.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var isDirectory = entry.FullName.EndsWith('/');
            var relative = Paths.Relative(isDirectory ? entry.FullName[..^1] : entry.FullName);
            if (!seen.Add(relative) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
            {
                throw new UpdateException("invalidPackage", "Duplicate or linked archive entry.");
            }

            size = checked(size + entry.Length);
            if (size > MaxExpandedBytes || entry.Length > ReleaseClient.MaxPackageBytes)
            {
                throw new UpdateException("invalidPackage", "Expanded package exceeds size limit.");
            }

            var target = Paths.Under(destination, relative);
            if (isDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[128 * 1024];
            long written = 0;
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                written += count;
                if (written > entry.Length)
                {
                    throw new UpdateException("invalidPackage", "Expanded entry exceeds declared size.");
                }

                output.Write(buffer, 0, count);
            }

            if (written != entry.Length)
            {
                throw new UpdateException("invalidPackage", "Truncated archive entry.");
            }
        }

        var manifest = ReadManifest(destination);
        if (manifest.Version != expectedVersion.ToString(3))
        {
            throw new UpdateException("invalidPackage", "Manifest version mismatch.");
        }

        var inventory = manifest.Files.Select(file => file.Path).Append(ManifestName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(destination, file).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!inventory.SetEquals(actual))
        {
            throw new UpdateException("invalidPackage", "Manifest does not match archive contents.");
        }

        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!Paths.Hash(Paths.Under(destination, file.Path)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("hashMismatch", "Manifest file checksum mismatch.");
            }
        }

        foreach (var required in new[] { "Kit.exe", "Updater/Kit.Updater.exe", "WinUI3Apps/Kit.Settings.exe" })
        {
            if (!inventory.Contains(required))
            {
                throw new UpdateException("invalidPackage", "Missing required Kit runtime.");
            }

            var info = FileVersionInfo.GetVersionInfo(Paths.Under(destination, required));
            if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) != expectedVersion)
            {
                throw new UpdateException("invalidPackage", "Runtime version mismatch.");
            }
        }

        return manifest;
    }

    public static PortableManifest ReadManifest(string directory)
    {
        var path = Paths.Under(directory, ManifestName);
        if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024)
        {
            throw new UpdateException("invalidPackage", "Missing or oversized Portable manifest.");
        }

        var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), UpdateJson.Default.PortableManifest) ?? throw new UpdateException("invalidPackage", "Empty Portable manifest.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (manifest.Files.Count == 0 || manifest.Files.Count > 30000)
        {
            throw new UpdateException("invalidPackage", "Invalid Portable inventory.");
        }

        foreach (var file in manifest.Files)
        {
            file.Path = Paths.Relative(file.Path);
            if (file.Path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || !seen.Add(file.Path) || !ReleaseClient.IsHash(file.Sha256))
            {
                throw new UpdateException("invalidPackage", "Invalid Portable inventory entry.");
            }
        }

        return manifest;
    }
}
