using RtcForge.Sdp;

namespace RtcForge.Tests.Sdp;

public class SdpRobustnessTests
{
    [Fact]
    public void TryParse_ValidSdp_ReturnsTrue()
    {
        string sdp = "v=0\r\no=- 123 456 IN IP4 127.0.0.1\r\ns=-\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n";

        bool result = SdpMessage.TryParse(sdp, out var message);

        Assert.True(result);
        Assert.NotNull(message);
        Assert.Equal(0, message!.Version);
        Assert.Equal(123UL, message.Origin.SessionId);
    }

    [Fact]
    public void TryParse_EmptyString_ReturnsTrue()
    {
        bool result = SdpMessage.TryParse("", out var message);

        Assert.True(result);
        Assert.NotNull(message);
    }

    [Fact]
    public void TryParse_MalformedOrigin_TooFewFields_ReturnsFalse()
    {
        string sdp = "v=0\r\no=- 123\r\ns=-\r\nt=0 0\r\n";

        bool result = SdpMessage.TryParse(sdp, out var message);

        Assert.False(result);
        Assert.Null(message);
    }

    [Fact]
    public void TryParse_MalformedOrigin_NonNumericSessionId_ReturnsFalse()
    {
        string sdp = "v=0\r\no=- abc 456 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n";

        bool result = SdpMessage.TryParse(sdp, out var message);

        Assert.False(result);
        Assert.Null(message);
    }

    [Fact]
    public void TryParse_NonNumericPort_ReturnsFalse()
    {
        string sdp = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nm=audio abc UDP/TLS/RTP/SAVPF 111\r\n";

        bool result = SdpMessage.TryParse(sdp, out var message);

        Assert.False(result);
        Assert.Null(message);
    }

    [Fact]
    public void TryParse_NonNumericVersion_ReturnsFalse()
    {
        string sdp = "v=abc\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n";

        bool result = SdpMessage.TryParse(sdp, out var message);

        Assert.False(result);
        Assert.Null(message);
    }

    [Fact]
    public void TryParse_MalformedTiming_ReturnsFalse()
    {
        string sdp = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=-\r\nt=abc\r\n";

        bool result = SdpMessage.TryParse(sdp, out var message);

        Assert.False(result);
    }

    [Fact]
    public void TryParse_MalformedMediaLine_TooFewParts_ReturnsFalse()
    {
        string sdp = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nm=audio 9\r\n";

        bool result = SdpMessage.TryParse(sdp, out var message);

        Assert.False(result);
    }

    [Fact]
    public void Parse_MalformedSdp_ThrowsFormatException()
    {
        string sdp = "v=0\r\no=bad\r\n";

        Assert.Throws<FormatException>(() => SdpMessage.Parse(sdp));
    }

    [Fact]
    public void Parse_ValidSdp_ReturnsMessage()
    {
        string sdp = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n";

        var message = SdpMessage.Parse(sdp);

        Assert.NotNull(message);
        Assert.Equal(1UL, message.Origin.SessionId);
    }

    [Fact]
    public void SdpOrigin_TryParse_ValidInput_Succeeds()
    {
        bool result = SdpOrigin.TryParse("- 12345 67890 IN IP4 127.0.0.1", out var origin);

        Assert.True(result);
        Assert.NotNull(origin);
        Assert.Equal(12345UL, origin!.SessionId);
        Assert.Equal(67890UL, origin.SessionVersion);
    }

    [Fact]
    public void SdpMediaDescription_TryParse_ValidInput_Succeeds()
    {
        bool result = SdpMediaDescription.TryParse("audio 9 UDP/TLS/RTP/SAVPF 111 112", out var md);

        Assert.True(result);
        Assert.NotNull(md);
        Assert.Equal("audio", md!.Media);
        Assert.Equal(9, md.Port);
        Assert.Equal(2, md.Formats.Count);
    }
}
