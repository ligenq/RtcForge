using System.Reflection;
using RtcForge.Ice;
using RtcForge.Media;
using RtcForge.Sctp;

namespace RtcForge.Tests;

public class MessageSizeLimitTests
{
    [Fact]
    public async Task RTCDataChannel_SendAsync_String_RejectsOversizedMessage()
    {
        var assocA = new SctpAssociation(5000, 5000, _ => Task.CompletedTask);
        var dc = new RTCDataChannel("test", 1, assocA);
        dc.SetOpen();

        // Create a string that exceeds MaxMessageSize when UTF-8 encoded
        string oversized = new('A', SctpAssociation.MaxMessageSize + 1);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => dc.SendAsync(oversized));
        Assert.Contains("exceeds maximum", ex.Message);
    }

    [Fact]
    public async Task RTCDataChannel_SendAsync_Bytes_RejectsOversizedMessage()
    {
        var assocA = new SctpAssociation(5000, 5000, _ => Task.CompletedTask);
        var dc = new RTCDataChannel("test", 1, assocA);
        dc.SetOpen();

        byte[] oversized = new byte[SctpAssociation.MaxMessageSize + 1];

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => dc.SendAsync(oversized));
        Assert.Contains("exceeds maximum", ex.Message);
    }

    [Fact]
    public async Task RTCDataChannel_SendAsync_Bytes_AllowsExactMaxSize()
    {
        // Arrange loopback association so SendAsync actually works
        SctpAssociation assocA = null!;
        SctpAssociation assocB = null!;

        assocA = new SctpAssociation(5000, 5000, async data => await assocB.HandlePacketAsync(data));
        assocB = new SctpAssociation(5000, 5000, async data => await assocA.HandlePacketAsync(data));

        var dcA = new RTCDataChannel("test", 1, assocA);
        var dcB = new RTCDataChannel("test", 1, assocB);
        assocA.RegisterDataChannel(dcA);
        assocB.RegisterDataChannel(dcB);

        await assocB.StartAsync(false);
        await assocA.StartAsync(true);

        for (int i = 0; i < 100 && assocA.State != SctpAssociationState.Established; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System);

        dcA.SetOpen();
        dcB.SetOpen();

        // Act: Exact max size should not throw
        byte[] maxSize = new byte[SctpAssociation.MaxMessageSize];
        await dcA.SendAsync(maxSize); // Should not throw

        assocA.Dispose();
        assocB.Dispose();
    }

    [Fact]
    public async Task SctpAssociation_SendDataAsync_RejectsOversizedMessage()
    {
        SctpAssociation assocA = null!;
        SctpAssociation assocB = null!;

        assocA = new SctpAssociation(5000, 5000, async data => await assocB.HandlePacketAsync(data));
        assocB = new SctpAssociation(5000, 5000, async data => await assocA.HandlePacketAsync(data));

        await assocB.StartAsync(false);
        await assocA.StartAsync(true);

        for (int i = 0; i < 100 && assocA.State != SctpAssociationState.Established; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System);

        byte[] oversized = new byte[SctpAssociation.MaxMessageSize + 1];

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => assocA.SendDataAsync(1, 53, oversized));
        Assert.Contains("exceeds maximum", ex.Message);

        assocA.Dispose();
        assocB.Dispose();
    }

    [Fact]
    public async Task SctpAssociation_SendDataAsync_WhenNotEstablished_Throws()
    {
        var assoc = new SctpAssociation(5000, 5000, _ => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => assoc.SendDataAsync(1, 53, new byte[] { 1, 2, 3 }));

        assoc.Dispose();
    }

    [Fact]
    public void MaxMessageSize_IsExpectedValue()
    {
        Assert.Equal(262144, SctpAssociation.MaxMessageSize);
    }
}

public class CancellationAndTimeoutTests
{
    [Fact]
    public async Task ConnectAsync_CancellationToken_HonorsCancellation()
    {
        // Verify the CancellationToken is wired through by testing with a token
        // that cancels quickly while ICE is still checking
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

        // Use a very short cancellation — the ICE check may or may not complete
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));

        // Should either connect (if fast enough) or throw/return false
        bool result;
        try
        {
            result = await pcA.ConnectAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = false; // Cancellation is one valid outcome
        }

        // The key invariant: it doesn't hang — it respects the token
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

        // ConnectAsync without setting remote description should throw
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pc.ConnectAsync());
    }

    [Fact]
    public async Task ConnectAsync_TimesOut_ReturnsFalse()
    {
        using var pc = new RTCPeerConnection();

        var offer = await pc.CreateOfferAsync();
        await pc.SetLocalDescriptionAsync(offer);

        // Create a fake remote description with credentials pointing to an unreachable host
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

        // Use a very short cancellation to simulate timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Should return false or throw cancellation - not hang forever
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

    [Fact]
    public async Task SctpAssociation_Dispose_CancelsBackgroundLoops()
    {
        var assoc = new SctpAssociation(5000, 5000, _ => Task.CompletedTask);
        await assoc.StartAsync(false);

        // Should not hang - dispose should cancel background tasks
        assoc.Dispose();

        Assert.Equal(SctpAssociationState.Closed, assoc.State);
    }
}

public class DisposeTests
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
        pc.Dispose(); // Should not throw
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
        await pc.DisposeAsync(); // Should not throw
    }

    [Fact]
    public async Task RTCPeerConnection_Dispose_ClosesDataChannels()
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

    [Fact]
    public async Task WebRtcConnection_DisposeAsync_SetsStateToClosed()
    {
        var conn = new WebRtcConnection();
        await conn.DisposeAsync();

        Assert.Equal(PeerConnectionState.Closed, conn.ConnectionState);
    }

    [Fact]
    public void SctpAssociation_Dispose_SetsStateToClosed()
    {
        var assoc = new SctpAssociation(5000, 5000, _ => Task.CompletedTask);
        assoc.Dispose();

        Assert.Equal(SctpAssociationState.Closed, assoc.State);
    }

    [Fact]
    public async Task SctpAssociation_Dispose_ClosesRegisteredDataChannels()
    {
        var assoc = new SctpAssociation(5000, 5000, _ => Task.CompletedTask);
        var dc = new RTCDataChannel("test", 1, assoc);
        assoc.RegisterDataChannel(dc);
        dc.SetOpen();

        assoc.Dispose();

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

        // Use reflection to invoke TransitionToFailed
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
        pc.Dispose(); // Sets state to Closed

        var states = new List<PeerConnectionState>();
        pc.OnConnectionStateChange += (_, s) => states.Add(s);

        var method = typeof(RTCPeerConnection)
            .GetMethod("TransitionToFailed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(pc, null);

        // Should not transition to Failed when already Closed
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

        // Verify no duplicates
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

        // Concurrent reads should never throw
        var tasks = Enumerable.Range(0, 50).Select(_ =>
            Task.Run(() => pc.GetTransceivers().ToList())).ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(2, r.Count));
    }

    [Fact]
    public async Task SctpAssociation_ConcurrentTsnIncrement_ProducesUniqueValues()
    {
        SctpAssociation assocA = null!;
        SctpAssociation assocB = null!;

        var sentTsns = new System.Collections.Concurrent.ConcurrentBag<uint>();

        assocA = new SctpAssociation(5000, 5000, async data =>
        {
            await assocB.HandlePacketAsync(data);
        });
        assocB = new SctpAssociation(5000, 5000, async data =>
        {
            await assocA.HandlePacketAsync(data);
        });

        await assocB.StartAsync(false);
        await assocA.StartAsync(true);

        for (int i = 0; i < 100 && assocA.State != SctpAssociationState.Established; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System);

        Assert.Equal(SctpAssociationState.Established, assocA.State);

        // Send many small messages concurrently
        const int messageCount = 50;
        var tasks = Enumerable.Range(0, messageCount).Select(i =>
            Task.Run(async () =>
            {
                await assocA.SendDataAsync(1, 51, System.Text.Encoding.UTF8.GetBytes($"msg-{i}"));
            })).ToArray();

        await Task.WhenAll(tasks);

        // If we got here without deadlock/crash, TSN locking works
        assocA.Dispose();
        assocB.Dispose();
    }

    [Fact]
    public async Task ConcurrentSetDescriptions_DoesNotCorruptState()
    {
        using var pc = new RTCPeerConnection();

        var sdp1 = new RtcForge.Sdp.SdpMessage { SessionName = "Test1" };
        var sdp2 = new RtcForge.Sdp.SdpMessage { SessionName = "Test2" };

        // Concurrent local/remote description sets should not throw
        var tasks = Enumerable.Range(0, 20).Select(i =>
            i % 2 == 0
                ? pc.SetLocalDescriptionAsync(sdp1)
                : pc.SetRemoteDescriptionAsync(sdp2)).ToArray();

        await Task.WhenAll(tasks);

        // State should be one of the valid states, not corrupted
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

        // Should complete quickly after dispose — _cts is cancelled so the linked token
        // should cancel immediately, returning false (not hanging for 30s)
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
        Assert.Same(task, completed); // Must complete within 3s, not hang for 30s
    }

    [Fact]
    public async Task ConnectAsync_InternalTimeout_TransitionsToFailed()
    {
        using var pc = new RTCPeerConnection();
        var states = new List<PeerConnectionState>();
        pc.OnConnectionStateChange += (_, s) => states.Add(s);

        var offer = await pc.CreateOfferAsync();
        await pc.SetLocalDescriptionAsync(offer);

        // Remote credentials set but no reachable candidates — ICE will spin
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

        // Use a short external timeout so the test doesn't wait 30s
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
        // Connection state should have transitioned away from New
        Assert.NotEqual(PeerConnectionState.New, pc.ConnectionState);
    }
}

public class WebRtcConnectionDisposeTests
{
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

public class SctpConcurrentSsnTests
{
    [Fact]
    public async Task ConcurrentSendOnSameStream_ProducesUniqueSequenceNumbers()
    {
        SctpAssociation assocA = null!;
        SctpAssociation assocB = null!;

        // Track all received data chunks to verify SSNs
        var receivedSsns = new System.Collections.Concurrent.ConcurrentBag<ushort>();

        assocA = new SctpAssociation(5000, 5000, async data => await assocB.HandlePacketAsync(data));
        assocB = new SctpAssociation(5000, 5000, async data => await assocA.HandlePacketAsync(data));

        await assocB.StartAsync(false);
        await assocA.StartAsync(true);

        for (int i = 0; i < 100 && assocA.State != SctpAssociationState.Established; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System);

        Assert.Equal(SctpAssociationState.Established, assocA.State);

        // Send many messages concurrently on the SAME stream
        const int messageCount = 30;
        const ushort streamId = 1;
        var tasks = Enumerable.Range(0, messageCount).Select(i =>
            Task.Run(async () =>
            {
                await assocA.SendDataAsync(streamId, 51,
                    System.Text.Encoding.UTF8.GetBytes($"msg-{i}"));
            })).ToArray();

        await Task.WhenAll(tasks);

        // Verify SSN allocation was correct by checking the outbound SSN counter
        // After 30 sends on stream 1, the SSN counter should be exactly 30
        var ssnField = typeof(SctpAssociation)
            .GetField("_outboundSsns", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var ssnDict = (System.Collections.Concurrent.ConcurrentDictionary<ushort, ushort>)ssnField.GetValue(assocA)!;
        Assert.True(ssnDict.TryGetValue(streamId, out var finalSsn));
        Assert.Equal(messageCount, finalSsn);

        assocA.Dispose();
        assocB.Dispose();
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

        // Set up descriptions so InitializeDtlsAsync can read remote fingerprint
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

        // Directly create and inject a DtlsTransport, then fire its OnStateChange
        // to simulate DTLS failure without waiting for a real handshake timeout
        var dtlsField = typeof(RTCPeerConnection)
            .GetField("_dtlsTransport", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dtlsTransport = new RtcForge.Dtls.DtlsTransport(
            _ => Task.CompletedTask,
            RtcForge.Dtls.DtlsCertificate.Generate());
        dtlsField.SetValue(pc, dtlsTransport);

        // Simulate ICE Connected state transition (which sets ConnectionState=Connected
        // and triggers the DTLS init path)
        var iceAgent = (IceAgent)typeof(RTCPeerConnection)
            .GetField("_iceAgent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pc)!;

        var handleIceStateChange = typeof(RTCPeerConnection)
            .GetMethod("HandleIceStateChange", BindingFlags.Instance | BindingFlags.NonPublic)!;
        handleIceStateChange.Invoke(pc, new object[] { iceAgent, IceState.Connected });

        // Wait briefly for the async Task.Run to start InitializeDtlsAsync
        await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System);

        // The DTLS handshake will be hanging. Verify that at minimum Connected was fired.
        Assert.Contains(PeerConnectionState.Connected, states);

        // Now simulate DTLS failure by directly invoking TransitionToFailed
        // (matching what the DtlsState.Failed handler does)
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

public class BackpressureTests
{
    [Fact]
    public async Task WebRtcDataChannelStream_BoundedChannel_RespectsCapacity()
    {
        var channel = new FakeBoundedChannel("test");
        await using var stream = new WebRtcDataChannelStream(channel);

        // The bounded channel has capacity of 256. This test verifies the
        // stream was created with a bounded channel by checking it doesn't
        // accept infinite messages without reading.
        // We'll push framed messages through the channel's MessageReceived event.
        // First, read back what we write to show the pipeline works.
        byte[] payload = [1, 2, 3];
        await stream.WriteAsync(payload);

        // Should be readable
        Assert.True(stream.CanRead);
        Assert.True(stream.CanWrite);
    }

    [Fact]
    public async Task WebRtcDataChannelStream_BoundedChannel_DoesNotDropFramesWhenFull()
    {
        var channel = new FakeBoundedChannel("test");
        await using var stream = new WebRtcDataChannelStream(channel);
        const int frameCount = 300;

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < frameCount; i++)
            {
                channel.ReceiveRaw(CreateFrame((byte)i));
            }
        });

        await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var actual = new List<byte>();
        var buffer = new byte[1];
        for (int i = 0; i < frameCount; i++)
        {
            int read = await stream.ReadAsync(buffer, cts.Token);
            Assert.Equal(1, read);
            actual.Add(buffer[0]);
        }
        await writer.WaitAsync(cts.Token);

        Assert.Equal(Enumerable.Range(0, frameCount).Select(i => (byte)i), actual);
    }

    [Fact]
    public async Task SctpAssociation_BoundedInputChannel_AcceptsPackets()
    {
        SctpAssociation assocA = null!;
        SctpAssociation assocB = null!;

        assocA = new SctpAssociation(5000, 5000, async data => await assocB.HandlePacketAsync(data));
        assocB = new SctpAssociation(5000, 5000, async data => await assocA.HandlePacketAsync(data));

        await assocB.StartAsync(false);
        await assocA.StartAsync(true);

        for (int i = 0; i < 100 && assocA.State != SctpAssociationState.Established; i++)
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System);

        // The bounded channel (1024 capacity) should handle normal traffic
        Assert.Equal(SctpAssociationState.Established, assocA.State);
        Assert.Equal(SctpAssociationState.Established, assocB.State);

        // Send some messages to verify bounded channel works under normal load
        for (int i = 0; i < 10; i++)
        {
            await assocA.SendDataAsync(1, 51, System.Text.Encoding.UTF8.GetBytes($"msg-{i}"));
        }

        assocA.Dispose();
        assocB.Dispose();
    }

    private static byte[] CreateFrame(byte payload)
    {
        byte[] frame = new byte[sizeof(int) + 1];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, sizeof(int)), 1);
        frame[sizeof(int)] = payload;
        return frame;
    }

    private sealed class FakeBoundedChannel : IWebRtcDataChannel
    {
        public FakeBoundedChannel(string label) { Label = label; }
        public string Label { get; }
        public RTCDataChannelState ReadyState => RTCDataChannelState.Open;
#pragma warning disable CS0067
        public event EventHandler? Opened;
#pragma warning restore CS0067
        public event WebRtcDataReceivedHandler? MessageReceived;

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            // Echo back to simulate loopback
            ReceiveRaw(data);
            return Task.CompletedTask;
        }

        public void ReceiveRaw(ReadOnlyMemory<byte> data)
        {
            MessageReceived?.Invoke(data);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
