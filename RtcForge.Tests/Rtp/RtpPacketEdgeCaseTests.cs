using System.Buffers.Binary;
using RtcForge.Rtp;

namespace RtcForge.Tests.Rtp;

public class RtpPacketEdgeCaseTests
{
    [Fact]
    public void TryParse_TooShort_ReturnsFalse()
    {
        byte[] buffer = new byte[8];
        buffer[0] = 0x80; // V=2

        bool result = RtpPacket.TryParse(buffer, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryParse_EmptyBuffer_ReturnsFalse()
    {
        bool result = RtpPacket.TryParse(ReadOnlyMemory<byte>.Empty, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryParse_WrongVersion_ReturnsFalse()
    {
        byte[] buffer = new byte[12];
        buffer[0] = 0x40; // V=1

        bool result = RtpPacket.TryParse(buffer, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryParse_WithPadding_RemovesPaddingFromPayload()
    {
        // V=2, P=1, X=0, CC=0 = 0xA0
        byte[] buffer = new byte[16];
        buffer[0] = 0xA0; // V=2, P=1
        buffer[1] = 0x60; // PT=96
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), 1); // SN
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), 100); // TS
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8, 4), 200); // SSRC
        buffer[12] = 0xAA; // payload byte
        buffer[13] = 0x00; // padding
        buffer[14] = 0x00; // padding
        buffer[15] = 0x03; // padding count = 3

        bool result = RtpPacket.TryParse(buffer, out RtpPacket packet);

        Assert.True(result);
        Assert.True(packet.Padding);
        Assert.Equal(1, packet.Payload.Length); // 4 - 3 padding = 1
        Assert.Equal(0xAA, packet.Payload.Span[0]);
    }

    [Fact]
    public void TryParse_WithExtension_ParsesExtensionHeader()
    {
        // V=2, P=0, X=1, CC=0 = 0x90
        byte[] buffer = new byte[20];
        buffer[0] = 0x90; // V=2, X=1
        buffer[1] = 0x60; // PT=96
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), 100);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8, 4), 200);
        // Extension header: profile (2 bytes) + length in 32-bit words (2 bytes)
        buffer[12] = 0xBE; buffer[13] = 0xDE; // Profile
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(14, 2), 1); // 1 word = 4 bytes
        buffer[16] = 0x01; buffer[17] = 0x02; buffer[18] = 0x03; buffer[19] = 0x04; // Extension data

        bool result = RtpPacket.TryParse(buffer, out RtpPacket packet);

        Assert.True(result);
        Assert.True(packet.Extension);
        Assert.NotNull(packet.ExtensionHeader);
        Assert.Equal(8, packet.ExtensionHeader!.Length); // 4 header + 4 data
        Assert.Equal(0, packet.Payload.Length); // no payload after extension
    }

    [Fact]
    public void TryParse_WithExtensionAndPayload_ParsesBoth()
    {
        byte[] buffer = new byte[24];
        buffer[0] = 0x90; // V=2, X=1
        buffer[1] = 0x60; // PT=96
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), 100);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8, 4), 200);
        buffer[12] = 0xBE; buffer[13] = 0xDE;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(14, 2), 1); // 1 word extension
        buffer[16] = 0x01; buffer[17] = 0x02; buffer[18] = 0x03; buffer[19] = 0x04;
        // 4 bytes of payload
        buffer[20] = 0xCA; buffer[21] = 0xFE; buffer[22] = 0xBA; buffer[23] = 0xBE;

        bool result = RtpPacket.TryParse(buffer, out RtpPacket packet);

        Assert.True(result);
        Assert.Equal(4, packet.Payload.Length);
        Assert.Equal(0xCA, packet.Payload.Span[0]);
    }

    [Fact]
    public void TryParse_CsrcCountMismatch_TruncatedBuffer_ReturnsFalse()
    {
        byte[] buffer = new byte[14]; // header says CC=1 but only 14 bytes (need 16)
        buffer[0] = 0x81; // V=2, CC=1
        buffer[1] = 0x60;

        bool result = RtpPacket.TryParse(buffer, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryParse_ExtensionTruncated_ReturnsFalse()
    {
        byte[] buffer = new byte[14]; // has extension bit but not enough data
        buffer[0] = 0x90; // V=2, X=1
        buffer[1] = 0x60;

        bool result = RtpPacket.TryParse(buffer, out _);

        Assert.False(result);
    }

    [Fact]
    public void Serialize_BufferTooSmall_ReturnsNegative()
    {
        var packet = new RtpPacket
        {
            PayloadType = 96,
            SequenceNumber = 1,
            Timestamp = 100,
            Ssrc = 200,
            Payload = new byte[10].AsMemory()
        };

        byte[] buffer = new byte[4]; // too small

        int len = packet.Serialize(buffer);

        Assert.Equal(-1, len);
    }

    [Fact]
    public void TryParse_PaddingLargerThanPayload_ReturnsFalse()
    {
        byte[] buffer = new byte[13]; // 12 header + 1 "payload"
        buffer[0] = 0xA0; // V=2, P=1
        buffer[1] = 0x60;
        buffer[12] = 5; // padding count = 5, but only 1 byte of payload

        bool result = RtpPacket.TryParse(buffer, out _);

        Assert.False(result);
    }

    [Fact]
    public void Serialize_WithExtension_IncludesExtensionInOutput()
    {
        var packet = new RtpPacket
        {
            Extension = true,
            PayloadType = 96,
            SequenceNumber = 1,
            Timestamp = 100,
            Ssrc = 200,
            ExtensionHeader = [0xBE, 0xDE, 0x00, 0x01, 0x01, 0x02, 0x03, 0x04],
            Payload = new byte[] { 0xFF }.AsMemory()
        };

        byte[] buffer = new byte[packet.GetSerializedLength()];
        int len = packet.Serialize(buffer);

        Assert.Equal(12 + 8 + 1, len); // header + extension + payload
        // Verify extension is present
        Assert.Equal(0xBE, buffer[12]);
        Assert.Equal(0xDE, buffer[13]);
    }
}
