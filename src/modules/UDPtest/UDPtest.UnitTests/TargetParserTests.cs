using Microsoft.VisualStudio.TestTools.UnitTesting;
using UDPtestLib.Core;

namespace UDPtest.UnitTests
{
    [TestClass]
    public sealed class TargetParserTests
    {
        [TestMethod]
        public void UdpParser_ValidStunHostAndPort_ParsesCorrectly()
        {
            bool ok = UdpTargetParser.TryParse(
                "stun.cloudflare.com:3478",
                ProbeKind.StunBinding,
                out string host,
                out int port,
                out string canonical,
                out string error);

            Assert.IsTrue(ok);
            Assert.AreEqual("stun.cloudflare.com", host);
            Assert.AreEqual(3478, port);
            Assert.AreEqual("stun.cloudflare.com:3478", canonical);
            Assert.IsTrue(string.IsNullOrEmpty(error));
        }

        [TestMethod]
        public void UdpParser_ValidEchoIpAndPort_ParsesCorrectly()
        {
            bool ok = UdpTargetParser.TryParse(
                "150.242.146.124:7",
                ProbeKind.UdpEcho,
                out string host,
                out int port,
                out string canonical,
                out string error);

            Assert.IsTrue(ok);
            Assert.AreEqual("150.242.146.124", host);
            Assert.AreEqual(7, port);
            Assert.AreEqual("150.242.146.124:7", canonical);
            Assert.IsTrue(string.IsNullOrEmpty(error));
        }

        [TestMethod]
        public void UdpParser_RejectsInvalidPort_ZeroOrOutOfRange()
        {
            bool ok = UdpTargetParser.TryParse(
                "stun.cloudflare.com:0",
                ProbeKind.StunBinding,
                out _,
                out _,
                out _,
                out string error);

            Assert.IsFalse(ok);
            Assert.IsTrue(error.Contains("between 1 and 65535"));

            ok = UdpTargetParser.TryParse(
                "stun.cloudflare.com:70000",
                ProbeKind.StunBinding,
                out _,
                out _,
                out _,
                out error);

            Assert.IsFalse(ok);
        }

        [TestMethod]
        public void UdpParser_RejectsPathsAndFragments()
        {
            bool ok = UdpTargetParser.TryParse(
                "stun://stun.cloudflare.com:3478/path",
                ProbeKind.StunBinding,
                out _,
                out _,
                out _,
                out string error);

            Assert.IsFalse(ok);
            Assert.IsTrue(error.Contains("paths, credentials, query strings, and fragments are not allowed"));
        }

        [TestMethod]
        public void UdpParser_RejectsIpv6()
        {
            bool ok = UdpTargetParser.TryParse(
                "[2001:db8::1]:3478",
                ProbeKind.StunBinding,
                out _,
                out _,
                out _,
                out string error);

            Assert.IsFalse(ok);
            Assert.IsTrue(error.Contains("IPv4"));
        }

        [TestMethod]
        public void TcpParser_ValidHttpsEndpoint_ParsesCorrectly()
        {
            bool ok = TcpTargetParser.TryParse(
                "https://www.google.com/generate_204",
                out string canonical,
                out int port,
                out string error);

            Assert.IsTrue(ok);
            Assert.AreEqual("https://www.google.com/generate_204", canonical);
            Assert.AreEqual(443, port);
            Assert.IsTrue(string.IsNullOrEmpty(error));
        }

        [TestMethod]
        public void TcpParser_RejectsNonHttps()
        {
            bool ok = TcpTargetParser.TryParse(
                "http://www.google.com/generate_204",
                out _,
                out _,
                out string error);

            Assert.IsFalse(ok);
            Assert.IsTrue(error.Contains("HTTPS"));
        }
    }
}
