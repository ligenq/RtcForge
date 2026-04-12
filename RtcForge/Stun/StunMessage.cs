using System.Buffers.Binary;

namespace RtcForge.Stun;

public enum StunMessageType : ushort
{
    BindingRequest = 0x0001,
    BindingSuccessResponse = 0x0101,
    BindingErrorResponse = 0x0111,
    BindingIndication = 0x0011,
    AllocateRequest = 0x0003,
    AllocateSuccessResponse = 0x0103,
    AllocateErrorResponse = 0x0113,
    RefreshRequest = 0x0004,
    RefreshSuccessResponse = 0x0104,
    RefreshErrorResponse = 0x0114,
    CreatePermissionRequest = 0x0008,
    CreatePermissionSuccessResponse = 0x0108,
    CreatePermissionErrorResponse = 0x0118,
    ChannelBindRequest = 0x0009,
    ChannelBindSuccessResponse = 0x0109,
    ChannelBindErrorResponse = 0x0119,
    SendIndication = 0x0016,
    DataIndication = 0x0017
}

public enum StunAttributeType : ushort
{
    MappedAddress = 0x0001,
    Username = 0x0006,
    MessageIntegrity = 0x0008,
    ErrorCode = 0x0009,
    UnknownAttributes = 0x000A,
    ChannelNumber = 0x000C,
    Realm = 0x0014,
    Nonce = 0x0015,
    XorMappedAddress = 0x0020,
    Priority = 0x0024,
    UseCandidate = 0x0025,
    IceControlled = 0x8029,
    IceControlling = 0x802A,
    Fingerprint = 0x8028,
    Lifetime = 0x000D,
    XorPeerAddress = 0x0012,
    Data = 0x0013,
    RelayedAddress = 0x0016,
    RequestedTransport = 0x0019
}

public class StunMessage
{
    public const uint MagicCookie = 0x2112A442;
    public const int HeaderLength = 20;

    public StunMessageType Type { get; set; }
    public byte[] TransactionId { get; set; } = new byte[12];
    public List<StunAttribute> Attributes { get; set; } = new();
    public byte[]? RawBytes { get; private set; }

    public static bool TryParse(ReadOnlySpan<byte> buffer, out StunMessage message)
    {
        message = null!;
        if (buffer.Length < HeaderLength)
        {
            return false;
        }

        ushort type = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(0, 2));
        if ((type & 0xC000) != 0)
        {
            return false; // First two bits must be 0
        }

        ushort length = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2));
        if (buffer.Length < HeaderLength + length)
        {
            return false;
        }

        uint cookie = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));
        if (cookie != MagicCookie)
        {
            return false;
        }

        message = new StunMessage
        {
            Type = (StunMessageType)type,
            TransactionId = buffer.Slice(8, 12).ToArray(),
            RawBytes = buffer.Slice(0, HeaderLength + length).ToArray()
        };

        int offset = HeaderLength;
        while (offset < HeaderLength + length)
        {
            if (buffer.Length - offset < 4)
            {
                break;
            }

            ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset, 2));
            ushort attrLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 2, 2));
            offset += 4;

            if (buffer.Length - offset < attrLength)
            {
                break;
            }

            message.Attributes.Add(new StunAttribute
            {
                Type = (StunAttributeType)attrType,
                Value = buffer.Slice(offset, attrLength).ToArray()
            });

            // Attributes are padded to 4 bytes
            offset += (attrLength + 3) & ~3;
        }

        return true;
    }

    public int GetSerializedLength()
    {
        int length = HeaderLength;
        foreach (var attr in Attributes)
        {
            if (attr.Type == StunAttributeType.MessageIntegrity)
            {
                length += 24; // 4 byte header + 20 byte HMAC
            }
            else if (attr.Type == StunAttributeType.Fingerprint)
            {
                length += 8; // 4 byte header + 4 byte CRC
            }
            else
            {
                length += 4 + ((attr.Value.Length + 3) & ~3);
            }
        }
        return length;
    }

    public int Serialize(Span<byte> buffer, ReadOnlySpan<byte> integrityKey = default)
    {
        int totalLength = GetSerializedLength();
        if (buffer.Length < totalLength)
        {
            return -1;
        }

        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(0, 2), (ushort)Type);
        SetMessageLength(buffer, 0);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), MagicCookie);
        TransactionId.CopyTo(buffer.Slice(8, 12));

        int offset = HeaderLength;
        foreach (var attr in Attributes)
        {
            if (attr.Type == StunAttributeType.MessageIntegrity)
            {
                if (integrityKey.IsEmpty)
                {
                    throw new ArgumentNullException(nameof(integrityKey));
                }

                BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(offset, 2), (ushort)attr.Type);
                BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(offset + 2, 2), 20);
                offset += 4;
                // RFC 5389: set length as if MessageIntegrity were the last attribute
                SetMessageLength(buffer, offset + 20);
                byte[] hmac = StunSecurity.CalculateMessageIntegrity(buffer.Slice(0, offset), integrityKey);
                hmac.CopyTo(buffer.Slice(offset, 20));
                offset += 20;
                continue;
            }

            if (attr.Type == StunAttributeType.Fingerprint)
            {
                BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(offset, 2), (ushort)attr.Type);
                BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(offset + 2, 2), 4);
                offset += 4;
                // RFC 5389: set length as if Fingerprint were the last attribute
                SetMessageLength(buffer, offset + 4);
                uint crc = StunSecurity.CalculateFingerprint(buffer.Slice(0, offset));
                BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(offset, 4), crc);
                offset += 4;
                continue;
            }

            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(offset, 2), (ushort)attr.Type);
            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(offset + 2, 2), (ushort)attr.Value.Length);
            offset += 4;
            attr.Value.CopyTo(buffer.Slice(offset, attr.Value.Length));
            offset += attr.Value.Length;

            int padding = (4 - (attr.Value.Length % 4)) % 4;
            for (int i = 0; i < padding; i++)
            {
                buffer[offset++] = 0;
            }
        }

        SetMessageLength(buffer, offset);
        return offset;
    }

    private static void SetMessageLength(Span<byte> buffer, int totalOffset)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), (ushort)(totalOffset - HeaderLength));
    }
}

public class StunAttribute
{
    public StunAttributeType Type { get; set; }
    public byte[] Value { get; set; } = Array.Empty<byte>();

    public System.Net.IPEndPoint? GetXorMappedAddress(byte[] transactionId)
    {
        if (Type != StunAttributeType.XorMappedAddress && Type != StunAttributeType.RelayedAddress && Type != StunAttributeType.XorPeerAddress)
        {
            return null;
        }

        if (Value.Length < 8)
        {
            return null;
        }

        ushort xorPort = BinaryPrimitives.ReadUInt16BigEndian(Value.AsSpan().Slice(2, 2));
        ushort port = (ushort)(xorPort ^ (StunMessage.MagicCookie >> 16));

        if (Value[1] == 0x01 && Value.Length >= 8)
        {
            uint xorAddr = BinaryPrimitives.ReadUInt32BigEndian(Value.AsSpan().Slice(4, 4));
            uint addr = xorAddr ^ StunMessage.MagicCookie;
            return new System.Net.IPEndPoint(new System.Net.IPAddress(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(addr))), port);
        }

        if (Value[1] == 0x02 && Value.Length >= 20)
        {
            // IPv6: XOR with magic cookie (4 bytes) + transaction ID (12 bytes) = 16 bytes
            byte[] xorKey = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(xorKey.AsSpan(0, 4), StunMessage.MagicCookie);
            transactionId.AsSpan(0, 12).CopyTo(xorKey.AsSpan(4));

            byte[] addrBytes = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                addrBytes[i] = (byte)(Value[4 + i] ^ xorKey[i]);
            }
            return new System.Net.IPEndPoint(new System.Net.IPAddress(addrBytes), port);
        }

        return null;
    }

    public static StunAttribute CreateXorMappedAddress(System.Net.IPEndPoint endpoint, byte[] transactionId)
    {
        return CreateXorAddress(StunAttributeType.XorMappedAddress, endpoint, transactionId);
    }

    public static StunAttribute CreateXorPeerAddress(System.Net.IPEndPoint endpoint, byte[] transactionId)
    {
        return CreateXorAddress(StunAttributeType.XorPeerAddress, endpoint, transactionId);
    }

    private static StunAttribute CreateXorAddress(StunAttributeType type, System.Net.IPEndPoint endpoint, byte[] transactionId)
    {
        ushort xorPort = (ushort)(endpoint.Port ^ (StunMessage.MagicCookie >> 16));

        if (endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            byte[] value = new byte[20];
            value[1] = 0x02;
            BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan().Slice(2, 2), xorPort);

            byte[] xorKey = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(xorKey.AsSpan(0, 4), StunMessage.MagicCookie);
            transactionId.AsSpan(0, 12).CopyTo(xorKey.AsSpan(4));

            byte[] addrBytes = endpoint.Address.GetAddressBytes();
            for (int i = 0; i < 16; i++)
            {
                value[4 + i] = (byte)(addrBytes[i] ^ xorKey[i]);
            }
            return new StunAttribute { Type = type, Value = value };
        }

        byte[] v4value = new byte[8];
        v4value[1] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(v4value.AsSpan().Slice(2, 2), xorPort);
        uint addr = BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt32(endpoint.Address.GetAddressBytes(), 0));
        uint xorAddr = addr ^ StunMessage.MagicCookie;
        BinaryPrimitives.WriteUInt32BigEndian(v4value.AsSpan().Slice(4, 4), xorAddr);
        return new StunAttribute { Type = type, Value = v4value };
    }

    public (int Code, string Reason)? GetErrorCode()
    {
        if (Type != StunAttributeType.ErrorCode || Value.Length < 4)
        {
            return null;
        }

        int code = (Value[2] * 100) + Value[3];
        string reason = System.Text.Encoding.UTF8.GetString(Value.AsSpan().Slice(4));
        return (code, reason);
    }

    public uint? GetUInt32()
    {
        if (Value.Length != sizeof(uint))
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt32BigEndian(Value);
    }
}
