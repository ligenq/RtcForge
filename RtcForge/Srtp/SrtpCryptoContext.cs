using System.Buffers.Binary;
using System.Security.Cryptography;
using RtcForge.Rtp;

namespace RtcForge.Srtp;

public class SrtpCryptoContext
{
    private readonly byte[] _masterKey;
    private readonly byte[] _masterSalt;
    private byte[]? _sessionKey;
    private byte[]? _sessionSalt;
    private byte[]? _sessionAuthKey;

    private uint _roc = 0;
    private ushort _lastSeq = 0;
    private bool _firstPacket = true;

    public SrtpCryptoContext(byte[] masterKey, byte[] masterSalt)
    {
        _masterKey = masterKey;
        _masterSalt = masterSalt;
        DeriveSessionKeys();
    }

    private void DeriveSessionKeys()
    {
        _sessionKey = SrtpKeyDerivation.DeriveKey(_masterKey, _masterSalt, 0x00, 16);
        _sessionAuthKey = SrtpKeyDerivation.DeriveKey(_masterKey, _masterSalt, 0x01, 20);
        _sessionSalt = SrtpKeyDerivation.DeriveKey(_masterKey, _masterSalt, 0x02, 14);
    }

    public bool Protect(RtpPacket packet, Span<byte> output, out int length)
    {
        length = 0;
        UpdateRoc(packet.SequenceNumber);

        int totalLenBeforeTag = packet.Serialize(output);
        if (totalLenBeforeTag <= 0)
        {
            return false;
        }

        int headerLen = totalLenBeforeTag - packet.Payload.Length;

        // Construct IV
        Span<byte> iv = stackalloc byte[16];
        ConstructIv(iv, packet.Ssrc, packet.SequenceNumber, _roc);

        Span<byte> payload = output.Slice(headerLen, packet.Payload.Length);
        SrtpAesCtr.Transform(_sessionKey!, iv.ToArray(), payload); // AesCtr currently takes byte[] for IV

        // Authentication Tag (HMAC-SHA1-80)
        Span<byte> rocBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(rocBytes, _roc);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, _sessionAuthKey!);
        hmac.AppendData(output.Slice(0, totalLenBeforeTag));
        hmac.AppendData(rocBytes);

        Span<byte> fullHash = stackalloc byte[20];
        if (!hmac.TryGetHashAndReset(fullHash, out int bytesWritten))
        {
            return false;
        }

        fullHash.Slice(0, 10).CopyTo(output.Slice(totalLenBeforeTag, 10));

        length = totalLenBeforeTag + 10;
        return true;
    }

    public bool Unprotect(ReadOnlyMemory<byte> input, out RtpPacket packet)
    {
        packet = null!;
        if (input.Length < 12 + 10)
        {
            return false;
        }

        int totalLenBeforeTag = input.Length - 10;
        var rawPacket = input.Slice(0, totalLenBeforeTag);

        // TryParse doesn't copy the payload if we use its Memory variant (though current impl does)
        if (!RtpPacket.TryParse(rawPacket, out packet))
        {
            return false;
        }

        UpdateRoc(packet.SequenceNumber);

        // Verify Authentication Tag
        Span<byte> rocBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(rocBytes, _roc);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, _sessionAuthKey!);
        hmac.AppendData(rawPacket.Span);
        hmac.AppendData(rocBytes);

        Span<byte> hash = stackalloc byte[20];
        hmac.TryGetHashAndReset(hash, out _);
        ReadOnlySpan<byte> actualTag = input.Span.Slice(totalLenBeforeTag, 10);

        if (!hash.Slice(0, 10).SequenceEqual(actualTag))
        {
            return false;
        }

        // Decrypt Payload
        Span<byte> iv = stackalloc byte[16];
        ConstructIv(iv, packet.Ssrc, packet.SequenceNumber, _roc);

        // We must copy the payload to decrypt it as packet.Payload might be read-only or shared
        byte[] decryptedPayload = packet.Payload.ToArray();
        SrtpAesCtr.Transform(_sessionKey!, iv.ToArray(), decryptedPayload);
        packet.Payload = decryptedPayload.AsMemory();

        return true;
    }

    private void ConstructIv(Span<byte> iv, uint ssrc, ushort seq, uint roc)
    {
        _sessionSalt!.CopyTo(iv);

        iv[4] ^= (byte)(ssrc >> 24);
        iv[5] ^= (byte)(ssrc >> 16);
        iv[6] ^= (byte)(ssrc >> 8);
        iv[7] ^= (byte)(ssrc);

        iv[8] ^= (byte)(roc >> 24);
        iv[9] ^= (byte)(roc >> 16);
        iv[10] ^= (byte)(roc >> 8);
        iv[11] ^= (byte)(roc);

        iv[12] ^= (byte)(seq >> 8);
        iv[13] ^= (byte)(seq);
    }

    private void UpdateRoc(ushort seq)
    {
        if (_firstPacket)
        {
            _lastSeq = seq;
            _firstPacket = false;
            return;
        }

        if (seq < _lastSeq && (_lastSeq - seq) > 32768)
        {
            _roc++;
        }
        _lastSeq = seq;
    }
}
