using System.Net;
using Kit.Updater;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Kit.Updater.Tests;

[TestClass]
public sealed class ReleaseClientRateLimitTests
{
    [TestMethod]
    [DataRow(403, "X-RateLimit-Remaining", "0")]
    [DataRow(403, "Retry-After", "60")]
    [DataRow(403, "Retry-After", "Wed, 25 Nov 2026 12:00:00 GMT")]
    [DataRow(429, null, null)]
    public async Task ExplicitRateLimitStopsAfterOneRequest(int status, string? header, string? value)
    {
        using var handler = new ResponseHandler(() => Response(status, header, value));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<UpdateException>(() => new ReleaseClient(http).LatestAsync(CancellationToken.None));

        Assert.AreEqual("rateLimited", error.Code);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual("https://api.github.com/repos/guijianchou/Kit/releases/latest", handler.Requests[0].AbsoluteUri);
    }

    [TestMethod]
    [DataRow(403, null, null)]
    [DataRow(403, "X-RateLimit-Remaining", "25")]
    [DataRow(403, "Retry-After", "invalid")]
    [DataRow(503, "Retry-After", "60")]
    public async Task OtherHttpFailuresKeepTheirOriginalStatus(int status, string? header, string? value)
    {
        using var handler = new ResponseHandler(() => Response(status, header, value));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new ReleaseClient(http).LatestAsync(CancellationToken.None));

        Assert.AreEqual((HttpStatusCode)status, error.StatusCode);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadStopsAtRateLimitedChecksumOrPackage(bool limitPackage)
    {
        var temp = Directory.CreateTempSubdirectory("KitUpdaterRateLimitTests-").FullName;
        try
        {
            var asset = ReleaseClient.Repository + "/releases/download/3.5.2/kit_3.5.2_Portable.zip";
            var release = new ReleaseInfo(
                new Version(3, 5, 2),
                ReleaseClient.Repository + "/releases/tag/3.5.2",
                "kit_3.5.2_Portable.zip",
                new Uri(asset),
                new Uri(asset + ".sha256"),
                4,
                string.Empty);
            int requests = 0;
            using var handler = new ResponseHandler(() => limitPackage && ++requests == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('a', 64) + "  " + release.Name) }
                : Response(429, null, null));
            using var http = new HttpClient(handler);
            var destination = Path.Combine(temp, "package.zip");

            var error = await Assert.ThrowsExactlyAsync<UpdateException>(() => new ReleaseClient(http).DownloadAsync(release, destination, _ => { }, CancellationToken.None));

            Assert.AreEqual("rateLimited", error.Code);
            Assert.AreEqual(limitPackage ? 2 : 1, handler.Requests.Count);
            Assert.AreEqual(release.Checksum, handler.Requests[0]);
            if (limitPackage)
            {
                Assert.AreEqual(release.Download, handler.Requests[1]);
            }

            Assert.IsFalse(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CheckPersistsRateLimitAndRetainsPreparedPackage(bool readyToInstall)
    {
        var temp = Directory.CreateTempSubdirectory("KitUpdaterRateLimitTests-").FullName;
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(temp, "installed")).FullName;
            File.WriteAllText(Path.Combine(root, "Kit.exe"), "test installation");
            var storage = new UpdateStorage(root, Path.Combine(temp, "data"));
            var state = new UpdateState
            {
                State = readyToInstall ? UpdateStatus.ReadyToInstall : UpdateStatus.UpToDate,
                TargetVersion = "3.5.2",
                PackageSha256 = new string('a', 64),
                DownloadedInstallerFilename = "kit_3.5.2_Portable.zip",
            };
            File.WriteAllText(storage.Package, "verified package fixture");
            storage.Save(state);
            using var handler = new ResponseHandler(() => Response(403, "X-RateLimit-Remaining", "0"));
            using var http = new HttpClient(handler);

            await new UpdateService(storage, new ReleaseClient(http)).CheckAsync(CancellationToken.None);

            var result = storage.Read();
            Assert.AreEqual("rateLimited", result.ErrorCode);
            Assert.AreEqual(readyToInstall ? UpdateStatus.ReadyToInstall : UpdateStatus.NetworkError, result.State);
            Assert.IsTrue(long.TryParse(result.GithubUpdateLastCheckedDate, out var checkedAt) && checkedAt > 0);
            Assert.AreEqual(state.TargetVersion, result.TargetVersion);
            Assert.AreEqual(state.PackageSha256, result.PackageSha256);
            Assert.AreEqual(state.DownloadedInstallerFilename, result.DownloadedInstallerFilename);
            Assert.AreEqual("verified package fixture", File.ReadAllText(storage.Package));
            Assert.AreEqual(1, handler.Requests.Count);
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static HttpResponseMessage Response(int status, string? header, string? value)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        if (header != null)
        {
            response.Headers.TryAddWithoutValidation(header, value);
        }

        return response;
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(response());
        }
    }
}
