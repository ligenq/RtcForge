using RtcForge.Rtp;

namespace RtcForge.Tests.Rtp;

public class RtcpPliAndFirTests
{
    [Fact]
    public void Serialize_PliPacket_ProducesCorrectBytes()
    {
        var pli = new RtcpPliPacket { SenderSsrc = 1, MediaSsrc = 2 };

        byte[] buffer = new byte[pli.GetSerializedLength()];
        int len = pli.Serialize(buffer);

        Assert.Equal(12, len);
    }

    [Fact]
    public void Serialize_Parse_PliPacket_RoundTrip()
    {
        var original = new RtcpPliPacket { SenderSsrc = 0x12345678, MediaSsrc = 0xABCDEF01 };

        byte[] buffer = new byte[original.GetSerializedLength()];
        original.Serialize(buffer);

        var packets = RtcpPacket.ParseCompound(buffer);
        var parsed = Assert.Single(packets);
        var pli = Assert.IsType<RtcpPliPacket>(parsed);

        Assert.Equal(original.SenderSsrc, pli.SenderSsrc);
        Assert.Equal(original.MediaSsrc, pli.MediaSsrc);
    }

    [Fact]
    public void PliPacket_TryParse_TooShort_ReturnsNull()
    {
        byte[] buffer = new byte[8];
        var result = RtcpPliPacket.TryParse(buffer);

        Assert.Null(result);
    }

    [Fact]
    public void Serialize_FirPacket_ProducesCorrectBytes()
    {
        var fir = new RtcpFirPacket { SenderSsrc = 1, MediaSsrc = 2, SequenceNumber = 42 };

        byte[] buffer = new byte[fir.GetSerializedLength()];
        int len = fir.Serialize(buffer);

        Assert.Equal(20, len);
    }

    [Fact]
    public void Serialize_Parse_FirPacket_RoundTrip()
    {
        var original = new RtcpFirPacket
        {
            SenderSsrc = 0x11223344,
            MediaSsrc = 0x55667788,
            SequenceNumber = 99
        };

        byte[] buffer = new byte[original.GetSerializedLength()];
        original.Serialize(buffer);

        var packets = RtcpPacket.ParseCompound(buffer);
        var parsed = Assert.Single(packets);
        var fir = Assert.IsType<RtcpFirPacket>(parsed);

        Assert.Equal(original.SenderSsrc, fir.SenderSsrc);
        Assert.Equal(original.MediaSsrc, fir.MediaSsrc);
        Assert.Equal(original.SequenceNumber, fir.SequenceNumber);
    }

    [Fact]
    public void FirPacket_TryParse_TooShort_ReturnsNull()
    {
        byte[] buffer = new byte[16];
        var result = RtcpFirPacket.TryParse(buffer);

        Assert.Null(result);
    }

    [Fact]
    public void Serialize_ReceiverReportPacket_RoundTrip()
    {
        var original = new RtcpReceiverReportPacket { Ssrc = 0xDEADBEEF };

        byte[] buffer = new byte[original.GetSerializedLength()];
        original.Serialize(buffer);

        var packets = RtcpPacket.ParseCompound(buffer);
        var parsed = Assert.Single(packets);
        var rr = Assert.IsType<RtcpReceiverReportPacket>(parsed);

        Assert.Equal(original.Ssrc, rr.Ssrc);
    }

    [Fact]
    public void Serialize_PliPacket_BufferTooSmall_ReturnsNegative()
    {
        var pli = new RtcpPliPacket { SenderSsrc = 1, MediaSsrc = 2 };
        byte[] buffer = new byte[8];

        int len = pli.Serialize(buffer);

        Assert.Equal(-1, len);
    }

    [Fact]
    public void Serialize_FirPacket_BufferTooSmall_ReturnsNegative()
    {
        var fir = new RtcpFirPacket { SenderSsrc = 1, MediaSsrc = 2, SequenceNumber = 1 };
        byte[] buffer = new byte[16];

        int len = fir.Serialize(buffer);

        Assert.Equal(-1, len);
    }

    [Fact]
    public void ParseCompound_PliAndFirTogether_ReturnsBoth()
    {
        var pli = new RtcpPliPacket { SenderSsrc = 1, MediaSsrc = 2 };
        var fir = new RtcpFirPacket { SenderSsrc = 3, MediaSsrc = 4, SequenceNumber = 5 };

        byte[] buffer = new byte[pli.GetSerializedLength() + fir.GetSerializedLength()];
        int offset = pli.Serialize(buffer);
        fir.Serialize(buffer.AsSpan(offset));

        var packets = RtcpPacket.ParseCompound(buffer);

        Assert.Equal(2, packets.Count);
        Assert.IsType<RtcpPliPacket>(packets[0]);
        Assert.IsType<RtcpFirPacket>(packets[1]);
    }

    [Fact]
    public void NackPacket_EmptyList_SerializeReturnsNegative()
    {
        var nack = new RtcpNackPacket { SenderSsrc = 1, MediaSsrc = 2 };
        byte[] buffer = new byte[100];

        int len = nack.Serialize(buffer);

        Assert.Equal(-1, len);
    }

    [Fact]
    public void NackPacket_SingleSequenceNumber_RoundTrip()
    {
        var nack = new RtcpNackPacket
        {
            SenderSsrc = 10,
            MediaSsrc = 20,
            LostSequenceNumbers = new List<ushort> { 500 }
        };

        byte[] buffer = new byte[nack.GetSerializedLength()];
        int len = nack.Serialize(buffer);

        Assert.Equal(16, len); // 12 header + 4 one block

        var parsed = RtcpNackPacket.TryParse(buffer, 1);
        Assert.NotNull(parsed);
        Assert.Single(parsed.LostSequenceNumbers);
        Assert.Equal((ushort)500, parsed.LostSequenceNumbers[0]);
    }
}
