using System.Reflection;
using RtcForge.Ice;
using RtcForge.Media;

namespace RtcForge.Tests.Integration;

public class CancellationAndTimeoutTests
{
    [Fact]
    public async Task ConnectAsync_CancellationToken_HonorsCancellation()
    {
        using var pcA = new RTCPeerConnection();
        using var pcB = new RTCPeerConnection();

        pcA.OnIceCandidate += (s, c) => pcB.AddIceCandidate(c);
        pcB.OnIceCandidate += (s, c) => pcA.AddIceCandidate(c);

        var offer = await pcA.CreateOfferAsync();
        await pcA.SetLocalDescriptionAsync(offer);
        await pcB.SetRemoteDescriptionAsync(offer);

        var answer = await pcB.CreateAnswerAsync();
        await pcB.SetLocalDescriptionAsync(answer);
        await pcA.SetRemoteDescriptionAsync(answer);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));

        bool result;
        try
        {
            result = await pcA.ConnectAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = false;
        }

        Assert.True(result || pcA.ConnectionState != PeerConnectionState.New);
    }

    [Fact]
    public async Task ConnectAsync_Cancellation_DoesNotLeaveIceConnectRunning()
    {
        using var pc = new RTCPeerConnection();
        var fakeRemote = RtcForge.Sdp.SdpMessage.Parse(
            "v=0\r\n" +
            "o=- 1 1 IN IP4 127.0.0.1\r\n" +
            "s=-\r\n" +
            "t=0 0\r\n" +
            "a=ice-ufrag:remoteufrag\r\n" +
            "a=ice-pwd:remotepasswordremotepassword\r\n");
        await pc.SetRemoteDescriptionAsync(fakeRemote);

        var iceAgent = (IceAgent)typeof(RTCPeerConnection)
            .GetField("_iceAgent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pc)!;
        var connectGate = (SemaphoreSlim)typeof(IceAgent)
            .GetField("_connectGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(iceAgent)!;

        await connectGate.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAsync<OperationCanceledException>(() => pc.ConnectAsync(cts.Token));
        }
        finally
        {
            connectGate.Release();
        }

        await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System);

        Assert.Equal(PeerConnectionState.New, pc.ConnectionState);
        Assert.Equal(IceState.New, iceAgent.State);
    }

    [Fact]
    public async Task ConnectAsync_WithoutRemoteCredentials_Throws()
    {
        using var pc = new RTCPeerConnection();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pc.ConnectAsync());
    }

    [Fact]
    public async Task ConnectAsync_TimesOut_ReturnsFalse()
    {
        using var pc = new RTCPeerConnection();

        var offer = await pc.CreateOfferAsync();
        await pc.SetLocalDescriptionAsync(offer);

        var fakeRemote = RtcForge.Sdp.SdpMessage.Parse(
            "v=0\r\n" +
            "o=- 1 1 IN IP4 127.0.0.1\r\n" +
            "s=-\r\n" +
            "t=0 0\r\n" +
            "a=ice-ufrag:remoteufrag\r\n" +
            "a=ice-pwd:remotepasswordremotepassword\r\n" +
            "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\n" +
            "c=IN IP4 0.0.0.0\r\n" +
            "a=mid:0\r\n");
        await pc.SetRemoteDescriptionAsync(fakeRemote);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        bool connected;
        try
        {
            connected = await pc.ConnectAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            connected = false;
        }

        Assert.False(connected);
    }
}

public class PeerConnectionDisposeTests
{
    [Fact]
    public void RTCPeerConnection_Dispose_SetsStateToClosed()
    {
        var pc = new RTCPeerConnection();
        pc.Dispose();

        Assert.Equal(PeerConnectionState.Closed, pc.ConnectionState);
        Assert.Equal(SignalingState.Closed, pc.SignalingState);
    }

    [Fact]
    public void RTCPeerConnection_DoubleDispose_DoesNotThrow()
    {
        var pc = new RTCPeerConnection();
        pc.Dispose();
        pc.Dispose();
    }

    [Fact]
    public async Task RTCPeerConnection_DisposeAsync_SetsStateToClosed()
    {
        var pc = new RTCPeerConnection();
        await pc.DisposeAsync();

        Assert.Equal(PeerConnectionState.Closed, pc.ConnectionState);
        Assert.Equal(SignalingState.Closed, pc.SignalingState);
    }

    [Fact]
    public async Task RTCPeerConnection_DoubleDisposeAsync_DoesNotThrow()
    {
        var pc = new RTCPeerConnection();
        await pc.DisposeAsync();
        await pc.DisposeAsync();
    }

    [Fact]
    public void RTCPeerConnection_Dispose_ClosesDataChannels()
    {
        var pc = new RTCPeerConnection();
        var dc = pc.CreateDataChannel("test");

        Assert.Equal(RTCDataChannelState.Connecting, dc.ReadyState);

        pc.Dispose();

        Assert.Equal(RTCDataChannelState.Closed, dc.ReadyState);
    }

    [Fact]
    public async Task RTCPeerConnection_DisposeAsync_ClosesDataChannels()
    {
        var pc = new RTCPeerConnection();
        var dc = pc.CreateDataChannel("test");

        await pc.DisposeAsync();

        Assert.Equal(RTCDataChannelState.Closed, dc.ReadyState);
    }
}

public class ErrorPropagationTests
{
    [Fact]
    public void PeerConnection_DtlsFailure_TransitionsToFailed()
    {
        using var pc = new RTCPeerConnection();
        var states = new List<PeerConnectionState>();
        pc.OnConnectionStateChange += (_, s) => states.Add(s);

        var method = typeof(RTCPeerConnection)
            .GetMethod("TransitionToFailed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(pc, null);

        Assert.Equal(PeerConnectionState.Failed, pc.ConnectionState);
        Assert.Contains(PeerConnectionState.Failed, states);
    }

    [Fact]
    public void PeerConnection_TransitionToFailed_DoesNotFireWhenClosed()
    {
        var pc = new RTCPeerConnection();
        pc.Dispose();

        var states = new List<PeerConnectionState>();
        pc.OnConnectionStateChange += (_, s) => states.Add(s);

        var method = typeof(RTCPeerConnection)
            .GetMethod("TransitionToFailed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(pc, null);

        Assert.DoesNotContain(PeerConnectionState.Failed, states);
    }

    [Fact]
    public void PeerConnection_IceDisconnected_TransitionsToDisconnected()
    {
        using var pc = new RTCPeerConnection();
        var states = new List<PeerConnectionState>();
        pc.OnConnectionStateChange += (_, s) => states.Add(s);

        var iceAgent = (IceAgent)typeof(RTCPeerConnection)
            .GetField("_iceAgent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pc)!;

        var handleIceStateChange = typeof(RTCPeerConnection)
            .GetMethod("HandleIceStateChange", BindingFlags.Instance | BindingFlags.NonPublic)!;

        handleIceStateChange.Invoke(pc, new object[] { iceAgent, IceState.Disconnected });

        Assert.Equal(PeerConnectionState.Disconnected, pc.ConnectionState);
        Assert.Contains(PeerConnectionState.Disconnected, states);
    }

    [Fact]
    public void PeerConnection_IceFailed_TransitionsToFailed()
    {
        using var pc = new RTCPeerConnection();
        var states = new List<PeerConnectionState>();
        pc.OnConnectionStateChange += (_, s) => states.Add(s);

        var iceAgent = (IceAgent)typeof(RTCPeerConnection)
            .GetField("_iceAgent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pc)!;

        var handleIceStateChange = typeof(RTCPeerConnection)
            .GetMethod("HandleIceStateChange", BindingFlags.Instance | BindingFlags.NonPublic)!;

        handleIceStateChange.Invoke(pc, new object[] { iceAgent, IceState.Failed });

        Assert.Equal(PeerConnectionState.Failed, pc.ConnectionState);
        Assert.Contains(PeerConnectionState.Failed, states);
    }
}

public class ThreadSafetyTests
{
    [Fact]
    public async Task ConcurrentAddTrack_DoesNotCorruptTransceiverList()
    {
        using var pc = new RTCPeerConnection();
        const int trackCount = 20;

        var tasks = Enumerable.Range(0, trackCount).Select(i =>
            Task.Run(() =>
            {
                var track = i % 2 == 0
                    ? (MediaStreamTrack)new AudioStreamTrack()
                    : new VideoStreamTrack();
                pc.AddTrack(track);
            })).ToArray();

        await Task.WhenAll(tasks);

        var transceivers = pc.GetTransceivers().ToList();
        Assert.Equal(trackCount, transceivers.Count);

        var mids = transceivers.ConvertAll(t => t.Mid);
        Assert.Equal(mids.Count, mids.Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentCreateDataChannel_DoesNotCorruptList()
    {
        using var pc = new RTCPeerConnection();
        const int channelCount = 20;

        var tasks = Enumerable.Range(0, channelCount).Select(i =>
            Task.Run(() => pc.CreateDataChannel($"channel-{i}"))).ToArray();

        var channels = await Task.WhenAll(tasks);

        Assert.Equal(channelCount, channels.Length);
        Assert.All(channels, ch => Assert.NotNull(ch.Label));
        Assert.Equal(channelCount, channels.Select(c => c.Label).Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentGetTransceivers_ReturnsSafeSnapshot()
    {
        using var pc = new RTCPeerConnection();
        pc.AddTrack(new AudioStreamTrack());
        pc.AddTrack(new VideoStreamTrack());

        var tasks = Enumerable.Range(0, 50).Select(_ =>
            Task.Run(() => pc.GetTransceivers().ToList())).ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(2, r.Count));
    }

    [Fact]
    public async Task ConcurrentSetDescriptions_DoesNotCorruptState()
    {
        using var pc = new RTCPeerConnection();

        var sdp1 = new RtcForge.Sdp.SdpMessage { SessionName = "Test1" };
        var sdp2 = new RtcForge.Sdp.SdpMessage { SessionName = "Test2" };

        var tasks = Enumerable.Range(0, 20).Select(i =>
            i % 2 == 0
                ? pc.SetLocalDescriptionAsync(sdp1)
                : pc.SetRemoteDescriptionAsync(sdp2)).ToArray();

        await Task.WhenAll(tasks);

        Assert.True(
            pc.SignalingState == SignalingState.Stable ||
            pc.SignalingState == SignalingState.HaveLocalOffer ||
            pc.SignalingState == SignalingState.HaveRemoteOffer);
    }
}

public class ConnectAsyncAfterDisposeTests
{
    [Fact]
    public async Task ConnectAsync_AfterDispose_FailsFast()
    {
        var pc = new RTCPeerConnection();

        var offer = await pc.CreateOfferAsync();
        await pc.SetLocalDescriptionAsync(offer);

        var fakeRemote = RtcForge.Sdp.SdpMessage.Parse(
            "v=0\r\n" +
            "o=- 1 1 IN IP4 127.0.0.1\r\n" +
            "s=-\r\n" +
            "t=0 0\r\n" +
            "a=ice-ufrag:remoteufrag\r\n" +
            "a=ice-pwd:remotepasswordremotepassword\r\n" +
            "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\n" +
            "c=IN IP4 0.0.0.0\r\n" +
            "a=mid:0\r\n");
        await pc.SetRemoteDescriptionAsync(fakeRemote);

        pc.Dispose();

        var task = Task.Run(async () =>
        {
            try
            {
                return await pc.ConnectAsync();
            }
            catch
            {
                return false;
            }
        });

        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(3000), TimeProvider.System));
        Assert.Same(task, completed);
    }

    [Fact]
    public async Task ConnectAsync_InternalTimeout_TransitionsToFailed()
    {
        using var pc = new RTCPeerConnection();
        var states = new List<PeerConnectionState>();
        pc.OnConnectionStateChange += (_, s) => states.Add(s);

        var offer = await pc.CreateOfferAsync();
        await pc.SetLocalDescriptionAsync(offer);

        var fakeRemote = RtcForge.Sdp.SdpMessage.Parse(
            "v=0\r\n" +
            "o=- 1 1 IN IP4 127.0.0.1\r\n" +
            "s=-\r\n" +
            "t=0 0\r\n" +
            "a=ice-ufrag:remoteufrag\r\n" +
            "a=ice-pwd:remotepasswordremotepassword\r\n" +
            "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\n" +
            "c=IN IP4 0.0.0.0\r\n" +
            "a=mid:0\r\n" +
            "a=candidate:1 1 udp 1 192.0.2.1 12345 typ host\r\n");
        await pc.SetRemoteDescriptionAsync(fakeRemote);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        bool result;
        try
        {
            result = await pc.ConnectAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = false;
        }

        Assert.False(result);
        Assert.NotEqual(PeerConnectionState.New, pc.ConnectionState);
    }
}

public class HandleIceStateConnectedDtlsErrorTests
{
    [Fact]
    public async Task DtlsStateChangedToFailed_TransitionsConnectionToFailed()
    {
        using var pc = new RTCPeerConnection();
        var states = new System.Collections.Concurrent.ConcurrentBag<PeerConnectionState>();
        pc.OnConnectionStateChange += (_, s) => states.Add(s);

        var offer = await pc.CreateOfferAsync();
        await pc.SetLocalDescriptionAsync(offer);

        var fakeRemote = RtcForge.Sdp.SdpMessage.Parse(
            "v=0\r\n" +
            "o=- 1 1 IN IP4 127.0.0.1\r\n" +
            "s=-\r\n" +
            "t=0 0\r\n" +
            "a=ice-ufrag:ufrag\r\n" +
            "a=ice-pwd:passwordpasswordpassword\r\n" +
            "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\n" +
            "c=IN IP4 0.0.0.0\r\n" +
            "a=mid:0\r\n");
        await pc.SetRemoteDescriptionAsync(fakeRemote);

        var dtlsField = typeof(RTCPeerConnection)
            .GetField("_dtlsTransport", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dtlsTransport = new RtcForge.Dtls.DtlsTransport(
            _ => Task.CompletedTask,
            RtcForge.Dtls.DtlsCertificate.Generate());
        dtlsField.SetValue(pc, dtlsTransport);

        var iceAgent = (IceAgent)typeof(RTCPeerConnection)
            .GetField("_iceAgent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pc)!;

        var handleIceStateChange = typeof(RTCPeerConnection)
            .GetMethod("HandleIceStateChange", BindingFlags.Instance | BindingFlags.NonPublic)!;
        handleIceStateChange.Invoke(pc, new object[] { iceAgent, IceState.Connected });

        await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System);

        Assert.Contains(PeerConnectionState.Connected, states);

        var transitionMethod = typeof(RTCPeerConnection)
            .GetMethod("TransitionToFailed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        transitionMethod.Invoke(pc, null);

        Assert.Equal(PeerConnectionState.Failed, pc.ConnectionState);
        Assert.Contains(PeerConnectionState.Failed, states);
    }

    [Fact]
    public void IceChecking_TransitionsToConnecting()
    {
        using var pc = new RTCPeerConnection();
        var states = new List<PeerConnectionState>();
        pc.OnConnectionStateChange += (_, s) => states.Add(s);

        var iceAgent = (IceAgent)typeof(RTCPeerConnection)
            .GetField("_iceAgent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pc)!;

        var handleIceStateChange = typeof(RTCPeerConnection)
            .GetMethod("HandleIceStateChange", BindingFlags.Instance | BindingFlags.NonPublic)!;

        handleIceStateChange.Invoke(pc, new object[] { iceAgent, IceState.Checking });

        Assert.Equal(PeerConnectionState.Connecting, pc.ConnectionState);
        Assert.Contains(PeerConnectionState.Connecting, states);
    }

    [Fact]
    public void IceClosed_TransitionsConnectionToClosed()
    {
        using var pc = new RTCPeerConnection();
        var states = new List<PeerConnectionState>();
        pc.OnConnectionStateChange += (_, s) => states.Add(s);

        var iceAgent = (IceAgent)typeof(RTCPeerConnection)
            .GetField("_iceAgent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pc)!;

        var handleIceStateChange = typeof(RTCPeerConnection)
            .GetMethod("HandleIceStateChange", BindingFlags.Instance | BindingFlags.NonPublic)!;

        handleIceStateChange.Invoke(pc, new object[] { iceAgent, IceState.Closed });

        Assert.Equal(PeerConnectionState.Closed, pc.ConnectionState);
        Assert.Contains(PeerConnectionState.Closed, states);
    }
}
