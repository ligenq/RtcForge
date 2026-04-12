using System.Buffers.Binary;

namespace RtcForge.Rtp;

/// <summary>
/// Represents an RTP (Real-time Transport Protocol) packet as defined in RFC 3550.
/// This implementation is designed for high performance using Span and Memory.
/// </summary>
public class RtpPacket
{
    public const int MinHeaderLength = 12;

    public byte Version { get; set; } = 2;
    public bool Padding { get; set; }
    public bool Extension { get; set; }
    public byte CsrcCount { get; set; }
    public bool Marker { get; set; }
    public byte PayloadType { get; set; }
    public ushort SequenceNumber { get; set; }
    public uint Timestamp { get; set; }
    public uint Ssrc { get; set; }
    public uint[]? Csrc { get; set; }
    public byte[]? ExtensionHeader { get; set; } // Optional: Can be further refined
    public Memory<byte> Payload { get; set; }

    public RtpPacket() { }

    public static bool TryParse(ReadOnlyMemory<byte> memory, out RtpPacket packet)
    {
        var buffer = memory.Span;
        packet = null!;
        if (buffer.Length < MinHeaderLength)
        {
            return false;
        }

        byte firstByte = buffer[0];
        byte version = (byte)(firstByte >> 6);
        if (version != 2)
        {
            return false;
        }

        bool padding = (firstByte & 0x20) != 0;
        bool extension = (firstByte & 0x10) != 0;
        byte csrcCount = (byte)(firstByte & 0x0F);

        byte secondByte = buffer[1];
        bool marker = (secondByte & 0x80) != 0;
        byte payloadType = (byte)(secondByte & 0x7F);

        ushort sequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2));
        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(4, 4));
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(8, 4));

        int offset = MinHeaderLength;

        uint[]? csrc = null;
        if (csrcCount > 0)
        {
            if (buffer.Length < offset + (csrcCount * 4))
            {
                return false;
            }

            csrc = new uint[csrcCount];
            for (int i = 0; i < csrcCount; i++)
            {
                csrc[i] = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset, 4));
                offset += 4;
            }
        }

        // Extension handling (optional for now, but should be supported)
        byte[]? extensionHeader = null;
        if (extension)
        {
            if (buffer.Length < offset + 4)
            {
                return false;
            }
            // Extension header is 4 bytes: profile-defined (2 bytes) + length (2 bytes, length in 32-bit words)
            ushort extensionLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 2, 2));
            int totalExtensionBytes = 4 + (extensionLength * 4);
            if (buffer.Length < offset + totalExtensionBytes)
            {
                return false;
            }

            extensionHeader = buffer.Slice(offset, totalExtensionBytes).ToArray();
            offset += totalExtensionBytes;
        }

        int payloadLength = buffer.Length - offset;
        if (padding)
        {
            if (buffer.Length == 0)
            {
                return false;
            }

            byte paddingCount = buffer[buffer.Length - 1];
            if (paddingCount > payloadLength)
            {
                return false;
            }

            payloadLength -= paddingCount;
        }

        if (payloadLength < 0)
        {
            return false;
        }

        packet = new RtpPacket
        {
            Version = version,
            Padding = padding,
            Extension = extension,
            CsrcCount = csrcCount,
            Marker = marker,
            PayloadType = payloadType,
            SequenceNumber = sequenceNumber,
            Timestamp = timestamp,
            Ssrc = ssrc,
            Csrc = csrc,
            ExtensionHeader = extensionHeader,
            Payload = buffer.Slice(offset, payloadLength).ToArray().AsMemory() // TODO: Optimize with memory pooling
        };

        return true;
    }

    public int GetSerializedLength()
    {
        int length = MinHeaderLength;
        length += (CsrcCount * 4);
        if (Extension && ExtensionHeader != null)
        {
            length += ExtensionHeader.Length;
        }
        length += Payload.Length;
        if (Padding)
        {
            // Simple padding for now (just 1 byte at the end if padding is set, though padding should make it multiple of 4 usually)
            // For now, we'll assume the payload already includes any required padding if Padding is true, or we add 1 byte.
            length += 1;
        }
        return length;
    }

    public int Serialize(Span<byte> buffer)
    {
        if (buffer.Length < GetSerializedLength())
        {
            return -1;
        }

        buffer[0] = (byte)((Version << 6) | (Padding ? 0x20 : 0x00) | (Extension ? 0x10 : 0x00) | (CsrcCount & 0x0F));
        buffer[1] = (byte)((Marker ? 0x80 : 0x00) | (PayloadType & 0x7F));

        BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), SequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(8, 4), Ssrc);

        int offset = MinHeaderLength;
        if (CsrcCount > 0 && Csrc != null)
        {
            for (int i = 0; i < CsrcCount; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(offset, 4), Csrc[i]);
                offset += 4;
            }
        }

        if (Extension && ExtensionHeader != null)
        {
            ExtensionHeader.CopyTo(buffer.Slice(offset, ExtensionHeader.Length));
            offset += ExtensionHeader.Length;
        }

        Payload.Span.CopyTo(buffer.Slice(offset, Payload.Length));
        offset += Payload.Length;

        if (Padding)
        {
            // Example: Add 1 byte of padding as requested by the bit
            buffer[offset] = 1;
            offset += 1;
        }

        return offset;
    }
}
