using System.Buffers.Binary;
using System.Buffers;
using System.Security.Cryptography;

namespace RtcForge.Stun;

public static class StunSecurity
{
    public static byte[] CalculateMessageIntegrity(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        return HMACSHA1.HashData(key, data);
    }

    public static uint CalculateFingerprint(ReadOnlySpan<byte> data)
    {
        uint crc = System.IO.Hashing.Crc32.HashToUInt32(data);
        return crc ^ 0x5354554e;
    }

    public static bool ValidateFingerprint(ReadOnlySpan<byte> data)
    {
        if (!TryGetAttributeSpan(data, StunAttributeType.Fingerprint, out int fingerprintHeaderOffset, out int fingerprintValueOffset, out int fingerprintLength) || fingerprintLength != 4)
        {
            return false;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(fingerprintValueOffset);
        try
        {
            Span<byte> buffer = rented.AsSpan(0, fingerprintValueOffset);
            data[..fingerprintValueOffset].CopyTo(buffer);
            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), (ushort)(fingerprintHeaderOffset + 8 - StunMessage.HeaderLength));

            uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(fingerprintValueOffset, 4));
            uint actualCrc = CalculateFingerprint(buffer);

            return expectedCrc == actualCrc;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static bool ValidateMessageIntegrity(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty || !TryGetAttributeSpan(data, StunAttributeType.MessageIntegrity, out int integrityHeaderOffset, out int integrityValueOffset, out int integrityLength) || integrityLength != 20)
        {
            return false;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(integrityValueOffset);
        try
        {
            Span<byte> buffer = rented.AsSpan(0, integrityValueOffset);
            data[..integrityValueOffset].CopyTo(buffer);
            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), (ushort)(integrityHeaderOffset + 24 - StunMessage.HeaderLength));

            byte[] actual = CalculateMessageIntegrity(buffer, key);
            return actual.AsSpan().SequenceEqual(data.Slice(integrityValueOffset, 20));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    public static byte[] DeriveTurnKey(string username, string realm, string password)
    {
        string s = $"{username}:{realm}:{password}";
        return System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s));
    }

    private static bool TryGetAttributeSpan(ReadOnlySpan<byte> data, StunAttributeType attributeType, out int attributeHeaderOffset, out int attributeValueOffset, out int attributeValueLength)
    {
        attributeHeaderOffset = 0;
        attributeValueOffset = 0;
        attributeValueLength = 0;

        if (data.Length < StunMessage.HeaderLength)
        {
            return false;
        }

        int messageLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
        int offset = StunMessage.HeaderLength;
        int end = Math.Min(data.Length, StunMessage.HeaderLength + messageLength);

        while (offset + 4 <= end)
        {
            ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            ushort attrLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
            int valueOffset = offset + 4;
            if (valueOffset + attrLength > end)
            {
                return false;
            }

            if ((StunAttributeType)attrType == attributeType)
            {
                attributeHeaderOffset = offset;
                attributeValueOffset = valueOffset;
                attributeValueLength = attrLength;
                return true;
            }

            offset = valueOffset + ((attrLength + 3) & ~3);
        }

        return false;
    }
}
