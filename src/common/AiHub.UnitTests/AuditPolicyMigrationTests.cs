namespace Kit.AiHub.UnitTests;

using System.IO;
using System.Threading.Tasks;
using Kit.AiHub.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AuditPolicyMigrationTests
{
    [TestMethod]
    public async Task Version2AddsStructuredChecksWithoutOverwritingCustomPolicy()
    {
        using var fixture = new FixtureDirectory();
        var service = new SecurityPolicyService(fixture.RootPath, fixture.PathFor("modules"));
        await service.SaveTaskPolicyAsync("security-audit", TaskPolicyDefaults.Version2SecurityAuditInstructions);
        service.EnsureDefaultPolicyExists();
        string migrated = await service.LoadTaskAgentsPolicyAsync("aihub", "security-audit");
        Assert.AreEqual(TaskPolicyDefaults.DefaultSecurityAuditInstructions, migrated);
        StringAssert.Contains(migrated, "how to verify recovery");
        StringAssert.Contains(migrated, "one issue per distinct problem");
        await service.SaveTaskPolicyAsync("security-audit", TaskPolicyDefaults.Version2SecurityAuditInstructions + "\nCustom rule.");
        var before = File.ReadAllBytes(service.GetTaskPolicyPath("security-audit"));
        service.EnsureDefaultPolicyExists();
        CollectionAssert.AreEqual(before, File.ReadAllBytes(service.GetTaskPolicyPath("security-audit")));
    }
}
