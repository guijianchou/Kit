// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Kit.AiHub.UnitTests;

using System;
using System.Collections.Generic;
using Kit.AIHubLib.Models;
using Kit.AIHubLib.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class WerEventParsingTests
{
    private const string AudiodgMessage = @"Faulting application name: AUDIODG.EXE, version: 10.0.26100.9444, time stamp: 0x966f569f
Faulting module name: NahimicAPO4.dll, version: 4.17.8.0, time stamp: 0x69ea3a87
Exception code: 0xc0000005
Fault offset: 0x0000000000007baf
Faulting process id: 0x571C
Faulting application path: C:\Windows\system32\AUDIODG.EXE
Faulting module path: C:\Windows\system32\NahimicAPO4.dll
Report Id: 5aec5527-d101-4abc-965d-278ce173c997";

    [TestMethod]
    public void Event1000NamesTheFaultingApplicationAndModule()
    {
        var events = new List<SecurityEvent>
        {
            new(1000, "Application", "Application Error", 2, DateTime.UtcNow, AudiodgMessage),
        };

        var issues = AuditRuleEngine.Analyze(events);

        Assert.AreEqual(1, issues.Count);
        var issue = issues[0];
        StringAssert.Contains(issue.Title, "AUDIODG.EXE", "The crash target must be named.");
        StringAssert.Contains(issue.Title, "NahimicAPO4.dll", "The faulting module must be named.");
        StringAssert.Contains(issue.RootCause, "NahimicAPO4.dll");
        StringAssert.Contains(issue.RootCause, "0xc0000005", "The exception code must be surfaced.");
        StringAssert.Contains(issue.TitleZh, "AUDIODG.EXE");
        StringAssert.Contains(issue.TitleZh, "NahimicAPO4.dll");
    }

    [TestMethod]
    public void AudioModuleCrashGivesDriverGuidance()
    {
        var events = new List<SecurityEvent>
        {
            new(1000, "Application", "Application Error", 2, DateTime.UtcNow, AudiodgMessage),
        };

        var issues = AuditRuleEngine.Analyze(events);

        // An APO is a driver-side component, so "update the app" advice would be wrong.
        StringAssert.Contains(issues[0].Recommendation, "devmgmt.msc");
        Assert.IsFalse(issues[0].Recommendation.Contains("Visual C++ Redistributable"), "Driver faults must not get runtime-redistributable advice.");
    }

    [TestMethod]
    public void Event1000WithoutWerDetailStillProducesAFinding()
    {
        var events = new List<SecurityEvent>
        {
            new(1000, "Application", "SomeApp", 2, DateTime.UtcNow, "Application crashed."),
        };

        var issues = AuditRuleEngine.Analyze(events);

        Assert.AreEqual(1, issues.Count);
        StringAssert.Contains(issues[0].Title, "SomeApp", "Falls back to the provider when WER detail is absent.");
    }
}
