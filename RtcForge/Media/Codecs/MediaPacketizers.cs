using RtcForge.Rtp;

namespace RtcForge.Media.Codecs;

public class OpusPacketizer
{
    public List<RtpPacket> Packetize(ReadOnlySpan<byte> frame, uint timestamp, uint ssrc, ushort sequenceNumber)
    {
        return new List<RtpPacket>
        {
            new RtpPacket
            {
                PayloadType = 111,
                SequenceNumber = sequenceNumber,
                Timestamp = timestamp,
                Ssrc = ssrc,
                Marker = true,
                Payload = frame.ToArray().AsMemory()
            }
        };
    }
}

public class Vp8Packetizer
{
    public List<RtpPacket> Packetize(ReadOnlySpan<byte> frame, uint timestamp, uint ssrc, ushort sequenceNumber)
    {
        const int maxPayload = 1200;
        int offset = 0;
        bool first = true;
        ushort sn = sequenceNumber;
        var packets = new List<RtpPacket>();

        while (offset < frame.Length)
        {
            int remaining = frame.Length - offset;
            int size = Math.Min(remaining, maxPayload);
            byte[] payload = new byte[size + 1];

            payload[0] = (byte)(first ? 0x10 : 0x00);
            frame.Slice(offset, size).CopyTo(payload.AsSpan(1));

            packets.Add(new RtpPacket
            {
                PayloadType = 96,
                SequenceNumber = sn++,
                Timestamp = timestamp,
                Ssrc = ssrc,
                Marker = (offset + size == frame.Length),
                Payload = payload.AsMemory()
            });

            offset += size;
            first = false;
        }
        return packets;
    }
}
