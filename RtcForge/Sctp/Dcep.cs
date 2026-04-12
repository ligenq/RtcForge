using System.Buffers.Binary;
using System.Text;

namespace RtcForge.Sctp;

internal enum DcepMessageType : byte
{
    DataChannelAck = 0x02,
    DataChannelOpen = 0x03
}

internal class DcepMessage
{
    public DcepMessageType Type { get; set; }
    public byte ChannelType { get; set; }
    public ushort Priority { get; set; }
    public uint Reliability { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;

    public static DcepMessage Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 1)
        {
            return null!;
        }

        var msg = new DcepMessage { Type = (DcepMessageType)buffer[0] };

        if (msg.Type == DcepMessageType.DataChannelOpen)
        {
            msg.ChannelType = buffer[1];
            msg.Priority = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2));
            msg.Reliability = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));
            ushort labelLen = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(8, 2));
            ushort protoLen = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(10, 2));
            msg.Label = Encoding.UTF8.GetString(buffer.Slice(12, labelLen));
            msg.Protocol = Encoding.UTF8.GetString(buffer.Slice(12 + labelLen, protoLen));
        }

        return msg;
    }

    public byte[] Serialize()
    {
        if (Type == DcepMessageType.DataChannelAck)
        {
            return new byte[] { (byte)Type };
        }

        byte[] labelBytes = Encoding.UTF8.GetBytes(Label);
        byte[] protoBytes = Encoding.UTF8.GetBytes(Protocol);
        byte[] buffer = new byte[12 + labelBytes.Length + protoBytes.Length];

        buffer[0] = (byte)Type;
        buffer[1] = ChannelType;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), Priority);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), Reliability);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(8, 2), (ushort)labelBytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(10, 2), (ushort)protoBytes.Length);
        labelBytes.CopyTo(buffer, 12);
        protoBytes.CopyTo(buffer, 12 + labelBytes.Length);

        return buffer;
    }
}
