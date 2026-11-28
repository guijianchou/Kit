namespace Kit.AiHub.UnitTests;

using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class OptimizationProtectionTests
{
    [TestMethod]
    [DataRow("notes.docx")]
    [DataRow("private.pdf")]
    [DataRow(".env")]
    [DataRow(".env.local")]
    [DataRow("id_ed25519")]
    [DataRow("credentials.json")]
    [DataRow("settings.json")]
    [DataRow("appsettings.Production.json")]
    [DataRow("backup.bak")]
    [DataRow("notes.docx.tmp")]
    [DataRow(".env.tmp")]
    [DataRow(".npmrc")]
    [DataRow(".git-credentials")]
    [DataRow(".git")]
    public async Task ProtectedLeavesAreExcludedAndForgedApprovalCannotDeleteThem(string name)
    {
        using var fixture = new FixtureDirectory();
        string temp = fixture.PathFor("Temp");
        Directory.CreateDirectory(temp);
        string path = Path.Combine(temp, name);
        File.WriteAllText(path, "private");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        int writes = 0;
        var service = new CacheCleanupService(fixture.PathFor("Local"), temp, _ =>
        {
            writes++;
            return (true, null);
        });
        Assert.AreEqual(0, (await service.ScanAsync()).Count);
        var file = new FileInfo(path);
        var forged = new TempFileInfo
        {
            FilePath = path, FileName = name, CacheRoot = temp, SizeInBytes = file.Length, LastModified = file.LastWriteTimeUtc,
            Action = "delete", ReviewState = "approved", IsSelected = true, Risk = RiskLevel.Medium,
        };
        Assert.IsFalse(service.CleanupItem(forged).Success);
        Assert.AreEqual(0, writes);
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task CredentialDownloadsCannotBeMovedByForgedAiAdviceButDocumentsRemainOrganizable()
    {
        using var fixture = new FixtureDirectory();
        string root = fixture.PathFor("Downloads");
        Directory.CreateDirectory(root);
        foreach (string name in new[] { "credentials.json", "report.pdf" })
        {
            File.WriteAllText(Path.Combine(root, name), "fixture");
            File.SetLastWriteTimeUtc(Path.Combine(root, name), DateTime.UtcNow.AddDays(-1));
        }

        var service = new DownloadOrganizerService(root);
        CollectionAssert.AreEqual(new[] { "report.pdf" }, (await service.ScanAsync()).Select(item => item.FileName).ToArray());
        var secret = new FileInfo(Path.Combine(root, "credentials.json"));
        Assert.IsFalse(service.OrganizeItem(new TempFileInfo
        {
            FilePath = secret.FullName, FileName = secret.Name, SizeInBytes = secret.Length, LastModified = secret.LastWriteTimeUtc,
            Action = "move", TargetRelativePath = "Others", ReviewState = "approved", IsSelected = true,
        }));
        Assert.IsTrue(secret.Exists);
    }
}
