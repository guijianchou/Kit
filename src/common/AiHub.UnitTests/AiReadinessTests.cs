// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using System;
using System.IO;
using Kit.AiHub.Contract;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Readiness reporting for the shared AI service. A caller decides whether to spend time
/// collecting evidence based on this answer, so its classification is pinned here.
/// </summary>
[TestClass]
public sealed class AiReadinessTests
{
    [TestMethod]
    public void CanAttemptCoversEveryUsableLevel()
    {
        Assert.IsTrue(Ready(AiReadinessLevel.Ready).CanAttempt);
        Assert.IsTrue(Ready(AiReadinessLevel.Degraded).CanAttempt, "A usable fallback still allows a call.");
        Assert.IsTrue(Ready(AiReadinessLevel.Unverified).CanAttempt, "An unverified route is attempted optimistically.");
    }

    [TestMethod]
    public void CanAttemptRejectsUnusableLevels()
    {
        Assert.IsFalse(Ready(AiReadinessLevel.Disabled).CanAttempt);
        Assert.IsFalse(Ready(AiReadinessLevel.NotConfigured).CanAttempt);
    }

    [TestMethod]
    public void OnlyReadyCountsAsVerified()
    {
        Assert.IsTrue(Ready(AiReadinessLevel.Ready).IsVerifiedReady);
        Assert.IsFalse(Ready(AiReadinessLevel.Degraded).IsVerifiedReady, "A degraded route is usable but not fully verified.");
        Assert.IsFalse(Ready(AiReadinessLevel.Unverified).IsVerifiedReady);
    }

    [TestMethod]
    public void DefaultsDescribeAnUnprobedRoute()
    {
        var readiness = new AiReadiness(AiReadinessLevel.Unverified, "codex", "gpt-test");

        Assert.IsNull(readiness.VerifiedAtUtc, "Nothing has verified this route yet.");
        Assert.IsNull(readiness.ProbeLatency);
        Assert.IsFalse(readiness.FromCache);
        Assert.IsNull(readiness.Detail);
    }

    [TestMethod]
    public void CachedResultCanBeFlaggedWithoutLosingDetail()
    {
        DateTimeOffset verified = DateTimeOffset.UtcNow;
        var fresh = new AiReadiness(AiReadinessLevel.Ready, "codex", "gpt-test", null, verified, TimeSpan.FromMilliseconds(900));

        AiReadiness cached = fresh with { FromCache = true };

        Assert.IsTrue(cached.FromCache);
        Assert.AreEqual(verified, cached.VerifiedAtUtc, "Serving from cache must preserve when it was verified.");
        Assert.AreEqual(TimeSpan.FromMilliseconds(900), cached.ProbeLatency);
    }

    private static AiReadiness Ready(AiReadinessLevel level) =>
        new(level, "codex", "gpt-test");
}
