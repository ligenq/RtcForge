using System.Buffers.Binary;

namespace RtcForge.Rtp;

public enum RtcpPacketType : byte
{
    SenderReport = 200,
    ReceiverReport = 201,
    SourceDescription = 202,
    Bye = 203,
    ApplicationDefined = 204,
    TransportFeedback = 205,
    PayloadFeedback = 206
}

public abstract class RtcpPacket
{
    public const int HeaderLength = 4;

    public byte Version { get; set; } = 2;
    public bool Padding { get; set; }
    public byte Count { get; set; } // Reception report count, source count, or FMT
    public RtcpPacketType PacketType { get; set; }

    public abstract int GetSerializedLength();
    public abstract int Serialize(Span<byte> buffer);

    public static List<RtcpPacket> ParseCompound(ReadOnlySpan<byte> buffer)
    {
        var packets = new List<RtcpPacket>();
        int offset = 0;

        while (offset < buffer.Length)
        {
            if (buffer.Length - offset < HeaderLength)
            {
                break;
            }

            byte firstByte = buffer[offset];
            byte version = (byte)(firstByte >> 6);
            if (version != 2)
            {
                break; // Only V=2 is supported
            }

            RtcpPacketType type = (RtcpPacketType)buffer[offset + 1];
            ushort lengthInWords = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 2, 2));
            int packetLength = (lengthInWords + 1) * 4;

            if (offset + packetLength > buffer.Length)
            {
                break;
            }

            RtcpPacket? packet = ParseSingle(buffer.Slice(offset, packetLength), type, firstByte);
            if (packet != null)
            {
                packets.Add(packet);
            }

            offset += packetLength;
        }

        return packets;
    }

    private static RtcpPacket? ParseSingle(ReadOnlySpan<byte> buffer, RtcpPacketType type, byte firstByte)
    {
        byte count = (byte)(firstByte & 0x1F);
        bool padding = (firstByte & 0x20) != 0;

        switch (type)
        {
            case RtcpPacketType.SenderReport:
                return RtcpSenderReportPacket.TryParse(buffer, count, padding);
            case RtcpPacketType.ReceiverReport:
                return RtcpReceiverReportPacket.TryParse(buffer, count, padding);
            case RtcpPacketType.TransportFeedback:
                return RtcpNackPacket.TryParse(buffer, count);
            case RtcpPacketType.PayloadFeedback:
                if (count == 1)
                {
                    return RtcpPliPacket.TryParse(buffer);
                }

                if (count == 4)
                {
                    return RtcpFirPacket.TryParse(buffer);
                }

                return null;
            default:
                return null;
        }
    }

    protected void WriteHeader(Span<byte> buffer, ushort lengthInWords)
    {
        buffer[0] = (byte)((Version << 6) | (Padding ? 0x20 : 0x00) | (Count & 0x1F));
        buffer[1] = (byte)PacketType;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), lengthInWords);
    }
}

public class RtcpSenderReportPacket : RtcpPacket
{
    public uint Ssrc { get; set; }
    public ulong NtpTimestamp { get; set; }
    public uint RtpTimestamp { get; set; }
    public uint PacketCount { get; set; }
    public uint OctetCount { get; set; }

    public RtcpSenderReportPacket() { PacketType = RtcpPacketType.SenderReport; }

    public static RtcpSenderReportPacket? TryParse(ReadOnlySpan<byte> buffer, byte count, bool padding)
    {
        if (buffer.Length < 28)
        {
            return null;
        }

        return new RtcpSenderReportPacket
        {
            Count = count,
            Padding = padding,
            Ssrc = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4)),
            NtpTimestamp = BinaryPrimitives.ReadUInt64BigEndian(buffer.Slice(8, 8)),
            RtpTimestamp = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(16, 4)),
            PacketCount = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(20, 4)),
            OctetCount = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(24, 4))
        };
    }

    public override int GetSerializedLength() => 28;

    public override int Serialize(Span<byte> buffer)
    {
        if (buffer.Length < 28)
        {
            return -1;
        }

        WriteHeader(buffer, (ushort)((GetSerializedLength() / 4) - 1));
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), Ssrc);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.Slice(8, 8), NtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(16, 4), RtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(20, 4), PacketCount);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(24, 4), OctetCount);
        return 28;
    }
}

public class RtcpReceiverReportPacket : RtcpPacket
{
    public uint Ssrc { get; set; }
    public RtcpReceiverReportPacket() { PacketType = RtcpPacketType.ReceiverReport; }
    public static RtcpReceiverReportPacket? TryParse(ReadOnlySpan<byte> buffer, byte count, bool padding)
    {
        if (buffer.Length < 8)
        {
            return null;
        }

        return new RtcpReceiverReportPacket { Count = count, Padding = padding, Ssrc = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4)) };
    }
    public override int GetSerializedLength() => 8;
    public override int Serialize(Span<byte> buffer)
    {
        if (buffer.Length < 8)
        {
            return -1;
        }

        WriteHeader(buffer, (ushort)((GetSerializedLength() / 4) - 1));
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), Ssrc);
        return 8;
    }
}

public class RtcpNackPacket : RtcpPacket
{
    public uint SenderSsrc { get; set; }
    public uint MediaSsrc { get; set; }
    public List<ushort> LostSequenceNumbers { get; set; } = [];

    public RtcpNackPacket()
    {
        PacketType = RtcpPacketType.TransportFeedback;
        Count = 1; // FMT = 1
    }

    public static RtcpNackPacket? TryParse(ReadOnlySpan<byte> buffer, byte fmt)
    {
        if (buffer.Length < 12)
        {
            return null;
        }

        if (fmt != 1)
        {
            return null;
        }

        var packet = new RtcpNackPacket
        {
            SenderSsrc = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4)),
            MediaSsrc = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4))
        };
        int offset = 12;
        while (offset + 4 <= buffer.Length)
        {
            ushort pid = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset, 2));
            ushort blp = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 2, 2));
            packet.LostSequenceNumbers.Add(pid);
            for (int i = 0; i < 16; i++)
            {
                if ((blp & (1 << i)) != 0)
                {
                    packet.LostSequenceNumbers.Add((ushort)(pid + i + 1));
                }
            }

            offset += 4;
        }
        return packet;
    }

    public override int GetSerializedLength()
    {
        if (LostSequenceNumbers.Count == 0)
        {
            return 12;
        }

        LostSequenceNumbers.Sort();
        int blocks = 1;
        ushort currentPid = LostSequenceNumbers[0];

        for (int i = 1; i < LostSequenceNumbers.Count; i++)
        {
            ushort seq = LostSequenceNumbers[i];
            int diff = (ushort)(seq - currentPid);
            if (diff > 16)
            {
                blocks++;
                currentPid = seq;
            }
        }

        return 12 + (blocks * 4);
    }

    public override int Serialize(Span<byte> buffer)
    {
        if (LostSequenceNumbers.Count == 0)
        {
            return -1;
        }

        // Group into PID and BLP
        var blocks = new List<(ushort Pid, ushort Blp)>();
        LostSequenceNumbers.Sort();

        ushort currentPid = LostSequenceNumbers[0];
        ushort currentBlp = 0;

        for (int i = 1; i < LostSequenceNumbers.Count; i++)
        {
            ushort seq = LostSequenceNumbers[i];
            int diff = (ushort)(seq - currentPid);

            if (diff >= 1 && diff <= 16)
            {
                currentBlp |= (ushort)(1 << (diff - 1));
            }
            else if (diff > 16)
            {
                blocks.Add((currentPid, currentBlp));
                currentPid = seq;
                currentBlp = 0;
            }
        }
        blocks.Add((currentPid, currentBlp));

        int len = 12 + (blocks.Count * 4);
        if (buffer.Length < len)
        {
            return -1;
        }

        WriteHeader(buffer, (ushort)((len / 4) - 1));
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), SenderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(8, 4), MediaSsrc);

        int offset = 12;
        foreach (var block in blocks)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(offset, 2), block.Pid);
            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(offset + 2, 2), block.Blp);
            offset += 4;
        }

        return len;
    }
}

public class RtcpPliPacket : RtcpPacket
{
    public uint SenderSsrc { get; set; }
    public uint MediaSsrc { get; set; }

    public RtcpPliPacket()
    {
        PacketType = RtcpPacketType.PayloadFeedback;
        Count = 1; // FMT = 1 for PLI
    }

    public static RtcpPliPacket? TryParse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 12)
        {
            return null;
        }

        return new RtcpPliPacket
        {
            SenderSsrc = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4)),
            MediaSsrc = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4))
        };
    }

    public override int GetSerializedLength() => 12;
    public override int Serialize(Span<byte> buffer)
    {
        if (buffer.Length < 12)
        {
            return -1;
        }

        WriteHeader(buffer, 2); // 3 words
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), SenderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(8, 4), MediaSsrc);
        return 12;
    }
}

public class RtcpFirPacket : RtcpPacket
{
    public uint SenderSsrc { get; set; }
    public uint MediaSsrc { get; set; }
    public byte SequenceNumber { get; set; }

    public RtcpFirPacket()
    {
        PacketType = RtcpPacketType.PayloadFeedback;
        Count = 4; // FMT = 4 for FIR
    }

    public static RtcpFirPacket? TryParse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 20)
        {
            return null;
        }

        return new RtcpFirPacket
        {
            SenderSsrc = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4)),
            MediaSsrc = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4)),
            SequenceNumber = buffer[16]
        };
    }

    public override int GetSerializedLength() => 20;
    public override int Serialize(Span<byte> buffer)
    {
        if (buffer.Length < 20)
        {
            return -1;
        }

        WriteHeader(buffer, 4); // 5 words
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), SenderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(8, 4), MediaSsrc);
        // First FCI: SSRC + Seq (4 bytes + 4 bytes)
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(12, 4), MediaSsrc);
        buffer[16] = SequenceNumber;
        // padding for remaining 3 bytes of FCI entry
        return 20;
    }
}
