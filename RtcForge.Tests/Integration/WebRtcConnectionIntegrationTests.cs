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
    public async Task WebRtcDataChannelStream_UsesLengthFramedMessages()
    {
        var left = new FakeDataChannel("left");
        var right = new FakeDataChannel("right");
        left.Connect(right);

        await using var leftStream = new WebRtcDataChannelStream(left);
        await using var rightStream = new WebRtcDataChannelStream(right);

        byte[] payload = [1, 2, 3, 4, 5];
        await leftStream.WriteAsync(payload);

        byte[] buffer = new byte[payload.Length];
        int read = await rightStream.ReadAsync(buffer);

        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, buffer);
    }

    private sealed class FakeDataChannel : IWebRtcDataChannel
    {
        private FakeDataChannel? _remote;

        public FakeDataChannel(string label)
        {
            Label = label;
        }

        public string Label { get; }
        public RTCDataChannelState ReadyState { get; private set; } = RTCDataChannelState.Open;
        public event EventHandler? Opened;
        public event WebRtcDataReceivedHandler? MessageReceived;

        public void Connect(FakeDataChannel remote)
        {
            _remote = remote;
            remote._remote = this;
            Opened?.Invoke(this, EventArgs.Empty);
            remote.Opened?.Invoke(remote, EventArgs.Empty);
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _remote?.MessageReceived?.Invoke(data.ToArray());
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
