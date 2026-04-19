using RtcForge.Srtp;

namespace RtcForge.Tests.Srtp;

public class SrtpAesCtrTests
{
    [Fact]
    public void Transform_ApplyingTwiceWithSameIv_RestoresPlaintext()
    {
        byte[] key = new byte[16];
        byte[] iv = new byte[16];
        byte[] plaintext = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19];
        byte[] data = [.. plaintext];
        new Random(42).NextBytes(key);
        new Random(43).NextBytes(iv);

        SrtpAesCtr.Transform(key, iv, data);
        Assert.NotEqual(plaintext, data);

        SrtpAesCtr.Transform(key, iv, data);
        Assert.Equal(plaintext, data);
    }

    [Fact]
    public void Transform_EmptyPayload_DoesNothing()
    {
        byte[] key = new byte[16];
        byte[] iv = new byte[16];
        Span<byte> data = [];

        SrtpAesCtr.Transform(key, iv, data);

        Assert.True(data.IsEmpty);
    }
}
