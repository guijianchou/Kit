using Microsoft.VisualStudio.TestTools.UnitTesting;
using UDPtestLib.Core;

namespace UDPtest.UnitTests
{
    [TestClass]
    public sealed class MetricsEngineTests
    {
        [TestMethod]
        public void Apply_SuccessfulProbes_CalculatesMetricsCorrectly()
        {
            MetricsEngine engine = new();
            var line = new ProbeLineDefinition("udp_1", "UDP 1", ProbeProtocol.Udp, ProbeKind.StunBinding, "stun.cloudflare.com", 3478, TargetLabel: "Cloudflare");
            engine.Reset([line], 1000);

            var now = DateTimeOffset.UtcNow;
            LineMetricSnapshot? snapshot = null;
            for (int i = 1; i <= 5; i++)
            {
                ProbeResult result = new(
                    "udp_1",
                    ProbeProtocol.Udp,
                    i,
                    now.AddSeconds(i),
                    i * 1000,
                    20.0 + (i % 2 == 0 ? 10.0 : 0.0),
                    ProbeOutcome.Success,
                    null);
                snapshot = engine.Apply(result);
            }

            Assert.IsNotNull(snapshot);
            Assert.AreEqual(5, snapshot.SuccessCount);
            Assert.AreEqual(0, snapshot.FailureCount);
            Assert.AreEqual(100, snapshot.QualityPercent);
            Assert.IsTrue(snapshot.AverageRttMilliseconds > 0);
        }

        [TestMethod]
        public void AssessUdp_AllOk_ReturnsSupportedStable()
        {
            MetricsEngine engine = new();
            var line = new ProbeLineDefinition("udp_1", "UDP 1", ProbeProtocol.Udp, ProbeKind.StunBinding, "stun.cloudflare.com", 3478, TargetLabel: "Cloudflare");
            engine.Reset([line], 1000);

            var now = DateTimeOffset.UtcNow;
            for (int i = 1; i <= 20; i++)
            {
                engine.Apply(new ProbeResult(
                    "udp_1",
                    ProbeProtocol.Udp,
                    i,
                    now.AddSeconds(i),
                    i * 1000,
                    15.0,
                    ProbeOutcome.Success,
                    null));
            }

            UdpAssessment assessment = engine.AssessUdp();
            Assert.IsTrue(assessment.State.Contains("Supported"));
        }
    }
}
