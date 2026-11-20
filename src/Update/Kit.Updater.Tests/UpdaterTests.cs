using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kit.Updater;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace Kit.Updater.Tests;

[TestClass]
public sealed class UpdaterTests
{
    private string temp = null!;
    private string Root => Path.Combine(temp, "installed");
    private string Payload => Path.Combine(temp, "payload");
    private string Work => Path.Combine(temp, "transaction");
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    [TestInitialize]
    public void Setup()
    {
        temp = Path.Combine(Path.GetTempPath(), "KitUpdaterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Payload);
        File.WriteAllText(Path.Combine(Root, "Kit.exe"), "old executable");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(temp, true);

    [TestMethod]
    [DataRow("3.5.2")]
    [DataRow("v3.5.2")]
    public void StableReleaseSelectsExactPortableAsset(string tag)
    {
        var release = ReleaseClient.Parse(ReleaseJson(tag));
        Assert.AreEqual(new Version(3, 5, 2), release.Version);
        Assert.AreEqual("kit_3.5.2_Portable.zip", release.Name);
    }

    [TestMethod]
    [DataRow("3.5")]
    [DataRow("3.5.2.1")]
    [DataRow("vv3.5.2")]
    [DataRow("3.05.2")]
    public void MalformedReleaseVersionsAreRejected(string tag) => Assert.ThrowsExactly<UpdateException>(() => ReleaseClient.Parse(ReleaseJson(tag)));

    [TestMethod]
    public void ForeignAssetAndPrereleaseAreRejected()
    {
        Assert.ThrowsExactly<UpdateException>(() => ReleaseClient.Parse(ReleaseJson().Replace("/releases/download/", "/another/download/", StringComparison.Ordinal)));
        Assert.ThrowsExactly<UpdateException>(() => ReleaseClient.Parse(ReleaseJson().Replace("\"prerelease\":false", "\"prerelease\":true", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void MissingChecksumAndDuplicateArchiveAreRejected()
    {
        var json = JsonNodeRelease();
        var assets = json["assets"]!.AsArray();
        assets.RemoveAt(1);
        Assert.ThrowsExactly<UpdateException>(() => ReleaseClient.Parse(json.ToJsonString()));
        json = JsonNodeRelease();
        assets = json["assets"]!.AsArray();
        assets.Add(assets[0]!.DeepClone());
        Assert.ThrowsExactly<UpdateException>(() => ReleaseClient.Parse(json.ToJsonString()));
    }

    [TestMethod]
    public void ChecksumMustNameExactArchive()
    {
        Assert.ThrowsExactly<UpdateException>(() => ReleaseClient.ParseChecksum(new string('a', 64) + "  other.zip", "kit_3.5.2_Portable.zip"));
    }

    [TestMethod]
    public async Task DownloadVerifiesBytesAndReportsProgress()
    {
        byte[] data = new byte[400000];
        Random.Shared.NextBytes(data);
        using var http = Client(data);
        var release = ReleaseClient.Parse(ReleaseJson(size: data.Length, digest: Hash(data)));
        long progress = 0;
        var path = Path.Combine(temp, "download.zip");
        var hash = await new ReleaseClient(http).DownloadAsync(release, path, count => progress = count, CancellationToken.None);
        Assert.AreEqual(data.Length, progress);
        Assert.AreEqual(Hash(data), hash);
        CollectionAssert.AreEqual(data, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task CorruptedAndTruncatedDownloadsAreRejected()
    {
        byte[] data = [1, 2, 3, 4];
        using var http = Client(data, checksum: new string('a', 64));
        var release = ReleaseClient.Parse(ReleaseJson(size: 4));
        await Assert.ThrowsExactlyAsync<UpdateException>(() => new ReleaseClient(http).DownloadAsync(release, Path.Combine(temp, "bad.zip"), _ => { }, CancellationToken.None));
        using var shortHttp = Client(data);
        release = ReleaseClient.Parse(ReleaseJson(size: 5));
        await Assert.ThrowsExactlyAsync<UpdateException>(() => new ReleaseClient(shortHttp).DownloadAsync(release, Path.Combine(temp, "short.zip"), _ => { }, CancellationToken.None));
    }

    [TestMethod]
    public async Task DigestDisagreementAndCancellationAreRejected()
    {
        byte[] data = [1, 2, 3];
        using var http = Client(data);
        var release = ReleaseClient.Parse(ReleaseJson(size: 3, digest: new string('a', 64)));
        await Assert.ThrowsExactlyAsync<UpdateException>(() => new ReleaseClient(http).DownloadAsync(release, Path.Combine(temp, "digest.zip"), _ => { }, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ReleaseClient(http).DownloadAsync(release, Path.Combine(temp, "cancel.zip"), _ => { }, cancellation.Token));
    }

    [TestMethod]
    [DataRow("../escape")]
    [DataRow("C:/escape")]
    [DataRow("/escape")]
    [DataRow("file:stream")]
    [DataRow("NUL.txt")]
    [DataRow("dir/trailing.")]
    [DataRow("dir/../escape")]
    public void ArchivePathTraversalIsRejected(string entry)
    {
        var archive = Path.Combine(temp, "unsafe.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntry(entry);
        }

        Assert.ThrowsExactly<UpdateException>(() => PortablePackage.Extract(archive, Path.Combine(temp, "expanded"), new Version(3, 5, 2), CancellationToken.None));
        Assert.IsFalse(File.Exists(Path.Combine(temp, "escape")));
    }

    [TestMethod]
    public void CaseInsensitiveDuplicateZipEntryIsRejected()
    {
        var archive = Path.Combine(temp, "duplicate.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntry("Kit.exe");
            zip.CreateEntry("kit.exe");
        }

        Assert.ThrowsExactly<UpdateException>(() => PortablePackage.Extract(archive, Path.Combine(temp, "expanded"), new Version(3, 5, 2), CancellationToken.None));
    }

    [TestMethod]
    public void SymlinkZipEntryIsRejected()
    {
        var archive = Path.Combine(temp, "link.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntry("link").ExternalAttributes = unchecked((int)0xA1FF0000);
        }

        Assert.ThrowsExactly<UpdateException>(() => PortablePackage.Extract(archive, Path.Combine(temp, "expanded"), new Version(3, 5, 2), CancellationToken.None));
    }

    [TestMethod]
    public void CompletePackageChecksInventoryHashesAndVersions()
    {
        var version = CreateExecutablePayload();
        WriteManifest(Payload, version.ToString(3));
        var zip = Path.Combine(temp, "valid.zip");
        ZipFile.CreateFromDirectory(Payload, zip);
        var manifest = PortablePackage.Extract(zip, Path.Combine(temp, "expanded"), version, CancellationToken.None);
        Assert.HasCount(3, manifest.Files);
        Assert.ThrowsExactly<UpdateException>(() => PortablePackage.Extract(zip, Path.Combine(temp, "wrong-version"), new Version(99, 0, 0), CancellationToken.None));
    }

    [TestMethod]
    public void UnlistedFilesAndTamperedManifestHashesAreRejected()
    {
        var version = CreateExecutablePayload();
        WriteManifest(Payload, version.ToString(3));
        File.WriteAllText(Path.Combine(Payload, "surprise.dll"), "unexpected");
        var zip = Path.Combine(temp, "extra.zip");
        ZipFile.CreateFromDirectory(Payload, zip);
        Assert.ThrowsExactly<UpdateException>(() => PortablePackage.Extract(zip, Path.Combine(temp, "extra"), version, CancellationToken.None));
        File.Delete(Path.Combine(Payload, "surprise.dll"));
        File.AppendAllText(Path.Combine(Payload, "Kit.exe"), "tampered");
        zip = Path.Combine(temp, "tampered.zip");
        ZipFile.CreateFromDirectory(Payload, zip);
        Assert.ThrowsExactly<UpdateException>(() => PortablePackage.Extract(zip, Path.Combine(temp, "tampered"), version, CancellationToken.None));
    }

    [TestMethod]
    public void SuccessfulReplacementPreservesUserFilesAndRemovesUnmodifiedObsoleteFiles()
    {
        File.WriteAllText(Path.Combine(Root, "obsolete.dll"), "old dependency");
        File.WriteAllText(Path.Combine(Root, "modified.dll"), "original");
        WriteManifest(Root);
        File.WriteAllText(Path.Combine(Root, "modified.dll"), "user changed");
        File.WriteAllText(Path.Combine(Root, "personal.txt"), "keep this");
        File.WriteAllText(Path.Combine(Payload, "Kit.exe"), "new executable");
        File.WriteAllText(Path.Combine(Payload, "new.dll"), "new dependency");
        var manifest = WriteManifest(Payload);
        var restarted = false;
        new InstallTransaction(Root, Work).Apply(Payload, manifest, () => restarted = File.ReadAllText(Path.Combine(Root, "Kit.exe")) == "new executable");
        Assert.IsTrue(restarted);
        Assert.IsFalse(File.Exists(Path.Combine(Root, "obsolete.dll")));
        Assert.AreEqual("user changed", File.ReadAllText(Path.Combine(Root, "modified.dll")));
        Assert.AreEqual("keep this", File.ReadAllText(Path.Combine(Root, "personal.txt")));
    }

    [TestMethod]
    public void PartialReplacementFailureRestoresOriginalAndRemovesNewFiles()
    {
        File.WriteAllText(Path.Combine(Root, "user.json"), "settings");
        File.WriteAllText(Path.Combine(Payload, "a-new.dll"), "new");
        File.WriteAllText(Path.Combine(Payload, "Kit.exe"), "new executable");
        var manifest = WriteManifest(Payload);
        var transaction = new InstallTransaction(Root, Work);
        Assert.ThrowsExactly<IOException>(() => transaction.Apply(Payload, manifest, afterReplace: count =>
        {
            if (count == 2)
            {
                throw new IOException("Injected disk failure");
            }
        }));
        Assert.AreEqual("old executable", File.ReadAllText(Path.Combine(Root, "Kit.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(Root, "a-new.dll")));
        Assert.AreEqual("settings", File.ReadAllText(Path.Combine(Root, "user.json")));
        Assert.IsFalse(transaction.NeedsRecovery);
    }

    [TestMethod]
    public void FailedRestartRollsBackReplacement()
    {
        File.WriteAllText(Path.Combine(Payload, "Kit.exe"), "new executable");
        Assert.ThrowsExactly<IOException>(() => new InstallTransaction(Root, Work).Apply(Payload, WriteManifest(Payload), () => throw new IOException("Could not restart")));
        Assert.AreEqual("old executable", File.ReadAllText(Path.Combine(Root, "Kit.exe")));
    }

    [TestMethod]
    public void LockedFileStopsBeforeAnyReplacement()
    {
        File.WriteAllText(Path.Combine(Payload, "Kit.exe"), "new executable");
        var manifest = WriteManifest(Payload);
        using (var locked = new FileStream(Path.Combine(Root, "Kit.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsExactly<IOException>(() => new InstallTransaction(Root, Work).Apply(Payload, manifest));
        }

        Assert.AreEqual("old executable", File.ReadAllText(Path.Combine(Root, "Kit.exe")));
    }

    [TestMethod]
    public void DurableJournalRecoversInterruptedReplacement()
    {
        Directory.CreateDirectory(Path.Combine(Work, "backup"));
        File.Copy(Path.Combine(Root, "Kit.exe"), Path.Combine(Work, "backup", "Kit.exe"));
        UpdateStorage.WriteJson(
            Path.Combine(Work, "journal.json"),
            new TransactionJournal
            {
                InstallRoot = Root,
                Files = [new JournalFile { Path = "Kit.exe", Existed = true }, new JournalFile { Path = "new.dll", Existed = false }],
            },
            UpdateJson.Default.TransactionJournal);
        File.WriteAllText(Path.Combine(Root, "Kit.exe"), "partially installed");
        File.WriteAllText(Path.Combine(Root, "new.dll"), "new");
        var recovery = new InstallTransaction(Root, Work);
        Assert.IsTrue(recovery.NeedsRecovery);
        recovery.Recover();
        recovery.Recover();
        Assert.AreEqual("old executable", File.ReadAllText(Path.Combine(Root, "Kit.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(Root, "new.dll")));
    }

    [TestMethod]
    public async Task RecheckingPreservesVerifiedDownloadAndNetworkFailureAllowsInstall()
    {
        var storage = new UpdateStorage(Root, Path.Combine(temp, "data"));
        File.WriteAllText(storage.Package, "download");
        storage.Save(new UpdateState { State = UpdateStatus.ReadyToInstall, TargetVersion = "3.5.2", PackageSha256 = new string('a', 64), DownloadedInstallerFilename = "kit_3.5.2_Portable.zip" });
        using var client = Client([]);
        await new UpdateService(storage, new ReleaseClient(client)).CheckAsync(CancellationToken.None);
        Assert.AreEqual(UpdateStatus.ReadyToInstall, storage.Read().State);
        Assert.AreEqual("kit_3.5.2_Portable.zip", storage.Read().DownloadedInstallerFilename);
        using var offline = new HttpClient(new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await new UpdateService(storage, new ReleaseClient(offline)).CheckAsync(CancellationToken.None);
        Assert.AreEqual(UpdateStatus.ReadyToInstall, storage.Read().State);
    }

    [TestMethod]
    public void StaleProgressRecoversAndStateIsBoundToInstallation()
    {
        var storage = new UpdateStorage(Root, Path.Combine(temp, "data"));
        storage.Save(new UpdateState { State = UpdateStatus.Downloading, TargetVersion = "99.0.0", DownloadedBytes = 500 });
        using var http = Client([]);
        new UpdateService(storage, new ReleaseClient(http)).Refresh();
        Assert.AreEqual("interrupted", storage.Read().ErrorCode);
        Assert.AreEqual(UpdateStatus.ErrorDownloading, storage.Read().State);
        var other = Path.Combine(temp, "another-installation");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "Kit.exe"), "other");
        Assert.AreEqual(UpdateStatus.UpToDate, new UpdateStorage(other, storage.DataRoot).Read().State);
    }

    [TestMethod]
    public async Task ServiceDownloadReachesReadyToInstallAndKeepsConfiguration()
    {
        var version = CreateExecutablePayload();
        WriteManifest(Payload, version.ToString(3));
        var archive = Path.Combine(temp, "release.zip");
        ZipFile.CreateFromDirectory(Payload, archive);
        var bytes = File.ReadAllBytes(archive);
        var storage = new UpdateStorage(Root, Path.Combine(temp, "data"));
        File.WriteAllText(Path.Combine(storage.DataRoot, "settings.json"), "user settings");
        using var http = Client(bytes, version: version.ToString(3));
        await new UpdateService(storage, new ReleaseClient(http)).DownloadAsync(CancellationToken.None);
        Assert.AreEqual(UpdateStatus.ReadyToInstall, storage.Read().State);
        Assert.AreEqual(Hash(bytes), storage.Read().PackageSha256);
        Assert.AreEqual("user settings", File.ReadAllText(Path.Combine(storage.DataRoot, "settings.json")));
        Assert.IsFalse(File.Exists(storage.Package + ".part"));
    }

    [TestMethod]
    public void RepeatedChecksHaveMonotonicCompletionTimes()
    {
        var storage = new UpdateStorage(Root, Path.Combine(temp, "data"));
        var state = new UpdateState();
        storage.Checked(state);
        var first = long.Parse(state.GithubUpdateLastCheckedDate);
        storage.Checked(state);
        Assert.IsTrue(long.Parse(state.GithubUpdateLastCheckedDate) > first);
    }

    private static System.Text.Json.Nodes.JsonObject JsonNodeRelease() => System.Text.Json.Nodes.JsonNode.Parse(ReleaseJson())!.AsObject();

    private static string ReleaseJson(string tag = "3.5.2", long size = 100, string digest = "")
    {
        var name = $"kit_{tag.TrimStart('v')}_Portable.zip";
        var url = $"{ReleaseClient.Repository}/releases/download/{tag}/";
        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft = false,
            prerelease = false,
            assets = new[]
            {
                new { name, size, browser_download_url = url + name, digest = digest.Length == 0 ? null : "sha256:" + digest },
                new { name = name + ".sha256", size = 90L, browser_download_url = url + name + ".sha256", digest = (string?)null },
            },
        });
    }

    private static HttpClient Client(byte[] bytes, string? checksum = null, string version = "3.5.2") => new(new FakeHttp(request => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = request.RequestUri!.Host == "api.github.com" ? new StringContent(ReleaseJson(tag: version, size: Math.Max(bytes.Length, 1))) :
            request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) ? new StringContent($"{checksum ?? Hash(bytes)}  kit_{version}_Portable.zip\n") : new ByteArrayContent(bytes),
    }));

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }

    private Version CreateExecutablePayload()
    {
        var assembly = typeof(UpdaterTests).Assembly.Location;
        foreach (var relative in new[] { "Kit.exe", "Updater/Kit.Updater.exe", "WinUI3Apps/Kit.Settings.exe" })
        {
            var target = Path.Combine(Payload, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(assembly, target);
        }

        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
    }

    private static PortableManifest WriteManifest(string root, string version = "3.5.2")
    {
        var manifest = new PortableManifest
        {
            Version = version,
            Files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(path => Path.GetFileName(path) != PortablePackage.ManifestName).Select(path => new ManifestFile
            {
                Path = Path.GetRelativePath(root, path).Replace('\\', '/'),
                Sha256 = Paths.Hash(path),
            }).ToList(),
        };
        UpdateStorage.WriteJson(Path.Combine(root, PortablePackage.ManifestName), manifest, UpdateJson.Default.PortableManifest);
        return manifest;
    }
}
