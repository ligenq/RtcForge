using RtcForge.Sctp;

namespace RtcForge.Tests.Sctp;

public class SctpPacketTests
{
    [Fact]
    public void TryParse_ValidDataChunk_ReturnsCorrectFields()
    {
        // Arrange
        byte[] buffer = new byte[12 + 16 + 4]; // Header + Data Chunk + Padding
        // Header
        buffer[0] = 0x13; buffer[1] = 0x88; // Src: 5000
        buffer[2] = 0x13; buffer[3] = 0x88; // Dst: 5000
        buffer[4] = 0xDE; buffer[5] = 0xAD; buffer[6] = 0xBE; buffer[7] = 0xEF; // VTag
        // Data Chunk
        buffer[12] = 0; // Type: Data
        buffer[13] = 0x03; // Flags: BE (Beginning/End)
        buffer[14] = 0x00; buffer[15] = 0x14; // Length: 20
        buffer[16] = 0x00; buffer[17] = 0x00; buffer[18] = 0x00; buffer[19] = 0x01; // TSN: 1
        buffer[20] = 0x00; buffer[21] = 0x01; // Stream: 1
        buffer[22] = 0x00; buffer[23] = 0x00; // SSN: 0
        buffer[24] = 0x00; buffer[25] = 0x00; buffer[26] = 0x00; buffer[27] = 0x35; // PPID: 53
        buffer[28] = 0xAA; buffer[29] = 0xBB; buffer[30] = 0xCC; buffer[31] = 0xDD; // User Data

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
            UserData = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }
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
    }
}
