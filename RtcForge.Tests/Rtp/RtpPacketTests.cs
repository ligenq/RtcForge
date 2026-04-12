using RtcForge.Rtp;

namespace RtcForge.Tests.Rtp;

public class RtpPacketTests
{
    [Fact]
    public void TryParse_ValidRtpPacket_ReturnsTrueAndCorrectFields()
    {
        // Arrange
        // V=2, P=0, X=0, CC=0, M=1, PT=96, SN=123, TS=456, SSRC=789
        byte[] buffer = new byte[12 + 4]; // 12 bytes header + 4 bytes payload
        buffer[0] = 0x80; // V=2
        buffer[1] = 0xE0; // M=1, PT=96 (0x60 | 0x80)
        buffer[2] = 0x00; buffer[3] = 0x7B; // SN=123
        buffer[4] = 0x00; buffer[5] = 0x00; buffer[6] = 0x01; buffer[7] = 0xC8; // TS=456
        buffer[8] = 0x00; buffer[9] = 0x00; buffer[10] = 0x03; buffer[11] = 0x15; // SSRC=789
        buffer[12] = 0xDE; buffer[13] = 0xAD; buffer[14] = 0xBE; buffer[15] = 0xEF; // Payload

        // Act
        bool result = RtpPacket.TryParse(buffer, out RtpPacket packet);

        // Assert
        Assert.True(result);
        Assert.Equal(2, packet.Version);
        Assert.False(packet.Padding);
        Assert.False(packet.Extension);
        Assert.Equal(0, packet.CsrcCount);
        Assert.True(packet.Marker);
        Assert.Equal(96, packet.PayloadType);
        Assert.Equal(123, packet.SequenceNumber);
        Assert.Equal(456u, packet.Timestamp);
        Assert.Equal(789u, packet.Ssrc);
        Assert.Equal(4, packet.Payload.Length);
        Assert.Equal(0xDE, packet.Payload.Span[0]);
    }

    [Fact]
    public void Serialize_ValidRtpPacket_ProducesCorrectBytes()
    {
        // Arrange
        var packet = new RtpPacket
        {
            Version = 2,
            Marker = true,
            PayloadType = 96,
            SequenceNumber = 123,
            Timestamp = 456,
            Ssrc = 789,
            Payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }.AsMemory()
        };

        byte[] buffer = new byte[packet.GetSerializedLength()];

        // Act
        int length = packet.Serialize(buffer);

        // Assert
        Assert.Equal(16, length);
        Assert.Equal(0x80, buffer[0]);
        Assert.Equal(0xE0, buffer[1]);
        Assert.Equal(0x00, buffer[2]);
        Assert.Equal(0x7B, buffer[3]);
        Assert.Equal(0xDE, buffer[12]);
    }

    [Fact]
    public void TryParse_WithCsrc_ReturnsTrue()
    {
        // Arrange
        byte[] buffer = new byte[12 + 4 + 4]; // 12 header + 4 CSRC + 4 payload
        buffer[0] = 0x81; // V=2, CC=1
        buffer[1] = 0x60; // M=0, PT=96
        buffer[2] = 0x00; buffer[3] = 0x7B; // SN=123
        buffer[4] = 0x00; buffer[5] = 0x00; buffer[6] = 0x01; buffer[7] = 0xC8; // TS=456
        buffer[8] = 0x00; buffer[9] = 0x00; buffer[10] = 0x03; buffer[11] = 0x15; // SSRC=789
        buffer[12] = 0x00; buffer[13] = 0x00; buffer[14] = 0x04; buffer[15] = 0xD2; // CSRC=1234
        buffer[16] = 0xAA; buffer[17] = 0xBB; buffer[18] = 0xCC; buffer[19] = 0xDD; // Payload

        // Act
        bool result = RtpPacket.TryParse(buffer, out RtpPacket packet);

        // Assert
        Assert.True(result);
        Assert.Equal(1, packet.CsrcCount);
        Assert.NotNull(packet.Csrc);
        Assert.Single(packet.Csrc);
        Assert.Equal(1234u, packet.Csrc[0]);
        Assert.Equal(4, packet.Payload.Length);
    }
}
