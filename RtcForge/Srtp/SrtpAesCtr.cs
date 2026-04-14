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
        Transform(aes, aes, iv, data);
    }

    internal static void Transform(Aes aes, object syncRoot, ReadOnlySpan<byte> iv, Span<byte> data)
    {
        Span<byte> counter = stackalloc byte[16];
        Span<byte> keystream = stackalloc byte[16];
        iv.CopyTo(counter);

        int offset = 0;
        while (offset < data.Length)
        {
            lock (syncRoot)
            {
                aes.EncryptEcb(counter, keystream, PaddingMode.None);
            }

            int bytesToProcess = Math.Min(16, data.Length - offset);
            for (int i = 0; i < bytesToProcess; i++)
            {
                data[offset + i] ^= keystream[i];
            }

            // Increment counter (last 16 bits)
            ushort val = BinaryPrimitives.ReadUInt16BigEndian(counter.Slice(14, 2));
            BinaryPrimitives.WriteUInt16BigEndian(counter.Slice(14, 2), (ushort)(val + 1));

            offset += bytesToProcess;
        }
    }
}
