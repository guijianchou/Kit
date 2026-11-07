// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Kit.WingetLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winget.UnitTests;

[TestClass]
public sealed class WingetSafetyPolicyTests
{
    [TestMethod]
    [DataRow("user", "User", "ElevationProhibited", false, true)]
    [DataRow("machine", "User", "ElevationProhibited", false, false)]
    [DataRow("Unknown", "User", "ElevationProhibited", false, false)]
    [DataRow("user", "System", "ElevationProhibited", false, false)]
    [DataRow("user", "Unknown", "ElevationProhibited", false, false)]
    [DataRow("user", "User", "Unknown", false, false)]
    [DataRow("user", "User", "ElevationRequired", false, false)]
    [DataRow("user", "User", "ElevatesSelf", false, false)]
    [DataRow("user", "User", "ElevationProhibited", true, false)]
    public void OnlyExplicitUserInstallerCanPromiseNoElevation(string installedScope, string installerScope, string elevation, bool elevated, bool allowed)
    {
        var result = WingetSafetyPolicy.Evaluate(installedScope, installerScope, elevation, elevated);
        Assert.AreEqual(allowed, result.Eligibility == WingetInstallEligibility.UserWithoutElevation);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Reason));
        Assert.AreNotEqual(WingetInstallEligibility.ManualOnly, result.Eligibility, "WinGet may handle native elevation after explicit user confirmation.");
    }

    [TestMethod]
    public void ManualCommandPreservesLiteralPowerShellMetacharacters()
    {
        var command = WingetSafetyPolicy.CreateManualUpgradeCommand("Vendor.App'$(calc);", "2.0", "winget");
        Assert.AreEqual("winget upgrade --id 'Vendor.App''$(calc);' --exact --version '2.0' --source 'winget' --interactive", command);
        var install = WingetSafetyPolicy.CreateManualInstallCommand("Vendor.App'$(calc);", "2.0", "winget");
        Assert.AreEqual("winget install --id 'Vendor.App''$(calc);' --exact --version '2.0' --source 'winget' --interactive --no-upgrade", install);
    }

    [TestMethod]
    public void ManualCommandRejectsControlCharacters()
    {
        Assert.ThrowsExactly<ArgumentException>(() => WingetSafetyPolicy.CreateManualUpgradeCommand("Vendor.App\r\n", "2.0", "winget"));
    }

    [DataTestMethod]
    [DataRow("Unknown", true, false, false)]
    [DataRow("unknown", false, false, false)]
    [DataRow("Unknown", false, true, true)]
    [DataRow("1.0", true, false, true)]
    [DataRow("2.0", false, true, false)]
    [DataRow("", true, true, false)]
    public void UnknownVersionRequiresExplicitOptInAndCannotPermitKnownDowngrade(string installedVersion, bool isNewer, bool includeUnknown, bool expected)
    {
        Assert.AreEqual(expected, WingetSafetyPolicy.CanUpgradeVersion(installedVersion, isNewer, includeUnknown));
    }

    [DataTestMethod]
    [DataRow(null, null)]
    [DataRow("user", "x86")]
    [DataRow("Machine", "x64")]
    [DataRow("User", "ARM64")]
    public void SupportedOptionsAreNormalizedWithoutChangingConsentFlags(string? scope, string? architecture)
    {
        var validated = WingetSafetyPolicy.ValidateOptions(new() { Scope = scope, Architecture = architecture, Silent = true, IncludeUnknown = true });
        Assert.AreEqual(scope?.ToLowerInvariant(), validated.Scope);
        Assert.AreEqual(architecture?.ToLowerInvariant(), validated.Architecture);
        Assert.IsTrue(validated.Silent);
        Assert.IsTrue(validated.IncludeUnknown);
    }
}
