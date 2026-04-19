using System.Buffers.Binary;
using System.Text;

namespace RtcForge.Tests.Integration;

public class WebRtcDataChannelAdapterTests
{
    [Fact]
    public async Task WaitUntilOpenAsync_CompletesWhenInnerChannelOpens()
    {
        var inner = new RTCDataChannel("chat", 1);
        await using var channel = new WebRtcDataChannel(inner);

        var waitTask = channel.WaitUntilOpenAsync();
        Assert.False(waitTask.IsCompleted);

        inner.SetOpen();

        await waitTask;
        Assert.Equal(RTCDataChannelState.Open, channel.ReadyState);
        Assert.Equal("chat", channel.Label);
    }

    [Fact]
    public async Task WaitUntilOpenAsync_CanceledToken_CancelsWait()
    {
        var inner = new RTCDataChannel("chat", 1);
        await using var channel = new WebRtcDataChannel(inner);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => channel.WaitUntilOpenAsync(cts.Token));
    }

    [Fact]
    public async Task ClosingInnerChannel_CancelsOpenWaitAndCompletesMessages()
    {
        var inner = new RTCDataChannel("chat", 1);
        await using var channel = new WebRtcDataChannel(inner);

        var waitTask = channel.WaitUntilOpenAsync();
        inner.Close();

        await Assert.ThrowsAsync<TaskCanceledException>(() => waitTask);
        await foreach (var _ in channel.Messages)
        {
            throw new Xunit.Sdk.XunitException("Messages should complete without yielding.");
        }
    }

    [Fact]
    public async Task IncomingTextAndBinaryMessages_ArePublishedAsBytes()
    {
        var inner = new RTCDataChannel("chat", 1);
        await using var channel = new WebRtcDataChannel(inner);

        inner.HandleIncomingData(51, Encoding.UTF8.GetBytes("hello"));
        inner.HandleIncomingData(53, [1, 2, 3]);

        Assert.Equal(Encoding.UTF8.GetBytes("hello"), (await ReadFirstAsync(channel.Messages)).ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, (await ReadFirstAsync(channel.Messages)).ToArray());
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesFromInnerEventsAndCompletesMessages()
    {
        var inner = new RTCDataChannel("chat", 1);
        var channel = new WebRtcDataChannel(inner);

        await channel.DisposeAsync();
        await channel.DisposeAsync();
        inner.HandleIncomingData(53, [1]);

        await foreach (var _ in channel.Messages)
        {
            throw new Xunit.Sdk.XunitException("Messages should complete without yielding after dispose.");
        }
    }

    [Fact]
    public async Task Stream_SynchronousRead_ReturnsPayloadInChunksThenEndOfStream()
    {
        var channel = new ScriptedDataChannel("stream");
        await using var stream = new WebRtcDataChannelStream(channel);
        channel.PublishFrame([1, 2, 3, 4]);
        channel.Complete();

        byte[] buffer = new byte[2];

        Assert.Equal(2, stream.Read(buffer, 0, 2));
        Assert.Equal(new byte[] { 1, 2 }, buffer);
        Assert.Equal(2, stream.Read(buffer, 0, 2));
        Assert.Equal(new byte[] { 3, 4 }, buffer);
        Assert.Equal(0, stream.Read(buffer, 0, 2));
    }

    [Fact]
    public async Task Stream_IgnoresMalformedFrames()
    {
        var channel = new ScriptedDataChannel("stream");
        await using var stream = new WebRtcDataChannelStream(channel);
        channel.PublishRaw([0, 1]);
        channel.PublishRaw(CreateRawFrame(declaredLength: 10, payload: [1, 2]));
        channel.PublishRaw(CreateRawFrame(declaredLength: -1, payload: []));
        channel.PublishFrame([9]);
        channel.Complete();

        byte[] buffer = new byte[1];

        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal(9, buffer[0]);
        Assert.Equal(0, await stream.ReadAsync(buffer));
    }

    [Fact]
    public async Task Stream_WriteAsync_SendsLengthPrefixedFrame()
    {
        var channel = new ScriptedDataChannel("stream");
        await using var stream = new WebRtcDataChannelStream(channel);

        await stream.WriteAsync(new byte[] { 5, 6, 7 });

        byte[] sent = Assert.Single(channel.Sent);
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(sent.AsSpan(0, 4)));
        Assert.Equal(new byte[] { 5, 6, 7 }, sent.AsSpan(4).ToArray());
    }

    [Fact]
    public async Task Stream_UnsupportedMembersThrowAndFlushIsNoop()
    {
        var channel = new ScriptedDataChannel("stream");
        await using var stream = new WebRtcDataChannelStream(channel);

        stream.Flush();
        await stream.FlushAsync();
        Assert.False(stream.CanSeek);
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 1);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
    }

    [Fact]
    public async Task Stream_DisposeMakesReadAndWriteThrow()
    {
        var channel = new ScriptedDataChannel("stream");
        var stream = new WebRtcDataChannelStream(channel);

        await stream.DisposeAsync();

        Assert.False(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[1], 0, 1));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await stream.WriteAsync(new byte[] { 1 }));
    }

    private static async Task<ReadOnlyMemory<byte>> ReadFirstAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> source)
    {
        await foreach (var item in source)
        {
            return item;
        }

        throw new InvalidOperationException("The sequence completed before producing an item.");
    }

    private static byte[] CreateRawFrame(int declaredLength, byte[] payload)
    {
        byte[] frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, sizeof(int)), declaredLength);
        payload.CopyTo(frame.AsSpan(sizeof(int)));
        return frame;
    }

    private sealed class ScriptedDataChannel : IWebRtcDataChannel
    {
        private readonly System.Threading.Channels.Channel<ReadOnlyMemory<byte>> _messages = System.Threading.Channels.Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public ScriptedDataChannel(string label)
        {
            Label = label;
        }

        public string Label { get; }
        public RTCDataChannelState ReadyState { get; private set; } = RTCDataChannelState.Open;
        public IAsyncEnumerable<ReadOnlyMemory<byte>> Messages => _messages.Reader.ReadAllAsync();
        public List<byte[]> Sent { get; } = [];

        public Task WaitUntilOpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add(data.ToArray());
            return Task.CompletedTask;
        }

        public Stream AsStream() => new WebRtcDataChannelStream(this);

        public ValueTask DisposeAsync()
        {
            ReadyState = RTCDataChannelState.Closed;
            _messages.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void PublishFrame(byte[] payload) => _messages.Writer.TryWrite(CreateRawFrame(payload.Length, payload));

        public void PublishRaw(byte[] data) => _messages.Writer.TryWrite(data);

        public void Complete() => _messages.Writer.TryComplete();
    }
}
