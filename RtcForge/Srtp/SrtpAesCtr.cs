using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RtcForge.Srtp;

public static class SrtpAesCtr
{
    public static void Transform(byte[] key, byte[] iv, Span<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        byte[] counter = new byte[16];
        byte[] keystream = new byte[16];
        iv.CopyTo(counter, 0);

        int offset = 0;
        while (offset < data.Length)
        {
            aes.EncryptEcb(counter, keystream, PaddingMode.None);

            int bytesToProcess = Math.Min(16, data.Length - offset);
            for (int i = 0; i < bytesToProcess; i++)
            {
                data[offset + i] ^= keystream[i];
            }

            // Increment counter (last 16 bits)
            ushort val = BinaryPrimitives.ReadUInt16BigEndian(counter.AsSpan(14, 2));
            BinaryPrimitives.WriteUInt16BigEndian(counter.AsSpan(14, 2), (ushort)(val + 1));

            offset += bytesToProcess;
        }
    }
}
