using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RtcForge.Srtp;

public static class SrtpKeyDerivation
{
    // RFC 3711 Section 4.3
    // L = label (1 byte)
    // n = length of key to derive
    public static byte[] DeriveKey(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> masterSalt, byte label, int n)
    {
        byte[] key = new byte[n];
        byte[] iv = new byte[16];
        masterSalt.CopyTo(iv.AsSpan(0, masterSalt.Length));
        iv[7] ^= label;

        using var aes = Aes.Create();
        aes.Key = masterKey.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        var encryptor = aes.CreateEncryptor();
        byte[] counter = new byte[16];
        byte[] output = new byte[16];
        int offset = 0;
        ushort blockIndex = 0;

        while (offset < n)
        {
            iv.CopyTo(counter, 0);
            BinaryPrimitives.WriteUInt16BigEndian(counter.AsSpan(14, 2), blockIndex++);
            encryptor.TransformBlock(counter, 0, 16, output, 0);

            int bytesToCopy = Math.Min(16, n - offset);
            Buffer.BlockCopy(output, 0, key, offset, bytesToCopy);
            offset += bytesToCopy;
        }

        return key;
    }
}
