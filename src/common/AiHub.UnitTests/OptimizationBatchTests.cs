namespace Kit.AiHub.UnitTests;

using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class OptimizationBatchTests
{
    [TestMethod]
    public void DevelopmentExecutionQueriesEachRepositoryOnceAndHonorsCancellation()
    {
        using var fixture = new FixtureDirectory();
        string root = fixture.PathFor("Repos");
        string repository = Path.Combine(root, "Example");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        File.WriteAllText(Path.Combine(repository, "Example.sln"), string.Empty);
        var items = new[] { "one.obj", "two.obj" }.Select(name => new TempFileInfo
        {
            FilePath = Path.Combine(repository, "obj", name), DevelopmentRepository = repository, Action = "delete",
        }).ToArray();
        int queries = 0;
        var allowed = DevelopmentCacheScanner.CollectIgnoredPaths(items, root, CancellationToken.None, (repo, paths, token) =>
        {
            queries++;
            Assert.AreEqual(repository, repo);
            Assert.AreEqual(2, paths.Count);
            return paths[0] + '\0' + "../unselected.obj\0";
        });
        Assert.AreEqual(1, queries);
        Assert.AreEqual(1, allowed.Count, "Unsolicited paths are not accepted from Git output.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => DevelopmentCacheScanner.CollectIgnoredPaths(items, root, cancelled.Token,
            (_, _, _) =>
            {
                queries++;
                return string.Empty;
            }));
        Assert.AreEqual(1, queries, "A cancelled batch must not start a Git process.");
    }

    [TestMethod]
    public void BatchGitEvidenceDoesNotBypassCurrentLeafEvidence()
    {
        using var fixture = new FixtureDirectory();
        string root = fixture.PathFor("Repos");
        string repository = Path.Combine(root, "Example");
        string cache = Path.Combine(repository, "__pycache__", "module.cpython-314.pyc");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        string source = Path.Combine(repository, "module.py");
        File.WriteAllText(source, "pass");
        File.WriteAllText(cache, "bytecode");
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { cache };
        Assert.IsTrue(DevelopmentCacheScanner.IsEligible(new FileInfo(cache), root, repository, ignoredPaths: ignored));
        File.Delete(source);
        Assert.IsFalse(DevelopmentCacheScanner.IsEligible(new FileInfo(cache), root, repository, ignoredPaths: ignored),
            "A batched Git verdict must not bypass source/project validation immediately before execution.");
    }
}
