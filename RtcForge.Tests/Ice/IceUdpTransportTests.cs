using System.Net;
using System.Net.Sockets;
using System.Reflection;
using RtcForge.Ice;

namespace RtcForge.Tests.Ice;

public class IceUdpTransportTests
{
    [Theory]
    [InlineData(0, "STUN")]
    [InlineData(3, "STUN")]
    [InlineData(20, "DTLS")]
    [InlineData(63, "DTLS")]
    [InlineData(128, "RTP/RTCP")]
    [InlineData(191, "RTP/RTCP")]
    [InlineData(64, "UNKNOWN")]
    public void ClassifyFirstByte_ReturnsExpectedClass(byte value, string expected)
    {
        var method = typeof(IceUdpTransport).GetMethod("ClassifyFirstByte", BindingFlags.Static | BindingFlags.NonPublic)!;

        var actual = (string)method.Invoke(null, [value])!;

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(SocketError.ConnectionReset, true)]
    [InlineData(SocketError.ConnectionRefused, true)]
    [InlineData(SocketError.HostUnreachable, false)]
    public void IsIgnorableUdpReceiveError_ReturnsExpectedResult(SocketError error, bool expected)
    {
        var method = typeof(IceUdpTransport).GetMethod("IsIgnorableUdpReceiveError", BindingFlags.Static | BindingFlags.NonPublic)!;
        var exception = new SocketException((int)error);

        var actual = (bool)method.Invoke(null, [exception])!;

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task SendAsync_AfterDispose_Throws()
    {
        var transport = new IceUdpTransport(new IPEndPoint(IPAddress.Loopback, 0));
        transport.Dispose();

        await Assert.ThrowsAnyAsync<Exception>(() => transport.SendAsync([1], new IPEndPoint(IPAddress.Loopback, 9)));
    }
}
