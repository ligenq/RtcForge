using RtcForge.Ice;
using RtcForge.Sdp;

namespace RtcForge.Tests.Integration;

public class WebRtcConnectionAdapterTests
{
    private const string MinimalSdp =
        "v=0\r\n" +
        "o=- 1 1 IN IP4 127.0.0.1\r\n" +
        "s=-\r\n" +
        "t=0 0\r\n";

    [Fact]
    public void Create_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WebRtcConnection.Create(null!));
    }

    [Fact]
    public async Task CreateOfferAndSetLocalAsync_ForwardsOfferAndPublishesSignalingState()
    {
        var peer = new FakePeerConnection();
        await using var connection = new WebRtcConnection(peer);

        var offer = await connection.CreateOfferAndSetLocalAsync();
        var initial = await ReadFirstAsync(connection.SignalingStates);
        var published = await ReadFirstAsync(connection.SignalingStates);

        Assert.Equal(WebRtcSessionDescriptionType.Offer, offer.Type);
        Assert.Equal(SignalingState.Stable, initial);
        Assert.Equal(SignalingState.HaveLocalOffer, published);
        Assert.Equal(1, peer.CreateOfferCalls);
        Assert.Single(peer.LocalDescriptions);
    }

    [Fact]
    public async Task AcceptOfferAsync_AppliesOfferCreatesAnswerAndSetsLocalDescription()
    {
        var peer = new FakePeerConnection();
        await using var connection = new WebRtcConnection(peer);

        var answer = await connection.AcceptOfferAsync(WebRtcSessionDescription.Offer(MinimalSdp));

        Assert.Equal(WebRtcSessionDescriptionType.Answer, answer.Type);
        Assert.Equal(1, peer.CreateAnswerCalls);
        Assert.Single(peer.RemoteDescriptions);
        Assert.Single(peer.LocalDescriptions);
        Assert.Equal(SignalingState.Stable, connection.SignalingState);
    }

    [Fact]
    public async Task AcceptOfferAsync_WithAnswer_Throws()
    {
        var peer = new FakePeerConnection();
        await using var connection = new WebRtcConnection(peer);

        await Assert.ThrowsAsync<ArgumentException>(() => connection.AcceptOfferAsync(WebRtcSessionDescription.Answer(MinimalSdp)));
        Assert.Empty(peer.RemoteDescriptions);
    }

    [Fact]
    public async Task SetAnswerAsync_RequiresAnswerAndForwardsRemoteDescription()
    {
        var peer = new FakePeerConnection();
        await using var connection = new WebRtcConnection(peer);

        await Assert.ThrowsAsync<ArgumentException>(() => connection.SetAnswerAsync(WebRtcSessionDescription.Offer(MinimalSdp)));
        await connection.SetAnswerAsync(WebRtcSessionDescription.Answer(MinimalSdp));

        Assert.Single(peer.RemoteDescriptions);
    }

    [Fact]
    public async Task CanceledOperations_ThrowBeforeCallingPeer()
    {
        var peer = new FakePeerConnection();
        await using var connection = new WebRtcConnection(peer);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => connection.CreateOfferAsync(cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => connection.CreateAnswerAsync(cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => connection.SetLocalDescriptionAsync(WebRtcSessionDescription.Offer(MinimalSdp), cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => connection.SetRemoteDescriptionAsync(WebRtcSessionDescription.Offer(MinimalSdp), cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => connection.AddRemoteIceCandidateAsync(new WebRtcIceCandidateDescription("candidate:1 1 udp 100 127.0.0.1 1234 typ host"), cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => connection.ConnectAsync(cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => connection.ConnectAsync(TimeSpan.FromSeconds(1), cts.Token));

        Assert.Equal(0, peer.CreateOfferCalls);
        Assert.Equal(0, peer.CreateAnswerCalls);
        Assert.Equal(0, peer.ConnectCalls);
        Assert.Empty(peer.RemoteCandidates);
    }

    [Fact]
    public async Task AddRemoteIceCandidateAsync_ParsesCandidateAndForwardsIt()
    {
        var peer = new FakePeerConnection();
        await using var connection = new WebRtcConnection(peer);

        await connection.AddRemoteIceCandidateAsync(new WebRtcIceCandidateDescription("candidate:1 1 udp 100 127.0.0.1 1234 typ host"));

        var candidate = Assert.Single(peer.RemoteCandidates);
        Assert.Equal("1", candidate.Foundation);
        Assert.Equal("127.0.0.1", candidate.Address);
        Assert.Equal(1234, candidate.Port);
        Assert.Equal(IceCandidateType.Host, candidate.Type);
    }

    [Fact]
    public async Task ConnectAsync_ForwardsDefaultAndTimeoutCalls()
    {
        var peer = new FakePeerConnection { ConnectResult = true };
        await using var connection = new WebRtcConnection(peer);

        Assert.True(await connection.ConnectAsync());
        Assert.True(await connection.ConnectAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(2, peer.ConnectCalls);
        Assert.Equal(TimeSpan.FromSeconds(5), peer.LastConnectTimeout);
    }

    [Fact]
    public async Task PeerEvents_ArePublishedToAsyncStreams()
    {
        var peer = new FakePeerConnection();
        await using var connection = new WebRtcConnection(peer);
        var remoteChannel = new FakeDataChannel("remote");

        peer.RaiseIceCandidate(new IceCandidate
        {
            Foundation = "1",
            Component = 1,
            Protocol = "udp",
            Priority = 100,
            Address = "127.0.0.1",
            Port = 1234,
            Type = IceCandidateType.Host
        });
        peer.RaiseDataChannel(remoteChannel);
        peer.RaiseConnectionState(PeerConnectionState.Connected);

        Assert.Contains("candidate:1", (await ReadFirstAsync(connection.IceCandidates)).Candidate);
        Assert.Same(remoteChannel, await ReadFirstAsync(connection.DataChannels));
        Assert.Equal(PeerConnectionState.New, await ReadFirstAsync(connection.ConnectionStates));
        Assert.Equal(PeerConnectionState.Connected, await ReadFirstAsync(connection.ConnectionStates));
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesAndCompletesStateStreams()
    {
        var peer = new FakePeerConnection();
        var connection = new WebRtcConnection(peer);

        await connection.DisposeAsync();
        peer.RaiseConnectionState(PeerConnectionState.Connected);

        Assert.True(peer.Disposed);
        Assert.Equal(PeerConnectionState.New, await ReadFirstAsync(connection.ConnectionStates));
        Assert.Equal(PeerConnectionState.Closed, await ReadFirstAsync(connection.ConnectionStates));
        await AssertSequenceCompletesAsync(connection.ConnectionStates);
    }

    [Fact]
    public async Task CreateDataChannelAndOpenStreamAsync_UsePeerChannel()
    {
        var peer = new FakePeerConnection();
        await using var connection = new WebRtcConnection(peer);
        peer.NextDataChannel = new FakeDataChannel("chat");

        var channel = connection.CreateDataChannel("chat");

        Assert.Same(peer.NextDataChannel, channel);
        Assert.Equal("chat", peer.CreatedDataChannelLabels.Single());

        peer.NextDataChannel = new FakeDataChannel("stream");
        await using var stream = await connection.OpenStreamAsync("stream");
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task OpenStreamAsync_DisposesChannelWhenOpenWaitFails()
    {
        var peer = new FakePeerConnection();
        await using var connection = new WebRtcConnection(peer);
        var channel = new FakeDataChannel("closed") { OpenException = new InvalidOperationException("closed") };
        peer.NextDataChannel = channel;

        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.OpenStreamAsync("closed"));

        Assert.True(channel.Disposed);
    }

    private static async Task<T> ReadFirstAsync<T>(IAsyncEnumerable<T> source)
    {
        await foreach (var item in source)
        {
            return item;
        }

        throw new InvalidOperationException("The sequence completed before producing an item.");
    }

    private static async Task AssertSequenceCompletesAsync<T>(IAsyncEnumerable<T> source)
    {
        await foreach (var _ in source)
        {
        }
    }

    private sealed class FakePeerConnection : IWebRtcPeerConnection
    {
        public PeerConnectionState ConnectionState { get; private set; } = PeerConnectionState.New;
        public SignalingState SignalingState { get; private set; } = SignalingState.Stable;
        public event EventHandler<IceCandidate>? OnIceCandidate;
        public event EventHandler<IWebRtcDataChannel>? OnDataChannel;
        public event EventHandler<PeerConnectionState>? OnConnectionStateChange;
        public List<SdpMessage> LocalDescriptions { get; } = [];
        public List<SdpMessage> RemoteDescriptions { get; } = [];
        public List<IceCandidate> RemoteCandidates { get; } = [];
        public List<string> CreatedDataChannelLabels { get; } = [];
        public FakeDataChannel NextDataChannel { get; set; } = new("default");
        public bool ConnectResult { get; set; }
        public int CreateOfferCalls { get; private set; }
        public int CreateAnswerCalls { get; private set; }
        public int ConnectCalls { get; private set; }
        public TimeSpan? LastConnectTimeout { get; private set; }
        public bool Disposed { get; private set; }

        public IWebRtcDataChannel CreateDataChannel(string label)
        {
            CreatedDataChannelLabels.Add(label);
            return NextDataChannel;
        }

        public Task<SdpMessage> CreateOfferAsync()
        {
            CreateOfferCalls++;
            return Task.FromResult(SdpMessage.Parse(MinimalSdp));
        }

        public Task<SdpMessage> CreateAnswerAsync()
        {
            CreateAnswerCalls++;
            return Task.FromResult(SdpMessage.Parse(MinimalSdp));
        }

        public Task SetLocalDescriptionAsync(SdpMessage description)
        {
            LocalDescriptions.Add(description);
            SignalingState = RemoteDescriptions.Count > 0 ? SignalingState.Stable : SignalingState.HaveLocalOffer;
            return Task.CompletedTask;
        }

        public Task SetRemoteDescriptionAsync(SdpMessage description)
        {
            RemoteDescriptions.Add(description);
            SignalingState = LocalDescriptions.Count > 0 ? SignalingState.Stable : SignalingState.HaveRemoteOffer;
            return Task.CompletedTask;
        }

        public void AddIceCandidate(IceCandidate candidate) => RemoteCandidates.Add(candidate);

        public Task<bool> ConnectAsync(CancellationToken cancellationToken)
        {
            ConnectCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ConnectResult);
        }

        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            ConnectCalls++;
            LastConnectTimeout = timeout;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ConnectResult);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            ConnectionState = PeerConnectionState.Closed;
            SignalingState = SignalingState.Closed;
            return ValueTask.CompletedTask;
        }

        public void RaiseIceCandidate(IceCandidate candidate) => OnIceCandidate?.Invoke(this, candidate);

        public void RaiseDataChannel(IWebRtcDataChannel channel) => OnDataChannel?.Invoke(this, channel);

        public void RaiseConnectionState(PeerConnectionState state)
        {
            ConnectionState = state;
            OnConnectionStateChange?.Invoke(this, state);
        }
    }

    private sealed class FakeDataChannel : IWebRtcDataChannel
    {
        public FakeDataChannel(string label)
        {
            Label = label;
        }

        public string Label { get; }
        public RTCDataChannelState ReadyState { get; set; } = RTCDataChannelState.Open;
        public IAsyncEnumerable<ReadOnlyMemory<byte>> Messages => AsyncEnumerable.Empty<ReadOnlyMemory<byte>>();
        public Exception? OpenException { get; set; }
        public bool Disposed { get; private set; }
        public List<byte[]> Sent { get; } = [];

        public Task WaitUntilOpenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return OpenException == null ? Task.CompletedTask : Task.FromException(OpenException);
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add(data.ToArray());
            return Task.CompletedTask;
        }

        public Stream AsStream() => new WebRtcDataChannelStream(this);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
