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
    private static readonly bool TraceEnabled = string.Equals(
        Environment.GetEnvironmentVariable("RTCFORGE_TRACE_ICE"),
        "1",
        StringComparison.Ordinal);
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
    private IceUdpTransport? _selectedTransport;
    private IceCandidate? _selectedLocalCandidate;
    private IceCandidate? _selectedRemoteCandidate;
    private RTCIceTransportPolicy _transportPolicy = RTCIceTransportPolicy.All;
    private bool _gatheringStarted;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
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
                        var transport = new IceUdpTransport(new IPEndPoint(bindAddress, 0));
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

    private async Task GatherSrflxCandidatesAsync(IceUdpTransport transport, IceCandidate hostCandidate)
    {
        foreach (var server in _iceServers)
        {
            foreach (var url in server.Urls)
            {
                if (url.StartsWith("stun:"))
                {
                    try
                    {
                        var uri = new Uri(url.Replace("stun:", "stun://"));
                        var stunEp = new IPEndPoint(Dns.GetHostAddresses(uri.Host)[0], uri.Port > 0 ? uri.Port : 3478);

                        var response = await SendStunBindingRequestAsync(transport, stunEp);
                        if (response != null)
                        {
                            var addrAttr = response.Attributes.Find(a => a.Type == StunAttributeType.XorMappedAddress);
                            var srflxAddr = addrAttr?.GetXorMappedAddress(response.TransactionId);

                            if (srflxAddr != null)
                            {
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
                                if (_transportPolicy != RTCIceTransportPolicy.Relay)
                                {
                                    lock (_candidateLock) { _localCandidates.Add(srflxCandidate); }
                                    OnLocalCandidate?.Invoke(this, srflxCandidate);
                                    Trace($"local candidate {srflxCandidate}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace($"srflx gathering failed for {url}: {ex.Message}");
                    }
                }
            }
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
                        var uri = new Uri(url.Replace("turn:", "stun://"));
                        var turnEp = new IPEndPoint(Dns.GetHostAddresses(uri.Host)[0], uri.Port > 0 ? uri.Port : 3478);

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
                await transport.SendAsync(buffer, remoteEp);

                var delayTask = Task.Delay(TimeSpan.FromMilliseconds(rtoMs), _timeProvider, cancellationToken);
                var resultTask = await Task.WhenAny(tcs.Task, delayTask);
                if (resultTask == tcs.Task)
                {
                    return await tcs.Task;
                }
                cancellationToken.ThrowIfCancellationRequested();

                rtoMs = Math.Min(rtoMs * 2, 8000);
            }

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
        catch (OperationCanceledException) { }
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
        }
        else if (firstByte >= 20 && firstByte <= 63)
        {
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
    }

    private void HandleStunMessage(IceUdpTransport transport, IPEndPoint remoteEndPoint, StunMessage stun)
    {
        if (!ValidateStunMessage(stun))
        {
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
        }
        else if (stun.Type == StunMessageType.BindingRequest)
        {
            HandleBindingRequestAsync(transport, remoteEndPoint, stun).FireAndForget();
        }
    }

    private async Task HandleBindingRequestAsync(IceUdpTransport transport, IPEndPoint remoteEndPoint, StunMessage request)
    {
        if (request.RawBytes == null
            || !StunSecurity.ValidateFingerprint(request.RawBytes)
            || string.IsNullOrEmpty(LocalPassword)
            || !StunSecurity.ValidateMessageIntegrity(request.RawBytes, System.Text.Encoding.UTF8.GetBytes(LocalPassword)))
        {
            return;
        }

        var userAttr = request.Attributes.FirstOrDefault(a => a.Type == StunAttributeType.Username);
        if (userAttr == null || System.Text.Encoding.UTF8.GetString(userAttr.Value) != $"{LocalUfrag}:{_remoteUfrag}")
        {
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

    public void AddRemoteCandidate(IceCandidate candidate)
    {
        lock (_candidateLock)
        {
            if (_remoteCandidates.Any(existing => AreSameCandidate(existing, candidate)))
            {
                return;
            }

            _remoteCandidates.Add(candidate);
        }
        Trace($"remote candidate {candidate}");
        if (!string.IsNullOrEmpty(_remoteUfrag)
            && !string.IsNullOrEmpty(_remotePassword)
            && (State == IceState.New
                || State == IceState.Complete
                || State == IceState.Checking
                || State == IceState.Connected
                || State == IceState.Failed))
        {
            ConnectAsync().FireAndForget();
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

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var connectToken = linkedCts.Token;

        await _connectGate.WaitAsync(connectToken).ConfigureAwait(false);
        try
        {
            connectToken.ThrowIfCancellationRequested();
            State = IceState.Checking;
            _checklist.Clear();
            List<IceCandidate> localSnapshot, remoteSnapshot;
            lock (_candidateLock)
            {
                localSnapshot = [.. _localCandidates];
                remoteSnapshot = [.. _remoteCandidates];
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

            foreach (var pair in _checklist)
            {
                if (pair.State == IceState.Connected)
                {
                    continue;
                }

                pair.State = IceState.Checking;
                var transport = GetTransportForLocalCandidate(pair.Local);
                if (transport == null)
                {
                    continue;
                }

                var remoteEp = await ResolveRemoteEndPointAsync(pair.Remote, connectToken).ConfigureAwait(false);
                if (remoteEp == null)
                {
                    Trace($"pair unresolved local={pair.Local} remote={pair.Remote}");
                    pair.State = IceState.Failed;
                    continue;
                }

                Trace($"checking pair local={pair.Local.Address}:{pair.Local.Port} remote={remoteEp}");
                var response = await SendStunBindingRequestAsync(transport, remoteEp, cancellationToken: connectToken).ConfigureAwait(false);
                if (IsRoleConflictResponse(response))
                {
                    pair.State = IceState.New;
                    response = await SendStunBindingRequestAsync(transport, remoteEp, cancellationToken: connectToken).ConfigureAwait(false);
                }

                if (response?.Type == StunMessageType.BindingSuccessResponse)
                {
                    if (IsControlling)
                    {
                        var nomination = await SendStunBindingRequestAsync(transport, remoteEp, useCandidate: true, cancellationToken: connectToken).ConfigureAwait(false);
                        if (IsRoleConflictResponse(nomination))
                        {
                            pair.State = IceState.New;
                            continue;
                        }

                        if (nomination?.Type == StunMessageType.BindingSuccessResponse)
                        {
                            pair.State = IceState.Connected;
                            SelectPair(transport, pair.Local, pair.Remote, nominated: true);
                            return true;
                        }
                    }
                    else
                    {
                        pair.State = IceState.Connected;
                        SelectPair(transport, pair.Local, pair.Remote, nominated: false);
                        return true;
                    }
                }

                Trace($"pair failed local={pair.Local.Address}:{pair.Local.Port} remote={remoteEp} response={response?.Type.ToString() ?? "null"}");
                pair.State = IceState.Failed;
            }

            if (State != IceState.Connected && State != IceState.Completed)
            {
                State = IceState.Failed;
            }

            return State == IceState.Connected || State == IceState.Completed;
        }
        finally
        {
            _connectGate.Release();
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
            if (!IsControlling || IsSelectedPair(local, remote))
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
            State = IceState.Checking;
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

    private bool ValidateStunMessage(StunMessage stun)
    {
        if (stun.RawBytes == null)
        {
            return false;
        }

        bool hasFingerprint = stun.Attributes.Any(a => a.Type == StunAttributeType.Fingerprint);
        if (hasFingerprint && !StunSecurity.ValidateFingerprint(stun.RawBytes))
        {
            return false;
        }

        bool hasIntegrity = stun.Attributes.Any(a => a.Type == StunAttributeType.MessageIntegrity);
        if (!hasIntegrity)
        {
            return stun.Type != StunMessageType.BindingRequest;
        }

        return stun.Type switch
        {
            StunMessageType.BindingRequest => !string.IsNullOrEmpty(LocalPassword)
                && StunSecurity.ValidateMessageIntegrity(stun.RawBytes, System.Text.Encoding.UTF8.GetBytes(LocalPassword)),
            StunMessageType.BindingSuccessResponse => !string.IsNullOrEmpty(_remotePassword)
                && StunSecurity.ValidateMessageIntegrity(stun.RawBytes, System.Text.Encoding.UTF8.GetBytes(_remotePassword)),
            StunMessageType.BindingErrorResponse => !string.IsNullOrEmpty(_remotePassword)
                && StunSecurity.ValidateMessageIntegrity(stun.RawBytes, System.Text.Encoding.UTF8.GetBytes(_remotePassword)),
            _ => true
        };
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

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(candidate.Address, cancellationToken).ConfigureAwait(false);
            var resolved = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault();
            Trace($"resolved {candidate.Address} -> {resolved}");
            return resolved == null ? null : new IPEndPoint(resolved, candidate.Port);
        }
        catch (SocketException)
        {
            Trace($"failed to resolve {candidate.Address}");
            return null;
        }
    }

    private void Trace(string message)
    {
        if (TraceEnabled && _logger?.IsEnabled(LogLevel.Debug) == true)
        {
            _logger.LogDebug("{Message}", message);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
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
        GC.SuppressFinalize(this);
    }
}
