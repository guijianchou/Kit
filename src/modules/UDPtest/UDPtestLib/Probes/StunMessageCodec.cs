using System.Buffers.Binary;
using System.Net;

namespace UDPtestLib.Probes
{
    public static class StunMessageCodec
    {
        public const uint MagicCookie = 0x2112A442;

        private const ushort BindingRequest = 0x0001;
        private const ushort BindingSuccessResponse = 0x0101;
        private const ushort BindingErrorResponse = 0x0111;
        private const ushort MappedAddressAttribute = 0x0001;
        private const ushort ChangeRequestAttribute = 0x0003;
        private const ushort ChangedAddressAttribute = 0x0005;
        private const ushort XorMappedAddressAttribute = 0x0020;
        private const ushort OtherAddressAttribute = 0x802C;

        public static byte[] CreateBindingRequest(
            ReadOnlySpan<byte> transactionId,
            StunChangeRequest changeRequest = StunChangeRequest.None)
        {
            if (transactionId.Length != 12)
            {
                throw new ArgumentException("STUN transaction IDs must contain 12 bytes.", nameof(transactionId));
            }

            int attributeLength = changeRequest == StunChangeRequest.None ? 0 : 8;
            byte[] request = new byte[20 + attributeLength];
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), BindingRequest);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), (ushort)attributeLength);
            BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4, 4), MagicCookie);
            transactionId.CopyTo(request.AsSpan(8, 12));

            if (changeRequest != StunChangeRequest.None)
            {
                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(20, 2), ChangeRequestAttribute);
                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(22, 2), 4);
                uint flags = changeRequest == StunChangeRequest.IpAndPort ? 0x00000006u : 0x00000002u;
                BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(24, 4), flags);
            }

            return request;
        }

        public static StunParseStatus ParseBindingResponse(
            ReadOnlySpan<byte> response,
            ReadOnlySpan<byte> transactionId,
            IPEndPoint? sourceAddress,
            out StunBindingResponse parsed)
        {
            parsed = default;
            if (transactionId.Length != 12 || response.Length < 20)
            {
                return StunParseStatus.Invalid;
            }

            uint cookie = BinaryPrimitives.ReadUInt32BigEndian(response.Slice(4, 4));
            if (cookie != MagicCookie)
            {
                return StunParseStatus.Invalid;
            }

            if (!response.Slice(8, 12).SequenceEqual(transactionId))
            {
                return StunParseStatus.TransactionMismatch;
            }

            ushort messageType = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(0, 2));
            ushort messageLength = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(2, 2));
            if ((messageLength & 3) != 0 || messageLength != response.Length - 20)
            {
                return StunParseStatus.Invalid;
            }

            bool isErrorResponse = messageType == BindingErrorResponse;
            if (!isErrorResponse && messageType != BindingSuccessResponse)
            {
                return StunParseStatus.Invalid;
            }

            IPEndPoint? mappedAddress = null;
            IPEndPoint? xorMappedAddress = null;
            IPEndPoint? changedAddress = null;
            IPEndPoint? otherAddress = null;
            int offset = 20;
            int end = response.Length;
            while (offset < end)
            {
                if (offset + 4 > end)
                {
                    return StunParseStatus.Invalid;
                }

                ushort attributeType = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset, 2));
                ushort attributeLength = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset + 2, 2));
                int valueOffset = offset + 4;
                int paddedLength = (attributeLength + 3) & ~3;
                if (valueOffset + paddedLength > end)
                {
                    return StunParseStatus.Invalid;
                }

                ReadOnlySpan<byte> value = response.Slice(valueOffset, attributeLength);
                switch (attributeType)
                {
                    case MappedAddressAttribute:
                        mappedAddress ??= ParseIpv4Address(value, xor: false);
                        break;
                    case XorMappedAddressAttribute:
                        xorMappedAddress ??= ParseIpv4Address(value, xor: true);
                        break;
                    case ChangedAddressAttribute:
                        changedAddress ??= ParseIpv4Address(value, xor: false);
                        break;
                    case OtherAddressAttribute:
                        otherAddress ??= ParseIpv4Address(value, xor: false);
                        break;
                }

                offset = valueOffset + paddedLength;
            }

            parsed = new StunBindingResponse(
                xorMappedAddress ?? mappedAddress,
                otherAddress ?? changedAddress,
                sourceAddress);
            return isErrorResponse ? StunParseStatus.ErrorResponse : StunParseStatus.Success;
        }

        private static IPEndPoint? ParseIpv4Address(ReadOnlySpan<byte> value, bool xor)
        {
            if (value.Length != 8 || value[1] != 0x01)
            {
                return null;
            }

            ushort port = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(2, 2));
            uint address = BinaryPrimitives.ReadUInt32BigEndian(value.Slice(4, 4));
            if (xor)
            {
                port ^= (ushort)(MagicCookie >> 16);
                address ^= MagicCookie;
            }

            Span<byte> addressBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(addressBytes, address);
            return new IPEndPoint(new IPAddress(addressBytes), port);
        }
    }

    public enum StunChangeRequest
    {
        None,
        Port,
        IpAndPort,
    }

    public enum StunParseStatus
    {
        Invalid,
        TransactionMismatch,
        ErrorResponse,
        Success,
    }

    public readonly record struct StunBindingResponse(
        IPEndPoint? MappedAddress,
        IPEndPoint? AlternateAddress,
        IPEndPoint? SourceAddress);
}
