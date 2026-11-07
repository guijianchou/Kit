namespace Kit.AiHub.UnitTests;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Kit.AiHub.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class PolicyTemplateMigrationTests
{
    [TestMethod]
    [DataRow(false, "\n")]
    [DataRow(true, "\r\n")]
    public async Task ShippedDefaultsMigrateTogetherAndAreIdempotent(bool bom, string newline)
    {
        using var fixture = new FixtureDirectory();
        string data = fixture.PathFor("data");
        Write(Path.Combine(data, "security.md"), PreviousGlobalPolicy(), bom, newline);
        Write(Path.Combine(data, "chains", "security-audit", "AGENTS.md"), TaskPolicyDefaults.PreviousSecurityAuditInstructions, bom, newline);
        Write(Path.Combine(data, "chains", "system-optimization", "AGENTS.md"), TaskPolicyDefaults.SnapshotSystemOptimizationInstructions, bom, newline);
        Write(Path.Combine(data, "chains", SelfTestPolicy.TaskId, "AGENTS.md"), SelfTestPolicy.PreviousInstructions, bom, newline);

        var service = new SecurityPolicyService(data, fixture.PathFor("modules"));
        Assert.AreEqual(SecurityPolicyService.GetDefaultPolicyContent(), await service.LoadGlobalPolicyAsync());
        foreach (string taskId in new[] { "security-audit", "system-optimization", SelfTestPolicy.TaskId })
        {
            Assert.AreEqual(SecurityPolicyService.GetDefaultTaskPolicy(taskId), await service.LoadTaskAgentsPolicyAsync("aihub", taskId));
        }

        string[] paths = [service.GlobalSecurityPolicyPath, service.GetTaskPolicyPath("security-audit"), service.GetTaskPolicyPath("system-optimization"), service.GetTaskPolicyPath(SelfTestPolicy.TaskId)];
        byte[][] firstMigration = paths.Select(File.ReadAllBytes).ToArray();
        service.EnsureDefaultPolicyExists();
        for (int index = 0; index < paths.Length; index++)
        {
            CollectionAssert.AreEqual(firstMigration[index], File.ReadAllBytes(paths[index]), paths[index]);
        }
    }

    [TestMethod]
    public async Task OneCharacterCustomizationsArePreservedByteForByte()
    {
        using var fixture = new FixtureDirectory();
        string data = fixture.PathFor("data");
        string[] paths =
        [
            Path.Combine(data, "security.md"),
            Path.Combine(data, "chains", "security-audit", "AGENTS.md"),
            Path.Combine(data, "chains", "system-optimization", "AGENTS.md"),
            Path.Combine(data, "chains", SelfTestPolicy.TaskId, "AGENTS.md"),
        ];
        string[] contents = [PreviousGlobalPolicy(), TaskPolicyDefaults.PreviousSecurityAuditInstructions, TaskPolicyDefaults.SnapshotSystemOptimizationInstructions, SelfTestPolicy.PreviousInstructions];
        for (int index = 0; index < paths.Length; index++)
        {
            Write(paths[index], contents[index] + "!", bom: true, "\r\n");
        }

        byte[][] original = paths.Select(File.ReadAllBytes).ToArray();
        var service = new SecurityPolicyService(data, fixture.PathFor("modules"));
        service.EnsureDefaultPolicyExists();
        Assert.IsTrue((await service.LoadGlobalPolicyAsync()).EndsWith('!'));
        for (int index = 0; index < paths.Length; index++)
        {
            CollectionAssert.AreEqual(original[index], File.ReadAllBytes(paths[index]), paths[index]);
        }
    }

    [TestMethod]
    public async Task SelfTestSupportsFallbackAndExplicitReset()
    {
        using var fixture = new FixtureDirectory();
        var service = new SecurityPolicyService(fixture.PathFor("data"), fixture.PathFor("modules"));
        File.Delete(service.GetTaskPolicyPath(SelfTestPolicy.TaskId));
        Assert.AreEqual(SelfTestPolicy.Instructions, await service.LoadTaskAgentsPolicyAsync("aihub", SelfTestPolicy.TaskId));
        Assert.AreEqual(SelfTestPolicy.Instructions, await service.LoadTaskAgentsPolicyAsync("kit", SelfTestPolicy.TaskId));
        Assert.AreEqual(string.Empty, await service.LoadTaskAgentsPolicyAsync("external-plugin", SelfTestPolicy.TaskId));

        await service.SaveTaskPolicyAsync(SelfTestPolicy.TaskId, "custom self-test policy");
        Assert.AreEqual("custom self-test policy", await service.LoadTaskAgentsPolicyAsync("aihub", SelfTestPolicy.TaskId));
        service.ResetTaskPolicy(SelfTestPolicy.TaskId);
        Assert.AreEqual(SelfTestPolicy.Instructions, await service.LoadTaskAgentsPolicyAsync("aihub", SelfTestPolicy.TaskId));
    }

    private static string PreviousGlobalPolicy()
    {
        var assembly = typeof(PolicyTemplateMigrationTests).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("Fixtures.Policies.global-v1.md", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Write(string path, string content, bool bom, string newline)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.ReplaceLineEndings(newline), new UTF8Encoding(bom));
    }
}
