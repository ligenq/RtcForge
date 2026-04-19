using System.Buffers.Binary;
using RtcForge.Sctp;

namespace RtcForge.Tests.Sctp;

public class SctpPacketTests
{
    [Fact]
    public void TryParse_ValidDataChunk_ReturnsCorrectFields()
    {
        var source = new SctpPacket
        {
            SourcePort = 5000,
            DestinationPort = 5000,
            VerificationTag = 0xDEADBEEF
        };
        source.Chunks.Add(new SctpDataChunk
        {
            Flags = 0x03,
            Tsn = 1,
            StreamId = 1,
            StreamSequenceNumber = 0,
            PayloadProtocolId = 53,
            UserData = [0xAA, 0xBB, 0xCC, 0xDD]
        });
        byte[] buffer = new byte[source.GetSerializedLength()];
        source.Serialize(buffer);

        // Act
        bool result = SctpPacket.TryParse(buffer, out SctpPacket packet);

        // Assert
        Assert.True(result);
        Assert.Equal(5000, packet.SourcePort);
        Assert.Single(packet.Chunks);
        Assert.IsType<SctpDataChunk>(packet.Chunks[0]);
        var data = (SctpDataChunk)packet.Chunks[0];
        Assert.Equal(1u, data.Tsn);
        Assert.Equal(4, data.UserData.Length);
        Assert.Equal(0xAA, data.UserData[0]);
    }

    [Fact]
    public void TryParse_InvalidChecksum_ReturnsFalse()
    {
        var packet = new SctpPacket
        {
            SourcePort = 5000,
            DestinationPort = 5000,
            VerificationTag = 0xDEADBEEF
        };
        packet.Chunks.Add(new SctpSimpleChunk { Type = SctpChunkType.CookieAck });
        byte[] buffer = new byte[packet.GetSerializedLength()];
        packet.Serialize(buffer);

        buffer[^1] ^= 0xFF;

        Assert.False(SctpPacket.TryParse(buffer, out _));
    }

    [Fact]
    public void Serialize_DataChunk_ProducesCorrectBytes()
    {
        // Arrange
        var packet = new SctpPacket
        {
            SourcePort = 5000,
            DestinationPort = 5000,
            VerificationTag = 0xDEADBEEF
        };
        packet.Chunks.Add(new SctpDataChunk
        {
            Flags = 0x03,
            Tsn = 1,
            StreamId = 1,
            StreamSequenceNumber = 0,
            PayloadProtocolId = 53,
            UserData = [0xAA, 0xBB, 0xCC, 0xDD]
        });

        byte[] buffer = new byte[packet.GetSerializedLength()];

        // Act
        Assert.Single(packet.Chunks);
        int length = packet.Serialize(buffer);

        // Assert
        Assert.Equal(32, length);
        Assert.Equal(0x13, buffer[0]);
        Assert.Equal(0x00, buffer[12]); // Type: Data
        Assert.Equal(0x14, buffer[15]); // Length: 20
        Assert.Equal(0xAA, buffer[28]);
        Assert.NotEqual([0, 0, 0, 0], buffer[8..12]);
    }

    [Fact]
    public void Serialize_InitAck_WithStateCookie_RoundTripsCookie()
    {
        var packet = new SctpPacket
        {
            SourcePort = 5000,
            DestinationPort = 5000,
            VerificationTag = 0x01020304
        };
        packet.Chunks.Add(new SctpInitChunk(SctpChunkType.InitAck)
        {
            InitiateTag = 0xAABBCCDD,
            AdvertisedReceiverWindowCredit = 1024,
            NumberOfInboundStreams = 2048,
            NumberOfOutboundStreams = 2048,
            InitialTsn = 0x11223344,
            StateCookie = [1, 2, 3, 4, 5]
        });

        byte[] buffer = new byte[packet.GetSerializedLength()];
        int length = packet.Serialize(buffer);

        Assert.Equal(buffer.Length, length);
        Assert.NotEqual([0, 0, 0, 0], buffer[8..12]);
        Assert.True(SctpPacket.TryParse(buffer, out var parsed));
        var initAck = Assert.IsType<SctpInitChunk>(Assert.Single(parsed.Chunks));
        Assert.Equal(SctpChunkType.InitAck, initAck.Type);
        Assert.Equal([1, 2, 3, 4, 5], initAck.StateCookie);
    }

    [Fact]
    public void Serialize_WithTooSmallBuffer_ReturnsMinusOne()
    {
        var packet = new SctpPacket
        {
            SourcePort = 5000,
            DestinationPort = 5000
        };
        packet.Chunks.Add(new SctpDataChunk
        {
            Flags = 0x03,
            Tsn = 1,
            UserData = [1, 2, 3]
        });

        Assert.Equal(-1, packet.Serialize(new byte[packet.GetSerializedLength() - 1]));
    }

    [Fact]
    public void TryParse_TooShortPacket_ReturnsFalse()
    {
        Assert.False(SctpPacket.TryParse(new byte[SctpPacket.HeaderLength - 1], out _));
        Assert.False(SctpPacket.VerifyChecksum(new byte[SctpPacket.HeaderLength - 1]));
    }

    [Theory]
    [InlineData(SctpChunkType.Data, 15)]
    [InlineData(SctpChunkType.Init, 19)]
    [InlineData(SctpChunkType.Sack, 15)]
    [InlineData(SctpChunkType.Shutdown, 7)]
    [InlineData(SctpChunkType.CookieAck, 3)]
    public void TryParse_KnownChunkWithImpossibleLength_ReturnsFalse(SctpChunkType type, ushort length)
    {
        byte[] buffer = BuildPacketWithRawChunk((byte)type, 0, length, new byte[Math.Max(0, length - 4)]);

        Assert.False(SctpPacket.TryParse(buffer, out _));
    }

    [Fact]
    public void TryParse_UnknownChunk_RoundTripsAsUnknown()
    {
        var packet = new SctpPacket
        {
            SourcePort = 5000,
            DestinationPort = 5000,
            VerificationTag = 0x01020304
        };
        packet.Chunks.Add(new SctpUnknownChunk
        {
            Type = (SctpChunkType)0xFE,
            Flags = 0x80,
            Data = [1, 2, 3]
        });
        byte[] buffer = new byte[packet.GetSerializedLength()];
        Assert.Equal(buffer.Length, packet.Serialize(buffer));

        Assert.True(SctpPacket.TryParse(buffer, out var parsed));
        var unknown = Assert.IsType<SctpUnknownChunk>(Assert.Single(parsed.Chunks));
        Assert.Equal((SctpChunkType)0xFE, unknown.Type);
        Assert.Equal(0x80, unknown.Flags);
        Assert.Equal([1, 2, 3], unknown.Data);
    }

    [Fact]
    public void TryParse_SackChunk_RoundTripsGapBlocksAndDuplicateTsns()
    {
        var packet = new SctpPacket
        {
            SourcePort = 5000,
            DestinationPort = 5000,
            VerificationTag = 0x01020304
        };
        packet.Chunks.Add(new SctpSackChunk
        {
            CumulativeTsnAck = 10,
            AdvertisedReceiverWindowCredit = 4096,
            GapAckBlocks = { (1, 2), (4, 6) },
            DuplicateTsns = { 7, 8 }
        });
        byte[] buffer = new byte[packet.GetSerializedLength()];
        packet.Serialize(buffer);

        Assert.True(SctpPacket.TryParse(buffer, out var parsed));
        var sack = Assert.IsType<SctpSackChunk>(Assert.Single(parsed.Chunks));
        Assert.Equal(10u, sack.CumulativeTsnAck);
        Assert.Equal(4096u, sack.AdvertisedReceiverWindowCredit);
        Assert.Equal([(1, 2), (4, 6)], sack.GapAckBlocks);
        Assert.Equal([7u, 8u], sack.DuplicateTsns);
    }

    [Fact]
    public void TryParse_InitAckWithMalformedCookieParameter_IgnoresCookie()
    {
        byte[] chunkBody = new byte[20 + 4];
        BinaryPrimitives.WriteUInt32BigEndian(chunkBody.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(chunkBody.AsSpan(8, 4), 1024);
        BinaryPrimitives.WriteUInt16BigEndian(chunkBody.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(chunkBody.AsSpan(14, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(chunkBody.AsSpan(16, 4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(chunkBody.AsSpan(20, 2), 7);
        BinaryPrimitives.WriteUInt16BigEndian(chunkBody.AsSpan(22, 2), 3);
        byte[] buffer = BuildPacketWithRawChunk((byte)SctpChunkType.InitAck, 0, (ushort)chunkBody.Length, chunkBody[4..]);

        Assert.True(SctpPacket.TryParse(buffer, out var parsed));
        var initAck = Assert.IsType<SctpInitChunk>(Assert.Single(parsed.Chunks));
        Assert.Null(initAck.StateCookie);
    }

    private static byte[] BuildPacketWithRawChunk(byte type, byte flags, ushort length, byte[] payloadAfterHeader)
    {
        int paddedChunkLength = (length + 3) & ~3;
        byte[] buffer = new byte[SctpPacket.HeaderLength + paddedChunkLength];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), 5000);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), 5000);
        buffer[12] = type;
        buffer[13] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(14, 2), length);
        payloadAfterHeader.CopyTo(buffer.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), SctpPacket.ComputeChecksum(buffer));
        return buffer;
    }
}
