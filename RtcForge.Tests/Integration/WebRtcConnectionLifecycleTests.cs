namespace RtcForge.Tests.Integration;

public class WebRtcConnectionDisposeTests
{
    [Fact]
    public async Task WebRtcConnection_DisposeAsync_SetsStateToClosed()
    {
        var conn = new WebRtcConnection();
        await conn.DisposeAsync();

        Assert.Equal(PeerConnectionState.Closed, conn.ConnectionState);
    }

    [Fact]
    public async Task WebRtcConnection_DisposeAsync_SetsSignalingStateToClosed()
    {
        var conn = new WebRtcConnection();
        await conn.DisposeAsync();

        Assert.Equal(PeerConnectionState.Closed, conn.ConnectionState);
        Assert.Equal(SignalingState.Closed, conn.SignalingState);
    }

    [Fact]
    public async Task WebRtcConnection_DisposeAsync_ClosesDataChannels()
    {
        var conn = new WebRtcConnection();
        var dc = conn.CreateDataChannel("test");

        await conn.DisposeAsync();

        Assert.Equal(RTCDataChannelState.Closed, dc.ReadyState);
    }
}
