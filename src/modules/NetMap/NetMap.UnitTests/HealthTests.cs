// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetMapLib;

namespace NetMap.UnitTests;

[TestClass]
public sealed class HealthTests
{
    [DataTestMethod]
    [DataRow(0.0, ProbeHealth.Good)]
    [DataRow(75.0, ProbeHealth.Good)]
    [DataRow(75.1, ProbeHealth.Warning)]
    [DataRow(1000.0, ProbeHealth.Warning)]
    public void ServiceAndHopShareTheInclusive75msBoundary(double milliseconds, ProbeHealth expected)
    {
        Assert.AreEqual(expected, new ServiceResult("Google", true, ProbeError.None, 200, DateTimeOffset.Now, "PageResponded", milliseconds).Health);
        Assert.AreEqual(expected, new HopResult(1, "192.0.2.1", 1, 1, milliseconds, milliseconds, true).Health);
    }

    [DataTestMethod]
    [DataRow(403, "BrowserCheck")]
    [DataRow(429, "RateLimited")]
    [DataRow(200, "SignIn")]
    [DataRow(200, "TraceInvalid")]
    public void FastHttpResponseCannotHideProblems(int status, string outcome)
    {
        Assert.AreEqual(ProbeHealth.Warning, new ServiceResult("Gemini", true, ProbeError.None, status, DateTimeOffset.Now, outcome, 12).Health);
    }

    [TestMethod]
    public void ErrorsAreRedAndPartialPagesAreYellow()
    {
        Assert.AreEqual(ProbeHealth.Error, new ServiceResult("Google", true, ProbeError.Timeout, null, DateTimeOffset.Now).Health);
        Assert.AreEqual(ProbeHealth.Error, new ServiceResult("Google", true, ProbeError.None, 503, DateTimeOffset.Now, "ServerError", 12).Health);
        Assert.AreEqual(ProbeHealth.Warning, new ServiceResult("Google", true, ProbeError.None, 200, DateTimeOffset.Now, "PageResponded", 12, BodyIncomplete: true).Health);
        Assert.AreEqual(ProbeHealth.Error, new HopResult(1, "192.0.2.1", 2, 1, null, 12, false).Health);
    }

    [TestMethod]
    public async Task HopDropsLastRttOnThirdFailureAndRecoversWithoutLosingStatistics()
    {
        using var cancellation = new CancellationTokenSource();
        var samples = new List<HopResult>();
        int attempt = 0;
        await Assert.ThrowsAsync<TaskCanceledException>(() => RouteProbe.SampleAsync(
            rows =>
            {
                samples.Add(rows[0]);
                if (samples.Count == 5)
                {
                    cancellation.Cancel();
                }
            },
            (_, _) => Task.FromResult<(string, double?, bool)>(++attempt is 1 or 5 ? ("192.0.2.1", 25, true) : (string.Empty, null, false)),
            TimeSpan.Zero,
            cancellation.Token));
        Assert.AreEqual(25.0, samples[1].LastKnownMs);
        Assert.AreEqual(25.0, samples[2].LastKnownMs);
        Assert.AreEqual(3, samples[3].ConsecutiveFailures);
        Assert.IsNull(samples[3].LastKnownMs);
        Assert.AreEqual(0, samples[4].ConsecutiveFailures);
        Assert.AreEqual(ProbeHealth.Good, samples[4].Health);
        Assert.AreEqual(5, samples[4].Sent);
        Assert.AreEqual(60.0, samples[4].NoResponsePercent);
    }
}
