using Microsoft.VisualStudio.TestTools.UnitTesting;
using UDPtestLib.Core;

namespace UDPtest.UnitTests
{
    [TestClass]
    public sealed class NodeVerificationTests
    {
        [TestMethod]
        public void NodeVerification_ThreeMatchingEgressesForeignLocation_ReturnsVerified()
        {
            StunEgressEvidence[] evidence =
            [
                new(true, "104.28.19.42"),
                new(true, "104.28.19.42"),
                new(true, "104.28.19.42"),
            ];

            bool verified = NodeVerificationEvaluator.IsVerified("104.28.19.42", "US", evidence);
            Assert.IsTrue(verified);
        }

        [TestMethod]
        public void NodeVerification_DomesticLocationCN_ReturnsUnverified()
        {
            StunEgressEvidence[] evidence =
            [
                new(true, "104.28.19.42"),
                new(true, "104.28.19.42"),
                new(true, "104.28.19.42"),
            ];

            bool verified = NodeVerificationEvaluator.IsVerified("104.28.19.42", "CN", evidence);
            Assert.IsFalse(verified);
        }

        [TestMethod]
        public void NodeVerification_FewerThanThreeEnabled_ReturnsUnverified()
        {
            StunEgressEvidence[] evidence =
            [
                new(true, "104.28.19.42"),
                new(true, "104.28.19.42"),
                new(false, "104.28.19.42"),
            ];

            bool verified = NodeVerificationEvaluator.IsVerified("104.28.19.42", "JP", evidence);
            Assert.IsFalse(verified);
        }
    }
}
