using System.Buffers.Binary;

namespace RtcForge.Sctp;

/// <summary>
/// SCTP Packet Common Header (RFC 4960 Section 3.1)
/// </summary>
public class SctpPacket
{
    public const int HeaderLength = 12;
    private static readonly uint[] Crc32CTable = CreateCrc32CTable();

    public ushort SourcePort { get; set; }
    public ushort DestinationPort { get; set; }
    public uint VerificationTag { get; set; }
    public uint Checksum { get; set; } // CRC32c
    public List<SctpChunk> Chunks { get; set; } = [];

    public static bool TryParse(ReadOnlySpan<byte> buffer, out SctpPacket packet)
    {
        packet = null!;
        if (buffer.Length < HeaderLength)
        {
            return false;
        }

        if (!VerifyChecksum(buffer))
        {
            return false;
        }

        packet = new SctpPacket
        {
            SourcePort = BinaryPrimitives.ReadUInt16BigEndian(buffer[..2]),
            DestinationPort = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2)),
            VerificationTag = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4)),
            Checksum = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, 4))
        };

        int offset = HeaderLength;
        while (offset < buffer.Length)
        {
            if (buffer.Length - offset < 4)
            {
                break;
            }

            if (SctpChunk.TryParse(buffer[offset..], out var chunk))
            {
                packet.Chunks.Add(chunk);
                // Chunks are padded to 4 bytes
                offset += (chunk.GetSerializedLength() + 3) & ~3;
            }
            else
            {
                break;
            }
        }

        return packet.Chunks.Count > 0;
    }

    public static bool VerifyChecksum(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength)
        {
            return false;
        }

        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8, 4));
        byte[] copy = data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(8, 4), 0);
        return ComputeChecksum(copy) == expected;
    }

    public int GetSerializedLength()
    {
        int length = HeaderLength;
        foreach (var chunk in Chunks)
        {
            length += (chunk.GetSerializedLength() + 3) & ~3;
        }
        return length;
    }

    public int Serialize(Span<byte> buffer)
    {
        int totalLength = GetSerializedLength();
        if (buffer.Length < totalLength)
        {
            return -1;
        }

        BinaryPrimitives.WriteUInt16BigEndian(buffer[..2], SourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), DestinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), VerificationTag);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(8, 4), 0); // Checksum placeholder

        int offset = HeaderLength;
        foreach (var chunk in Chunks)
        {
            if (offset >= buffer.Length)
            {
                break;
            }

            int chunkLen = chunk.Serialize(buffer[offset..]);
            if (chunkLen < 0)
            {
                return -1;
            }

            offset += (chunkLen + 3) & ~3;
        }

        uint checksum = ComputeChecksum(buffer[..offset]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(8, 4), checksum);

        return offset;
    }

    public static uint ComputeChecksum(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = Crc32CTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreateCrc32CTable()
    {
        var table = new uint[256];
        const uint polynomial = 0x82F63B78;
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? polynomial ^ (crc >> 1) : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }
}

public enum SctpChunkType : byte
{
    Data = 0,
    Init = 1,
    InitAck = 2,
    Sack = 3,
    Heartbeat = 4,
    HeartbeatAck = 5,
    Abort = 6,
    Shutdown = 7,
    ShutdownAck = 8,
    Error = 9,
    CookieEcho = 10,
    CookieAck = 11,
    Ecne = 12,
    Cwr = 13,
    ShutdownComplete = 14
}

public abstract class SctpChunk
{
    public SctpChunkType Type { get; set; }
    public byte Flags { get; set; }
    public ushort Length { get; set; } // Includes header (4 bytes)

    public abstract int GetSerializedLength();
    public abstract int Serialize(Span<byte> buffer);

    public static bool TryParse(ReadOnlySpan<byte> buffer, out SctpChunk chunk)
    {
        chunk = null!;
        if (buffer.Length < 4)
        {
            return false;
        }

        byte type = buffer[0];
        byte flags = buffer[1];
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2));

        if (length < 4 || buffer.Length < length)
        {
            return false;
        }

        if (length < GetMinimumLength((SctpChunkType)type))
        {
            return false;
        }

        chunk = (SctpChunkType)type switch
        {
            SctpChunkType.Data => SctpDataChunk.Parse(buffer[..length], flags),
            SctpChunkType.Init or SctpChunkType.InitAck => SctpInitChunk.Parse(buffer[..length], (SctpChunkType)type, flags),
            SctpChunkType.Sack => SctpSackChunk.Parse(buffer[..length], flags),
            SctpChunkType.Shutdown => SctpShutdownChunk.Parse(buffer[..length], flags),
            SctpChunkType.CookieEcho => SctpCookieEchoChunk.Parse(buffer[..length], flags),
            SctpChunkType.ShutdownAck or SctpChunkType.ShutdownComplete or SctpChunkType.CookieAck => new SctpSimpleChunk { Type = (SctpChunkType)type, Flags = flags, Length = length },
            _ => new SctpUnknownChunk { Type = (SctpChunkType)type, Flags = flags, Length = length, Data = buffer[4..length].ToArray() },
        };
        return chunk != null;
    }

    private static int GetMinimumLength(SctpChunkType type)
    {
        return type switch
        {
            SctpChunkType.Data => 16,
            SctpChunkType.Init or SctpChunkType.InitAck => 20,
            SctpChunkType.Sack => 16,
            SctpChunkType.Shutdown => 8,
            _ => 4
        };
    }

    protected void WriteHeader(Span<byte> buffer)
    {
        buffer[0] = (byte)Type;
        buffer[1] = Flags;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), Length);
    }
}

public class SctpUnknownChunk : SctpChunk
{
    public byte[] Data { get; set; } = [];
    public override int GetSerializedLength() => 4 + Data.Length;
    public override int Serialize(Span<byte> buffer)
    {
        Length = (ushort)GetSerializedLength();
        WriteHeader(buffer);
        Data.CopyTo(buffer[4..]);
        return Length;
    }
}

public class SctpSimpleChunk : SctpChunk
{
    public override int GetSerializedLength() => 4;
    public override int Serialize(Span<byte> buffer)
    {
        Length = 4;
        WriteHeader(buffer);
        return 4;
    }
}

public class SctpCookieEchoChunk : SctpChunk
{
    public byte[] Cookie { get; set; } = [];

    public SctpCookieEchoChunk()
    {
        Type = SctpChunkType.CookieEcho;
    }

    public override int GetSerializedLength() => 4 + Cookie.Length;

    public static SctpCookieEchoChunk Parse(ReadOnlySpan<byte> buffer, byte flags)
    {
        return new SctpCookieEchoChunk
        {
            Flags = flags,
            Length = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2)),
            Cookie = buffer[4..].ToArray()
        };
    }

    public override int Serialize(Span<byte> buffer)
    {
        Length = (ushort)GetSerializedLength();
        if (buffer.Length < Length)
        {
            return -1;
        }

        WriteHeader(buffer);
        Cookie.CopyTo(buffer[4..]);
        return Length;
    }
}

public class SctpDataChunk : SctpChunk
{
    public uint Tsn { get; set; }
    public ushort StreamId { get; set; }
    public ushort StreamSequenceNumber { get; set; }
    public uint PayloadProtocolId { get; set; }
    public byte[] UserData { get; set; } = [];

    public SctpDataChunk() { Type = SctpChunkType.Data; }

    public override int GetSerializedLength() => 16 + UserData.Length;

    public static SctpDataChunk Parse(ReadOnlySpan<byte> buffer, byte flags)
    {
        var chunk = new SctpDataChunk
        {
            Flags = flags,
            Length = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2)),
            Tsn = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4)),
            StreamId = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(8, 2)),
            StreamSequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(10, 2)),
            PayloadProtocolId = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(12, 4)),
            UserData = buffer[16..].ToArray()
        };
        return chunk;
    }

    public override int Serialize(Span<byte> buffer)
    {
        Length = (ushort)GetSerializedLength();
        if (buffer.Length < Length)
        {
            return -1;
        }

        WriteHeader(buffer);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), Tsn);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(8, 2), StreamId);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(10, 2), StreamSequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(12, 4), PayloadProtocolId);
        UserData.CopyTo(buffer[16..]);
        return Length;
    }
}

public class SctpInitChunk : SctpChunk
{
    private const ushort StateCookieParameterType = 7;

    public uint InitiateTag { get; set; }
    public uint AdvertisedReceiverWindowCredit { get; set; }
    public ushort NumberOfOutboundStreams { get; set; }
    public ushort NumberOfInboundStreams { get; set; }
    public uint InitialTsn { get; set; }
    public byte[]? StateCookie { get; set; }

    public SctpInitChunk(SctpChunkType type) { Type = type; }

    public override int GetSerializedLength()
    {
        int length = 20;
        if (StateCookie is { Length: > 0 })
        {
            length += (4 + StateCookie.Length + 3) & ~3;
        }

        return length;
    }

    public static SctpInitChunk Parse(ReadOnlySpan<byte> buffer, SctpChunkType type, byte flags)
    {
        var chunk = new SctpInitChunk(type)
        {
            Flags = flags,
            Length = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2)),
            InitiateTag = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4)),
            AdvertisedReceiverWindowCredit = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4)),
            NumberOfOutboundStreams = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(12, 2)),
            NumberOfInboundStreams = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(14, 2)),
            InitialTsn = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(16, 4))
        };

        int offset = 20;
        while (offset + 4 <= chunk.Length)
        {
            ushort parameterType = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset, 2));
            ushort parameterLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 2, 2));
            if (parameterLength < 4 || offset + parameterLength > chunk.Length)
            {
                break;
            }

            if (parameterType == StateCookieParameterType)
            {
                chunk.StateCookie = buffer.Slice(offset + 4, parameterLength - 4).ToArray();
            }

            offset += (parameterLength + 3) & ~3;
        }

        return chunk;
    }

    public override int Serialize(Span<byte> buffer)
    {
        Length = (ushort)GetSerializedLength();
        if (buffer.Length < Length)
        {
            return -1;
        }

        WriteHeader(buffer);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), InitiateTag);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(8, 4), AdvertisedReceiverWindowCredit);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(12, 2), NumberOfOutboundStreams);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(14, 2), NumberOfInboundStreams);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(16, 4), InitialTsn);
        if (StateCookie is { Length: > 0 })
        {
            int parameterOffset = 20;
            int parameterLength = 4 + StateCookie.Length;
            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(parameterOffset, 2), StateCookieParameterType);
            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(parameterOffset + 2, 2), (ushort)parameterLength);
            StateCookie.CopyTo(buffer.Slice(parameterOffset + 4));
            int paddedLength = (parameterLength + 3) & ~3;
            buffer.Slice(parameterOffset + parameterLength, paddedLength - parameterLength).Clear();
        }

        return Length;
    }
}

public class SctpSackChunk : SctpChunk
{
    public uint CumulativeTsnAck { get; set; }
    public uint AdvertisedReceiverWindowCredit { get; set; }
    public List<(ushort Start, ushort End)> GapAckBlocks { get; set; } = [];
    public List<uint> DuplicateTsns { get; set; } = [];

    public SctpSackChunk() { Type = SctpChunkType.Sack; }

    public static SctpSackChunk Parse(ReadOnlySpan<byte> buffer, byte flags)
    {
        var chunk = new SctpSackChunk
        {
            Flags = flags,
            Length = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2)),
            CumulativeTsnAck = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4)),
            AdvertisedReceiverWindowCredit = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4))
        };
        ushort numGapBlocks = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(12, 2));
        ushort numDupTsns = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(14, 2));

        int offset = 16;
        for (int i = 0; i < numGapBlocks; i++)
        {
            chunk.GapAckBlocks.Add((BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset, 2)), BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 2, 2))));
            offset += 4;
        }
        for (int i = 0; i < numDupTsns; i++)
        {
            chunk.DuplicateTsns.Add(BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset, 4)));
            offset += 4;
        }
        return chunk;
    }

    public override int GetSerializedLength() => 16 + (GapAckBlocks.Count * 4) + (DuplicateTsns.Count * 4);

    public override int Serialize(Span<byte> buffer)
    {
        Length = (ushort)GetSerializedLength();
        if (buffer.Length < Length)
        {
            return -1;
        }

        WriteHeader(buffer);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), CumulativeTsnAck);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(8, 4), AdvertisedReceiverWindowCredit);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(12, 2), (ushort)GapAckBlocks.Count);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(14, 2), (ushort)DuplicateTsns.Count);

        int offset = 16;
        foreach (var block in GapAckBlocks)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(offset, 2), block.Start);
            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(offset + 2, 2), block.End);
            offset += 4;
        }
        foreach (var tsn in DuplicateTsns)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(offset, 4), tsn);
            offset += 4;
        }
        return Length;
    }
}

public class SctpShutdownChunk : SctpChunk
{
    public uint CumulativeTsnAck { get; set; }
    public SctpShutdownChunk() { Type = SctpChunkType.Shutdown; }
    public static SctpShutdownChunk Parse(ReadOnlySpan<byte> buffer, byte flags)
    {
        return new SctpShutdownChunk
        {
            Flags = flags,
            Length = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2)),
            CumulativeTsnAck = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4))
        };
    }
    public override int GetSerializedLength() => 8;
    public override int Serialize(Span<byte> buffer)
    {
        Length = 8;
        WriteHeader(buffer);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), CumulativeTsnAck);
        return 8;
    }
}
