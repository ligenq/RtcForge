using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using RtcForge.Rtp;

namespace RtcForge.Srtp;

public class SrtpCryptoContext
{
    private readonly byte[] _masterKey;
    private readonly byte[] _masterSalt;
    private readonly Aes _aes = Aes.Create();
    private readonly object _aesLock = new();
    private byte[]? _sessionKey;
    private byte[]? _sessionSalt;
    private byte[]? _sessionAuthKey;

    private readonly ConcurrentDictionary<uint, SsrcRtpState> _rtpStates = new();
    private uint _srtcpSendIndex;
    private readonly object _srtcpSendLock = new();
    private readonly ConcurrentDictionary<uint, SrtcpReplayState> _srtcpReplayStates = new();

    private sealed class SsrcRtpState
    {
        public uint Roc;
        public ushort LastSeq;
        public bool FirstPacket = true;
        public ulong ReplayWindow;
        public ulong HighestIndex;
    }

    private sealed class SrtcpReplayState
    {
        public ulong ReplayWindow;
        public uint HighestIndex;
    }

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
        _aes.Key = _sessionKey;
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
    }

    public bool Protect(RtpPacket packet, Span<byte> output, out int length)
    {
        length = 0;
        var state = _rtpStates.GetOrAdd(packet.Ssrc, _ => new SsrcRtpState());
        uint roc;
        lock (state)
        {
            roc = EstimateRoc(state, packet.SequenceNumber);
            CommitRtpIndex(state, packet.SequenceNumber, roc);
        }

        int totalLenBeforeTag = packet.Serialize(output);
        if (totalLenBeforeTag <= 0)
        {
            return false;
        }

        int headerLen = totalLenBeforeTag - packet.Payload.Length;

        // Construct IV
        Span<byte> iv = stackalloc byte[16];
        ConstructIv(iv, packet.Ssrc, packet.SequenceNumber, roc);

        Span<byte> payload = output.Slice(headerLen, packet.Payload.Length);
        SrtpAesCtr.Transform(_aes, _aesLock, iv, payload);

        // Authentication Tag (HMAC-SHA1-80)
        Span<byte> rocBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(rocBytes, roc);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, _sessionAuthKey!);
        hmac.AppendData(output[..totalLenBeforeTag]);
        hmac.AppendData(rocBytes);

        Span<byte> fullHash = stackalloc byte[20];
        if (!hmac.TryGetHashAndReset(fullHash, out int bytesWritten))
        {
            return false;
        }

        fullHash[..10].CopyTo(output.Slice(totalLenBeforeTag, 10));

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
        var rawPacket = input[..totalLenBeforeTag];

        // TryParse doesn't copy the payload if we use its Memory variant (though current impl does)
        if (!RtpPacket.TryParse(rawPacket, out packet))
        {
            return false;
        }

        var state = _rtpStates.GetOrAdd(packet.Ssrc, _ => new SsrcRtpState());
        uint roc;
        ulong index;
        lock (state)
        {
            roc = EstimateRoc(state, packet.SequenceNumber);
            index = ((ulong)roc << 16) | packet.SequenceNumber;
        }

        // Verify Authentication Tag
        Span<byte> rocBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(rocBytes, roc);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, _sessionAuthKey!);
        hmac.AppendData(rawPacket.Span);
        hmac.AppendData(rocBytes);

        Span<byte> hash = stackalloc byte[20];
        hmac.TryGetHashAndReset(hash, out _);
        ReadOnlySpan<byte> actualTag = input.Span.Slice(totalLenBeforeTag, 10);

        if (!hash[..10].SequenceEqual(actualTag))
        {
            return false;
        }

        lock (state)
        {
            ulong previousHighest = state.HighestIndex;
            if (!CheckReplay(state, index))
            {
                return false;
            }

            if (index >= previousHighest)
            {
                CommitRtpIndex(state, packet.SequenceNumber, roc);
            }
        }

        // Decrypt Payload
        Span<byte> iv = stackalloc byte[16];
        ConstructIv(iv, packet.Ssrc, packet.SequenceNumber, roc);

        // We must copy the payload to decrypt it as packet.Payload might be read-only or shared
        byte[] decryptedPayload = packet.Payload.ToArray();
        SrtpAesCtr.Transform(_aes, _aesLock, iv, decryptedPayload);
        packet.Payload = decryptedPayload.AsMemory();

        return true;
    }

    public bool ProtectRtcp(ReadOnlySpan<byte> rtcp, Span<byte> output, out int length)
    {
        length = 0;
        if (rtcp.Length < 8 || output.Length < rtcp.Length + 14)
        {
            return false;
        }

        rtcp.CopyTo(output);
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(rtcp.Slice(4, 4));
        uint index;
        lock (_srtcpSendLock)
        {
            _srtcpSendIndex = (_srtcpSendIndex + 1) & 0x7FFFFFFF;
            index = _srtcpSendIndex;
        }
        BinaryPrimitives.WriteUInt32BigEndian(output.Slice(rtcp.Length, 4), index);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, _sessionAuthKey!);
        hmac.AppendData(output[..(rtcp.Length + 4)]);
        Span<byte> fullHash = stackalloc byte[20];
        if (!hmac.TryGetHashAndReset(fullHash, out _))
        {
            return false;
        }

        fullHash[..10].CopyTo(output.Slice(rtcp.Length + 4, 10));
        length = rtcp.Length + 14;
        return true;
    }

    public bool UnprotectRtcp(ReadOnlyMemory<byte> input, out ReadOnlyMemory<byte> rtcp)
    {
        rtcp = default;
        if (input.Length < 8 + 4 + 10)
        {
            return false;
        }

        int rtcpLength = input.Length - 14;
        ReadOnlySpan<byte> span = input.Span;
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
        uint encodedIndex = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(rtcpLength, 4));
        uint index = encodedIndex & 0x7FFFFFFF;

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, _sessionAuthKey!);
        hmac.AppendData(span[..(rtcpLength + 4)]);
        Span<byte> hash = stackalloc byte[20];
        hmac.TryGetHashAndReset(hash, out _);
        if (!hash[..10].SequenceEqual(span.Slice(rtcpLength + 4, 10)))
        {
            return false;
        }

        var replay = _srtcpReplayStates.GetOrAdd(ssrc, _ => new SrtcpReplayState());
        lock (replay)
        {
            if (!CheckSrtcpReplay(replay, index))
            {
                return false;
            }
        }

        rtcp = input[..rtcpLength];
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

    private static uint EstimateRoc(SsrcRtpState state, ushort seq)
    {
        if (state.FirstPacket)
        {
            return 0;
        }

        if (state.LastSeq < 32768)
        {
            if (seq - state.LastSeq > 32768 && state.Roc > 0)
            {
                return state.Roc - 1;
            }

            return state.Roc;
        }

        if (state.LastSeq - 32768 > seq)
        {
            return state.Roc + 1;
        }

        return state.Roc;
    }

    private static void CommitRtpIndex(SsrcRtpState state, ushort seq, uint roc)
    {
        state.Roc = roc;
        state.LastSeq = seq;
        state.FirstPacket = false;
    }

    private static bool CheckReplay(SsrcRtpState state, ulong index)
    {
        if (state.HighestIndex == 0 && state.ReplayWindow == 0)
        {
            state.HighestIndex = index;
            state.ReplayWindow = 1;
            return true;
        }

        if (index > state.HighestIndex)
        {
            ulong delta = index - state.HighestIndex;
            state.ReplayWindow = delta >= 64 ? 1 : (state.ReplayWindow << (int)delta) | 1;
            state.HighestIndex = index;
            return true;
        }

        ulong behind = state.HighestIndex - index;
        if (behind >= 64)
        {
            return false;
        }

        ulong mask = 1UL << (int)behind;
        if ((state.ReplayWindow & mask) != 0)
        {
            return false;
        }

        state.ReplayWindow |= mask;
        return true;
    }

    private static bool CheckSrtcpReplay(SrtcpReplayState state, uint index)
    {
        if (state.HighestIndex == 0 && state.ReplayWindow == 0)
        {
            state.HighestIndex = index;
            state.ReplayWindow = 1;
            return true;
        }

        if (index > state.HighestIndex)
        {
            uint delta = index - state.HighestIndex;
            state.ReplayWindow = delta >= 64 ? 1 : (state.ReplayWindow << (int)delta) | 1;
            state.HighestIndex = index;
            return true;
        }

        uint behind = state.HighestIndex - index;
        if (behind >= 64)
        {
            return false;
        }

        ulong mask = 1UL << (int)behind;
        if ((state.ReplayWindow & mask) != 0)
        {
            return false;
        }

        state.ReplayWindow |= mask;
        return true;
    }
}
