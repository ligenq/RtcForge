using RtcForge.Sdp;

namespace RtcForge.Tests.Sdp;

public class SdpMessageTests
{
    [Fact]
    public void Parse_WithConnectionLine_SetsProperty()
    {
        // Arrange
        string sdp = "v=0\r\no=- 123 456 IN IP4 127.0.0.1\r\ns=-\r\nc=IN IP4 10.0.0.1\r\nt=0 0\r\n";

        // Act
        var message = SdpMessage.Parse(sdp);

        // Assert
        Assert.Equal("IN IP4 10.0.0.1", message.Connection);
    }

    [Fact]
    public void ToString_IncludesConnectionLine()
    {
        // Arrange
        var message = new SdpMessage
        {
            Connection = "IN IP4 192.168.1.1"
        };

        // Act
        string result = message.ToString();

        // Assert
        Assert.Contains("c=IN IP4 192.168.1.1\r\n", result);
    }

    [Fact]
    public void Parse_WithMultipleMedia_SetsCorrectMids()
    {
        // Arrange
        string sdp = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=-\r\nc=IN IP4 0.0.0.0\r\nt=0 0\r\n" +
                     "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=mid:0\r\n" +
                     "m=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:1\r\n";

        // Act
        var message = SdpMessage.Parse(sdp);

        // Assert
        Assert.Equal(2, message.MediaDescriptions.Count);
        Assert.Equal("0", message.MediaDescriptions[0].Attributes.First(a => a.Name == "mid").Value);
        Assert.Equal("1", message.MediaDescriptions[1].Attributes.First(a => a.Name == "mid").Value);
    }

    [Fact]
    public void Parse_WithMediaLevelConnectionLine_PreservesMediaConnection()
    {
        string sdp = "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n" +
                     "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\nc=IN IP4 0.0.0.0\r\na=mid:0\r\n";

        var message = SdpMessage.Parse(sdp);

        Assert.Single(message.MediaDescriptions);
        Assert.Equal("IN IP4 0.0.0.0", message.MediaDescriptions[0].Connection);
    }

    [Fact]
    public void ToString_WithMediaLevelConnectionLine_IncludesConnectionLine()
    {
        var message = new SdpMessage();
        message.MediaDescriptions.Add(new SdpMediaDescription
        {
            Media = "audio",
            Port = 9,
            Proto = "UDP/TLS/RTP/SAVPF",
            Connection = "IN IP4 0.0.0.0",
            Formats = { "111" }
        });

        string result = message.ToString();

        Assert.Contains("m=audio 9 UDP/TLS/RTP/SAVPF 111\r\nc=IN IP4 0.0.0.0\r\n", result);
    }
}
