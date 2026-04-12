using RtcForge.Media.Codecs;

namespace RtcForge.Tests.Media;

public class MediaPacketizerTests
{
    [Fact]
    public void OpusPacketizer_ReturnsSinglePacket()
    {
        var packetizer = new OpusPacketizer();
        ushort sn = 100;
        var frame = new byte[] { 1, 2, 3, 4 };

        var packets = packetizer.Packetize(frame, 1000, 1234, sn).ToList();

        Assert.Single(packets);
        Assert.Equal(111, packets[0].PayloadType);
        Assert.Equal((ushort)100, packets[0].SequenceNumber);
        Assert.Equal(4, packets[0].Payload.Length);
    }

    [Fact]
    public void Vp8Packetizer_FragmentsLargeFrame()
    {
        var packetizer = new Vp8Packetizer();
        ushort sn = 100;
        var frame = new byte[2500]; // Should result in 3 packets (1200, 1200, 100)
        new Random().NextBytes(frame);

        var packets = packetizer.Packetize(frame, 1000, 1234, sn).ToList();

        Assert.Equal(3, packets.Count);
        Assert.Equal(0x10, packets[0].Payload.Span[0]); // S bit set
        Assert.Equal((ushort)100, packets[0].SequenceNumber);
        Assert.Equal((ushort)101, packets[1].SequenceNumber);
        Assert.Equal((ushort)102, packets[2].SequenceNumber);

        Assert.True(packets[2].Marker); // Last packet
    }
}
