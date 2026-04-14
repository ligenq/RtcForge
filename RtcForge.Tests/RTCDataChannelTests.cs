using System.Text;

namespace RtcForge.Tests;

public class RTCDataChannelTests
{
    [Fact]
    public void Constructor_SetsLabelAndId()
    {
        var dc = new RTCDataChannel("test", 5);

        Assert.Equal("test", dc.Label);
        Assert.Equal(5, dc.Id);
        Assert.Equal(RTCDataChannelState.Connecting, dc.ReadyState);
    }

    [Fact]
    public void SetOpen_ChangesStateAndFiresEvent()
    {
        var dc = new RTCDataChannel("test", 1);
        bool opened = false;
        dc.OnOpen += (_, _) => opened = true;

        dc.SetOpen();

        Assert.Equal(RTCDataChannelState.Open, dc.ReadyState);
        Assert.True(opened);
    }

    [Fact]
    public void Close_ChangesStateAndFiresEventOnce()
    {
        var dc = new RTCDataChannel("test", 1);
        int closeCount = 0;
        dc.OnClose += (_, _) => closeCount++;

        dc.Close();
        dc.Close();

        Assert.Equal(RTCDataChannelState.Closed, dc.ReadyState);
        Assert.Equal(1, closeCount);
    }

    [Fact]
    public async Task SendAsync_String_WhenNotOpen_Throws()
    {
        var dc = new RTCDataChannel("test", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => dc.SendAsync("hello"));
    }

    [Fact]
    public async Task SendAsync_Bytes_WhenNotOpen_Throws()
    {
        var dc = new RTCDataChannel("test", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => dc.SendAsync([1, 2]));
    }

    [Fact]
    public void HandleIncomingData_StringPpid_FiresOnMessage()
    {
        var dc = new RTCDataChannel("test", 1);
        string? received = null;
        dc.OnMessage += (_, msg) => received = msg;

        dc.HandleIncomingData(51, Encoding.UTF8.GetBytes("hello"));

        Assert.Equal("hello", received);
    }

    [Fact]
    public void HandleIncomingData_BinaryPpid_FiresOnBinaryMessage()
    {
        var dc = new RTCDataChannel("test", 1);
        byte[]? received = null;
        dc.OnBinaryMessage += (_, data) => received = data;

        dc.HandleIncomingData(53, [0xCA, 0xFE]);

        Assert.NotNull(received);
        Assert.Equal(new byte[] { 0xCA, 0xFE }, received);
    }

    [Fact]
    public void HandleIncomingData_UnknownPpid_DoesNotFire()
    {
        var dc = new RTCDataChannel("test", 1);
        bool stringFired = false;
        bool binaryFired = false;
        dc.OnMessage += (_, _) => stringFired = true;
        dc.OnBinaryMessage += (_, _) => binaryFired = true;

        dc.HandleIncomingData(99, [1]);

        Assert.False(stringFired);
        Assert.False(binaryFired);
    }

    [Fact]
    public void HandleIncomingData_NoSubscribers_DoesNotThrow()
    {
        var dc = new RTCDataChannel("test", 1);

        // Should not throw even with no event subscribers
        dc.HandleIncomingData(51, Encoding.UTF8.GetBytes("test"));
        dc.HandleIncomingData(53, [1]);
    }
}
