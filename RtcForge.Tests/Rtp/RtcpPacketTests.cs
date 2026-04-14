using RtcForge.Rtp;

namespace RtcForge.Tests.Rtp;

public class RtcpPacketTests
{
    [Fact]
    public void ParseCompound_SingleSenderReport_ReturnsCorrectPacket()
    {
        // Arrange
        byte[] buffer = new byte[28];
        buffer[0] = 0x80; // V=2, RC=0
        buffer[1] = 200;  // PT=SR
        buffer[2] = 0x00; buffer[3] = 0x06; // Length=6 (7 words = 28 bytes)
        buffer[4] = 0x00; buffer[5] = 0x00; buffer[6] = 0x03; buffer[7] = 0x15; // SSRC=789

        // Act
        List<RtcpPacket> packets = RtcpPacket.ParseCompound(buffer);

        // Assert
        Assert.Single(packets);
        Assert.IsType<RtcpSenderReportPacket>(packets[0]);
        var sr = (RtcpSenderReportPacket)packets[0];
        Assert.Equal(789u, sr.Ssrc);
    }

    [Fact]
    public void Serialize_SenderReport_ProducesCorrectBytes()
    {
        // Arrange
        var sr = new RtcpSenderReportPacket
        {
            Ssrc = 789,
            NtpTimestamp = 0x1122334455667788,
            RtpTimestamp = 0xAABBCCDD,
            PacketCount = 100,
            OctetCount = 5000
        };
        byte[] buffer = new byte[sr.GetSerializedLength()];

        // Act
        int length = sr.Serialize(buffer);

        // Assert
        Assert.Equal(28, length);
        Assert.Equal(0x80, buffer[0]);
        Assert.Equal(200, buffer[1]);
        Assert.Equal(0x00, buffer[2]);
        Assert.Equal(0x06, buffer[3]);
        Assert.Equal(0x11, buffer[8]);
        Assert.Equal(100u, BitConverter.ToUInt32([buffer[23], buffer[22], buffer[21], buffer[20]])); // Big-endian check
    }

    [Fact]
    public void ParseCompound_MultiplePackets_ReturnsAll()
    {
        // Arrange: SR (28 bytes) + RR (8 bytes)
        byte[] buffer = new byte[28 + 8];
        // SR
        buffer[0] = 0x80; buffer[1] = 200; buffer[2] = 0x00; buffer[3] = 0x06;
        buffer[4] = 0x00; buffer[5] = 0x00; buffer[6] = 0x00; buffer[7] = 0x01;
        // RR
        buffer[28] = 0x80; buffer[29] = 201; buffer[30] = 0x00; buffer[31] = 0x01;
        buffer[32] = 0x00; buffer[33] = 0x00; buffer[34] = 0x00; buffer[35] = 0x02;

        // Act
        List<RtcpPacket> packets = RtcpPacket.ParseCompound(buffer);

        // Assert
        Assert.Equal(2, packets.Count);
        Assert.IsType<RtcpSenderReportPacket>(packets[0]);
        Assert.IsType<RtcpReceiverReportPacket>(packets[1]);
        Assert.Equal(1u, ((RtcpSenderReportPacket)packets[0]).Ssrc);
        Assert.Equal(2u, ((RtcpReceiverReportPacket)packets[1]).Ssrc);
    }

    [Fact]
    public void Serialize_NackPacket_WithBlp_ProducesCorrectBytes()
    {
        var nack = new RtcpNackPacket
        {
            SenderSsrc = 1,
            MediaSsrc = 2,
            LostSequenceNumbers = new List<ushort> { 10, 11, 13, 35 } // 10 base, 11 (+1), 13 (+3), 35 (new block)
        };
        
        byte[] buffer = new byte[nack.GetSerializedLength()];
        int len = nack.Serialize(buffer);
        
        Assert.Equal(20, len); // 12 header + 4 byte block 1 + 4 byte block 2
        var parsed = RtcpNackPacket.TryParse(buffer, 1);
        Assert.NotNull(parsed);
        Assert.Equal(1u, parsed.SenderSsrc);
        Assert.Equal(2u, parsed.MediaSsrc);
        Assert.Equal("10,11,13,35", string.Join(",", parsed.LostSequenceNumbers));
        Assert.Contains((ushort)10, parsed.LostSequenceNumbers);
        Assert.Contains((ushort)11, parsed.LostSequenceNumbers);
        Assert.Contains((ushort)13, parsed.LostSequenceNumbers);
        Assert.Contains((ushort)35, parsed.LostSequenceNumbers);
    }
}
