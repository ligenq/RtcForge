using RtcForge.Media;
using RtcForge.Rtp;

namespace RtcForge.Tests.Media;

public class RtpJitterBufferTests
{
    [Fact]
    public void Push_OutOrderPackets_Pop_ReturnsInOrder()
    {
        // Arrange
        var buffer = new RtpJitterBuffer();
        var p1 = new RtpPacket { SequenceNumber = 1 };
        var p2 = new RtpPacket { SequenceNumber = 2 };
        var p3 = new RtpPacket { SequenceNumber = 3 };

        // Act: Push out of order
        buffer.Push(p3);
        buffer.Push(p1);
        buffer.Push(p2);

        // Assert
        Assert.Equal(3, buffer.Count);
        Assert.Equal((ushort)1, buffer.Pop()?.SequenceNumber);
        Assert.Equal((ushort)2, buffer.Pop()?.SequenceNumber);
        Assert.Equal((ushort)3, buffer.Pop()?.SequenceNumber);
        Assert.Null(buffer.Pop());
    }

    [Fact]
    public void Push_DuplicatePackets_Ignored()
    {
        // Arrange
        var buffer = new RtpJitterBuffer();
        var p1 = new RtpPacket { SequenceNumber = 1 };

        // Act
        buffer.Push(p1);
        buffer.Push(p1);

        // Assert
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void Push_OldPackets_Ignored()
    {
        // Arrange
        var buffer = new RtpJitterBuffer();
        buffer.Push(new RtpPacket { SequenceNumber = 10 });
        buffer.Pop(); // lastPoppedSeq = 10

        // Act
        buffer.Push(new RtpPacket { SequenceNumber = 5 });

        // Assert
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Push_SequenceRollover_Handled()
    {
        // Arrange
        var buffer = new RtpJitterBuffer();
        buffer.Push(new RtpPacket { SequenceNumber = 65535 });
        buffer.Pop(); // lastPoppedSeq = 65535

        // Act
        var p0 = new RtpPacket { SequenceNumber = 0 };
        buffer.Push(p0);

        // Assert
        Assert.Equal(1, buffer.Count);
        Assert.Equal((ushort)0, buffer.Pop()?.SequenceNumber);
    }

    [Fact]
    public async Task Pop_MissingPacket_WaitsThenTimesOut()
    {
        // Arrange
        var buffer = new RtpJitterBuffer(50, maxWaitTimeMs: 10);
        buffer.Push(new RtpPacket { SequenceNumber = 1 });
        Assert.Equal((ushort)1, buffer.Pop()?.SequenceNumber);

        // Act
        buffer.Push(new RtpPacket { SequenceNumber = 3 }); // 2 is missing

        var packet = buffer.Pop();
        Assert.Null(packet); // Initially null, waiting

        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System); // wait for timeout

        packet = buffer.Pop();
        Assert.NotNull(packet);
        Assert.Equal((ushort)3, packet.SequenceNumber); // 2 was skipped
    }

    [Fact]
    public void Push_WhenOverCapacity_EvictsOldestPackets()
    {
        var buffer = new RtpJitterBuffer(maxPackets: 2);

        buffer.Push(new RtpPacket { SequenceNumber = 1 });
        buffer.Push(new RtpPacket { SequenceNumber = 2 });
        buffer.Push(new RtpPacket { SequenceNumber = 3 });

        Assert.Equal(2, buffer.Count);
        Assert.Equal((ushort)2, buffer.Pop()?.SequenceNumber);
        Assert.Equal((ushort)3, buffer.Pop()?.SequenceNumber);
    }

    [Fact]
    public void Clear_RemovesBufferedPacketsAndResetsSequenceState()
    {
        var buffer = new RtpJitterBuffer();
        buffer.Push(new RtpPacket { SequenceNumber = 10 });
        Assert.Equal((ushort)10, buffer.Pop()?.SequenceNumber);

        buffer.Push(new RtpPacket { SequenceNumber = 11 });
        buffer.Clear();
        buffer.Push(new RtpPacket { SequenceNumber = 5 });

        Assert.Equal(1, buffer.Count);
        Assert.Equal((ushort)5, buffer.Pop()?.SequenceNumber);
    }
}
