using System.Buffers.Binary;
using System.Text;
using RtcForge.Sctp;

namespace RtcForge.Tests.Sctp;

public class DcepMessageTests
{
    [Fact]
    public void Parse_DataChannelOpen_ReturnsCorrectFields()
    {
        var original = new DcepMessage
        {
            Type = DcepMessageType.DataChannelOpen,
            ChannelType = 0x00,
            Priority = 256,
            Reliability = 0,
            Label = "test-channel",
            Protocol = ""
        };

        byte[] data = original.Serialize();
        var parsed = DcepMessage.Parse(data);

        Assert.Equal(DcepMessageType.DataChannelOpen, parsed.Type);
        Assert.Equal(0x00, parsed.ChannelType);
        Assert.Equal((ushort)256, parsed.Priority);
        Assert.Equal(0u, parsed.Reliability);
        Assert.Equal("test-channel", parsed.Label);
        Assert.Equal("", parsed.Protocol);
    }

    [Fact]
    public void Parse_DataChannelAck_ReturnsAckType()
    {
        var original = new DcepMessage { Type = DcepMessageType.DataChannelAck };
        byte[] data = original.Serialize();

        var parsed = DcepMessage.Parse(data);

        Assert.Equal(DcepMessageType.DataChannelAck, parsed.Type);
    }

    [Fact]
    public void Serialize_DataChannelAck_SingleByte()
    {
        var msg = new DcepMessage { Type = DcepMessageType.DataChannelAck };
        byte[] data = msg.Serialize();

        Assert.Single(data);
        Assert.Equal(0x02, data[0]);
    }

    [Fact]
    public void Serialize_DataChannelOpen_CorrectLayout()
    {
        var msg = new DcepMessage
        {
            Type = DcepMessageType.DataChannelOpen,
            ChannelType = 0x01,
            Priority = 512,
            Reliability = 100,
            Label = "chat",
            Protocol = "proto"
        };

        byte[] data = msg.Serialize();

        Assert.Equal(0x03, data[0]); // DataChannelOpen
        Assert.Equal(0x01, data[1]); // ChannelType
        Assert.Equal((ushort)512, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2)));
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)));
        ushort labelLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8, 2));
        ushort protoLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10, 2));
        Assert.Equal(4, labelLen);
        Assert.Equal(5, protoLen);
        Assert.Equal("chat", Encoding.UTF8.GetString(data, 12, labelLen));
        Assert.Equal("proto", Encoding.UTF8.GetString(data, 12 + labelLen, protoLen));
    }

    [Fact]
    public void Parse_EmptyBuffer_ReturnsNull()
    {
        var result = DcepMessage.Parse(ReadOnlySpan<byte>.Empty);

        // Current implementation returns null! for empty buffer
        Assert.Null(result);
    }

    [Fact]
    public void Parse_Serialize_RoundTrip_WithLabelAndProtocol()
    {
        var original = new DcepMessage
        {
            Type = DcepMessageType.DataChannelOpen,
            ChannelType = 0x02,
            Priority = 1000,
            Reliability = 50,
            Label = "my-data-channel",
            Protocol = "my-protocol"
        };

        byte[] serialized = original.Serialize();
        var parsed = DcepMessage.Parse(serialized);

        Assert.Equal(original.Type, parsed.Type);
        Assert.Equal(original.ChannelType, parsed.ChannelType);
        Assert.Equal(original.Priority, parsed.Priority);
        Assert.Equal(original.Reliability, parsed.Reliability);
        Assert.Equal(original.Label, parsed.Label);
        Assert.Equal(original.Protocol, parsed.Protocol);
    }

    [Fact]
    public void Parse_Serialize_RoundTrip_EmptyLabelAndProtocol()
    {
        var original = new DcepMessage
        {
            Type = DcepMessageType.DataChannelOpen,
            ChannelType = 0x00,
            Priority = 0,
            Reliability = 0,
            Label = "",
            Protocol = ""
        };

        byte[] serialized = original.Serialize();
        var parsed = DcepMessage.Parse(serialized);

        Assert.Equal("", parsed.Label);
        Assert.Equal("", parsed.Protocol);
    }

    [Fact]
    public void Parse_Serialize_RoundTrip_UnicodeLabel()
    {
        var original = new DcepMessage
        {
            Type = DcepMessageType.DataChannelOpen,
            Label = "channel-\u00e9\u00e8\u00ea",
            Protocol = ""
        };

        byte[] serialized = original.Serialize();
        var parsed = DcepMessage.Parse(serialized);

        Assert.Equal(original.Label, parsed.Label);
    }
}
