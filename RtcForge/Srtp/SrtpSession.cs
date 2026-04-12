using RtcForge.Rtp;
using RtcForge.Dtls;

namespace RtcForge.Srtp;

public class SrtpSession
{
    private readonly SrtpCryptoContext _localContext;
    private readonly SrtpCryptoContext _remoteContext;
    private readonly ushort _profile;

    public SrtpSession(SrtpKeys keys, bool isClient)
    {
        _profile = keys.ProtectionProfile;

        // RFC 5764: client_write_SRTP_master_key | server_write_SRTP_master_key | client_write_SRTP_master_salt | server_write_SRTP_master_salt
        int keyLen = 16;
        int saltLen = 14;

        byte[] ck = new byte[keyLen];
        byte[] sk = new byte[keyLen];
        byte[] cs = new byte[saltLen];
        byte[] ss = new byte[saltLen];

        Buffer.BlockCopy(keys.MasterKey, 0, ck, 0, keyLen);
        Buffer.BlockCopy(keys.MasterKey, keyLen, sk, 0, keyLen);
        Buffer.BlockCopy(keys.MasterKey, 2 * keyLen, cs, 0, saltLen);
        Buffer.BlockCopy(keys.MasterKey, (2 * keyLen) + saltLen, ss, 0, saltLen);

        if (isClient)
        {
            _localContext = new SrtpCryptoContext(ck, cs);
            _remoteContext = new SrtpCryptoContext(sk, ss);
        }
        else
        {
            _localContext = new SrtpCryptoContext(sk, ss);
            _remoteContext = new SrtpCryptoContext(ck, cs);
        }
    }

    public bool Protect(RtpPacket packet, Span<byte> output, out int length)
    {
        return _localContext.Protect(packet, output, out length);
    }

    public bool Unprotect(ReadOnlyMemory<byte> input, out RtpPacket packet)
    {
        return _remoteContext.Unprotect(input, out packet);
    }
}
