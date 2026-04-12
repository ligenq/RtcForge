using RtcForge.Sdp;

namespace RtcForge.Tests.Sdp;

public class SdpTests
{
    [Fact]
    public void Parse_SimpleSdp_ReturnsCorrectMessage()
    {
        // Arrange
        string sdp = "v=0\r\n" +
                     "o=- 12345 67890 IN IP4 127.0.0.1\r\n" +
                     "s=TestSession\r\n" +
                     "t=0 0\r\n" +
                     "a=group:BUNDLE audio video\r\n" +
                     "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\n" +
                     "a=rtpmap:111 opus/48000/2\r\n";

        // Act
        var message = SdpMessage.Parse(sdp);

        // Assert
        Assert.Equal(0, message.Version);
        Assert.Equal(12345ul, message.Origin.SessionId);
        Assert.Equal("TestSession", message.SessionName);
        Assert.Single(message.Attributes);
        Assert.Equal("group", message.Attributes[0].Name);
        Assert.Single(message.MediaDescriptions);
        Assert.Equal("audio", message.MediaDescriptions[0].Media);
        Assert.Equal(9, message.MediaDescriptions[0].Port);
        Assert.Single(message.MediaDescriptions[0].Attributes);
        Assert.Equal("rtpmap", message.MediaDescriptions[0].Attributes[0].Name);
    }

    [Fact]
    public void ToString_SimpleSdp_ProducesCorrectString()
    {
        // Arrange
        var message = new SdpMessage
        {
            Version = 0,
            Origin = new SdpOrigin { SessionId = 123, SessionVersion = 456 },
            SessionName = "Test"
        };
        message.MediaDescriptions.Add(new SdpMediaDescription { Media = "audio", Port = 1234, Formats = { "111" } });

        // Act
        string result = message.ToString();

        // Assert
        Assert.Contains("v=0", result);
        Assert.Contains("o=- 123 456 IN IP4 127.0.0.1", result);
        Assert.Contains("s=Test", result);
        Assert.Contains("m=audio 1234 RTP/AVP 111", result);
    }
}
