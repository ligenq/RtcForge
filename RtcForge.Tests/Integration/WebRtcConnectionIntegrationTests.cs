namespace RtcForge.Tests.Integration;

public class WebRtcConnectionIntegrationTests
{
    [Fact]
    public async Task CreateOfferAsync_IncludesGatheredIceCandidatesInSdp()
    {
        await using var connection = new WebRtcConnection();

        var offer = await connection.CreateOfferAsync();
        var parsed = RtcForge.Sdp.SdpMessage.Parse(offer.Sdp);

        Assert.All(parsed.MediaDescriptions, md => Assert.Contains(md.Attributes, a => a.Name == "candidate"));
    }

    [Fact]
    public async Task WebRtcConnection_CreateOfferWithDataChannel_IncludesApplicationMediaSection()
    {
        await using var connection = new WebRtcConnection();
        var channel = connection.CreateDataChannel("torrent");
        var offer = await connection.CreateOfferAsync();
        var parsed = RtcForge.Sdp.SdpMessage.Parse(offer.Sdp);

        Assert.Equal("torrent", channel.Label);
        Assert.Equal(RTCDataChannelState.Connecting, channel.ReadyState);
        Assert.Contains(parsed.MediaDescriptions, md => md.Media == "application");
    }

    [Fact]
    public async Task WebRtcConnection_CreateOfferAndSetLocalAsync_SetsLocalOffer()
    {
        await using var connection = new WebRtcConnection();

        var offer = await connection.CreateOfferAndSetLocalAsync();

        Assert.Equal(WebRtcSessionDescriptionType.Offer, offer.Type);
        Assert.Equal(SignalingState.HaveLocalOffer, connection.SignalingState);
    }

    [Fact]
    public async Task WebRtcConnection_AcceptOfferAsync_SetsAnswerLocally()
    {
        await using var offerer = new WebRtcConnection();
        await using var answerer = new WebRtcConnection();

        var offer = await offerer.CreateOfferAndSetLocalAsync();
        var answer = await answerer.AcceptOfferAsync(offer);

        Assert.Equal(WebRtcSessionDescriptionType.Answer, answer.Type);
        Assert.Equal(SignalingState.Stable, answerer.SignalingState);
    }

    [Fact]
    public async Task WebRtcConnection_AcceptOfferAsync_IncludesGatheredIceCandidatesInAnswerSdp()
    {
        await using var offerer = new WebRtcConnection();
        await using var answerer = new WebRtcConnection();

        var offer = await offerer.CreateOfferAndSetLocalAsync();
        var answer = await answerer.AcceptOfferAsync(offer);
        var parsed = RtcForge.Sdp.SdpMessage.Parse(answer.Sdp);

        Assert.All(parsed.MediaDescriptions, md => Assert.Contains(md.Attributes, a => a.Name == "candidate"));
    }

    [Fact]
    public async Task WebRtcConnection_StateStreams_EmitInitialStates()
    {
        await using var connection = new WebRtcConnection();

        var connectionState = await ReadFirstAsync(connection.ConnectionStates);
        var signalingState = await ReadFirstAsync(connection.SignalingStates);

        Assert.Equal(PeerConnectionState.New, connectionState);
        Assert.Equal(SignalingState.Stable, signalingState);
    }

    [Fact]
    public async Task WebRtcDataChannelStream_UsesLengthFramedMessages()
    {
        var left = new FakeDataChannel("left");
        var right = new FakeDataChannel("right");
        left.Connect(right);

        await using var leftStream = left.AsStream();
        await using var rightStream = right.AsStream();

        byte[] payload = [1, 2, 3, 4, 5];
        await leftStream.WriteAsync(payload);

        byte[] buffer = new byte[payload.Length];
        int read = await rightStream.ReadAsync(buffer);

        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, buffer);
    }

    [Fact]
    public void WebRtcSessionDescription_Factories_SetType()
    {
        Assert.Equal(WebRtcSessionDescriptionType.Offer, WebRtcSessionDescription.Offer("offer").Type);
        Assert.Equal(WebRtcSessionDescriptionType.Answer, WebRtcSessionDescription.Answer("answer").Type);
    }

    private static async Task<T> ReadFirstAsync<T>(IAsyncEnumerable<T> source)
    {
        await foreach (var item in source)
        {
            return item;
        }

        throw new InvalidOperationException("The sequence completed before producing an item.");
    }

    private sealed class FakeDataChannel : IWebRtcDataChannel
    {
        private readonly System.Threading.Channels.Channel<ReadOnlyMemory<byte>> _messages = System.Threading.Channels.Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private FakeDataChannel? _remote;

        public FakeDataChannel(string label)
        {
            Label = label;
        }

        public string Label { get; }
        public RTCDataChannelState ReadyState { get; private set; } = RTCDataChannelState.Open;
        public IAsyncEnumerable<ReadOnlyMemory<byte>> Messages => _messages.Reader.ReadAllAsync();

        public void Connect(FakeDataChannel remote)
        {
            _remote = remote;
            remote._remote = this;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _remote?._messages.Writer.TryWrite(data.ToArray());
            return Task.CompletedTask;
        }

        public Task WaitUntilOpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Stream AsStream() => new WebRtcDataChannelStream(this);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
