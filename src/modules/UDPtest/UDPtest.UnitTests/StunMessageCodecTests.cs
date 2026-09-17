using System.Buffers.Binary;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UDPtestLib.Probes;

namespace UDPtest.UnitTests
{
    [TestClass]
    public sealed class StunMessageCodecTests
    {
        private static readonly byte[] TransactionId =
            [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C];

        [TestMethod]
        public void Parser_Extracts_Xor_Mapping_And_Other_Address()
        {
            IPEndPoint mapped = new(IPAddress.Parse("203.0.113.17"), 45123);
            IPEndPoint alternate = new(IPAddress.Parse("198.51.100.8"), 3479);
            byte[] response = BuildSuccessResponse(TransactionId, mapped, alternate);

            StunParseStatus status = StunMessageCodec.ParseBindingResponse(
                response,
                TransactionId,
                new IPEndPoint(IPAddress.Loopback, 3478),
                out StunBindingResponse parsed);

            Assert.AreEqual(StunParseStatus.Success, status);
            Assert.AreEqual(mapped, parsed.MappedAddress);
            Assert.AreEqual(alternate, parsed.AlternateAddress);
        }

        [TestMethod]
        public void Parser_Rejects_Wrong_Magic_Cookie()
        {
            byte[] response = BuildSuccessResponse(
                TransactionId,
                new IPEndPoint(IPAddress.Parse("203.0.113.17"), 45123));
            response[4] ^= 0xFF;

            StunParseStatus status = StunMessageCodec.ParseBindingResponse(
                response,
                TransactionId,
                null,
                out _);

            Assert.AreEqual(StunParseStatus.Invalid, status);
        }

        [TestMethod]
        public void Parser_Rejects_Wrong_Transaction_Id()
        {
            byte[] response = BuildSuccessResponse(
                TransactionId,
                new IPEndPoint(IPAddress.Parse("203.0.113.17"), 45123));
            response[8] ^= 0xFF;

            StunParseStatus status = StunMessageCodec.ParseBindingResponse(
                response,
                TransactionId,
                null,
                out _);

            Assert.AreEqual(StunParseStatus.TransactionMismatch, status);
        }

        [TestMethod]
        public void Parser_Validates_Attributes_After_Mapped_Address()
        {
            byte[] valid = BuildSuccessResponse(
                TransactionId,
                new IPEndPoint(IPAddress.Parse("203.0.113.17"), 45123));
            byte[] malformed = new byte[valid.Length + 4];
            valid.CopyTo(malformed, 0);
            BinaryPrimitives.WriteUInt16BigEndian(malformed.AsSpan(2, 2), 16);
            BinaryPrimitives.WriteUInt16BigEndian(malformed.AsSpan(valid.Length, 2), 0x0006);
            BinaryPrimitives.WriteUInt16BigEndian(malformed.AsSpan(valid.Length + 2, 2), 8);

            StunParseStatus status = StunMessageCodec.ParseBindingResponse(
                malformed,
                TransactionId,
                null,
                out _);

            Assert.AreEqual(StunParseStatus.Invalid, status);
        }

        [TestMethod]
        public void Change_Request_Uses_Registered_Flags()
        {
            byte[] request = StunMessageCodec.CreateBindingRequest(
                TransactionId,
                StunChangeRequest.IpAndPort);

            Assert.AreEqual(28, request.Length);
            Assert.AreEqual((ushort)8, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2, 2)));
            Assert.AreEqual((ushort)0x0003, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(20, 2)));
            Assert.AreEqual(0x00000006u, BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(24, 4)));
        }

        private static byte[] BuildSuccessResponse(
            ReadOnlySpan<byte> transactionId,
            IPEndPoint mapped,
            IPEndPoint? alternate = null)
        {
            int attributeBytes = alternate is null ? 12 : 24;
            byte[] response = new byte[20 + attributeBytes];
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), 0x0101);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), (ushort)attributeBytes);
            BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(4, 4), StunMessageCodec.MagicCookie);
            transactionId.CopyTo(response.AsSpan(8, 12));
            WriteAddressAttribute(response.AsSpan(20, 12), 0x0020, mapped, xor: true);
            if (alternate is not null)
            {
                WriteAddressAttribute(response.AsSpan(32, 12), 0x802C, alternate, xor: false);
            }

            return response;
        }

        private static void WriteAddressAttribute(
            Span<byte> destination,
            ushort type,
            IPEndPoint endpoint,
            bool xor)
        {
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(0, 2), type);
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), 8);
            destination[4] = 0;
            destination[5] = 0x01;
            ushort port = (ushort)endpoint.Port;
            uint address = BinaryPrimitives.ReadUInt32BigEndian(endpoint.Address.GetAddressBytes());
            if (xor)
            {
                port ^= (ushort)(StunMessageCodec.MagicCookie >> 16);
                address ^= StunMessageCodec.MagicCookie;
            }

            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(6, 2), port);
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(8, 4), address);
        }
    }
}
