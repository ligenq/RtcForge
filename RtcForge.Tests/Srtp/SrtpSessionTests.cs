using RtcForge.Srtp;
using RtcForge.Rtp;

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
}
