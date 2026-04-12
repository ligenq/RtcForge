using RtcForge.Sctp;

namespace RtcForge.Tests.Sctp;

public class SctpStreamTests
{
    [Fact]
    public void HandleChunk_CompleteMessage_TriggersAction()
    {
        // Arrange
        byte[]? receivedData = null;
        uint receivedPpid = 0;
        var stream = new SctpStream(1, (ppid, data) => {
            receivedData = data;
            receivedPpid = ppid;
        });

        var chunk = new SctpDataChunk
        {
            Tsn = 100,
            StreamId = 1,
            StreamSequenceNumber = 0,
            PayloadProtocolId = 51,
            Flags = 0x03, // B and E
            UserData = new byte[] { 0x41, 0x42, 0x43 }
        };

        // Act
        stream.HandleChunk(chunk);

        // Assert
        Assert.NotNull(receivedData);
        Assert.Equal(51u, receivedPpid);
        Assert.Equal(new byte[] { 0x41, 0x42, 0x43 }, receivedData);
    }

    [Fact]
    public void HandleChunk_FragmentedMessage_ReassemblesCorrectly()
    {
        // Arrange
        byte[]? receivedData = null;
        var stream = new SctpStream(1, (ppid, data) => receivedData = data);

        var chunk1 = new SctpDataChunk
        {
            Tsn = 100,
            StreamSequenceNumber = 5,
            Flags = 0x02, // B
            UserData = new byte[] { 1, 2 }
        };
        var chunk2 = new SctpDataChunk
        {
            Tsn = 101,
            StreamSequenceNumber = 5,
            Flags = 0x01, // E
            UserData = new byte[] { 3, 4 }
        };

        // Act: Push out of order to verify sorting by TSN
        stream.HandleChunk(chunk2);
        Assert.Null(receivedData);
        stream.HandleChunk(chunk1);

        // Assert
        Assert.NotNull(receivedData);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, receivedData);
    }
}
