using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kit.Updater;

internal sealed class ReleaseClient(HttpClient client)
{
    public const string Repository = "https://github.com/guijianchou/Kit";
    public const long MaxPackageBytes = 1024L * 1024 * 1024;

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kit-Portable-Updater/1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        return client;
    }

    public async Task<ReleaseInfo> LatestAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await client.GetAsync("https://api.github.com/repos/guijianchou/Kit/releases/latest", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(1024 * 1024, timeout.Token);
        return Parse(await response.Content.ReadAsStringAsync(timeout.Token));
    }

    public static ReleaseInfo Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
        {
            throw new UpdateException("invalidRelease", "Only stable published releases are supported.");
        }

        var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
        var versionText = tag.TrimStart('v');
        if (!Version.TryParse(versionText, out var version) || version.Build < 0 || version.Revision != -1 ||
            (tag != version.ToString(3) && tag != "v" + version.ToString(3)))
        {
            throw new UpdateException("invalidRelease", "Invalid release version.");
        }

        var name = $"kit_{version}_Portable.zip";
        var baseUrl = $"{Repository}/releases/download/{tag}/";
        var assets = release.GetProperty("assets").EnumerateArray().ToList();
        JsonElement Asset(string assetName)
        {
            var matches = assets.Where(a => a.GetProperty("name").GetString() == assetName).ToList();
            if (matches.Count != 1 || matches[0].GetProperty("browser_download_url").GetString() != baseUrl + assetName)
            {
                throw new UpdateException("invalidRelease", "Missing or ambiguous Portable release asset.");
            }

            return matches[0];
        }

        var zip = Asset(name);
        _ = Asset(name + ".sha256");
        var size = zip.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaxPackageBytes)
        {
            throw new UpdateException("invalidRelease", "Invalid Portable asset size.");
        }

        var digest = zip.TryGetProperty("digest", out var digestValue) ? digestValue.GetString() ?? string.Empty : string.Empty;
        if (digest.Length > 0 && (!digest.StartsWith("sha256:", StringComparison.Ordinal) || !IsHash(digest[7..])))
        {
            throw new UpdateException("invalidRelease", "Invalid asset digest.");
        }

        return new ReleaseInfo(version, $"{Repository}/releases/tag/{tag}", name, new Uri(baseUrl + name), new Uri(baseUrl + name + ".sha256"), size, digest.Length == 0 ? string.Empty : digest[7..]);
    }

    public async Task<string> DownloadAsync(ReleaseInfo release, string path, Action<long> progress, CancellationToken token)
    {
        using var checksumTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        checksumTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var checksumResponse = await client.GetAsync(release.Checksum, HttpCompletionOption.ResponseHeadersRead, checksumTimeout.Token);
        checksumResponse.EnsureSuccessStatusCode();
        await checksumResponse.Content.LoadIntoBufferAsync(1024, checksumTimeout.Token);
        var checksum = ParseChecksum(await checksumResponse.Content.ReadAsStringAsync(checksumTimeout.Token), release.Name);
        if (release.Digest.Length != 0 && !checksum.Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("hashMismatch", "GitHub asset digest and checksum disagree.");
        }

        using var response = await client.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != release.Size)
        {
            throw new UpdateException("invalidPackage", "Download length differs from release metadata.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            idleTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            var count = await input.ReadAsync(buffer, idleTimeout.Token);
            if (count == 0)
            {
                break;
            }

            total += count;
            if (total > release.Size)
            {
                throw new UpdateException("invalidPackage", "Download exceeds expected size.");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), token);
            hash.AppendData(buffer, 0, count);
            progress(total);
        }

        await output.FlushAsync(token);
        output.Flush(true);
        if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(checksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("hashMismatch", "Portable ZIP checksum validation failed.");
        }

        return checksum;
    }

    public static string ParseChecksum(string text, string name)
    {
        var parts = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !IsHash(parts[0]) || parts[1] != name)
        {
            throw new UpdateException("invalidRelease", "Invalid Portable checksum file.");
        }

        return parts[0];
    }

    public static bool IsHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
}
