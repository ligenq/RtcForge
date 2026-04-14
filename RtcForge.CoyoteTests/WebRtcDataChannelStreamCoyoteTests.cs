using System.Threading.Channels;
using Microsoft.Coyote.Specifications;
using Xunit.Abstractions;

namespace RtcForge.CoyoteTests;

public sealed class WebRtcDataChannelStreamCoyoteTests
{
    private readonly CoyoteTestRunner _runner;

    public WebRtcDataChannelStreamCoyoteTests(ITestOutputHelper output)
    {
        _runner = new CoyoteTestRunner(output);
    }

    [Fact]
    public void ConcurrentWritesAndReads_DeliverCompleteFrames()
    {
        _runner.Run(ConcurrentWritesAndReads_DeliverCompleteFramesAsync);
    }

    [Fact]
    public void DisposeAsync_CompletesPendingRead()
    {
        _runner.Run(DisposeAsync_CompletesPendingReadAsync);
    }

    [Fact]
    public void CompletedMessageStream_ReadsEndOfStream()
    {
        _runner.Run(CompletedMessageStream_ReadsEndOfStreamAsync);
    }

    private static async Task ConcurrentWritesAndReads_DeliverCompleteFramesAsync()
    {
        var channel = new LoopbackDataChannel("test");
        await using var stream = channel.AsStream();

        byte[] firstPayload = [1, 2, 3];
        byte[] secondPayload = [4, 5, 6, 7];

        var firstWrite = Task.Run(async () => await stream.WriteAsync(firstPayload));
        var secondWrite = Task.Run(async () => await stream.WriteAsync(secondPayload));

        await Task.WhenAll(firstWrite, secondWrite);

        var observed = new[]
        {
            await ReadNextFrameAsync(stream, maxFrameLength: 8),
            await ReadNextFrameAsync(stream, maxFrameLength: 8)
        };
        Specification.Assert(
            observed.Any(payload => payload.SequenceEqual(firstPayload)),
            "Expected the first payload to be delivered exactly once.");
        Specification.Assert(
            observed.Any(payload => payload.SequenceEqual(secondPayload)),
            "Expected the second payload to be delivered exactly once.");
        Specification.Assert(
            !observed[0].SequenceEqual(observed[1]),
            "Expected distinct frames for distinct payloads.");
    }

    private static async Task DisposeAsync_CompletesPendingReadAsync()
    {
        var channel = new LoopbackDataChannel("test");
        var stream = channel.AsStream();

        var read = Task.Run(async () =>
        {
            try
            {
                byte[] buffer = new byte[1];
                return await stream.ReadAsync(buffer);
            }
            catch (ObjectDisposedException)
            {
                return -1;
            }
        });

        var dispose = Task.Run(async () => await stream.DisposeAsync());

        await Task.WhenAll(read, dispose);

        Specification.Assert(
            read.Result is 0 or -1,
            $"Expected pending read to complete as end-of-stream or disposed, got {read.Result}.");
    }

    private static async Task CompletedMessageStream_ReadsEndOfStreamAsync()
    {
        var channel = new LoopbackDataChannel("test");
        await using var stream = channel.AsStream();

        channel.CompleteInput();

        byte[] buffer = new byte[1];
        int read = await stream.ReadAsync(buffer);

        Specification.Assert(read == 0, $"Expected end-of-stream read to return 0, got {read}.");
    }

    private static async Task<byte[]> ReadNextFrameAsync(Stream stream, int maxFrameLength)
    {
        byte[] buffer = new byte[maxFrameLength];
        int read = await stream.ReadAsync(buffer);
        Specification.Assert(read > 0, "Expected frame data before end-of-stream.");

        return buffer[..read];
    }

    private sealed class LoopbackDataChannel : IWebRtcDataChannel
    {
        private readonly Channel<ReadOnlyMemory<byte>> _messages = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public LoopbackDataChannel(string label)
        {
            Label = label;
        }

        public string Label { get; }
        public RTCDataChannelState ReadyState => RTCDataChannelState.Open;
        public IAsyncEnumerable<ReadOnlyMemory<byte>> Messages => _messages.Reader.ReadAllAsync();

        public Task WaitUntilOpenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _messages.Writer.TryWrite(data.ToArray());
            return Task.CompletedTask;
        }

        public Stream AsStream() => new WebRtcDataChannelStream(this);

        public void CompleteInput()
        {
            _messages.Writer.TryComplete();
        }

        public ValueTask DisposeAsync()
        {
            _messages.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
