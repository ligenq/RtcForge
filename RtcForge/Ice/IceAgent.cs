using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using RtcForge.Media;
using RtcForge.Stun;

namespace RtcForge.Ice;

public class IceAgent : IIceAgent
{
    private static readonly TimeSpan ConsentInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConsentTimeout = TimeSpan.FromSeconds(30);
    // ICE pair-check tracing is emitted at Debug level via the supplied logger. It used to
    // be gated behind the RTCFORGE_TRACE_ICE=1 environment variable; that made connectivity
    // problems effectively invisible when debugging. Callers that don't want the spam can
    // simply raise the minimum log level for IceAgent's category.
    private IceState _state = IceState.New;
    private readonly Lock _candidateLock = new();
    private readonly List<IceCandidate> _localCandidates = [];
    private readonly List<IceCandidate> _remoteCandidates = [];
    private readonly List<IceUdpTransport> _transports = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<StunMessage>> _transactions = new();
    private string? _remoteUfrag;
    private string? _remotePassword;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IceCandidatePair> _checklist = [];
    private readonly ulong _tieBreaker;
    private readonly List<RTCIceServer> _iceServers = [];
    private readonly Dictionary<string, TurnAllocation> _turnAllocations = [];
    private readonly ConcurrentDictionary<string, Task<IPAddress?>> _hostnameResolutionCache = new(StringComparer.OrdinalIgnoreCase);
    private IceUdpTransport? _selectedTransport;
    private IceCandidate? _selectedLocalCandidate;
    private IceCandidate? _selectedRemoteCandidate;
    private RTCIceTransportPolicy _transportPolicy = RTCIceTransportPolicy.All;
    private bool _gatheringStarted;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private int _connectKickPending;
    private CancellationTokenSource? _consentCts;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    public IceState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                OnStateChange?.Invoke(this, _state);
            }
        }
    }

    public string LocalUfrag { get; }
    public string LocalPassword { get; }
    public IReadOnlyList<IceCandidate> LocalCandidates { get { lock (_candidateLock) { return [.. _localCandidates]; } } }
    public bool IsControlling { get; set; } = true;
    internal IceCandidate? SelectedRemoteCandidate => _selectedRemoteCandidate;

    public event EventHandler<IceCandidate>? OnLocalCandidate;
    public event EventHandler<IceState>? OnStateChange;
    public event EventHandler<UdpPacket>? OnDtlsPacket;
    public event EventHandler<UdpPacket>? OnRtpPacket;
    public event EventHandler<UdpPacket>? OnRtcpPacket;

    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<IceAgent>? _logger;

    public IceAgent(ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<IceAgent>();
        _timeProvider = timeProvider ?? TimeProvider.System;
        LocalUfrag = GenerateRandomString(8);
        LocalPassword = GenerateRandomString(24);
        _tieBreaker = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
        Trace($"created ufrag={LocalUfrag}");
    }

    public void SetIceServers(IEnumerable<RTCIceServer> servers)
    {
        _iceServers.Clear();
        _iceServers.AddRange(servers);
    }

    public void SetTransportPolicy(RTCIceTransportPolicy transportPolicy)
    {
        _transportPolicy = transportPolicy;
    }

    public async Task StartGatheringAsync()
    {
        if (_gatheringStarted)
        {
            return;
        }

        Trace("starting candidate gathering");
        _gatheringStarted = true;
        State = IceState.Gathering;
        var gatheringTasks = new List<Task>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var ip in ni.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork
                    || ip.Address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    try
                    {
                        var bindAddress = ip.Address.AddressFamily == AddressFamily.InterNetworkV6
                            ? IPAddress.IPv6Any : IPAddress.Any;
                        var transport = new IceUdpTransport(new IPEndPoint(bindAddress, 0), _loggerFactory);
                        _transports.Add(transport);
                        ProcessTransportAsync(transport).FireAndForget();

                        var hostCandidate = new IceCandidate
                        {
                            Foundation = "1",
                            Component = 1,
                            Protocol = "udp",
                            Priority = CalculateHostPriority(),
                            Address = ip.Address.ToString(),
                            Port = transport.LocalEndPoint.Port,
                            Type = IceCandidateType.Host
                        };
                        if (_transportPolicy != RTCIceTransportPolicy.Relay)
                        {
                            lock (_candidateLock) { _localCandidates.Add(hostCandidate); }
                            OnLocalCandidate?.Invoke(this, hostCandidate);
                            Trace($"local candidate {hostCandidate}");
                        }

                        gatheringTasks.Add(GatherSrflxCandidatesAsync(transport, hostCandidate));
                        gatheringTasks.Add(GatherRelayCandidatesAsync(transport, hostCandidate));
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to bind UDP transport for {IpAddress}", ip.Address);
                    }
                }
            }
        }

        await Task.WhenAll(gatheringTasks);
        State = IceState.Complete;
    }

    private static readonly TimeSpan SrflxGatherTimeout = TimeSpan.FromSeconds(3);

    private async Task GatherSrflxCandidatesAsync(IceUdpTransport transport, IceCandidate hostCandidate)
    {
        // Query every configured STUN server in parallel so a single slow or dead server
        // cannot hold up gathering for the whole transport. Each request is capped with
        // a per-request timeout so we never burn the full RFC retransmission budget.
        var perServerTasks = new List<Task>();
        foreach (var server in _iceServers)
        {
            foreach (var url in server.Urls)
            {
                if (url.StartsWith("stun:"))
                {
                    perServerTasks.Add(GatherSrflxFromSingleServerAsync(transport, hostCandidate, url));
                }
            }
        }

        if (perServerTasks.Count > 0)
        {
            await Task.WhenAll(perServerTasks).ConfigureAwait(false);
        }
    }

    private async Task GatherSrflxFromSingleServerAsync(IceUdpTransport transport, IceCandidate hostCandidate, string url)
    {
        try
        {
            var uri = new Uri(url.Replace("stun:", "stun://"));
            // Pick a DNS result that matches the transport's address family. Otherwise,
            // sending to (e.g.) an IPv6 STUN server from an IPv4-bound socket fails on
            // Windows with WSAEFAULT ("invalid pointer"), which is how that mismatch
            // surfaces via Winsock.
            var transportFamily = transport.LocalEndPoint.AddressFamily;
            var candidates = await Dns.GetHostAddressesAsync(uri.Host).ConfigureAwait(false);
            var matching = Array.Find(candidates, a => a.AddressFamily == transportFamily);
            if (matching == null)
            {
                Trace($"srflx gathering skipped for {url}: no DNS result matching address family {transportFamily}");
                return;
            }
            var stunEp = new IPEndPoint(matching, uri.Port > 0 ? uri.Port : 3478);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeoutCts.CancelAfter(SrflxGatherTimeout);

            StunMessage? response;
            try
            {
                response = await SendPlainStunBindingRequestAsync(transport, stunEp, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !_cts.IsCancellationRequested)
            {
                Trace($"srflx gathering timed out for {url}");
                return;
            }

            if (response == null)
            {
                return;
            }

            var addrAttr = response.Attributes.Find(a => a.Type == StunAttributeType.XorMappedAddress);
            var srflxAddr = addrAttr?.GetXorMappedAddress(response.TransactionId);
            if (srflxAddr == null)
            {
                return;
            }

            if (_transportPolicy == RTCIceTransportPolicy.Relay)
            {
                return;
            }

            var srflxCandidate = new IceCandidate
            {
                Foundation = "2",
                Component = 1,
                Protocol = "udp",
                Priority = CalculateSrflxPriority(),
                Address = srflxAddr.Address.ToString(),
                Port = srflxAddr.Port,
                Type = IceCandidateType.Srflx,
                RelatedAddress = hostCandidate.Address,
                RelatedPort = hostCandidate.Port
            };

            lock (_candidateLock)
            {
                // Dedupe srflx candidates that multiple STUN servers report with the
                // same public ip:port for the same host candidate.
                foreach (var existing in _localCandidates)
                {
                    if (existing.Type == IceCandidateType.Srflx
                        && existing.Address == srflxCandidate.Address
                        && existing.Port == srflxCandidate.Port
                        && existing.RelatedAddress == srflxCandidate.RelatedAddress
                        && existing.RelatedPort == srflxCandidate.RelatedPort)
                    {
                        return;
                    }
                }

                _localCandidates.Add(srflxCandidate);
            }

            OnLocalCandidate?.Invoke(this, srflxCandidate);
            Trace($"local candidate {srflxCandidate}");
        }
        catch (Exception ex)
        {
            Trace($"srflx gathering failed for {url}: {ex.Message}");
        }
    }

    private async Task GatherRelayCandidatesAsync(IceUdpTransport transport, IceCandidate hostCandidate)
    {
        foreach (var server in _iceServers)
        {
            foreach (var url in server.Urls)
            {
                if (url.StartsWith("turn:"))
                {
                    try
                    {
                        var uri = new Uri(url.Replace("turn:", "stun://", StringComparison.Ordinal));
                        var hostAddresses = await Dns.GetHostAddressesAsync(uri.Host).ConfigureAwait(false);
                        var turnEp = new IPEndPoint(hostAddresses[0], uri.Port > 0 ? uri.Port : 3478);

                        var request = new StunMessage { Type = StunMessageType.AllocateRequest, TransactionId = Guid.NewGuid().ToByteArray().AsSpan(0, 12).ToArray() };
                        request.Attributes.Add(new StunAttribute { Type = StunAttributeType.RequestedTransport, Value = [17, 0, 0, 0] });

                        var response = await SendTransactionAsync(transport, turnEp, request);
                        if (response?.Type == StunMessageType.AllocateErrorResponse)
                        {
                            var err = response.Attributes.Find(a => a.Type == StunAttributeType.ErrorCode)?.GetErrorCode();
                            if (err?.Code == 401)
                            {
                                var realm = response.Attributes.Find(a => a.Type == StunAttributeType.Realm);
                                var nonce = response.Attributes.Find(a => a.Type == StunAttributeType.Nonce);

                                if (realm != null && nonce != null && server.Username != null && server.Credential != null)
                                {
                                    var currentRealm = realm.Value;
                                    var currentNonce = nonce.Value;
                                    var realmStr = System.Text.Encoding.UTF8.GetString(currentRealm);
                                    var key = StunSecurity.DeriveTurnKey(server.Username, realmStr, server.Credential);

                                    StunMessage? authResponse = null;
                                    for (int attempt = 0; attempt < 2; attempt++)
                                    {
                                        var authRequest = new StunMessage { Type = StunMessageType.AllocateRequest, TransactionId = Guid.NewGuid().ToByteArray().AsSpan(0, 12).ToArray() };
                                        authRequest.Attributes.Add(new StunAttribute { Type = StunAttributeType.RequestedTransport, Value = [17, 0, 0, 0] });
                                        authRequest.Attributes.Add(new StunAttribute { Type = StunAttributeType.Username, Value = System.Text.Encoding.UTF8.GetBytes(server.Username) });
                                        authRequest.Attributes.Add(new StunAttribute { Type = StunAttributeType.Realm, Value = currentRealm });
                                        authRequest.Attributes.Add(new StunAttribute { Type = StunAttributeType.Nonce, Value = currentNonce });
                                        authRequest.Attributes.Add(new StunAttribute { Type = StunAttributeType.MessageIntegrity });
                                        authRequest.Attributes.Add(new StunAttribute { Type = StunAttributeType.Fingerprint });

                                        authResponse = await SendTransactionAsync(transport, turnEp, authRequest, key);
                                        if (authResponse?.Type == StunMessageType.AllocateSuccessResponse)
                                        {
                                            break;
                                        }

                                        var authError = authResponse?.Attributes.Find(a => a.Type == StunAttributeType.ErrorCode)?.GetErrorCode();
                                        if (authResponse?.Type != StunMessageType.AllocateErrorResponse || authError?.Code != 438)
                                        {
                                            break;
                                        }

                                        var updatedRealm = authResponse.Attributes.Find(a => a.Type == StunAttributeType.Realm);
                                        var updatedNonce = authResponse.Attributes.Find(a => a.Type == StunAttributeType.Nonce);
                                        if (updatedNonce == null)
                                        {
                                            break;
                                        }

                                        currentRealm = updatedRealm?.Value ?? currentRealm;
                                        currentNonce = updatedNonce.Value;
                                        realmStr = System.Text.Encoding.UTF8.GetString(currentRealm);
                                        key = StunSecurity.DeriveTurnKey(server.Username, realmStr, server.Credential);
                                    }

                                    if (authResponse?.Type == StunMessageType.AllocateSuccessResponse)
                                    {
                                        var relayedAddrAttr = authResponse.Attributes.Find(a => a.Type == StunAttributeType.RelayedAddress);
                                        var relayedAddr = relayedAddrAttr?.GetXorMappedAddress(authResponse.TransactionId);
                                        if (relayedAddr != null)
                                        {
                                            var relayCandidate = new IceCandidate
                                            {
                                                Foundation = "3",
                                                Component = 1,
                                                Protocol = "udp",
                                                Priority = 0,
                                                Address = relayedAddr.Address.ToString(),
                                                Port = relayedAddr.Port,
                                                Type = IceCandidateType.Relay,
                                                RelatedAddress = hostCandidate.Address,
                                                RelatedPort = hostCandidate.Port
                                            };
                                            int allocationLifetimeSeconds = authResponse.Attributes
                                                .FirstOrDefault(attribute => attribute.Type == StunAttributeType.Lifetime)
                                                ?.GetUInt32() switch
                                            {
                                                uint lifetime when lifetime > 0 => (int)lifetime,
                                                _ => 600
                                            };
                                            _turnAllocations[$"{relayCandidate.Address}:{relayCandidate.Port}"] = new TurnAllocation(
                                                turnEp,
                                                relayedAddr,
                                                server.Username,
                                                currentRealm,
                                                currentNonce,
                                                key,
                                                TimeSpan.FromSeconds(allocationLifetimeSeconds),
                                                (request, integrityKey) => SendTransactionAsync(transport, turnEp, request, integrityKey),
                                                payload => transport.SendAsync(payload, turnEp),
                                                _timeProvider);
                                            lock (_candidateLock) { _localCandidates.Add(relayCandidate); }
                                            OnLocalCandidate?.Invoke(this, relayCandidate);
                                            Trace($"local candidate {relayCandidate}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace($"relay gathering failed for {url}: {ex.Message}");
                    }
                }
            }
        }
    }

    private async Task<StunMessage?> SendTransactionAsync(IceUdpTransport transport, IPEndPoint remoteEp, StunMessage request, byte[]? key = null, CancellationToken cancellationToken = default)
    {
        const int maxRetransmissions = 4;
        const int initialRtoMs = 500;

        string tid = BitConverter.ToString(request.TransactionId);
        var tcs = new TaskCompletionSource<StunMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transactions[tid] = tcs;

        try
        {
            byte[] buffer = new byte[request.GetSerializedLength()];
            request.Serialize(buffer, key);
            int rtoMs = initialRtoMs;

            for (int attempt = 0; attempt <= maxRetransmissions; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Trace($"stun tx {request.Type} id={tid} attempt={attempt + 1} local={transport.LocalEndPoint} remote={remoteEp} bytes={buffer.Length}");
                await transport.SendAsync(buffer, remoteEp);

                var delayTask = Task.Delay(TimeSpan.FromMilliseconds(rtoMs), _timeProvider, cancellationToken);
                var resultTask = await Task.WhenAny(tcs.Task, delayTask);
                if (resultTask == tcs.Task)
                {
                    var response = await tcs.Task;
                    Trace($"stun rx matched {response.Type} id={tid} remote={remoteEp}");
                    return response;
                }
                cancellationToken.ThrowIfCancellationRequested();

                rtoMs = Math.Min(rtoMs * 2, 8000);
            }

            Trace($"stun tx timeout {request.Type} id={tid} remote={remoteEp}");
            return null;
        }
        finally
        {
            _transactions.TryRemove(tid, out _);
        }
    }

    private static uint CalculateSrflxPriority() => (100 << 24) | (65535 << 8) | (256 - 1);

    private static uint CalculateHostPriority()
    {
        const uint typePref = 126;
        const uint localPref = 65535;
        const uint componentId = 1;
        return (typePref << 24) | (localPref << 8) | (256 - componentId);
    }

    private async Task ProcessTransportAsync(IceUdpTransport transport)
    {
        try
        {
            var reader = transport.GetReader();
            while (await reader.WaitToReadAsync(_cts.Token))
            {
                while (reader.TryRead(out var packet))
                {
                    try
                    {
                        HandleIncomingPacket(transport, packet);
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(packet.Array);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the ICE agent is disposed.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ICE transport processing failed");
        }
    }

    private void HandleIncomingPacket(IceUdpTransport transport, UdpPacket packet)
    {
        if (packet.Length == 0)
        {
            return;
        }

        if (TryTranslateTurnPacket(packet, out var translatedPacket))
        {
            packet = translatedPacket;
        }

        byte firstByte = packet.Span[0];

        if (firstByte <= 3)
        {
            if (StunMessage.TryParse(packet.Span, out var stun))
            {
                HandleStunMessage(transport, packet.RemoteEndPoint, stun);
            }
            else
            {
                Trace($"ignored stun-like packet from {packet.RemoteEndPoint}: parse failed bytes={packet.Length}");
            }
        }
        else if (firstByte >= 20 && firstByte <= 63)
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger.LogDebug(
                    "IceAgent DTLS-classified rx bytes={Bytes} from={Remote} local={Local} first=0x{First:X2} subscribers={Subs}",
                    packet.Length, packet.RemoteEndPoint, transport.LocalEndPoint, firstByte,
                    OnDtlsPacket?.GetInvocationList().Length ?? 0);
            }
            OnDtlsPacket?.Invoke(this, packet);
        }
        else if (firstByte >= 128 && firstByte <= 191)
        {
            if (packet.Length > 1)
            {
                byte payloadType = (byte)(packet.Span[1] & 0x7F);
                if (payloadType >= 200 && payloadType <= 206)
                {
                    OnRtcpPacket?.Invoke(this, packet);
                }
                else
                {
                    OnRtpPacket?.Invoke(this, packet);
                }
            }
        }
        else
        {
            Trace($"unclassified packet from {packet.RemoteEndPoint} bytes={packet.Length} first=0x{firstByte:X2}");
        }
    }

    private void HandleStunMessage(IceUdpTransport transport, IPEndPoint remoteEndPoint, StunMessage stun)
    {
        if (!ValidateStunMessage(stun, out string validationFailure))
        {
            Trace($"ignored stun message from {remoteEndPoint}: {validationFailure} type={stun.Type} id={BitConverter.ToString(stun.TransactionId)} attrs={FormatStunAttributes(stun)}");
            return;
        }

        string transactionId = BitConverter.ToString(stun.TransactionId);

        if (stun.Type == StunMessageType.BindingSuccessResponse
            || stun.Type == StunMessageType.BindingErrorResponse
            || stun.Type == StunMessageType.AllocateSuccessResponse
            || stun.Type == StunMessageType.AllocateErrorResponse
            || stun.Type == StunMessageType.CreatePermissionSuccessResponse
            || stun.Type == StunMessageType.CreatePermissionErrorResponse
            || stun.Type == StunMessageType.ChannelBindSuccessResponse
            || stun.Type == StunMessageType.ChannelBindErrorResponse
            || stun.Type == StunMessageType.RefreshSuccessResponse
            || stun.Type == StunMessageType.RefreshErrorResponse)
        {
            if (_transactions.TryGetValue(transactionId, out var tcs))
            {
                tcs.TrySetResult(stun);
            }
            else
            {
                Trace($"ignored stun response from {remoteEndPoint}: no matching transaction type={stun.Type} id={transactionId}");
            }
        }
        else if (stun.Type == StunMessageType.BindingRequest)
        {
            Trace($"received binding request from {remoteEndPoint} id={transactionId}");
            HandleBindingRequestAsync(transport, remoteEndPoint, stun).FireAndForget();
        }
    }

    private async Task HandleBindingRequestAsync(IceUdpTransport transport, IPEndPoint remoteEndPoint, StunMessage request)
    {
        if (request.RawBytes == null)
        {
            Trace($"ignored binding request from {remoteEndPoint}: missing raw bytes");
            return;
        }

        if (request.Attributes.Any(a => a.Type == StunAttributeType.Fingerprint)
            && !StunSecurity.ValidateFingerprint(request.RawBytes))
        {
            Trace($"ignored binding request from {remoteEndPoint}: invalid fingerprint");
            return;
        }

        if (string.IsNullOrEmpty(LocalPassword)
            || !StunSecurity.ValidateMessageIntegrity(request.RawBytes, System.Text.Encoding.UTF8.GetBytes(LocalPassword)))
        {
            Trace($"ignored binding request from {remoteEndPoint}: invalid message integrity");
            return;
        }

        var userAttr = request.Attributes.FirstOrDefault(a => a.Type == StunAttributeType.Username);
        if (userAttr == null || System.Text.Encoding.UTF8.GetString(userAttr.Value) != $"{LocalUfrag}:{_remoteUfrag}")
        {
            Trace($"ignored binding request from {remoteEndPoint}: username mismatch");
            return;
        }

        Trace($"binding request from {remoteEndPoint} use-candidate={request.Attributes.Any(a => a.Type == StunAttributeType.UseCandidate)}");

        if (await HandleRoleConflictAsync(transport, remoteEndPoint, request).ConfigureAwait(false))
        {
            return;
        }

        var localCandidate = GetLocalCandidateForTransport(transport);
        var remoteCandidate = GetOrCreateRemoteCandidate(remoteEndPoint, request);

        if (!IsControlling
            && request.Attributes.Any(a => a.Type == StunAttributeType.UseCandidate)
            && localCandidate != null
            && remoteCandidate != null)
        {
            SelectPair(transport, localCandidate, remoteCandidate, nominated: true);
        }
        else if (IsControlling
            && localCandidate != null
            && remoteCandidate != null
            && !IsSelectedPair(localCandidate, remoteCandidate))
        {
            Task.Run(() => TryNominateDiscoveredPairAsync(transport, localCandidate, remoteCandidate), _cts.Token).FireAndForget();
        }

        var response = new StunMessage
        {
            Type = StunMessageType.BindingSuccessResponse,
            TransactionId = request.TransactionId
        };
        response.Attributes.Add(StunAttribute.CreateXorMappedAddress(remoteEndPoint, request.TransactionId));
        response.Attributes.Add(new StunAttribute { Type = StunAttributeType.MessageIntegrity });
        response.Attributes.Add(new StunAttribute { Type = StunAttributeType.Fingerprint });

        byte[] buffer = new byte[response.GetSerializedLength()];
        response.Serialize(buffer, System.Text.Encoding.UTF8.GetBytes(LocalPassword));
        await transport.SendAsync(buffer, remoteEndPoint);
    }

    public void AddRemoteCandidate(IceCandidate candidate) => AddRemoteCandidate(candidate, kickConnect: true);

    internal void AddRemoteCandidate(IceCandidate candidate, bool kickConnect)
    {
        if (!candidate.Protocol.Equals("udp", StringComparison.OrdinalIgnoreCase))
        {
            Trace($"ignored unsupported remote candidate protocol={candidate.Protocol} candidate={candidate}");
            return;
        }

        lock (_candidateLock)
        {
            if (_remoteCandidates.Any(existing => AreSameCandidate(existing, candidate)))
            {
                return;
            }

            _remoteCandidates.Add(candidate);
        }
        Trace($"remote candidate {candidate}");
        if (kickConnect)
        {
            KickConnectIfReady();
        }
    }

    internal void KickConnectIfReady()
    {
        if (!string.IsNullOrEmpty(_remoteUfrag)
            && !string.IsNullOrEmpty(_remotePassword)
            && HasCandidatePair()
            && (State == IceState.New
                || State == IceState.Complete
                || State == IceState.Checking
                || State == IceState.Connected
                || State == IceState.Failed)
            && Interlocked.CompareExchange(ref _connectKickPending, 1, 0) == 0)
        {
            // Coalesce burst candidate additions (e.g. SDP parsing adds several
            // a=candidate lines back-to-back) into a single ConnectAsync run.
            // Without this, each call serializes on _connectGate and each one
            // burns its own STUN retransmission budget, so lower-priority srflx
            // pairs never get checked before the outer connect timeout fires.
            RunConnectKickAsync().FireAndForget();
        }
    }

    private async Task RunConnectKickAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), _timeProvider, _cts.Token).ConfigureAwait(false);
            if (Volatile.Read(ref _connectKickPending) == 0)
            {
                return;
            }

            await ConnectAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when a deferred connect kick is superseded or the agent is disposed.
        }
    }

    private bool HasCandidatePair()
    {
        lock (_candidateLock)
        {
            return _localCandidates.Count > 0 && _remoteCandidates.Count > 0;
        }
    }

    public void SetRemoteCredentials(string ufrag, string password)
    {
        bool isRestart = !string.IsNullOrEmpty(_remoteUfrag)
            && (!string.Equals(_remoteUfrag, ufrag, StringComparison.Ordinal)
                || !string.Equals(_remotePassword, password, StringComparison.Ordinal));

        _remoteUfrag = ufrag;
        _remotePassword = password;

        if (isRestart)
        {
            lock (_candidateLock) { _remoteCandidates.Clear(); }
            ResetSelectedPair();
            State = IceState.New;
        }
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_remoteUfrag) || string.IsNullOrEmpty(_remotePassword))
        {
            throw new InvalidOperationException("Remote credentials not set");
        }

        if (_logger?.IsEnabled(LogLevel.Information) == true)
        {
            _logger.LogInformation("IceAgent.ConnectAsync entered (IsControlling={IsControlling})", IsControlling);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var connectToken = linkedCts.Token;

        await _connectGate.WaitAsync(connectToken).ConfigureAwait(false);
        try
        {
            connectToken.ThrowIfCancellationRequested();
            // Clear the coalescing flag now that we hold the gate and are about
            // to snapshot candidates. Any AddRemoteCandidate that wins the CAS
            // after this point will schedule a follow-up ConnectAsync which will
            // run as soon as we release the gate.
            Interlocked.Exchange(ref _connectKickPending, 0);
            State = IceState.Checking;
            _checklist.Clear();
            List<IceCandidate> localSnapshot, remoteSnapshot;
            lock (_candidateLock)
            {
                localSnapshot = [.. _localCandidates];
                remoteSnapshot = [.. _remoteCandidates];
            }
            if (_logger?.IsEnabled(LogLevel.Information) == true)
            {
                _logger.LogInformation("IceAgent pair-check starting: {LocalCount} local x {RemoteCount} remote candidates", localSnapshot.Count, remoteSnapshot.Count);
            }
            foreach (var local in localSnapshot)
            {
                foreach (var remote in remoteSnapshot)
                {
                    if (!AreCandidateAddressFamiliesCompatible(local, remote))
                    {
                        Trace($"skipping incompatible pair local={local.Address}:{local.Port} remote={remote.Address}:{remote.Port}");
                        continue;
                    }

                    _checklist.Add(new IceCandidatePair(local, remote));
                }
            }

            _checklist.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            // Dispatch all pair checks concurrently. Sequential checks burn the full
            // STUN retransmission budget (~15s) on each unreachable pair, so unreachable
            // high-priority host-host pairs would prevent lower-priority srflx-srflx pairs
            // from ever being tried inside a realistic connect timeout.
            using var pairCts = CancellationTokenSource.CreateLinkedTokenSource(connectToken);
            var pending = _checklist
                .Where(p => p.State != IceState.Connected)
                .Select(p => TryPairCheckAsync(p, pairCts.Token))
                .ToList();

            (IceUdpTransport Transport, IceCandidatePair Pair, IPEndPoint RemoteEp)? winner = null;
            var successful = new List<(IceUdpTransport Transport, IceCandidatePair Pair, IPEndPoint RemoteEp)>();
            DateTimeOffset? firstSuccessAt = null;
            var nominationGrace = TimeSpan.FromMilliseconds(200);
            while (pending.Count > 0)
            {
                Task<(IceUdpTransport Transport, IceCandidatePair Pair, IPEndPoint RemoteEp)?> completed;
                if (successful.Count > 0)
                {
                    var best = successful.MaxBy(s => s.Pair.Priority);
                    bool higherPriorityStillPending = pending.Any(t =>
                        !t.IsCompleted
                        && _checklist.Any(pair =>
                            pair.State == IceState.Checking
                            && pair.Priority > best.Pair.Priority));
                    TimeSpan elapsedSinceFirstSuccess = firstSuccessAt.HasValue
                        ? _timeProvider.GetUtcNow() - firstSuccessAt.Value
                        : TimeSpan.Zero;

                    if (!higherPriorityStillPending || elapsedSinceFirstSuccess >= nominationGrace)
                    {
                        winner = best;
                        break;
                    }

                    var pendingCompletion = Task.WhenAny(pending);
                    var graceDelay = Task.Delay(nominationGrace - elapsedSinceFirstSuccess, _timeProvider, cancellationToken);
                    var next = await Task.WhenAny(pendingCompletion, graceDelay).ConfigureAwait(false);
                    if (next == graceDelay)
                    {
                        winner = best;
                        break;
                    }

                    completed = await pendingCompletion.ConfigureAwait(false);
                }
                else
                {
                    completed = await Task.WhenAny(pending).ConfigureAwait(false);
                }

                pending.Remove(completed);
                try
                {
                    var result = await completed.ConfigureAwait(false);
                    if (result.HasValue)
                    {
                        successful.Add(result.Value);
                        firstSuccessAt ??= _timeProvider.GetUtcNow();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected after a winner is selected or connect is canceled.
                }
            }

            if (!winner.HasValue && successful.Count > 0)
            {
                winner = successful.MaxBy(s => s.Pair.Priority);
            }

            await pairCts.CancelAsync().ConfigureAwait(false);
            foreach (var t in pending)
            {
                _ = t.ContinueWith(
                    static completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            if (winner.HasValue)
            {
                var (transport, pair, remoteEp) = winner.Value;
                if (IsControlling)
                {
                    var nomination = await SendStunBindingRequestAsync(transport, remoteEp, useCandidate: true, cancellationToken: connectToken).ConfigureAwait(false);
                    if (nomination?.Type == StunMessageType.BindingSuccessResponse)
                    {
                        pair.State = IceState.Connected;
                        SelectPair(transport, pair.Local, pair.Remote, nominated: true);
                        return true;
                    }
                    Trace($"nomination failed local={pair.Local.Address}:{pair.Local.Port} remote={remoteEp} response={nomination?.Type.ToString() ?? "null"}");
                }
                else
                {
                    pair.State = IceState.Connected;
                    SelectPair(transport, pair.Local, pair.Remote, nominated: false);
                    return true;
                }
            }

            if (State != IceState.Connected && State != IceState.Completed)
            {
                State = IceState.Failed;
            }

            bool success = State == IceState.Connected || State == IceState.Completed;
            if (_logger?.IsEnabled(LogLevel.Information) == true)
            {
                _logger.LogInformation("IceAgent.ConnectAsync exiting (state={State}, success={Success})", State, success);
            }
            return success;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task<(IceUdpTransport Transport, IceCandidatePair Pair, IPEndPoint RemoteEp)?> TryPairCheckAsync(IceCandidatePair pair, CancellationToken cancellationToken)
    {
        try
        {
            pair.State = IceState.Checking;
            var transport = GetTransportForLocalCandidate(pair.Local);
            if (transport == null)
            {
                return null;
            }

            var remoteEp = await ResolveRemoteEndPointAsync(pair.Remote, cancellationToken).ConfigureAwait(false);
            if (remoteEp == null)
            {
                Trace($"pair unresolved local={pair.Local} remote={pair.Remote}");
                pair.State = IceState.Failed;
                return null;
            }

            if (_logger?.IsEnabled(LogLevel.Information) == true)
            {
                _logger.LogInformation("IceAgent checking pair local={Local} remote={Remote}", $"{pair.Local.Address}:{pair.Local.Port}", remoteEp);
            }
            var response = await SendStunBindingRequestAsync(transport, remoteEp, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (IsRoleConflictResponse(response))
            {
                pair.State = IceState.New;
                response = await SendStunBindingRequestAsync(transport, remoteEp, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (response?.Type == StunMessageType.BindingSuccessResponse)
            {
                return (transport, pair, remoteEp);
            }

            if (_logger?.IsEnabled(LogLevel.Information) == true)
            {
                _logger.LogInformation("IceAgent pair failed local={Local} remote={Remote} response={Response}", $"{pair.Local.Address}:{pair.Local.Port}", remoteEp, response?.Type.ToString() ?? "null");
            }
            pair.State = IceState.Failed;
            return null;
        }
        catch (OperationCanceledException)
        {
            pair.State = IceState.Failed;
            throw;
        }
        catch (Exception ex)
        {
            // One unreachable pair (e.g. IPv6 link-local to a global, or a host that
            // the OS routing table rejects with ENETUNREACH) must not abort the whole
            // connectivity check. Log, mark the pair as failed, and let the other
            // parallel checks proceed.
            if (_logger?.IsEnabled(LogLevel.Information) == true)
            {
                _logger.LogInformation(ex, "IceAgent pair check threw local={Local} remote={Remote}", $"{pair.Local.Address}:{pair.Local.Port}", pair.Remote);
            }
            pair.State = IceState.Failed;
            return null;
        }
    }

    public async Task SendDataAsync(byte[] data)
    {
        await SendDataAsync(data.AsMemory()).ConfigureAwait(false);
    }

    internal async Task SendDataAsync(ReadOnlyMemory<byte> data)
    {
        if (_selectedTransport == null || _selectedRemoteCandidate == null || _selectedLocalCandidate == null)
        {
            return;
        }

        var remoteEp = await ResolveRemoteEndPointAsync(_selectedRemoteCandidate, _cts.Token).ConfigureAwait(false);
        if (remoteEp == null)
        {
            return;
        }

        if (_selectedLocalCandidate.Type == IceCandidateType.Relay)
        {
            var allocation = GetTurnAllocation(_selectedLocalCandidate)
                ?? throw new InvalidOperationException("TURN allocation not found for selected relay candidate.");

            await allocation.SendToPeerAsync(data.ToArray(), remoteEp, _cts.Token).ConfigureAwait(false);
            return;
        }

        await _selectedTransport.SendAsync(data, remoteEp).ConfigureAwait(false);
    }

    private void SelectPair(IceUdpTransport transport, IceCandidate local, IceCandidate remote, bool nominated)
    {
        _selectedLocalCandidate = local;
        _selectedTransport = transport;
        _selectedRemoteCandidate = remote;
        Trace($"selected pair local={local.Address}:{local.Port} remote={remote.Address}:{remote.Port} nominated={nominated}");
        State = nominated ? IceState.Completed : IceState.Connected;
        StartConsentFreshnessLoop();
    }

    private async Task TryNominateDiscoveredPairAsync(IceUdpTransport transport, IceCandidate local, IceCandidate remote)
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        await _connectGate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            // Don't re-nominate if we already have a selected pair (ICE connected/completed).
            if (!IsControlling || _selectedLocalCandidate != null || IsSelectedPair(local, remote))
            {
                return;
            }

            var remoteEp = await ResolveRemoteEndPointAsync(remote, _cts.Token).ConfigureAwait(false);
            if (remoteEp == null)
            {
                Trace($"triggered nomination skipped for unresolved remote={remote.Address}:{remote.Port}");
                return;
            }

            Trace($"triggered nomination local={local.Address}:{local.Port} remote={remoteEp}");
            var response = await SendStunBindingRequestAsync(transport, remoteEp, cancellationToken: _cts.Token).ConfigureAwait(false);
            if (response?.Type != StunMessageType.BindingSuccessResponse)
            {
                Trace($"triggered check failed remote={remoteEp} response={response?.Type.ToString() ?? "null"}");
                return;
            }

            var nomination = await SendStunBindingRequestAsync(transport, remoteEp, useCandidate: true, cancellationToken: _cts.Token).ConfigureAwait(false);
            if (nomination?.Type == StunMessageType.BindingSuccessResponse)
            {
                SelectPair(transport, local, remote, nominated: true);
            }
            else
            {
                Trace($"triggered nomination failed remote={remoteEp} response={nomination?.Type.ToString() ?? "null"}");
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>
    /// Sends a bare RFC 5389 Binding request — no USERNAME, no ICE role attributes,
    /// no MESSAGE-INTEGRITY. Used for srflx candidate gathering against a STUN server,
    /// where no ICE short-term credential has been negotiated yet.
    /// </summary>
    private async Task<StunMessage?> SendPlainStunBindingRequestAsync(IceUdpTransport transport, IPEndPoint remoteEp, CancellationToken cancellationToken = default)
    {
        var request = new StunMessage
        {
            Type = StunMessageType.BindingRequest,
            TransactionId = Guid.NewGuid().ToByteArray().AsSpan(0, 12).ToArray()
        };

        request.Attributes.Add(new StunAttribute { Type = StunAttributeType.Fingerprint });

        return await SendTransactionAsync(transport, remoteEp, request, key: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StunMessage?> SendStunBindingRequestAsync(IceUdpTransport transport, IPEndPoint remoteEp, bool useCandidate = false, CancellationToken cancellationToken = default)
    {
        var request = new StunMessage
        {
            Type = StunMessageType.BindingRequest,
            TransactionId = Guid.NewGuid().ToByteArray().AsSpan(0, 12).ToArray()
        };

        request.Attributes.Add(new StunAttribute { Type = StunAttributeType.Username, Value = System.Text.Encoding.UTF8.GetBytes($"{_remoteUfrag}:{LocalUfrag}") });
        byte[] priorityBytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(priorityBytes, 100);
        request.Attributes.Add(new StunAttribute { Type = StunAttributeType.Priority, Value = priorityBytes });
        byte[] tieBreakerBytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(tieBreakerBytes, _tieBreaker);
        request.Attributes.Add(new StunAttribute
        {
            Type = IsControlling ? StunAttributeType.IceControlling : StunAttributeType.IceControlled,
            Value = tieBreakerBytes
        });

        if (useCandidate)
        {
            request.Attributes.Add(new StunAttribute { Type = StunAttributeType.UseCandidate, Value = [] });
        }

        request.Attributes.Add(new StunAttribute { Type = StunAttributeType.MessageIntegrity });
        request.Attributes.Add(new StunAttribute { Type = StunAttributeType.Fingerprint });

        var response = await SendTransactionAsync(transport, remoteEp, request, System.Text.Encoding.UTF8.GetBytes(_remotePassword!), cancellationToken).ConfigureAwait(false);
        if (IsRoleConflictResponse(response))
        {
            IsControlling = !IsControlling;
        }

        return response;
    }

    private bool ValidateStunMessage(StunMessage stun, out string failure)
    {
        failure = string.Empty;

        if (stun.RawBytes == null)
        {
            failure = "missing raw bytes";
            return false;
        }

        bool hasFingerprint = stun.Attributes.Any(a => a.Type == StunAttributeType.Fingerprint);
        if (hasFingerprint && !StunSecurity.ValidateFingerprint(stun.RawBytes))
        {
            failure = "invalid fingerprint";
            return false;
        }

        bool hasIntegrity = stun.Attributes.Any(a => a.Type == StunAttributeType.MessageIntegrity);
        if (!hasIntegrity)
        {
            if (stun.Type != StunMessageType.BindingRequest)
            {
                return true;
            }

            failure = "missing message integrity";
            return false;
        }

        bool valid = stun.Type switch
        {
            StunMessageType.BindingRequest => !string.IsNullOrEmpty(LocalPassword)
                && StunSecurity.ValidateMessageIntegrity(stun.RawBytes, System.Text.Encoding.UTF8.GetBytes(LocalPassword)),
            StunMessageType.BindingSuccessResponse => !string.IsNullOrEmpty(_remotePassword)
                && StunSecurity.ValidateMessageIntegrity(stun.RawBytes, System.Text.Encoding.UTF8.GetBytes(_remotePassword)),
            StunMessageType.BindingErrorResponse => !string.IsNullOrEmpty(_remotePassword)
                && StunSecurity.ValidateMessageIntegrity(stun.RawBytes, System.Text.Encoding.UTF8.GetBytes(_remotePassword)),
            _ => true
        };

        if (valid)
        {
            return true;
        }

        failure = stun.Type switch
        {
            StunMessageType.BindingRequest when string.IsNullOrEmpty(LocalPassword) => "missing local ICE password",
            StunMessageType.BindingRequest when !string.IsNullOrEmpty(_remotePassword)
                && StunSecurity.ValidateMessageIntegrity(stun.RawBytes, System.Text.Encoding.UTF8.GetBytes(_remotePassword)) =>
                "invalid message integrity with local ICE password; valid with remote ICE password",
            StunMessageType.BindingSuccessResponse or StunMessageType.BindingErrorResponse when string.IsNullOrEmpty(_remotePassword) =>
                "missing remote ICE password",
            StunMessageType.BindingSuccessResponse or StunMessageType.BindingErrorResponse when !string.IsNullOrEmpty(LocalPassword)
                && StunSecurity.ValidateMessageIntegrity(stun.RawBytes, System.Text.Encoding.UTF8.GetBytes(LocalPassword)) =>
                "invalid message integrity with remote ICE password; valid with local ICE password",
            _ => "invalid message integrity"
        };

        return false;
    }

    private static string FormatStunAttributes(StunMessage stun)
    {
        return string.Join(",", stun.Attributes.Select(attr =>
        {
            if (attr.Type == StunAttributeType.Username)
            {
                return $"{attr.Type}={System.Text.Encoding.UTF8.GetString(attr.Value)}";
            }

            return attr.Type.ToString();
        }));
    }

    internal bool TryResolveRoleConflict(StunMessage request)
    {
        ulong? remoteControlling = ReadUInt64Attribute(request, StunAttributeType.IceControlling);
        if (IsControlling && remoteControlling.HasValue)
        {
            if (remoteControlling.Value >= _tieBreaker)
            {
                IsControlling = false;
                return false;
            }

            return true;
        }

        ulong? remoteControlled = ReadUInt64Attribute(request, StunAttributeType.IceControlled);
        if (!IsControlling && remoteControlled.HasValue)
        {
            if (remoteControlled.Value < _tieBreaker)
            {
                IsControlling = true;
                return false;
            }

            return true;
        }

        return false;
    }

    internal IceCandidate? GetOrCreateRemoteCandidate(IPEndPoint remoteEndPoint, StunMessage request)
    {
        lock (_candidateLock)
        {
            var existing = _remoteCandidates.FirstOrDefault(candidate => candidate.Address == remoteEndPoint.Address.ToString() && candidate.Port == remoteEndPoint.Port);
            if (existing != null)
            {
                return existing;
            }
        }

        var priorityAttr = request.Attributes.FirstOrDefault(a => a.Type == StunAttributeType.Priority);
        if (priorityAttr == null || priorityAttr.Value.Length != sizeof(uint))
        {
            return null;
        }

        uint priority = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(priorityAttr.Value);
        var prflx = new IceCandidate
        {
            Foundation = "prflx",
            Component = 1,
            Protocol = "udp",
            Priority = priority,
            Address = remoteEndPoint.Address.ToString(),
            Port = remoteEndPoint.Port,
            Type = IceCandidateType.Prflx
        };
        lock (_candidateLock) { _remoteCandidates.Add(prflx); }
        return prflx;
    }

    private async Task<bool> HandleRoleConflictAsync(IceUdpTransport transport, IPEndPoint remoteEndPoint, StunMessage request)
    {
        if (!TryResolveRoleConflict(request))
        {
            return false;
        }

        var response = new StunMessage
        {
            Type = StunMessageType.BindingErrorResponse,
            TransactionId = request.TransactionId
        };
        response.Attributes.Add(CreateErrorCodeAttribute(487, "Role Conflict"));
        response.Attributes.Add(new StunAttribute { Type = StunAttributeType.MessageIntegrity });
        response.Attributes.Add(new StunAttribute { Type = StunAttributeType.Fingerprint });

        byte[] buffer = new byte[response.GetSerializedLength()];
        response.Serialize(buffer, System.Text.Encoding.UTF8.GetBytes(LocalPassword!));
        await transport.SendAsync(buffer, remoteEndPoint).ConfigureAwait(false);
        return true;
    }

    private IceCandidate? GetLocalCandidateForTransport(IceUdpTransport transport)
    {
        lock (_candidateLock)
        {
            return _localCandidates.FirstOrDefault(candidate =>
                candidate.Component == 1
                && candidate.Protocol.Equals("udp", StringComparison.OrdinalIgnoreCase)
                && candidate.Port == transport.LocalEndPoint.Port);
        }
    }

    private IceUdpTransport? GetTransportForLocalCandidate(IceCandidate localCandidate)
    {
        if (localCandidate.Type == IceCandidateType.Relay && localCandidate.RelatedPort.HasValue)
        {
            return _transports.FirstOrDefault(transport => transport.LocalEndPoint.Port == localCandidate.RelatedPort.Value);
        }

        return _transports.FirstOrDefault(transport => transport.LocalEndPoint.Port == localCandidate.Port);
    }

    private static bool AreCandidateAddressFamiliesCompatible(IceCandidate local, IceCandidate remote)
    {
        if (!IPAddress.TryParse(local.Address, out var localAddress))
        {
            return true;
        }

        if (!IPAddress.TryParse(remote.Address, out var remoteAddress))
        {
            return localAddress.AddressFamily == AddressFamily.InterNetwork;
        }

        return localAddress.AddressFamily == remoteAddress.AddressFamily;
    }

    private TurnAllocation? GetTurnAllocation(IceCandidate localCandidate)
    {
        return _turnAllocations.TryGetValue($"{localCandidate.Address}:{localCandidate.Port}", out var allocation)
            ? allocation
            : null;
    }

    private bool TryTranslateTurnPacket(UdpPacket packet, out UdpPacket translated)
    {
        foreach (var allocation in _turnAllocations.Values)
        {
            if (allocation.TryTranslateIncoming(packet, out translated))
            {
                return true;
            }
        }

        translated = default;
        return false;
    }

    private static bool AreSameCandidate(IceCandidate left, IceCandidate right)
    {
        return left.Foundation == right.Foundation
            && left.Component == right.Component
            && left.Protocol.Equals(right.Protocol, StringComparison.OrdinalIgnoreCase)
            && left.Address == right.Address
            && left.Port == right.Port
            && left.Type == right.Type
            && left.RelatedAddress == right.RelatedAddress
            && left.RelatedPort == right.RelatedPort;
    }

    private bool IsSelectedPair(IceCandidate local, IceCandidate remote)
    {
        return _selectedLocalCandidate != null
            && _selectedRemoteCandidate != null
            && AreSameCandidate(_selectedLocalCandidate, local)
            && AreSameCandidate(_selectedRemoteCandidate, remote);
    }

    private static ulong? ReadUInt64Attribute(StunMessage message, StunAttributeType type)
    {
        var attribute = message.Attributes.FirstOrDefault(a => a.Type == type);
        if (attribute == null || attribute.Value.Length != sizeof(ulong))
        {
            return null;
        }

        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(attribute.Value);
    }

    private static bool IsRoleConflictResponse(StunMessage? response)
    {
        return response?.Type == StunMessageType.BindingErrorResponse
            && response.Attributes.FirstOrDefault(a => a.Type == StunAttributeType.ErrorCode)?.GetErrorCode()?.Code == 487;
    }

    private static StunAttribute CreateErrorCodeAttribute(int code, string reason)
    {
        byte[] reasonBytes = System.Text.Encoding.UTF8.GetBytes(reason);
        byte[] value = new byte[4 + reasonBytes.Length];
        value[2] = (byte)(code / 100);
        value[3] = (byte)(code % 100);
        reasonBytes.CopyTo(value, 4);
        return new StunAttribute { Type = StunAttributeType.ErrorCode, Value = value };
    }

    private void ResetSelectedPair()
    {
        _selectedLocalCandidate = null;
        _selectedTransport = null;
        _selectedRemoteCandidate = null;
        StopConsentFreshnessLoop();
    }

    private void StartConsentFreshnessLoop()
    {
        StopConsentFreshnessLoop();
        _consentCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        Task.Run(() => RunConsentFreshnessAsync(_consentCts.Token)).FireAndForget();
    }

    private void StopConsentFreshnessLoop()
    {
        if (_consentCts == null)
        {
            return;
        }

        _consentCts.Cancel();
        _consentCts.Dispose();
        _consentCts = null;
    }

    private async Task RunConsentFreshnessAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset lastSuccess = _timeProvider.GetUtcNow();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(ConsentInterval, _timeProvider, cancellationToken).ConfigureAwait(false);

                if (_selectedTransport == null || _selectedRemoteCandidate == null)
                {
                    return;
                }

                var remoteEp = await ResolveRemoteEndPointAsync(_selectedRemoteCandidate, cancellationToken).ConfigureAwait(false);
                bool success = false;
                if (remoteEp != null)
                {
                    var response = await SendStunBindingRequestAsync(_selectedTransport, remoteEp, cancellationToken: cancellationToken).ConfigureAwait(false);
                    success = response?.Type == StunMessageType.BindingSuccessResponse || IsRoleConflictResponse(response);
                }

                if (success)
                {
                    lastSuccess = _timeProvider.GetUtcNow();
                    continue;
                }

                if (_timeProvider.GetUtcNow() - lastSuccess >= ConsentTimeout)
                {
                    ResetSelectedPair();
                    State = IceState.Disconnected;
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when consent freshness is stopped.
        }
    }

    private static string GenerateRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        var result = new char[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = chars[bytes[i] % chars.Length];
        }

        return new string(result);
    }

    internal async Task<IPEndPoint?> ResolveRemoteEndPointAsync(IceCandidate candidate, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(candidate.Address, out var address))
        {
            return new IPEndPoint(address, candidate.Port);
        }

        // Resolve hostname-based candidates (notably Firefox's `*.local` mDNS candidates) at most
        // once per address. Windows mDNS resolution can take many seconds per call, and calling
        // it on every outbound packet stalls the DTLS handshake — Firefox retransmits faster
        // than we can respond. Caching collapses every subsequent send to an O(1) dictionary hit.
        var resolveTask = _hostnameResolutionCache.GetOrAdd(candidate.Address, addr => ResolveHostnameAsync(addr, cancellationToken));
        IPAddress? resolved;
        try
        {
            resolved = await resolveTask.ConfigureAwait(false);
        }
        catch
        {
            _hostnameResolutionCache.TryRemove(candidate.Address, out _);
            return null;
        }
        if (resolved == null)
        {
            return null;
        }
        return new IPEndPoint(resolved, candidate.Port);
    }

    private async Task<IPAddress?> ResolveHostnameAsync(string hostname, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostname, cancellationToken).ConfigureAwait(false);
            var resolved = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault();
            Trace($"resolved {hostname} -> {resolved}");
            return resolved;
        }
        catch (SocketException)
        {
            Trace($"failed to resolve {hostname}");
            return null;
        }
    }

    private void Trace(string message)
    {
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            _logger.LogDebug("{Message}", message);
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!disposing)
        {
            return;
        }

        _cts.Cancel();
        StopConsentFreshnessLoop();
        foreach (var allocation in _turnAllocations.Values)
        {
            allocation.Dispose();
        }
        foreach (var transport in _transports)
        {
            transport.Dispose();
        }

        _connectGate.Dispose();
        _cts.Dispose();
    }
}
