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
            UserData = [0x41, 0x42, 0x43]
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
            UserData = [1, 2]
        };
        var chunk2 = new SctpDataChunk
        {
            Tsn = 101,
            StreamSequenceNumber = 5,
            Flags = 0x01, // E
            UserData = [3, 4]
        };

        // Act: Push out of order to verify sorting by TSN
        stream.HandleChunk(chunk2);
        Assert.Null(receivedData);
        stream.HandleChunk(chunk1);

        // Assert
        Assert.NotNull(receivedData);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, receivedData);
    }

    [Fact]
    public void HandleChunk_FragmentedMessage_WithMissingMiddle_DoesNotDeliverUntilComplete()
    {
        byte[]? receivedData = null;
        var stream = new SctpStream(1, (ppid, data) => receivedData = data);

        var begin = new SctpDataChunk
        {
            Tsn = 100,
            StreamSequenceNumber = 5,
            Flags = 0x02,
            UserData = [1, 2]
        };
        var middle = new SctpDataChunk
        {
            Tsn = 101,
            StreamSequenceNumber = 5,
            Flags = 0x00,
            UserData = [3, 4]
        };
        var end = new SctpDataChunk
        {
            Tsn = 102,
            StreamSequenceNumber = 5,
            Flags = 0x01,
            UserData = [5, 6]
        };

        stream.HandleChunk(begin);
        stream.HandleChunk(end);
        Assert.Null(receivedData);

        stream.HandleChunk(middle);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, receivedData);
    }

    [Fact]
    public void HandleChunk_DuplicateFragment_IsIgnored()
    {
        var deliveries = 0;
        var stream = new SctpStream(1, (ppid, data) => deliveries++);
        var begin = new SctpDataChunk
        {
            Tsn = 100,
            StreamSequenceNumber = 5,
            Flags = 0x02,
            UserData = [1]
        };
        var end = new SctpDataChunk
        {
            Tsn = 101,
            StreamSequenceNumber = 5,
            Flags = 0x01,
            UserData = [2]
        };

        stream.HandleChunk(begin);
        stream.HandleChunk(begin);
        stream.HandleChunk(end);

        Assert.Equal(1, deliveries);
    }

    [Fact]
    public void HandleChunk_WhenCallbackThrows_SwallowsAndClearsCompletedMessage()
    {
        var stream = new SctpStream(1, (ppid, data) => throw new InvalidOperationException("boom"));
        var chunk = new SctpDataChunk
        {
            Tsn = 100,
            StreamSequenceNumber = 5,
            Flags = 0x03,
            UserData = [1]
        };

        stream.HandleChunk(chunk);
        stream.HandleChunk(chunk);
    }

    [Fact]
    public void HandleChunk_WithoutBeginOrEnd_DoesNotDeliver()
    {
        byte[]? receivedData = null;
        var stream = new SctpStream(1, (ppid, data) => receivedData = data);

        stream.HandleChunk(new SctpDataChunk
        {
            Tsn = 100,
            StreamSequenceNumber = 5,
            Flags = 0x00,
            UserData = [1]
        });

        Assert.Null(receivedData);
    }

    [Fact]
    public void HandleChunk_DropsMessageWithTooManyFragments()
    {
        byte[]? receivedData = null;
        var stream = new SctpStream(1, (ppid, data) => receivedData = data);

        for (uint tsn = 1; tsn <= 1002; tsn++)
        {
            stream.HandleChunk(new SctpDataChunk
            {
                Tsn = tsn,
                StreamSequenceNumber = 5,
                Flags = tsn == 1 ? (byte)0x02 : (byte)0x00,
                UserData = [(byte)(tsn & 0xFF)]
            });
        }

        stream.HandleChunk(new SctpDataChunk
        {
            Tsn = 1003,
            StreamSequenceNumber = 5,
            Flags = 0x01,
            UserData = [1]
        });

        Assert.Null(receivedData);
    }
}
