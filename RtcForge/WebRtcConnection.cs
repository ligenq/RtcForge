using System.Buffers.Binary;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RtcForge.Media;

namespace RtcForge;

/// <summary>
/// Describes the SDP role of a WebRTC session description.
/// </summary>
public enum WebRtcSessionDescriptionType
{
    /// <summary>
    /// An SDP offer created by the endpoint that starts negotiation.
    /// </summary>
    Offer,

    /// <summary>
    /// An SDP answer created by the endpoint that accepts an offer.
    /// </summary>
    Answer
}

/// <summary>
/// Represents an SDP offer or answer exchanged through an application-defined signaling channel.
/// </summary>
/// <param name="Type">The role of the session description.</param>
/// <param name="Sdp">The serialized SDP payload.</param>
public sealed record WebRtcSessionDescription(WebRtcSessionDescriptionType Type, string Sdp)
{
    /// <summary>
    /// Creates an offer session description from serialized SDP.
    /// </summary>
    /// <param name="sdp">The serialized SDP offer.</param>
    /// <returns>An offer session description.</returns>
    public static WebRtcSessionDescription Offer(string sdp) => new(WebRtcSessionDescriptionType.Offer, sdp);

    /// <summary>
    /// Creates an answer session description from serialized SDP.
    /// </summary>
    /// <param name="sdp">The serialized SDP answer.</param>
    /// <returns>An answer session description.</returns>
    public static WebRtcSessionDescription Answer(string sdp) => new(WebRtcSessionDescriptionType.Answer, sdp);
}

/// <summary>
/// Represents an ICE candidate exchanged through an application-defined signaling channel.
/// </summary>
/// <param name="Candidate">The candidate attribute value without the SDP <c>a=candidate:</c> prefix.</param>
public sealed record WebRtcIceCandidateDescription(string Candidate);

/// <summary>
/// Configures the high-level <see cref="WebRtcConnection"/> API.
/// </summary>
public sealed class WebRtcConnectionOptions
{
    /// <summary>
    /// Gets or sets the ICE servers used for server-reflexive or relayed connectivity.
    /// </summary>
    public List<RTCIceServer> IceServers { get; set; } = [];

    /// <summary>
    /// Gets or sets which ICE candidate types may be used.
    /// </summary>
    public RTCIceTransportPolicy IceTransportPolicy { get; set; } = RTCIceTransportPolicy.All;

    /// <summary>
    /// Gets or sets the logger factory used by the underlying protocol components.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// Gets or sets the time provider used for timers, delays, and timeouts.
    /// </summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>
    /// Gets or sets the default ICE connection timeout.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal RTCConfiguration ToRtcConfiguration() => new()
    {
        IceServers = [.. IceServers],
        IceTransportPolicy = IceTransportPolicy,
        LoggerFactory = LoggerFactory,
        TimeProvider = TimeProvider,
        ConnectionTimeout = ConnectionTimeout
    };
}

/// <summary>
/// Represents a WebRTC data channel exposed by the high-level connection API.
/// </summary>
public interface IWebRtcDataChannel : IAsyncDisposable
{
    /// <summary>
    /// Gets the data channel label negotiated with the remote peer.
    /// </summary>
    string Label { get; }

    /// <summary>
    /// Gets the current data channel state.
    /// </summary>
    RTCDataChannelState ReadyState { get; }

    /// <summary>
    /// Gets incoming complete data channel messages.
    /// </summary>
    /// <remarks>
    /// Consumers should choose either <see cref="Messages"/> or <see cref="AsStream"/> for a channel.
    /// Reading both from the same channel can split messages between consumers.
    /// </remarks>
    IAsyncEnumerable<ReadOnlyMemory<byte>> Messages { get; }

    /// <summary>
    /// Waits until the data channel is open.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the wait.</param>
    /// <returns>A task that completes when the channel opens.</returns>
    Task WaitUntilOpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a complete binary message on the data channel.
    /// </summary>
    /// <param name="data">The message payload.</param>
    /// <param name="cancellationToken">A token used to cancel the send operation before it starts.</param>
    /// <returns>A task that completes when the message has been handed to the data channel.</returns>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a length-prefixed stream abstraction over this data channel.
    /// </summary>
    /// <returns>A stream backed by this data channel.</returns>
    /// <remarks>
    /// The returned stream owns the channel message reader. Do not also enumerate <see cref="Messages"/> for the same channel.
    /// </remarks>
    Stream AsStream();
}

/// <summary>
/// Provides a high-level, event-free WebRTC data-channel API suitable for application code.
/// </summary>
public interface IWebRtcConnection : IAsyncDisposable
{
    /// <summary>
    /// Gets the current peer connection state.
    /// </summary>
    PeerConnectionState ConnectionState { get; }

    /// <summary>
    /// Gets the current signaling state.
    /// </summary>
    SignalingState SignalingState { get; }

    /// <summary>
    /// Gets locally gathered ICE candidates that should be delivered to the remote peer through signaling.
    /// </summary>
    IAsyncEnumerable<WebRtcIceCandidateDescription> IceCandidates { get; }

    /// <summary>
    /// Gets data channels opened by the remote peer.
    /// </summary>
    IAsyncEnumerable<IWebRtcDataChannel> DataChannels { get; }

    /// <summary>
    /// Gets peer connection state changes, including the initial state.
    /// </summary>
    IAsyncEnumerable<PeerConnectionState> ConnectionStates { get; }

    /// <summary>
    /// Gets signaling state changes published by this wrapper, including the initial state.
    /// </summary>
    IAsyncEnumerable<SignalingState> SignalingStates { get; }

    /// <summary>
    /// Creates a local data channel.
    /// </summary>
    /// <param name="label">The data channel label.</param>
    /// <returns>The created data channel.</returns>
    IWebRtcDataChannel CreateDataChannel(string label);

    /// <summary>
    /// Creates a local data channel, waits for it to open, and returns a stream over it.
    /// </summary>
    /// <param name="label">The data channel label.</param>
    /// <param name="cancellationToken">A token used to cancel the open wait.</param>
    /// <returns>A stream backed by the opened data channel.</returns>
    Task<Stream> OpenStreamAsync(string label, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an SDP offer.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the operation before it starts.</param>
    /// <returns>The created offer.</returns>
    Task<WebRtcSessionDescription> CreateOfferAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an SDP answer for the current remote offer.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the operation before it starts.</param>
    /// <returns>The created answer.</returns>
    Task<WebRtcSessionDescription> CreateAnswerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an SDP offer and sets it as the local description.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The local offer that should be sent through signaling.</returns>
    Task<WebRtcSessionDescription> CreateOfferAndSetLocalAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a remote offer, creates an answer, and sets it as the local description.
    /// </summary>
    /// <param name="offer">The remote offer received through signaling.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The local answer that should be sent through signaling.</returns>
    Task<WebRtcSessionDescription> AcceptOfferAsync(WebRtcSessionDescription offer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a remote answer received through signaling.
    /// </summary>
    /// <param name="answer">The remote answer.</param>
    /// <param name="cancellationToken">A token used to cancel the operation before it starts.</param>
    /// <returns>A task that completes when the answer has been applied.</returns>
    Task SetAnswerAsync(WebRtcSessionDescription answer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the local SDP description.
    /// </summary>
    /// <param name="description">The local offer or answer.</param>
    /// <param name="cancellationToken">A token used to cancel the operation before it starts.</param>
    /// <returns>A task that completes when the description has been applied.</returns>
    Task SetLocalDescriptionAsync(WebRtcSessionDescription description, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the remote SDP description.
    /// </summary>
    /// <param name="description">The remote offer or answer.</param>
    /// <param name="cancellationToken">A token used to cancel the operation before it starts.</param>
    /// <returns>A task that completes when the description has been applied.</returns>
    Task SetRemoteDescriptionAsync(WebRtcSessionDescription description, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a remote ICE candidate received through signaling.
    /// </summary>
    /// <param name="candidate">The remote ICE candidate.</param>
    /// <param name="cancellationToken">A token used to cancel the operation before it starts.</param>
    /// <returns>A completed task after the candidate has been added.</returns>
    Task AddRemoteIceCandidateAsync(WebRtcIceCandidateDescription candidate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts ICE connectivity checks using the configured default timeout.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the connection attempt.</param>
    /// <returns><see langword="true"/> when ICE connects; otherwise, <see langword="false"/> when the attempt times out or fails.</returns>
    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts ICE connectivity checks using a per-call timeout.
    /// </summary>
    /// <param name="timeout">The maximum time to spend connecting.</param>
    /// <param name="cancellationToken">A token used to cancel the connection attempt.</param>
    /// <returns><see langword="true"/> when ICE connects; otherwise, <see langword="false"/> when the attempt times out or fails.</returns>
    Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of the high-level WebRTC data-channel connection API.
/// </summary>
public sealed class WebRtcConnection : IWebRtcConnection
{
    private readonly RTCPeerConnection _peerConnection;
    private readonly Channel<WebRtcIceCandidateDescription> _iceCandidates = Channel.CreateUnbounded<WebRtcIceCandidateDescription>();
    private readonly Channel<IWebRtcDataChannel> _dataChannels = Channel.CreateUnbounded<IWebRtcDataChannel>();
    private readonly Channel<PeerConnectionState> _connectionStates = Channel.CreateUnbounded<PeerConnectionState>();
    private readonly Channel<SignalingState> _signalingStates = Channel.CreateUnbounded<SignalingState>();
    private bool _disposed;

    /// <summary>
    /// Creates a connection from high-level connection options.
    /// </summary>
    /// <param name="options">The connection options.</param>
    /// <returns>A configured connection.</returns>
    public static WebRtcConnection Create(WebRtcConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new WebRtcConnection(options.ToRtcConfiguration());
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebRtcConnection"/> class.
    /// </summary>
    /// <param name="configuration">Optional low-level peer connection configuration.</param>
    public WebRtcConnection(RTCConfiguration? configuration = null)
    {
        _peerConnection = new RTCPeerConnection(configuration);
        _peerConnection.OnIceCandidate += HandleIceCandidate;
        _peerConnection.OnDataChannel += HandleDataChannel;
        _peerConnection.OnConnectionStateChange += HandleConnectionStateChange;
        _connectionStates.Writer.TryWrite(_peerConnection.ConnectionState);
        _signalingStates.Writer.TryWrite(_peerConnection.SignalingState);
    }

    /// <inheritdoc />
    public PeerConnectionState ConnectionState => _peerConnection.ConnectionState;
    /// <inheritdoc />
    public SignalingState SignalingState => _peerConnection.SignalingState;
    /// <inheritdoc />
    public IAsyncEnumerable<WebRtcIceCandidateDescription> IceCandidates => _iceCandidates.Reader.ReadAllAsync();
    /// <inheritdoc />
    public IAsyncEnumerable<IWebRtcDataChannel> DataChannels => _dataChannels.Reader.ReadAllAsync();
    /// <inheritdoc />
    public IAsyncEnumerable<PeerConnectionState> ConnectionStates => _connectionStates.Reader.ReadAllAsync();
    /// <inheritdoc />
    public IAsyncEnumerable<SignalingState> SignalingStates => _signalingStates.Reader.ReadAllAsync();

    /// <inheritdoc />
    public IWebRtcDataChannel CreateDataChannel(string label)
    {
        return new WebRtcDataChannel(_peerConnection.CreateDataChannel(label));
    }

    /// <inheritdoc />
    public async Task<Stream> OpenStreamAsync(string label, CancellationToken cancellationToken = default)
    {
        var channel = CreateDataChannel(label);
        try
        {
            await channel.WaitUntilOpenAsync(cancellationToken).ConfigureAwait(false);
            return channel.AsStream();
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<WebRtcSessionDescription> CreateOfferAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var offer = await _peerConnection.CreateOfferAsync().ConfigureAwait(false);
        return WebRtcSessionDescription.Offer(offer.ToString());
    }

    /// <inheritdoc />
    public async Task<WebRtcSessionDescription> CreateAnswerAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var answer = await _peerConnection.CreateAnswerAsync().ConfigureAwait(false);
        return WebRtcSessionDescription.Answer(answer.ToString());
    }

    /// <inheritdoc />
    public async Task<WebRtcSessionDescription> CreateOfferAndSetLocalAsync(CancellationToken cancellationToken = default)
    {
        var offer = await CreateOfferAsync(cancellationToken).ConfigureAwait(false);
        await SetLocalDescriptionAsync(offer, cancellationToken).ConfigureAwait(false);
        return offer;
    }

    /// <inheritdoc />
    public async Task<WebRtcSessionDescription> AcceptOfferAsync(WebRtcSessionDescription offer, CancellationToken cancellationToken = default)
    {
        if (offer.Type != WebRtcSessionDescriptionType.Offer)
        {
            throw new ArgumentException("Expected an offer session description.", nameof(offer));
        }

        await SetRemoteDescriptionAsync(offer, cancellationToken).ConfigureAwait(false);
        var answer = await CreateAnswerAsync(cancellationToken).ConfigureAwait(false);
        await SetLocalDescriptionAsync(answer, cancellationToken).ConfigureAwait(false);
        return answer;
    }

    /// <inheritdoc />
    public Task SetAnswerAsync(WebRtcSessionDescription answer, CancellationToken cancellationToken = default)
    {
        if (answer.Type != WebRtcSessionDescriptionType.Answer)
        {
            throw new ArgumentException("Expected an answer session description.", nameof(answer));
        }

        return SetRemoteDescriptionAsync(answer, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetLocalDescriptionAsync(WebRtcSessionDescription description, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _peerConnection.SetLocalDescriptionAsync(Sdp.SdpMessage.Parse(description.Sdp)).ConfigureAwait(false);
        PublishSignalingState();
    }

    /// <inheritdoc />
    public async Task SetRemoteDescriptionAsync(WebRtcSessionDescription description, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _peerConnection.SetRemoteDescriptionAsync(Sdp.SdpMessage.Parse(description.Sdp)).ConfigureAwait(false);
        PublishSignalingState();
    }

    /// <inheritdoc />
    public Task AddRemoteIceCandidateAsync(WebRtcIceCandidateDescription candidate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _peerConnection.AddIceCandidate(Ice.IceCandidate.Parse(candidate.Candidate));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _peerConnection.ConnectAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _peerConnection.ConnectAsync(timeout, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _peerConnection.OnIceCandidate -= HandleIceCandidate;
        _peerConnection.OnDataChannel -= HandleDataChannel;
        _peerConnection.OnConnectionStateChange -= HandleConnectionStateChange;
        _iceCandidates.Writer.TryComplete();
        _dataChannels.Writer.TryComplete();
        await _peerConnection.DisposeAsync().ConfigureAwait(false);
        _connectionStates.Writer.TryWrite(_peerConnection.ConnectionState);
        _signalingStates.Writer.TryWrite(_peerConnection.SignalingState);
        _connectionStates.Writer.TryComplete();
        _signalingStates.Writer.TryComplete();
    }

    private void HandleIceCandidate(object? sender, Ice.IceCandidate candidate)
    {
        _iceCandidates.Writer.TryWrite(new WebRtcIceCandidateDescription(candidate.ToString()));
    }

    private void HandleDataChannel(object? sender, RTCDataChannel channel)
    {
        _dataChannels.Writer.TryWrite(new WebRtcDataChannel(channel));
    }

    private void HandleConnectionStateChange(object? sender, PeerConnectionState state)
    {
        _connectionStates.Writer.TryWrite(state);
    }

    private void PublishSignalingState()
    {
        _signalingStates.Writer.TryWrite(_peerConnection.SignalingState);
    }
}

/// <summary>
/// Adapts a low-level <see cref="RTCDataChannel"/> to the high-level data channel API.
/// </summary>
public sealed class WebRtcDataChannel : IWebRtcDataChannel
{
    private readonly RTCDataChannel _inner;
    private readonly Channel<ReadOnlyMemory<byte>> _messages = Channel.CreateBounded<ReadOnlyMemory<byte>>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebRtcDataChannel"/> class.
    /// </summary>
    /// <param name="inner">The low-level data channel to wrap.</param>
    public WebRtcDataChannel(RTCDataChannel inner)
    {
        _inner = inner;
        _inner.OnOpen += HandleOpen;
        _inner.OnClose += HandleClose;
        _inner.OnBinaryMessage += HandleBinaryMessage;
        _inner.OnMessage += HandleTextMessage;
        if (_inner.ReadyState == RTCDataChannelState.Open)
        {
            _opened.TrySetResult();
        }
    }

    /// <inheritdoc />
    public string Label => _inner.Label;
    /// <inheritdoc />
    public RTCDataChannelState ReadyState => _inner.ReadyState;
    /// <inheritdoc />
    public IAsyncEnumerable<ReadOnlyMemory<byte>> Messages => _messages.Reader.ReadAllAsync();

    /// <inheritdoc />
    public Task WaitUntilOpenAsync(CancellationToken cancellationToken = default)
    {
        if (_inner.ReadyState == RTCDataChannelState.Open)
        {
            return Task.CompletedTask;
        }

        return _opened.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _inner.SendAsync(data.ToArray());
    }

    /// <inheritdoc />
    public Stream AsStream() => new WebRtcDataChannelStream(this);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _inner.OnOpen -= HandleOpen;
        _inner.OnClose -= HandleClose;
        _inner.OnBinaryMessage -= HandleBinaryMessage;
        _inner.OnMessage -= HandleTextMessage;
        _messages.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void HandleOpen(object? sender, EventArgs args)
    {
        _opened.TrySetResult();
    }

    private void HandleClose(object? sender, EventArgs args)
    {
        _opened.TrySetCanceled();
        _messages.Writer.TryComplete();
    }

    private void HandleBinaryMessage(object? sender, byte[] data)
    {
        _messages.Writer.WriteAsync(data).AsTask().FireAndForget();
    }

    private void HandleTextMessage(object? sender, string text)
    {
        _messages.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text)).AsTask().FireAndForget();
    }
}

internal sealed class WebRtcDataChannelStream : Stream
{
    private const int IncomingFrameCapacity = 256;

    private readonly IWebRtcDataChannel _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _messagePump;
    private readonly Channel<byte[]> _incomingFrames = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(IncomingFrameCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    private readonly SemaphoreSlim _availableFrames = new(0);
    private byte[]? _currentBuffer;
    private int _currentOffset;
    private volatile bool _inputCompleted;
    private bool _disposed;

    public WebRtcDataChannelStream(IWebRtcDataChannel channel)
    {
        _channel = channel;
        _messagePump = Task.Run(ProcessMessagesAsync);
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
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            if (TryReadCurrentBuffer(buffer.AsMemory(offset, count), out int bytesRead))
            {
                return bytesRead;
            }

            _availableFrames.Wait();
            if (_inputCompleted && !_incomingFrames.Reader.TryPeek(out _))
            {
                return 0;
            }

            ObjectDisposedException.ThrowIf(_disposed, this);

            if (TryDequeueFrame(out var next))
            {
                _currentBuffer = next;
                _currentOffset = 0;
            }
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            if (TryReadCurrentBuffer(buffer, out int bytesRead))
            {
                return bytesRead;
            }

            await _availableFrames.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_inputCompleted && !_incomingFrames.Reader.TryPeek(out _))
            {
                return 0;
            }

            ObjectDisposedException.ThrowIf(_disposed, this);

            if (TryDequeueFrame(out var next))
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
        throw new NotSupportedException("Synchronous writes are not supported. Use WriteAsync instead.");
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
            _cts.Cancel();
            _incomingFrames.Writer.TryComplete();
            _availableFrames.Release();
            _channel.DisposeAsync().AsTask().FireAndForget();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cts.Cancel();
            _incomingFrames.Writer.TryComplete();
            _availableFrames.Release();
        }

        try
        {
            await _messagePump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
        await _channel.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ProcessMessagesAsync()
    {
        try
        {
            await foreach (var data in _channel.Messages.WithCancellation(_cts.Token).ConfigureAwait(false))
            {
                await HandleIncomingMessageAsync(data).ConfigureAwait(false);
            }
        }
        finally
        {
            _inputCompleted = true;
            _incomingFrames.Writer.TryComplete();
            _availableFrames.Release();
        }
    }

    private async Task HandleIncomingMessageAsync(ReadOnlyMemory<byte> data)
    {
        if (data.Length < sizeof(int))
        {
            return;
        }

        int payloadLength = BinaryPrimitives.ReadInt32BigEndian(data.Span[..sizeof(int)]);
        if (payloadLength < 0 || data.Length - sizeof(int) != payloadLength)
        {
            return;
        }

        try
        {
            await EnqueueFrameAsync(data.Slice(sizeof(int), payloadLength).ToArray()).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool TryReadCurrentBuffer(Memory<byte> buffer, out int bytesRead)
    {
        bytesRead = 0;
        if (_currentBuffer == null)
        {
            return false;
        }

        int remaining = _currentBuffer.Length - _currentOffset;
        int toCopy = Math.Min(remaining, buffer.Length);
        _currentBuffer.AsMemory(_currentOffset, toCopy).CopyTo(buffer);
        _currentOffset += toCopy;
        if (_currentOffset >= _currentBuffer.Length)
        {
            _currentBuffer = null;
            _currentOffset = 0;
        }

        bytesRead = toCopy;
        return true;
    }

    private async Task EnqueueFrameAsync(byte[] frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _incomingFrames.Writer.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
        _availableFrames.Release();
    }

    private bool TryDequeueFrame(out byte[]? frame)
    {
        return _incomingFrames.Reader.TryRead(out frame);
    }
}
