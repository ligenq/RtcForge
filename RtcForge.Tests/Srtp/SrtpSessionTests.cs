using RtcForge.Srtp;
using RtcForge.Rtp;
using RtcForge.Dtls;

namespace RtcForge.Tests.Srtp;

public class SrtpSessionTests
{
    [Fact]
    public void Protect_Unprotect_Loopback_Works()
    {
        byte[] masterKey = new byte[16];
        byte[] masterSalt = new byte[14];
        new Random(42).NextBytes(masterKey);
        new Random(42).NextBytes(masterSalt);

        var senderContext = new SrtpCryptoContext(masterKey, masterSalt);
        var receiverContext = new SrtpCryptoContext(masterKey, masterSalt);

        var originalPacket = new RtpPacket
        {
            PayloadType = 96,
            SequenceNumber = 1000,
            Timestamp = 12345,
            Ssrc = 6789,
            Payload = new byte[] { 1, 2, 3, 4, 5 }.AsMemory()
        };

        byte[] protectedBuffer = new byte[100];

        bool protectResult = senderContext.Protect(originalPacket, protectedBuffer, out int protectedLength);
        Assert.True(protectResult);

        bool unprotectResult = receiverContext.Unprotect(protectedBuffer.AsMemory(0, protectedLength), out RtpPacket decryptedPacket);
        Assert.True(unprotectResult);

        Assert.Equal(originalPacket.SequenceNumber, decryptedPacket.SequenceNumber);
        Assert.Equal(originalPacket.Payload.ToArray(), decryptedPacket.Payload.ToArray());
    }

    [Fact]
    public void Unprotect_WithWrongKey_Fails()
    {
        byte[] masterKey = new byte[16];
        byte[] masterSalt = new byte[14];
        byte[] wrongKey = new byte[16];
        new Random(42).NextBytes(masterKey);
        new Random(42).NextBytes(masterSalt);
        new Random(43).NextBytes(wrongKey);

        var senderContext = new SrtpCryptoContext(masterKey, masterSalt);
        var receiverContext = new SrtpCryptoContext(wrongKey, masterSalt);

        var originalPacket = new RtpPacket
        {
            SequenceNumber = 1000,
            Payload = new byte[] { 1, 2, 3, 4, 5 }.AsMemory()
        };

        byte[] protectedBuffer = new byte[100];
        senderContext.Protect(originalPacket, protectedBuffer, out int protectedLength);

        bool unprotectResult = receiverContext.Unprotect(protectedBuffer.AsMemory(0, protectedLength), out _);
        Assert.False(unprotectResult);
    }

    [Fact]
    public void Protect_WithTooSmallOutputBuffer_Fails()
    {
        byte[] masterKey = new byte[16];
        byte[] masterSalt = new byte[14];
        new Random(42).NextBytes(masterKey);
        new Random(43).NextBytes(masterSalt);
        var context = new SrtpCryptoContext(masterKey, masterSalt);
        var packet = new RtpPacket
        {
            PayloadType = 96,
            SequenceNumber = 1,
            Timestamp = 2,
            Ssrc = 3,
            Payload = new byte[] { 1, 2, 3 }.AsMemory()
        };

        Assert.False(context.Protect(packet, new byte[8], out int length));
        Assert.Equal(0, length);
    }

    [Fact]
    public void Unprotect_WithTooShortInput_Fails()
    {
        byte[] masterKey = new byte[16];
        byte[] masterSalt = new byte[14];
        new Random(42).NextBytes(masterKey);
        new Random(43).NextBytes(masterSalt);
        var context = new SrtpCryptoContext(masterKey, masterSalt);

        Assert.False(context.Unprotect(new byte[21], out _));
    }

    [Fact]
    public void Unprotect_ReplayedRtpPacket_Fails()
    {
        byte[] masterKey = new byte[16];
        byte[] masterSalt = new byte[14];
        new Random(42).NextBytes(masterKey);
        new Random(43).NextBytes(masterSalt);

        var senderContext = new SrtpCryptoContext(masterKey, masterSalt);
        var receiverContext = new SrtpCryptoContext(masterKey, masterSalt);
        var packet = new RtpPacket
        {
            PayloadType = 96,
            SequenceNumber = 10,
            Timestamp = 20,
            Ssrc = 30,
            Payload = new byte[] { 1, 2, 3 }.AsMemory()
        };

        byte[] protectedBuffer = new byte[100];
        Assert.True(senderContext.Protect(packet, protectedBuffer, out int protectedLength));

        Assert.True(receiverContext.Unprotect(protectedBuffer.AsMemory(0, protectedLength), out _));
        Assert.False(receiverContext.Unprotect(protectedBuffer.AsMemory(0, protectedLength), out _));
    }

    [Fact]
    public void Unprotect_RtpPacketOutsideReplayWindow_Fails()
    {
        byte[] masterKey = new byte[16];
        byte[] masterSalt = new byte[14];
        new Random(42).NextBytes(masterKey);
        new Random(43).NextBytes(masterSalt);

        var senderContext = new SrtpCryptoContext(masterKey, masterSalt);
        var receiverContext = new SrtpCryptoContext(masterKey, masterSalt);
        var protectedPackets = new List<byte[]>();
        for (ushort sequence = 1; sequence <= 66; sequence++)
        {
            var packet = new RtpPacket
            {
                PayloadType = 96,
                SequenceNumber = sequence,
                Timestamp = sequence,
                Ssrc = 30,
                Payload = new byte[] { (byte)sequence }.AsMemory()
            };
            byte[] buffer = new byte[100];
            Assert.True(senderContext.Protect(packet, buffer, out int length));
            protectedPackets.Add(buffer[..length]);
        }

        Assert.True(receiverContext.Unprotect(protectedPackets[^1], out _));
        Assert.False(receiverContext.Unprotect(protectedPackets[0], out _));
    }

    [Fact]
    public void ProtectUnprotect_MultipleSsrcs_TracksRocIndependently()
    {
        byte[] masterKey = new byte[16];
        byte[] masterSalt = new byte[14];
        new Random(42).NextBytes(masterKey);
        new Random(43).NextBytes(masterSalt);

        var senderContext = new SrtpCryptoContext(masterKey, masterSalt);
        var receiverContext = new SrtpCryptoContext(masterKey, masterSalt);
        var first = new RtpPacket
        {
            PayloadType = 96,
            SequenceNumber = ushort.MaxValue,
            Timestamp = 1,
            Ssrc = 111,
            Payload = new byte[] { 1 }.AsMemory()
        };
        var second = new RtpPacket
        {
            PayloadType = 96,
            SequenceNumber = 1,
            Timestamp = 2,
            Ssrc = 222,
            Payload = new byte[] { 2 }.AsMemory()
        };

        byte[] firstBuffer = new byte[100];
        byte[] secondBuffer = new byte[100];
        Assert.True(senderContext.Protect(first, firstBuffer, out int firstLength));
        Assert.True(senderContext.Protect(second, secondBuffer, out int secondLength));

        Assert.True(receiverContext.Unprotect(firstBuffer.AsMemory(0, firstLength), out var firstDecrypted));
        Assert.True(receiverContext.Unprotect(secondBuffer.AsMemory(0, secondLength), out var secondDecrypted));
        Assert.Equal(111u, firstDecrypted.Ssrc);
        Assert.Equal(222u, secondDecrypted.Ssrc);
        Assert.Equal(new byte[] { 1 }, firstDecrypted.Payload.ToArray());
        Assert.Equal(new byte[] { 2 }, secondDecrypted.Payload.ToArray());
    }

    [Fact]
    public void ProtectUnprotectRtcp_AuthenticatesAndRejectsReplay()
    {
        byte[] masterKey = new byte[16];
        byte[] masterSalt = new byte[14];
        new Random(42).NextBytes(masterKey);
        new Random(43).NextBytes(masterSalt);

        var senderContext = new SrtpCryptoContext(masterKey, masterSalt);
        var receiverContext = new SrtpCryptoContext(masterKey, masterSalt);
        var rtcp = new RtcpReceiverReportPacket { Ssrc = 1234 };
        byte[] rtcpBuffer = new byte[rtcp.GetSerializedLength()];
        int rtcpLength = rtcp.Serialize(rtcpBuffer);
        byte[] protectedBuffer = new byte[rtcpLength + 14];

        Assert.True(senderContext.ProtectRtcp(rtcpBuffer.AsSpan(0, rtcpLength), protectedBuffer, out int protectedLength));
        Assert.Equal(rtcpLength + 14, protectedLength);

        Assert.True(receiverContext.UnprotectRtcp(protectedBuffer.AsMemory(0, protectedLength), out var unprotected));
        var packets = RtcpPacket.ParseCompound(unprotected.Span);
        var parsed = Assert.IsType<RtcpReceiverReportPacket>(Assert.Single(packets));
        Assert.Equal(1234u, parsed.Ssrc);
        Assert.False(receiverContext.UnprotectRtcp(protectedBuffer.AsMemory(0, protectedLength), out _));
    }

    [Fact]
    public void ProtectRtcp_WithInvalidInputOrTooSmallOutput_Fails()
    {
        byte[] masterKey = new byte[16];
        byte[] masterSalt = new byte[14];
        new Random(42).NextBytes(masterKey);
        new Random(43).NextBytes(masterSalt);
        var context = new SrtpCryptoContext(masterKey, masterSalt);

        Assert.False(context.ProtectRtcp(new byte[7], new byte[64], out int shortInputLength));
        Assert.Equal(0, shortInputLength);

        var rtcp = new RtcpReceiverReportPacket { Ssrc = 1234 };
        byte[] rtcpBuffer = new byte[rtcp.GetSerializedLength()];
        int rtcpLength = rtcp.Serialize(rtcpBuffer);
        Assert.False(context.ProtectRtcp(rtcpBuffer.AsSpan(0, rtcpLength), new byte[rtcpLength + 13], out int smallOutputLength));
        Assert.Equal(0, smallOutputLength);
    }

    [Fact]
    public void UnprotectRtcp_WithTooShortInput_Fails()
    {
        byte[] masterKey = new byte[16];
        byte[] masterSalt = new byte[14];
        new Random(42).NextBytes(masterKey);
        new Random(43).NextBytes(masterSalt);
        var context = new SrtpCryptoContext(masterKey, masterSalt);

        Assert.False(context.UnprotectRtcp(new byte[21], out _));
    }

    [Fact]
    public void UnprotectRtcp_PacketOutsideReplayWindow_Fails()
    {
        byte[] masterKey = new byte[16];
        byte[] masterSalt = new byte[14];
        new Random(42).NextBytes(masterKey);
        new Random(43).NextBytes(masterSalt);

        var senderContext = new SrtpCryptoContext(masterKey, masterSalt);
        var receiverContext = new SrtpCryptoContext(masterKey, masterSalt);
        var rtcp = new RtcpReceiverReportPacket { Ssrc = 1234 };
        byte[] rtcpBuffer = new byte[rtcp.GetSerializedLength()];
        int rtcpLength = rtcp.Serialize(rtcpBuffer);
        var protectedPackets = new List<byte[]>();
        for (int i = 0; i < 66; i++)
        {
            byte[] protectedBuffer = new byte[rtcpLength + 14];
            Assert.True(senderContext.ProtectRtcp(rtcpBuffer.AsSpan(0, rtcpLength), protectedBuffer, out int protectedLength));
            protectedPackets.Add(protectedBuffer[..protectedLength]);
        }

        Assert.True(receiverContext.UnprotectRtcp(protectedPackets[^1], out _));
        Assert.False(receiverContext.UnprotectRtcp(protectedPackets[0], out _));
    }

    [Fact]
    public void UnprotectRtcp_TamperedPacket_Fails()
    {
        byte[] masterKey = new byte[16];
        byte[] masterSalt = new byte[14];
        new Random(42).NextBytes(masterKey);
        new Random(43).NextBytes(masterSalt);

        var senderContext = new SrtpCryptoContext(masterKey, masterSalt);
        var receiverContext = new SrtpCryptoContext(masterKey, masterSalt);
        var rtcp = new RtcpReceiverReportPacket { Ssrc = 5678 };
        byte[] rtcpBuffer = new byte[rtcp.GetSerializedLength()];
        int rtcpLength = rtcp.Serialize(rtcpBuffer);
        byte[] protectedBuffer = new byte[rtcpLength + 14];

        Assert.True(senderContext.ProtectRtcp(rtcpBuffer.AsSpan(0, rtcpLength), protectedBuffer, out int protectedLength));
        protectedBuffer[protectedLength - 1] ^= 0x01;

        Assert.False(receiverContext.UnprotectRtcp(protectedBuffer.AsMemory(0, protectedLength), out _));
    }

    [Fact]
    public void SrtpSession_ClientAndServer_ExchangeRtp()
    {
        var keys = CreateDtlsSrtpKeys();
        var client = new SrtpSession(keys, isClient: true);
        var server = new SrtpSession(keys, isClient: false);
        var packet = new RtpPacket
        {
            PayloadType = 96,
            SequenceNumber = 321,
            Timestamp = 654,
            Ssrc = 987,
            Payload = new byte[] { 9, 8, 7 }.AsMemory()
        };
        byte[] protectedBuffer = new byte[100];

        Assert.True(client.Protect(packet, protectedBuffer, out int protectedLength));
        Assert.True(server.Unprotect(protectedBuffer.AsMemory(0, protectedLength), out var decrypted));

        Assert.Equal(packet.SequenceNumber, decrypted.SequenceNumber);
        Assert.Equal(packet.Ssrc, decrypted.Ssrc);
        Assert.Equal(packet.Payload.ToArray(), decrypted.Payload.ToArray());
    }

    [Fact]
    public void SrtpSession_ClientAndServer_ExchangeRtcp()
    {
        var keys = CreateDtlsSrtpKeys();
        var client = new SrtpSession(keys, isClient: true);
        var server = new SrtpSession(keys, isClient: false);
        var rtcp = new RtcpReceiverReportPacket { Ssrc = 4321 };
        byte[] rtcpBuffer = new byte[rtcp.GetSerializedLength()];
        int rtcpLength = rtcp.Serialize(rtcpBuffer);
        byte[] protectedBuffer = new byte[rtcpLength + 14];

        Assert.True(client.ProtectRtcp(rtcpBuffer.AsSpan(0, rtcpLength), protectedBuffer, out int protectedLength));
        Assert.True(server.UnprotectRtcp(protectedBuffer.AsMemory(0, protectedLength), out var unprotected));

        var parsed = Assert.IsType<RtcpReceiverReportPacket>(Assert.Single(RtcpPacket.ParseCompound(unprotected.Span)));
        Assert.Equal(4321u, parsed.Ssrc);
    }

    private static SrtpKeys CreateDtlsSrtpKeys()
    {
        byte[] masterKey = new byte[60];
        new Random(42).NextBytes(masterKey);
        return new SrtpKeys
        {
            MasterKey = masterKey,
            ProtectionProfile = 0x0001
        };
    }
}
