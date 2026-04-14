using System.Threading.Channels;

namespace RtcForge.Tests.Integration;

public class WebRtcDataChannelStreamTests
{
    [Fact]
    public async Task WebRtcDataChannelStream_BoundedChannel_RespectsCapacity()
    {
        var channel = new FakeBoundedChannel("test");
        await using var stream = new WebRtcDataChannelStream(channel);

        byte[] payload = [1, 2, 3];
        await stream.WriteAsync(payload);

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

    private static byte[] CreateFrame(byte payload)
    {
        byte[] frame = new byte[sizeof(int) + 1];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, sizeof(int)), 1);
        frame[sizeof(int)] = payload;
        return frame;
    }

    private sealed class FakeBoundedChannel : IWebRtcDataChannel
    {
        private readonly Channel<ReadOnlyMemory<byte>> _messages = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public FakeBoundedChannel(string label)
        {
            Label = label;
        }

        public string Label { get; }
        public RTCDataChannelState ReadyState => RTCDataChannelState.Open;
        public IAsyncEnumerable<ReadOnlyMemory<byte>> Messages => _messages.Reader.ReadAllAsync();

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            ReceiveRaw(data);
            return Task.CompletedTask;
        }

        public void ReceiveRaw(ReadOnlyMemory<byte> data)
        {
            _messages.Writer.TryWrite(data.ToArray());
        }

        public Task WaitUntilOpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Stream AsStream() => new WebRtcDataChannelStream(this);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
