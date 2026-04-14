using RtcForge.Rtp;

namespace RtcForge.Tests.Rtp;

public class RtpPacketTests
{
    [Fact]
    public void TryParse_ValidRtpPacket_ReturnsTrueAndCorrectFields()
    {
        // Arrange
        // V=2, P=0, X=0, CC=0, M=1, PT=96, SN=123, TS=456, SSRC=789
        byte[] buffer =
        [
            0x80, // V=2
            0xE0, // M=1, PT=96 (0x60 | 0x80)
            0x00,
            0x7B, // SN=123
            0x00,
            0x00,
            0x01,
            0xC8, // TS=456
            0x00,
            0x00,
            0x03,
            0x15, // SSRC=789
            0xDE,
            0xAD,
            0xBE,
            0xEF, // Payload
        ]; // 12 bytes header + 4 bytes payload

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
        byte[] buffer =
        [
            0x81, // V=2, CC=1
            0x60, // M=0, PT=96
            0x00,
            0x7B, // SN=123
            0x00,
            0x00,
            0x01,
            0xC8, // TS=456
            0x00,
            0x00,
            0x03,
            0x15, // SSRC=789
            0x00,
            0x00,
            0x04,
            0xD2, // CSRC=1234
            0xAA,
            0xBB,
            0xCC,
            0xDD, // Payload
        ]; // 12 header + 4 CSRC + 4 payload

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
