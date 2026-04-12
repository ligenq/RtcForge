using System.Buffers.Binary;
using RtcForge.Media;

namespace RtcForge;

public enum WebRtcSessionDescriptionType
{
    Offer,
    Answer
}

public sealed record WebRtcSessionDescription(WebRtcSessionDescriptionType Type, string Sdp);

public sealed record WebRtcIceCandidateDescription(string Candidate);

public delegate void WebRtcDataReceivedHandler(ReadOnlyMemory<byte> data);

public interface IWebRtcDataChannel : IAsyncDisposable
{
    string Label { get; }
    RTCDataChannelState ReadyState { get; }
    event EventHandler? Opened;
    event WebRtcDataReceivedHandler? MessageReceived;
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
}

public interface IWebRtcConnection : IAsyncDisposable
{
    event EventHandler<WebRtcIceCandidateDescription>? IceCandidateReady;
    event EventHandler<IWebRtcDataChannel>? DataChannelOpened;

    PeerConnectionState ConnectionState { get; }
    SignalingState SignalingState { get; }

    IWebRtcDataChannel CreateDataChannel(string label);
    Task<WebRtcSessionDescription> CreateOfferAsync(CancellationToken cancellationToken = default);
    Task<WebRtcSessionDescription> CreateAnswerAsync(CancellationToken cancellationToken = default);
    Task SetLocalDescriptionAsync(WebRtcSessionDescription description, CancellationToken cancellationToken = default);
    Task SetRemoteDescriptionAsync(WebRtcSessionDescription description, CancellationToken cancellationToken = default);
    Task AddRemoteIceCandidateAsync(WebRtcIceCandidateDescription candidate, CancellationToken cancellationToken = default);
    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);
}

public sealed class WebRtcConnection : IWebRtcConnection
{
    private readonly RTCPeerConnection _peerConnection;

    public WebRtcConnection(RTCConfiguration? configuration = null)
    {
        _peerConnection = new RTCPeerConnection(configuration);
        _peerConnection.OnIceCandidate += (_, candidate) => IceCandidateReady?.Invoke(this, new WebRtcIceCandidateDescription(candidate.ToString()));
        _peerConnection.OnDataChannel += (_, channel) => DataChannelOpened?.Invoke(this, new WebRtcDataChannel(channel));
    }

    public event EventHandler<WebRtcIceCandidateDescription>? IceCandidateReady;
    public event EventHandler<IWebRtcDataChannel>? DataChannelOpened;

    public PeerConnectionState ConnectionState => _peerConnection.ConnectionState;
    public SignalingState SignalingState => _peerConnection.SignalingState;

    public IWebRtcDataChannel CreateDataChannel(string label)
    {
        return new WebRtcDataChannel(_peerConnection.CreateDataChannel(label));
    }

    public async Task<WebRtcSessionDescription> CreateOfferAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var offer = await _peerConnection.CreateOfferAsync().ConfigureAwait(false);
        return new WebRtcSessionDescription(WebRtcSessionDescriptionType.Offer, offer.ToString());
    }

    public async Task<WebRtcSessionDescription> CreateAnswerAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var answer = await _peerConnection.CreateAnswerAsync().ConfigureAwait(false);
        return new WebRtcSessionDescription(WebRtcSessionDescriptionType.Answer, answer.ToString());
    }

    public Task SetLocalDescriptionAsync(WebRtcSessionDescription description, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _peerConnection.SetLocalDescriptionAsync(Sdp.SdpMessage.Parse(description.Sdp));
    }

    public Task SetRemoteDescriptionAsync(WebRtcSessionDescription description, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _peerConnection.SetRemoteDescriptionAsync(Sdp.SdpMessage.Parse(description.Sdp));
    }

    public Task AddRemoteIceCandidateAsync(WebRtcIceCandidateDescription candidate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _peerConnection.AddIceCandidate(Ice.IceCandidate.Parse(candidate.Candidate));
        return Task.CompletedTask;
    }

    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _peerConnection.ConnectAsync();
    }

    public ValueTask DisposeAsync()
    {
        _peerConnection.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class WebRtcDataChannel : IWebRtcDataChannel
{
    private readonly RTCDataChannel _inner;

    public WebRtcDataChannel(RTCDataChannel inner)
    {
        _inner = inner;
        _inner.OnOpen += (_, _) => Opened?.Invoke(this, EventArgs.Empty);
        _inner.OnBinaryMessage += (_, data) => MessageReceived?.Invoke(data);
        _inner.OnMessage += (_, text) => MessageReceived?.Invoke(System.Text.Encoding.UTF8.GetBytes(text));
    }

    public string Label => _inner.Label;
    public RTCDataChannelState ReadyState => _inner.ReadyState;
    public event EventHandler? Opened;
    public event WebRtcDataReceivedHandler? MessageReceived;

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _inner.SendAsync(data.ToArray());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class WebRtcDataChannelStream : Stream
{
    private readonly IWebRtcDataChannel _channel;
    private readonly System.Threading.Channels.Channel<byte[]> _incomingFrames = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    private byte[]? _currentBuffer;
    private int _currentOffset;
    private bool _disposed;

    public WebRtcDataChannelStream(IWebRtcDataChannel channel)
    {
        _channel = channel;
        _channel.MessageReceived += HandleIncomingMessage;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            if (_currentBuffer != null)
            {
                int remaining = _currentBuffer.Length - _currentOffset;
                int toCopy = Math.Min(remaining, buffer.Length);
                _currentBuffer.AsMemory(_currentOffset, toCopy).CopyTo(buffer);
                _currentOffset += toCopy;
                if (_currentOffset >= _currentBuffer.Length)
                {
                    _currentBuffer = null;
                    _currentOffset = 0;
                }

                return toCopy;
            }

            if (!await _incomingFrames.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            if (_incomingFrames.Reader.TryRead(out var next))
            {
                _currentBuffer = next;
                _currentOffset = 0;
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] frame = new byte[sizeof(int) + buffer.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, sizeof(int)), buffer.Length);
        buffer.CopyTo(frame.AsMemory(sizeof(int)));
        await _channel.SendAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            _incomingFrames.Writer.TryComplete();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _incomingFrames.Writer.TryComplete();
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void HandleIncomingMessage(ReadOnlyMemory<byte> data)
    {
        if (data.Length < sizeof(int))
        {
            return;
        }

        int payloadLength = BinaryPrimitives.ReadInt32BigEndian(data.Span.Slice(0, sizeof(int)));
        if (payloadLength < 0 || data.Length - sizeof(int) != payloadLength)
        {
            return;
        }

        _incomingFrames.Writer.TryWrite(data.Slice(sizeof(int), payloadLength).ToArray());
    }
}
