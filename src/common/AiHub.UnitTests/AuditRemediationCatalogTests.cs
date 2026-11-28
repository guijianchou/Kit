namespace Kit.AiHub.UnitTests;

using System;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AuditRemediationCatalogTests
{
    [TestMethod]
    [DataRow(41, "System", "Microsoft-Windows-Kernel-Power")]
    [DataRow(1000, "Application", "Application Error")]
    [DataRow(1002, "Application", "Application Hang")]
    [DataRow(1001, "System", "Microsoft-Windows-WER-SystemErrorReporting")]
    [DataRow(7000, "System", "Service Control Manager")]
    [DataRow(7045, "System", "Service Control Manager")]
    [DataRow(10016, "System", "Microsoft-Windows-DistributedCOM")]
    [DataRow(1014, "System", "Microsoft-Windows-DNS-Client")]
    [DataRow(219, "System", "Microsoft-Windows-Kernel-PnP")]
    [DataRow(55, "System", "Ntfs")]
    [DataRow(4625, "Security", "Microsoft-Windows-Security-Auditing")]
    [DataRow(99999, "System", "Unknown provider")]
    public void LocalFindingsHaveAValidBilingualPlanWithoutAnAiRequest(int id, string log, string provider)
    {
        var finding = AuditRuleEngine.Analyze([new(id, log, provider, 2, DateTime.UtcNow, "Recorded fixture event.")])[0];
        Assert.AreEqual(provider, finding.Source);
        Assert.IsNotNull(finding.RemediationPlan);
        Assert.IsTrue(finding.RemediationPlan.IsValid);
        Assert.AreEqual(finding.RemediationPlan.ConditionalAction, finding.Recommendation);
        StringAssert.Contains(finding.RemediationPlan.Format(true), "恢复验证");
    }

    [TestMethod]
    public void SameEventIdFromAnUnrelatedProviderDoesNotOfferDcomPermissionAdvice()
    {
        var unrelated = new AuditIssueEnhanced { EventId = "10016", Source = "Unrelated application", LogName = "Application" };
        var dcom = new AuditIssueEnhanced { EventId = "10016", Affected = "Microsoft-Windows-DistributedCOM", LogName = "System" };
        StringAssert.Contains(dcom.EffectiveRemediationPlan.ConditionalAction, "no change is needed");
        Assert.IsFalse(unrelated.EffectiveRemediationPlan.ConditionalAction.Contains("no change is needed", StringComparison.Ordinal));
        Assert.IsNull(dcom.RemediationPlan, "Opening old history must not rewrite its saved evidence.");
    }

    [TestMethod]
    public void DnsGuideDoesNotBlindlyReplacePrivateDnsAndCrashGuideRequiresAttribution()
    {
        var dns = AuditRemediationCatalog.Resolve(new() { EventId = "1014", Source = "Microsoft-Windows-DNS-Client" });
        StringAssert.Contains(dns.Checks[0].ExpectedResult, "private/corporate");
        StringAssert.Contains(dns.RiskAndRecovery, "restore");
        Assert.IsFalse(dns.Format(false).Contains("1.1.1.1", StringComparison.Ordinal));
        var crash = AuditRemediationCatalog.Resolve(new() { EventId = "41", Source = "Microsoft-Windows-Kernel-Power" });
        StringAssert.Contains(crash.Checks[0].ExpectedResult, "does not prove");
        StringAssert.Contains(crash.ConditionalAction, "If a matching dump");
    }

    [TestMethod]
    public void ExistingAiPlanRemainsAuthoritativeAndOldRecommendationIsPreserved()
    {
        var plan = AuditRemediationCatalog.Resolve(new() { EventId = "1014", Source = "Microsoft-Windows-DNS-Client" });
        var finding = new AuditIssueEnhanced { EventId = "1014", RemediationPlan = plan, Recommendation = "Saved previous advice" };
        Assert.AreSame(plan, finding.EffectiveRemediationPlan);
        Assert.AreEqual("Saved previous advice", finding.Recommendation);
    }
}
