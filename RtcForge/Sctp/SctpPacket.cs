using System.Buffers.Binary;

namespace RtcForge.Sctp;

/// <summary>
/// SCTP Packet Common Header (RFC 4960 Section 3.1)
/// </summary>
public class SctpPacket
{
    public const int HeaderLength = 12;

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

        packet = new SctpPacket
        {
            SourcePort = BinaryPrimitives.ReadUInt16BigEndian(buffer[..2]),
            DestinationPort = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2)),
            VerificationTag = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4)),
            Checksum = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4))
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

        return true;
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
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(8, 4), 0); // Checksum placeholder

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

        return offset;
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

        if (buffer.Length < length)
        {
            return false;
        }

        chunk = (SctpChunkType)type switch
        {
            SctpChunkType.Data => SctpDataChunk.Parse(buffer[..length], flags),
            SctpChunkType.Init or SctpChunkType.InitAck => SctpInitChunk.Parse(buffer[..length], (SctpChunkType)type, flags),
            SctpChunkType.Sack => SctpSackChunk.Parse(buffer[..length], flags),
            SctpChunkType.Shutdown => SctpShutdownChunk.Parse(buffer[..length], flags),
            SctpChunkType.ShutdownAck or SctpChunkType.ShutdownComplete or SctpChunkType.CookieAck => new SctpSimpleChunk { Type = (SctpChunkType)type, Flags = flags, Length = length },
            _ => new SctpUnknownChunk { Type = (SctpChunkType)type, Flags = flags, Length = length, Data = buffer[4..length].ToArray() },
        };
        return chunk != null;
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
    public uint InitiateTag { get; set; }
    public uint AdvertisedReceiverWindowCredit { get; set; }
    public ushort NumberOfOutboundStreams { get; set; }
    public ushort NumberOfInboundStreams { get; set; }
    public uint InitialTsn { get; set; }

    public SctpInitChunk(SctpChunkType type) { Type = type; }

    public override int GetSerializedLength() => 20;

    public static SctpInitChunk Parse(ReadOnlySpan<byte> buffer, SctpChunkType type, byte flags)
    {
        return new SctpInitChunk(type)
        {
            Flags = flags,
            Length = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2)),
            InitiateTag = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4)),
            AdvertisedReceiverWindowCredit = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4)),
            NumberOfOutboundStreams = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(12, 2)),
            NumberOfInboundStreams = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(14, 2)),
            InitialTsn = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(16, 4))
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
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), InitiateTag);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(8, 4), AdvertisedReceiverWindowCredit);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(12, 2), NumberOfOutboundStreams);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(14, 2), NumberOfInboundStreams);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(16, 4), InitialTsn);
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
