using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RtcForge.Ice;
using RtcForge.Sdp;
using RtcForge.Dtls;
using RtcForge.Rtp;
using RtcForge.Media;

namespace RtcForge;

/// <summary>
/// Describes the SDP signaling state of an <see cref="RTCPeerConnection"/>.
/// </summary>
public enum SignalingState
{
    /// <summary>
    /// No offer/answer exchange is currently pending.
    /// </summary>
    Stable,

    /// <summary>
    /// A local offer has been created and applied.
    /// </summary>
    HaveLocalOffer,

    /// <summary>
    /// A remote offer has been applied and an answer has not yet been applied.
    /// </summary>
    HaveRemoteOffer,

    /// <summary>
    /// A local provisional answer has been applied.
    /// </summary>
    HaveLocalPranswer,

    /// <summary>
    /// A remote provisional answer has been applied.
    /// </summary>
    HaveRemotePranswer,

    /// <summary>
    /// The peer connection has been closed.
    /// </summary>
    Closed
}

/// <summary>
/// Describes the transport connectivity state of an <see cref="RTCPeerConnection"/>.
/// </summary>
public enum PeerConnectionState
{
    /// <summary>
    /// The connection has not started ICE connectivity checks.
    /// </summary>
    New,

    /// <summary>
    /// ICE connectivity checks are in progress.
    /// </summary>
    Connecting,

    /// <summary>
    /// ICE connectivity has succeeded.
    /// </summary>
    Connected,

    /// <summary>
    /// Connectivity was interrupted.
    /// </summary>
    Disconnected,

    /// <summary>
    /// Connectivity failed.
    /// </summary>
    Failed,

    /// <summary>
    /// The peer connection has been closed.
    /// </summary>
    Closed
}

/// <summary>
/// Provides low-level WebRTC peer connection functionality for ICE, DTLS, RTP, and SCTP data channels.
/// </summary>
/// <remarks>
/// Application code that only needs data channels should usually prefer <see cref="WebRtcConnection"/>.
/// </remarks>
public class RTCPeerConnection : IAsyncDisposable, IDisposable
{
    private const int DefaultSctpPort = 5000;
    private const int DefaultMaxMessageSize = 262144;
    private volatile SignalingState _signalingState = SignalingState.Stable;
    private volatile PeerConnectionState _connectionState = PeerConnectionState.New;
    private readonly IceAgent _iceAgent;
    private readonly DtlsCertificate _dtlsCertificate;
    private readonly Lock _stateLock = new();
    private SdpMessage? _localDescription;
    private SdpMessage? _remoteDescription;
    private Sctp.SctpAssociation? _sctpAssociation;
    private readonly List<RTCDataChannel> _dataChannels = new();
    private readonly Lock _dataChannelLock = new();
    private readonly List<RTCRtpTransceiver> _transceivers = new();
    private readonly Lock _transceiverLock = new();
    private DtlsTransport? _dtlsTransport;
    private RTCDtlsTransport? _publicDtlsTransport;
    private volatile Srtp.SrtpSession? _srtpSession;
    private int _dtlsInitialized;
    private readonly ConcurrentDictionary<byte, RTCRtpTransceiver> _remotePayloadTypeMap = new();
    private string? _dataChannelMid;
    private bool _dataChannelNegotiated;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<RTCPeerConnection>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _connectionTimeout;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>
    /// Gets the current SDP signaling state.
    /// </summary>
    public SignalingState SignalingState => _signalingState;

    /// <summary>
    /// Gets the current peer connection state.
    /// </summary>
    public PeerConnectionState ConnectionState => _connectionState;

    /// <summary>
    /// Gets a snapshot of the RTP transceivers owned by this connection.
    /// </summary>
    /// <returns>A snapshot of the current transceivers.</returns>
    public IEnumerable<RTCRtpTransceiver> GetTransceivers()
    {
        lock (_transceiverLock) { return _transceivers.ToList(); }
    }

    /// <summary>
    /// Occurs when a local ICE candidate has been gathered.
    /// </summary>
    public event EventHandler<IceCandidate>? OnIceCandidate;

    /// <summary>
    /// Occurs when the peer connection state changes.
    /// </summary>
    public event EventHandler<PeerConnectionState>? OnConnectionStateChange;

    /// <summary>
    /// Occurs when the remote peer opens a data channel.
    /// </summary>
    public event EventHandler<RTCDataChannel>? OnDataChannel;

    /// <summary>
    /// Occurs when a remote media track is discovered.
    /// </summary>
    public event EventHandler<RTCTrackEvent>? OnTrack;

    /// <summary>
    /// Initializes a new instance of the <see cref="RTCPeerConnection"/> class.
    /// </summary>
    /// <param name="configuration">Optional peer connection configuration.</param>
    public RTCPeerConnection(RTCConfiguration? configuration = null)
    {
        _loggerFactory = configuration?.LoggerFactory;
        _logger = configuration?.LoggerFactory?.CreateLogger<RTCPeerConnection>();
        _timeProvider = configuration?.TimeProvider ?? TimeProvider.System;
        _connectionTimeout = configuration?.ConnectionTimeout ?? TimeSpan.FromSeconds(30);
        _iceAgent = new IceAgent(configuration?.LoggerFactory, _timeProvider);
        if (configuration?.IceServers != null)
        {
            _iceAgent.SetIceServers(configuration.IceServers);
        }
        if (configuration != null)
        {
            _iceAgent.SetTransportPolicy(configuration.IceTransportPolicy);
        }
        _iceAgent.OnLocalCandidate += (s, c) => OnIceCandidate?.Invoke(this, c);
        _iceAgent.OnStateChange += HandleIceStateChange;
        _iceAgent.OnDtlsPacket += (s, p) => _dtlsTransport?.HandleIncomingPacket(p.Span.ToArray());
        _iceAgent.OnRtpPacket += HandleIncomingRtpPacket;
        _iceAgent.OnRtcpPacket += HandleIncomingRtcpPacket;
        _dtlsCertificate = DtlsCertificate.Generate(_timeProvider);
    }

    private void HandleIncomingRtcpPacket(object? sender, UdpPacket packet)
    {
        var rtcpPackets = RtcpPacket.ParseCompound(packet.Span);
        foreach (var rtcp in rtcpPackets)
        {
            RTCRtpTransceiver? transceiver;
            lock (_transceiverLock)
            {
                transceiver = _transceivers.FirstOrDefault(t => t.Sender.Transport != null);
            }
            if (transceiver == null) continue;

            if (rtcp is RtcpNackPacket nack)
            {
                transceiver.Sender.HandleNackAsync(nack).FireAndForget();
            }
            else if (rtcp is RtcpPliPacket pli)
            {
                transceiver.Sender.HandlePliAsync(pli).FireAndForget();
            }
            else if (rtcp is RtcpFirPacket)
            {
                transceiver.Sender.HandlePliAsync(new RtcpPliPacket()).FireAndForget();
            }
        }
    }

    private async Task SendRtcpInternal(RtcpPacket packet)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1500);
        try
        {
            int length = packet.Serialize(buffer);
            if (length > 0)
            {
                await _iceAgent.SendDataAsync(buffer.AsMemory(0, length)).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private readonly HashSet<string> _announcedTracks = new();

    private void HandleIncomingRtpPacket(object? sender, UdpPacket packet)
    {
        var srtp = _srtpSession;
        if (srtp != null && srtp.Unprotect(packet.Data, out var rtpPacket))
        {
            _remotePayloadTypeMap.TryGetValue(rtpPacket.PayloadType, out var transceiver);
            if (transceiver != null)
            {
                lock (_announcedTracks)
                {
                    if (_announcedTracks.Add(transceiver.Mid))
                    {
                        OnTrack?.Invoke(this, new RTCTrackEvent(transceiver.Receiver, transceiver.Receiver.Track, transceiver));
                    }
                }
                transceiver.Receiver.HandleRtpPacketAsync(rtpPacket).FireAndForget();
            }
        }
    }

    private async Task SendRtpInternal(RtpPacket packet)
    {
        var srtp = _srtpSession;
        if (srtp != null)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1500);
            try
            {
                if (srtp.Protect(packet, buffer, out int length))
                {
                    await _iceAgent.SendDataAsync(buffer.AsMemory(0, length)).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Adds a local media track to the peer connection.
    /// </summary>
    /// <param name="track">The media track to send.</param>
    /// <returns>The RTP sender associated with the track.</returns>
    public RTCRtpSender AddTrack(MediaStreamTrack track)
    {
        lock (_transceiverLock)
        {
            var transceiver = _transceivers.FirstOrDefault(t => t.Sender.Track == null && t.Receiver.Track.Kind == track.Kind);
            if (transceiver != null)
            {
                transceiver.Sender.ReplaceTrack(track);
                return transceiver.Sender;
            }

            var sender = new RTCRtpSender(track, SendRtpInternal);
            var receiverTrack = track.Kind == "audio" ? (MediaStreamTrack)new AudioStreamTrack() : new VideoStreamTrack();
            var receiver = new RTCRtpReceiver(receiverTrack, SendRtcpInternal);

            if (_publicDtlsTransport != null)
            {
                sender.Transport = _publicDtlsTransport;
                receiver.Transport = _publicDtlsTransport;
            }

            transceiver = new RTCRtpTransceiver(sender, receiver)
            {
                Mid = (_transceivers.Count).ToString(),
                Direction = RTCRtpTransceiverDirection.SendRecv
            };
            _transceivers.Add(transceiver);
            return sender;
        }
    }

    /// <summary>
    /// Creates a local SCTP data channel.
    /// </summary>
    /// <param name="label">The data channel label.</param>
    /// <returns>The created data channel.</returns>
    public RTCDataChannel CreateDataChannel(string label)
    {
        RTCDataChannel dc;
        lock (_dataChannelLock)
        {
            ushort id = (ushort)(_dataChannels.Count + 1);
            dc = new RTCDataChannel(label, id, _sctpAssociation);
            _dataChannels.Add(dc);
        }
        _sctpAssociation?.RegisterDataChannel(dc);

        if (_sctpAssociation?.State == Sctp.SctpAssociationState.Established)
        {
            _sctpAssociation.SendDataAsync(dc.Id, 50, new Sctp.DcepMessage { Type = Sctp.DcepMessageType.DataChannelOpen, Label = label }.Serialize()).FireAndForget();
        }

        return dc;
    }

    private async Task InitializeDtlsAsync(bool isClient)
    {
        _dtlsTransport = new DtlsTransport(_iceAgent.SendDataAsync, _dtlsCertificate, _loggerFactory);
        var publicTransport = new RTCDtlsTransport(_dtlsTransport, new RTCIceTransport());
        _publicDtlsTransport = publicTransport;

        lock (_transceiverLock)
        {
            foreach (var transceiver in _transceivers)
            {
                transceiver.Sender.Transport = publicTransport;
                transceiver.Receiver.Transport = publicTransport;
            }
        }

        _dtlsTransport.OnData += (s, data) => HandleIncomingDtlsApplicationDataAsync(data);

        SdpMessage? remoteDesc;
        lock (_stateLock) { remoteDesc = _remoteDescription; }

        var fingerprintAttr = remoteDesc?.Attributes.FirstOrDefault(a => a.Name == "fingerprint")?.Value;
        fingerprintAttr ??= remoteDesc?.MediaDescriptions
            .SelectMany(md => md.Attributes)
            .FirstOrDefault(a => a.Name == "fingerprint")
            ?.Value;
        if (fingerprintAttr != null)
        {
            var parts = fingerprintAttr.Split(' ', 2);
            if (parts.Length == 2)
            {
                _dtlsTransport.SetRemoteFingerprint(parts[0], parts[1]);
            }
        }

        _dtlsTransport.OnStateChange += async (s, state) =>
        {
            if (state == DtlsState.Connected)
            {
                try
                {
                    var keys = _dtlsTransport.GetSrtpKeys();
                    if (keys != null)
                    {
                        _srtpSession = new Srtp.SrtpSession(keys, isClient);
                    }

                    if (ShouldNegotiateDataChannels())
                    {
                        await InitializeSctpAsync(isClient);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Post-DTLS initialization failed");
                    TransitionToFailed();
                }
            }
            else if (state == DtlsState.Failed)
            {
                TransitionToFailed();
            }
        };
        await _dtlsTransport.StartAsync(isClient);
    }

    private Task HandleIncomingDtlsApplicationDataAsync(byte[] data)
    {
        return _sctpAssociation?.HandlePacketAsync(data) ?? Task.CompletedTask;
    }

    private async Task InitializeSctpAsync(bool isClient)
    {
        _sctpAssociation = new Sctp.SctpAssociation(DefaultSctpPort, DefaultSctpPort, async (data) => {
            if (_dtlsTransport != null)
            {
                await _dtlsTransport.SendAsync(data);
            }
        }, _loggerFactory, _timeProvider);

        _sctpAssociation.OnRemoteDataChannel += (s, dc) =>
        {
            lock (_dataChannelLock) { _dataChannels.Add(dc); }
            OnDataChannel?.Invoke(this, dc);
        };

        _sctpAssociation.OnEstablished += (s, e) =>
        {
            if (isClient)
            {
                List<RTCDataChannel> snapshot;
                lock (_dataChannelLock) { snapshot = _dataChannels.ToList(); }
                foreach (var dc in snapshot)
                {
                    dc.SetAssociation(_sctpAssociation!);
                    _sctpAssociation!.RegisterDataChannel(dc);
                    _sctpAssociation!.SendDataAsync(dc.Id, 50, new Sctp.DcepMessage { Type = Sctp.DcepMessageType.DataChannelOpen, Label = dc.Label }.Serialize()).FireAndForget();
                }
            }
        };

        await _sctpAssociation.StartAsync(isClient);
    }

    /// <summary>
    /// Creates an SDP offer for the current connection configuration.
    /// </summary>
    /// <returns>The generated SDP offer.</returns>
    public async Task<SdpMessage> CreateOfferAsync()
    {
        await _iceAgent.StartGatheringAsync();
        var offer = new SdpMessage { SessionName = "RtcForge Session" };

        EnsureDefaultAudioTrackForEmptyOffer();
        EnsureDataChannelMidIfNeeded();

        var bundleMids = GetBundleMids();
        if (bundleMids.Count > 0)
        {
            offer.Attributes.Add(new SdpAttribute { Name = "group", Value = $"BUNDLE {string.Join(" ", bundleMids)}" });
        }

        offer.Attributes.Add(new SdpAttribute { Name = "ice-options", Value = "trickle" });
        offer.Attributes.Add(new SdpAttribute { Name = "msid-semantic", Value = "WMS *" });
        offer.Attributes.Add(new SdpAttribute { Name = "fingerprint", Value = $"sha-256 {_dtlsCertificate.Fingerprint}" });
        offer.Attributes.Add(new SdpAttribute { Name = "setup", Value = "actpass" });
        offer.Attributes.Add(new SdpAttribute { Name = "ice-ufrag", Value = _iceAgent.LocalUfrag });
        offer.Attributes.Add(new SdpAttribute { Name = "ice-pwd", Value = _iceAgent.LocalPassword });

        List<RTCRtpTransceiver> snapshot;
        lock (_transceiverLock) { snapshot = _transceivers.ToList(); }
        foreach (var transceiver in snapshot)
        {
            offer.MediaDescriptions.Add(CreateMediaDescription(transceiver, transceiver.Direction));
        }

        if (ShouldNegotiateDataChannels())
        {
            offer.MediaDescriptions.Add(CreateDataChannelMediaDescription());
        }
        return offer;
    }

    /// <summary>
    /// Creates an SDP answer for the current remote offer.
    /// </summary>
    /// <returns>The generated SDP answer.</returns>
    public async Task<SdpMessage> CreateAnswerAsync()
    {
        _iceAgent.StartGatheringAsync().FireAndForget();
        var answer = new SdpMessage { SessionName = "RtcForge Session" };

        var bundleMids = GetBundleMids();
        if (bundleMids.Count > 0)
        {
            answer.Attributes.Add(new SdpAttribute { Name = "group", Value = $"BUNDLE {string.Join(" ", bundleMids)}" });
        }

        answer.Attributes.Add(new SdpAttribute { Name = "ice-options", Value = "trickle" });
        answer.Attributes.Add(new SdpAttribute { Name = "msid-semantic", Value = "WMS *" });
        answer.Attributes.Add(new SdpAttribute { Name = "fingerprint", Value = $"sha-256 {_dtlsCertificate.Fingerprint}" });

        SdpMessage? remoteDesc;
        lock (_stateLock) { remoteDesc = _remoteDescription; }
        var remoteSetup = GetAttributeValue(remoteDesc, "setup");
        answer.Attributes.Add(new SdpAttribute { Name = "setup", Value = remoteSetup == "actpass" ? "passive" : "active" });

        answer.Attributes.Add(new SdpAttribute { Name = "ice-ufrag", Value = _iceAgent.LocalUfrag });
        answer.Attributes.Add(new SdpAttribute { Name = "ice-pwd", Value = _iceAgent.LocalPassword });

        List<RTCRtpTransceiver> snapshot;
        lock (_transceiverLock) { snapshot = _transceivers.ToList(); }
        foreach (var transceiver in snapshot)
        {
            var direction = ResolveAnswerDirection(transceiver);
            answer.MediaDescriptions.Add(CreateMediaDescription(transceiver, direction));
        }

        if (_dataChannelNegotiated)
        {
            answer.MediaDescriptions.Add(CreateDataChannelMediaDescription());
        }

        return answer;
    }

    /// <summary>
    /// Sets the local SDP description.
    /// </summary>
    /// <param name="description">The local SDP offer or answer.</param>
    /// <returns>A completed task when the description has been applied.</returns>
    public Task SetLocalDescriptionAsync(SdpMessage description)
    {
        lock (_stateLock)
        {
            _localDescription = description;

            if (_remoteDescription == null)
            {
                _signalingState = SignalingState.HaveLocalOffer;
                _iceAgent.IsControlling = true;
            }
            else
            {
                _signalingState = SignalingState.Stable;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the remote SDP description.
    /// </summary>
    /// <param name="description">The remote SDP offer or answer.</param>
    /// <returns>A completed task when the description has been applied.</returns>
    public Task SetRemoteDescriptionAsync(SdpMessage description)
    {
        lock (_stateLock)
        {
            _remoteDescription = description;

            if (_localDescription == null)
            {
                _signalingState = SignalingState.HaveRemoteOffer;
                _iceAgent.IsControlling = false;
            }
            else
            {
                _signalingState = SignalingState.Stable;
            }
        }

        var ufrag = GetAttributeValue(description, "ice-ufrag");
        var pwd = GetAttributeValue(description, "ice-pwd");
        if (ufrag != null && pwd != null)
        {
            _iceAgent.SetRemoteCredentials(ufrag, pwd);
        }

        foreach (var attr in description.Attributes.Where(a => a.Name == "candidate"))
        {
            _iceAgent.AddRemoteCandidate(IceCandidate.Parse(attr.Value!));
        }

        foreach (var md in description.MediaDescriptions)
        {
            if (md.Media == "application")
            {
                EnsureDataChannelForRemoteMedia(md);
            }
            else
            {
                EnsureTransceiverForRemoteMedia(md);
            }
            foreach (var attr in md.Attributes.Where(a => a.Name == "candidate"))
            {
                _iceAgent.AddRemoteCandidate(IceCandidate.Parse(attr.Value!));
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds a remote ICE candidate.
    /// </summary>
    /// <param name="candidate">The remote ICE candidate.</param>
    public void AddIceCandidate(IceCandidate candidate) => _iceAgent.AddRemoteCandidate(candidate);

    /// <summary>
    /// Starts ICE connectivity checks using the configured default timeout.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the connection attempt.</param>
    /// <returns><see langword="true"/> when ICE connects; otherwise, <see langword="false"/> when the attempt times out or fails.</returns>
    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        return await ConnectAsync(_connectionTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts ICE connectivity checks using a per-call timeout.
    /// </summary>
    /// <param name="timeout">The maximum time to spend connecting.</param>
    /// <param name="cancellationToken">A token used to cancel the connection attempt.</param>
    /// <returns><see langword="true"/> when ICE connects; otherwise, <see langword="false"/> when the attempt times out or fails.</returns>
    public async Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token, timeoutCts.Token);
        try
        {
            return await _iceAgent.ConnectAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            TransitionToFailed();
            return false;
        }
    }

    private void HandleIceStateChange(object? sender, IceState state)
    {
        switch (state)
        {
            case IceState.Checking:
                _connectionState = PeerConnectionState.Connecting;
                break;
            case IceState.Connected:
            case IceState.Completed:
                _connectionState = PeerConnectionState.Connected;
                if (Interlocked.CompareExchange(ref _dtlsInitialized, 1, 0) == 0)
                {
                    bool isClient = ResolveDtlsClientRole();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await InitializeDtlsAsync(isClient);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "DTLS/SCTP initialization failed");
                            TransitionToFailed();
                        }
                    }, _cts.Token);
                }
                break;
            case IceState.Failed:
                _connectionState = PeerConnectionState.Failed;
                break;
            case IceState.Disconnected:
                _connectionState = PeerConnectionState.Disconnected;
                break;
            case IceState.Closed:
                _connectionState = PeerConnectionState.Closed;
                break;
        }
        OnConnectionStateChange?.Invoke(this, _connectionState);
    }

    private void TransitionToFailed()
    {
        if (_connectionState == PeerConnectionState.Closed) return;
        _connectionState = PeerConnectionState.Failed;
        OnConnectionStateChange?.Invoke(this, PeerConnectionState.Failed);
    }

    /// <summary>
    /// Releases resources held by the peer connection.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _sctpAssociation?.Dispose();
        _dtlsTransport?.Dispose();
        _iceAgent.Dispose();
        lock (_dataChannelLock)
        {
            foreach (var channel in _dataChannels)
            {
                channel.SetClosed();
            }
        }
        _connectionState = PeerConnectionState.Closed;
        _signalingState = SignalingState.Closed;
        _cts.Dispose();
    }

    /// <summary>
    /// Asynchronously releases resources held by the peer connection.
    /// </summary>
    /// <returns>A value task that completes when shutdown has finished.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        if (_sctpAssociation != null)
        {
            try { await _sctpAssociation.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5), _timeProvider); }
            catch (TimeoutException) { }
            _sctpAssociation.Dispose();
        }
        _dtlsTransport?.Dispose();
        _iceAgent.Dispose();
        lock (_dataChannelLock)
        {
            foreach (var channel in _dataChannels)
            {
                channel.SetClosed();
            }
        }
        _connectionState = PeerConnectionState.Closed;
        _signalingState = SignalingState.Closed;
        _cts.Dispose();
    }

    internal bool ResolveDtlsClientRole()
    {
        SdpMessage? localDesc, remoteDesc;
        lock (_stateLock)
        {
            localDesc = _localDescription;
            remoteDesc = _remoteDescription;
        }
        return GetSetupRole(localDesc) switch
        {
            "active" => true,
            "passive" => false,
            _ => GetSetupRole(remoteDesc) switch
            {
                "active" => false,
                "passive" => true,
                _ => !_iceAgent.IsControlling
            }
        };
    }

    private void EnsureTransceiverForRemoteMedia(SdpMediaDescription md)
    {
        if (md.Media != "audio" && md.Media != "video")
        {
            return;
        }

        string mid;
        RTCRtpTransceiver transceiver;
        lock (_transceiverLock)
        {
            mid = md.Attributes.FirstOrDefault(a => a.Name == "mid")?.Value ?? _transceivers.Count.ToString();
            transceiver = _transceivers.FirstOrDefault(t => t.Mid == mid)!;
            if (transceiver == null)
            {
                var receiverTrack = md.Media == "audio" ? (MediaStreamTrack)new AudioStreamTrack() : new VideoStreamTrack();
                var sender = new RTCRtpSender(null, SendRtpInternal);
                var receiver = new RTCRtpReceiver(receiverTrack, SendRtcpInternal);
                transceiver = new RTCRtpTransceiver(sender, receiver)
                {
                    Mid = mid
                };
                _transceivers.Add(transceiver);
            }
        }

        transceiver.RemoteDirection = md.Port == 0
            ? RTCRtpTransceiverDirection.Inactive
            : ParseDirection(md.Attributes);
        RegisterRemotePayloadTypes(md, transceiver);
        NegotiateCodecsFromRemote(md, transceiver);
    }

    private static void NegotiateCodecsFromRemote(SdpMediaDescription md, RTCRtpTransceiver transceiver)
    {
        transceiver.NegotiatedCodecs.Clear();
        foreach (var attr in md.Attributes.Where(a => a.Name == "rtpmap" && a.Value != null))
        {
            // Format: "PT codec/clockRate" or "PT codec/clockRate/channels"
            var parts = attr.Value!.Split(' ', 2);
            if (parts.Length != 2 || !byte.TryParse(parts[0], out var pt))
            {
                continue;
            }
            var codecParts = parts[1].Split('/');
            if (codecParts.Length < 2 || !int.TryParse(codecParts[1], out var clockRate))
            {
                continue;
            }
            int? channels = codecParts.Length >= 3 && int.TryParse(codecParts[2], out var ch) ? ch : null;
            transceiver.NegotiatedCodecs.Add(new NegotiatedCodec(pt, codecParts[0], clockRate, channels));
        }
    }

    private void EnsureDataChannelForRemoteMedia(SdpMediaDescription md)
    {
        _dataChannelNegotiated = true;
        string? midAttr = md.Attributes.FirstOrDefault(a => a.Name == "mid")?.Value;
        if (midAttr != null)
        {
            _dataChannelMid = midAttr;
        }
        else if (_dataChannelMid == null)
        {
            lock (_transceiverLock) { _dataChannelMid = _transceivers.Count.ToString(); }
        }
    }

    private RTCRtpTransceiverDirection ResolveAnswerDirection(RTCRtpTransceiver transceiver)
    {
        bool canSend = transceiver.Sender.Track != null && transceiver.Direction != RTCRtpTransceiverDirection.Inactive;
        var offeredDirection = transceiver.RemoteDirection ?? RTCRtpTransceiverDirection.SendRecv;

        return offeredDirection switch
        {
            RTCRtpTransceiverDirection.SendOnly => RTCRtpTransceiverDirection.RecvOnly,
            RTCRtpTransceiverDirection.RecvOnly => canSend ? RTCRtpTransceiverDirection.SendOnly : RTCRtpTransceiverDirection.Inactive,
            RTCRtpTransceiverDirection.Inactive => RTCRtpTransceiverDirection.Inactive,
            _ => canSend ? RTCRtpTransceiverDirection.SendRecv : RTCRtpTransceiverDirection.RecvOnly
        };
    }

    private static RTCRtpTransceiverDirection ParseDirection(IEnumerable<SdpAttribute> attributes)
    {
        foreach (var attr in attributes)
        {
            switch (attr.Name)
            {
                case "sendrecv": return RTCRtpTransceiverDirection.SendRecv;
                case "sendonly": return RTCRtpTransceiverDirection.SendOnly;
                case "recvonly": return RTCRtpTransceiverDirection.RecvOnly;
                case "inactive": return RTCRtpTransceiverDirection.Inactive;
            }
        }

        return RTCRtpTransceiverDirection.SendRecv;
    }

    private static string? GetSetupRole(SdpMessage? description)
    {
        if (description == null)
        {
            return null;
        }

        return GetAttributeValue(description, "setup");
    }

    private void EnsureDefaultAudioTrackForEmptyOffer()
    {
        bool needsTrack;
        lock (_transceiverLock) { needsTrack = _transceivers.Count == 0; }
        if (needsTrack && !ShouldNegotiateDataChannels())
        {
            AddTrack(new AudioStreamTrack());
        }
    }

    private void EnsureDataChannelMidIfNeeded()
    {
        if (ShouldNegotiateDataChannels() && string.IsNullOrEmpty(_dataChannelMid))
        {
            lock (_transceiverLock) { _dataChannelMid = _transceivers.Count.ToString(); }
            _dataChannelNegotiated = true;
        }
    }

    private bool ShouldNegotiateDataChannels()
    {
        return _dataChannels.Count > 0 || _dataChannelNegotiated;
    }

    private List<string> GetBundleMids()
    {
        List<string> mids;
        lock (_transceiverLock) { mids = _transceivers.ConvertAll(t => t.Mid); }
        if (ShouldNegotiateDataChannels())
        {
            mids.Add(_dataChannelMid!);
        }

        return mids;
    }

    private SdpMediaDescription CreateMediaDescription(RTCRtpTransceiver transceiver, RTCRtpTransceiverDirection direction)
    {
        var md = new SdpMediaDescription
        {
            Media = transceiver.Receiver.Track.Kind,
            Port = 9,
            Proto = "UDP/TLS/RTP/SAVPF",
            Connection = "IN IP4 0.0.0.0"
        };
        md.Attributes.Add(new SdpAttribute { Name = "mid", Value = transceiver.Mid });
        md.Attributes.Add(new SdpAttribute { Name = direction.ToString().ToLowerInvariant() });
        md.Attributes.Add(new SdpAttribute { Name = "rtcp-mux" });
        md.Attributes.Add(new SdpAttribute { Name = "rtcp-rsize" });

        if (transceiver.NegotiatedCodecs.Count > 0)
        {
            foreach (var codec in transceiver.NegotiatedCodecs)
            {
                md.Formats.Add(codec.PayloadType.ToString());
                string rtpmap = codec.Channels.HasValue
                    ? $"{codec.PayloadType} {codec.Name}/{codec.ClockRate}/{codec.Channels}"
                    : $"{codec.PayloadType} {codec.Name}/{codec.ClockRate}";
                md.Attributes.Add(new SdpAttribute { Name = "rtpmap", Value = rtpmap });
            }
        }
        else if (transceiver.Receiver.Track.Kind == "audio")
        {
            md.Formats.Add("111");
            md.Attributes.Add(new SdpAttribute { Name = "rtpmap", Value = "111 opus/48000/2" });
            md.Attributes.Add(new SdpAttribute { Name = "fmtp", Value = "111 minptime=10;useinbandfec=1" });
        }
        else
        {
            md.Formats.Add("96");
            md.Attributes.Add(new SdpAttribute { Name = "rtpmap", Value = "96 VP8/90000" });
            md.Attributes.Add(new SdpAttribute { Name = "rtcp-fb", Value = "96 nack" });
            md.Attributes.Add(new SdpAttribute { Name = "rtcp-fb", Value = "96 nack pli" });
        }

        AppendLocalIceAttributes(md);
        return md;
    }

    private SdpMediaDescription CreateDataChannelMediaDescription()
    {
        var md = new SdpMediaDescription
        {
            Media = "application",
            Port = 9,
            Proto = "UDP/DTLS/SCTP",
            Connection = "IN IP4 0.0.0.0",
            Formats = new List<string> { "webrtc-datachannel" },
            Attributes = new List<SdpAttribute>
            {
                new() { Name = "mid", Value = _dataChannelMid! },
                new() { Name = "sctp-port", Value = DefaultSctpPort.ToString() },
                new() { Name = "max-message-size", Value = DefaultMaxMessageSize.ToString() }
            }
        };
        AppendLocalIceAttributes(md);
        return md;
    }

    private void RegisterRemotePayloadTypes(SdpMediaDescription md, RTCRtpTransceiver transceiver)
    {
        foreach (var format in md.Formats)
        {
            if (byte.TryParse(format, out var payloadType))
            {
                _remotePayloadTypeMap.AddOrUpdate(payloadType, transceiver, (_, _) => transceiver);
            }
        }
    }

    private static string? GetAttributeValue(SdpMessage? description, string attributeName)
    {
        if (description == null)
        {
            return null;
        }

        return description.Attributes.FirstOrDefault(a => a.Name == attributeName)?.Value
            ?? description.MediaDescriptions
                .SelectMany(md => md.Attributes)
                .FirstOrDefault(a => a.Name == attributeName)
                ?.Value;
    }

    private void AppendLocalIceAttributes(SdpMediaDescription mediaDescription)
    {
        foreach (var candidate in _iceAgent.LocalCandidates)
        {
            mediaDescription.Attributes.Add(new SdpAttribute { Name = "candidate", Value = candidate.ToString() });
        }

        if (_iceAgent.LocalCandidates.Count > 0)
        {
            mediaDescription.Attributes.Add(new SdpAttribute { Name = "end-of-candidates" });
        }
    }
}
