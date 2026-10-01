// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using MaxMind.GeoIP2;

namespace NetMapLib;

public sealed record AsnUpdateResult(string Version, string Sha256, bool AlreadyCurrent);

public sealed class AsnDatabaseUpdater
{
    public const string FileName = "GeoLite2-ASN.mmdb";
    private const string DatabaseRepo = "P3TERX/GeoLite.mmdb";
    private static readonly SemaphoreSlim UpdateGate = new(1);
    private readonly string directory;
    private readonly Func<Uri, HttpClient> createClient;

    public AsnDatabaseUpdater(NetMapOptions options)
        : this(DataDirectory, uri => NetworkProbe.CreateClient(uri, true, options, out _))
    {
    }

    internal AsnDatabaseUpdater(string directory, Func<Uri, HttpClient> createClient)
    {
        this.directory = Path.GetFullPath(directory);
        this.createClient = createClient;
    }

    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kit", "NetMap", "Data");

    public static string LocalPath => Path.Combine(DataDirectory, FileName);

    public async Task<AsnUpdateResult> UpdateAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await UpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            var token = deadline.Token;
            using var metadata = new MemoryStream();
            await DownloadAsync(new Uri($"https://api.github.com/repos/{DatabaseRepo}/releases/latest"), metadata, 2 * 1024 * 1024, null, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(metadata.ToArray());
            var release = document.RootElement;
            string version = release.GetProperty("tag_name").GetString() ?? string.Empty;
            if (version.Length is 0 or > 80 || !version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            {
                throw new InvalidDataException("InvalidData");
            }

            const long limit = 32 * 1024 * 1024;
            var assets = release.GetProperty("assets").EnumerateArray().Where(asset => asset.GetProperty("name").GetString() == FileName).ToArray();
            if (assets.Length != 1)
            {
                throw new InvalidDataException("InvalidData");
            }

            var asset = assets[0];
            string value = asset.TryGetProperty("digest", out var hash) && hash.ValueKind == JsonValueKind.String ? hash.GetString()! : string.Empty;
            if (!value.StartsWith("sha256:", StringComparison.Ordinal) || value.Length != 71 || !value[7..].All(char.IsAsciiHexDigit))
            {
                throw new InvalidDataException("HashUnavailable");
            }

            string digest = value[7..];
            long expectedSize = asset.GetProperty("size").GetInt64();
            if (expectedSize <= 0 || expectedSize > limit)
            {
                throw new InvalidDataException("TooLarge");
            }

            var download = new Uri($"https://github.com/{DatabaseRepo}/releases/download/{version}/{FileName}");
            if (asset.GetProperty("browser_download_url").GetString() != download.AbsoluteUri)
            {
                throw new InvalidDataException("InvalidData");
            }

            RejectRedirectedDirectory(directory);
            Directory.CreateDirectory(directory);
            RejectRedirectedDirectory(directory);
            string destination = Path.Combine(directory, FileName);
            if (File.Exists(destination))
            {
                if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Redirected data file.");
                }

                if (new FileInfo(destination).Length == expectedSize && await HasHashAsync(destination, digest, token).ConfigureAwait(false))
                {
                    ValidateContent(destination);
                    return new(version, digest.ToLowerInvariant(), true);
                }
            }

            temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await DownloadAsync(download, file, expectedSize, progress, token).ConfigureAwait(false);
                if (file.Length != expectedSize)
                {
                    throw new InvalidDataException("InvalidData");
                }
            }

            if (!await HasHashAsync(temporary, digest, token).ConfigureAwait(false))
            {
                throw new InvalidDataException("HashMismatch");
            }

            ValidateContent(temporary);
            token.ThrowIfCancellationRequested();

            // Both paths are in the same directory; replacement happens only after complete verification.
            File.Move(temporary, destination, overwrite: true);
            return new(version, digest.ToLowerInvariant(), false);
        }
        finally
        {
            try
            {
                if (temporary != null && File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            finally
            {
                UpdateGate.Release();
            }
        }
    }

    internal static bool IsDownloadUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host is "api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    internal static async Task<bool> HasHashAsync(string path, string expected, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        return string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectRedirectedDirectory(string path)
    {
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Redirected data directory.");
            }
        }
    }

    private static void ValidateContent(string path)
    {
        using var reader = new DatabaseReader(path);
        if (reader.Metadata.DatabaseType != "GeoLite2-ASN")
        {
            throw new InvalidDataException("InvalidData");
        }
    }

    private async Task DownloadAsync(Uri uri, Stream output, long limit, IProgress<double>? progress, CancellationToken token)
    {
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            if (!IsDownloadUri(uri))
            {
                throw new InvalidDataException("InvalidData");
            }

            using var client = createClient(uri);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location ?? throw new InvalidDataException("InvalidData");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit)
            {
                throw new InvalidDataException("TooLarge");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[65536];
            long total = 0;
            int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                total += count;
                if (total > limit)
                {
                    throw new InvalidDataException("TooLarge");
                }

                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                if (response.Content.Headers.ContentLength is > 0)
                {
                    progress?.Report(total * 100.0 / response.Content.Headers.ContentLength.Value);
                }
            }

            return;
        }

        throw new InvalidDataException("InvalidData");
    }
}
