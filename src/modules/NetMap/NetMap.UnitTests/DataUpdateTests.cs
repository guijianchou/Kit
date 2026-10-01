// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetMapLib;

namespace NetMap.UnitTests;

[TestClass]
public sealed class DataUpdateTests
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "NetMapUpdate-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow("https://api.github.com/repos/test/repo", true)]
    [DataRow("https://release-assets.githubusercontent.com/test?signature=example", true)]
    [DataRow("https://github.com.evil.example/test", false)]
    [DataRow("https://user:password@github.com/test", false)]
    [DataRow("http://github.com/test", false)]
    [DataRow("https://127.0.0.1/test", false)]
    [DataRow("https://github.com:8443/test", false)]
    public void DownloadsOnlyUseGithubHttpsHosts(string uri, bool allowed)
    {
        Assert.AreEqual(allowed, AsnDatabaseUpdater.IsDownloadUri(new Uri(uri)));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("sha1:0123456789012345678901234567890123456789")]
    [DataRow("sha256:invalid")]
    public async Task MissingOrInvalidDigestNeverDownloadsOrReplaces(string digest)
    {
        string path = ExistingDatabase();
        int requests = 0;
        var updater = Create(_ =>
        {
            requests++;
            return JsonResponse(Release(digest, 12));
        });
        var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => updater.UpdateAsync(null, CancellationToken.None));
        Assert.AreEqual("HashUnavailable", error.Message);
        Assert.AreEqual(1, requests);
        AssertPreserved(path);
    }

    [TestMethod]
    public async Task MismatchedHashRetainsOldFileAndRemovesPartial()
    {
        string path = ExistingDatabase();
        var updater = Create(uri => uri.Host == "api.github.com" ? JsonResponse(Release("sha256:" + new string('0', 64), 12)) : BytesResponse("changed data"));
        var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => updater.UpdateAsync(null, CancellationToken.None));
        Assert.AreEqual("HashMismatch", error.Message);
        AssertPreserved(path);
    }

    [TestMethod]
    public async Task MatchingHashDoesNotAcceptInvalidMmdb()
    {
        string path = ExistingDatabase();
        string digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("changed data")));
        var updater = Create(uri => uri.Host == "api.github.com" ? JsonResponse(Release(digest, 12)) : BytesResponse("changed data"));
        await Assert.ThrowsExceptionAsync<MaxMind.Db.InvalidDatabaseException>(() => updater.UpdateAsync(null, CancellationToken.None));
        AssertPreserved(path);
    }

    [TestMethod]
    public async Task OversizedDownloadRetainsOldFile()
    {
        string path = ExistingDatabase();
        var updater = Create(uri => uri.Host == "api.github.com" ? JsonResponse(Release("sha256:" + new string('0', 64), 4)) : BytesResponse("too large"));
        var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => updater.UpdateAsync(null, CancellationToken.None));
        Assert.AreEqual("TooLarge", error.Message);
        AssertPreserved(path);
    }

    [TestMethod]
    public async Task CancellationRetainsOldFileAndRemovesPartial()
    {
        string path = ExistingDatabase();
        using var stop = new CancellationTokenSource();
        var updater = Create(uri =>
        {
            if (uri.Host == "api.github.com")
            {
                return JsonResponse(Release("sha256:" + new string('0', 64), 12));
            }

            stop.Cancel();
            return BytesResponse("changed data");
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => updater.UpdateAsync(null, stop.Token));
        AssertPreserved(path);
    }

    [TestMethod]
    public async Task RedirectToAnotherHostIsRejectedBeforeRequest()
    {
        string path = ExistingDatabase();
        int requests = 0;
        var updater = Create(_ =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://example.com/database");
            return response;
        });
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => updater.UpdateAsync(null, CancellationToken.None));
        Assert.AreEqual(1, requests);
        AssertPreserved(path);
    }

    private static string Release(string digest, int size) => JsonSerializer.Serialize(new
    {
        tag_name = "2026.10.01",
        assets = new[] { new { name = "GeoLite2-ASN.mmdb", size, digest, browser_download_url = "https://github.com/P3TERX/GeoLite.mmdb/releases/download/2026.10.01/GeoLite2-ASN.mmdb" } },
    });

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage BytesResponse(string body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };

    private AsnDatabaseUpdater Create(Func<Uri, HttpResponseMessage> respond) => new(directory, _ => new HttpClient(new StubHandler(respond)));

    private string ExistingDatabase()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, AsnDatabaseUpdater.FileName);
        File.WriteAllText(path, "existing data");
        return path;
    }

    private void AssertPreserved(string path)
    {
        Assert.AreEqual("existing data", File.ReadAllText(path));
        Assert.AreEqual(0, Directory.GetFiles(directory, "*.partial").Length);
    }

    private sealed class StubHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request.RequestUri!));
    }
}
