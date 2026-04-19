using System.Buffers.Binary;
using System.Net;
using RtcForge.Stun;

namespace RtcForge.Ice;

internal sealed class TurnAllocation : IDisposable
{
    private const ushort InitialChannelNumber = 0x4000;
    private static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(30);
    private readonly Func<StunMessage, byte[]?, Task<StunMessage?>> _sendRequestAsync;
    private readonly Func<byte[], Task> _sendRawAsync;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, TurnPeerBinding> _bindings = [];
    private readonly CancellationTokenSource _cts = new();
    private ushort _nextChannelNumber = InitialChannelNumber;
    private int _disposed;

    public TurnAllocation(
        IPEndPoint serverEndPoint,
        IPEndPoint relayedEndPoint,
        string username,
        byte[] realm,
        byte[] nonce,
        byte[] integrityKey,
        TimeSpan allocationLifetime,
        Func<StunMessage, byte[]?, Task<StunMessage?>> sendRequestAsync,
        Func<byte[], Task> sendRawAsync,
        TimeProvider? timeProvider = null)
    {
        ServerEndPoint = serverEndPoint;
        RelayedEndPoint = relayedEndPoint;
        Username = username;
        Realm = realm;
        Nonce = nonce;
        IntegrityKey = integrityKey;
        AllocationLifetime = allocationLifetime;
        _sendRequestAsync = sendRequestAsync;
        _sendRawAsync = sendRawAsync;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Task.Run(() => RunRefreshLoopAsync(_cts.Token)).FireAndForget();
    }

    public IPEndPoint ServerEndPoint { get; }
    public IPEndPoint RelayedEndPoint { get; }
    public string Username { get; }
    public byte[] Realm { get; private set; }
    public byte[] Nonce { get; private set; }
    public byte[] IntegrityKey { get; }
    public TimeSpan AllocationLifetime { get; private set; }

    public bool IsServer(IPEndPoint remoteEndPoint)
    {
        return remoteEndPoint.Address.Equals(ServerEndPoint.Address) && remoteEndPoint.Port == ServerEndPoint.Port;
    }

    public async Task SendToPeerAsync(byte[] data, IPEndPoint peerEndPoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var binding = await GetOrCreateBindingAsync(peerEndPoint, cancellationToken).ConfigureAwait(false);
        byte[] packet = CreateChannelDataPacket(binding.ChannelNumber, data);
        await _sendRawAsync(packet).ConfigureAwait(false);
    }

    public async Task RefreshAllocationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = CreateAuthenticatedRequest(StunMessageType.RefreshRequest);
        var response = await SendAuthenticatedAsync(
            request,
            static type => type == StunMessageType.RefreshSuccessResponse,
            static type => type == StunMessageType.RefreshErrorResponse).ConfigureAwait(false);

        int lifetimeSeconds = response.Attributes
            .FirstOrDefault(attribute => attribute.Type == StunAttributeType.Lifetime)
            ?.GetUInt32() switch
        {
            uint seconds when seconds > 0 => (int)seconds,
            _ => (int)AllocationLifetime.TotalSeconds
        };

        AllocationLifetime = TimeSpan.FromSeconds(Math.Max(1, lifetimeSeconds));
    }

    public bool TryTranslateIncoming(UdpPacket packet, out UdpPacket translated)
    {
        if (!IsServer(packet.RemoteEndPoint))
        {
            translated = default;
            return false;
        }

        if (TryParseChannelData(packet.Span, out var channelNumber, out var payload))
        {
            var binding = _bindings.Values.FirstOrDefault(candidate => candidate.ChannelNumber == channelNumber);
            if (binding == null)
            {
                translated = default;
                return false;
            }

            translated = new UdpPacket
            {
                Array = payload,
                Length = payload.Length,
                RemoteEndPoint = binding.PeerEndPoint
            };
            return true;
        }

        if (!StunMessage.TryParse(packet.Span, out var message) || message.Type != StunMessageType.DataIndication)
        {
            translated = default;
            return false;
        }

        var peerAddress = message.Attributes
            .FirstOrDefault(attribute => attribute.Type == StunAttributeType.XorPeerAddress)
            ?.GetXorMappedAddress(message.TransactionId);
        var data = message.Attributes.FirstOrDefault(attribute => attribute.Type == StunAttributeType.Data)?.Value;
        if (peerAddress == null || data == null)
        {
            translated = default;
            return false;
        }

        translated = new UdpPacket
        {
            Array = data,
            Length = data.Length,
            RemoteEndPoint = peerAddress
        };
        return true;
    }

    private async Task<TurnPeerBinding> GetOrCreateBindingAsync(IPEndPoint peerEndPoint, CancellationToken cancellationToken)
    {
        string key = GetPeerKey(peerEndPoint);
        if (_bindings.TryGetValue(key, out var existing))
        {
            return existing;
        }

        ushort channelNumber = _nextChannelNumber++;
        var created = new TurnPeerBinding(peerEndPoint, channelNumber);
        await CreatePermissionAsync(created, cancellationToken).ConfigureAwait(false);
        await ChannelBindAsync(created, cancellationToken).ConfigureAwait(false);
        _bindings[key] = created;
        return created;
    }

    private async Task CreatePermissionAsync(TurnPeerBinding binding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = CreateAuthenticatedRequest(StunMessageType.CreatePermissionRequest);
        request.Attributes.Add(StunAttribute.CreateXorPeerAddress(binding.PeerEndPoint, request.TransactionId));
        await SendAuthenticatedAsync(
            request,
            static type => type == StunMessageType.CreatePermissionSuccessResponse,
            static type => type == StunMessageType.CreatePermissionErrorResponse).ConfigureAwait(false);
    }

    private async Task ChannelBindAsync(TurnPeerBinding binding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] channelBytes = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(channelBytes.AsSpan(0, 2), binding.ChannelNumber);

        var request = CreateAuthenticatedRequest(StunMessageType.ChannelBindRequest);
        request.Attributes.Add(new StunAttribute { Type = StunAttributeType.ChannelNumber, Value = channelBytes });
        request.Attributes.Add(StunAttribute.CreateXorPeerAddress(binding.PeerEndPoint, request.TransactionId));
        await SendAuthenticatedAsync(
            request,
            static type => type == StunMessageType.ChannelBindSuccessResponse,
            static type => type == StunMessageType.ChannelBindErrorResponse).ConfigureAwait(false);
    }

    private async Task<StunMessage> SendAuthenticatedAsync(
        StunMessage request,
        Func<StunMessageType, bool> isSuccess,
        Func<StunMessageType, bool> isError)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var response = await _sendRequestAsync(request, IntegrityKey).ConfigureAwait(false);
            if (response == null)
            {
                throw new InvalidOperationException("TURN server did not respond.");
            }

            if (isSuccess(response.Type))
            {
                return response;
            }

            if (isError(response.Type))
            {
                var error = response.Attributes.FirstOrDefault(attribute => attribute.Type == StunAttributeType.ErrorCode)?.GetErrorCode();
                if (error?.Code == 438 && TryUpdateAuthState(response))
                {
                    request = CloneAuthenticatedRequest(request);
                    continue;
                }

                throw new InvalidOperationException(error?.Reason ?? $"TURN request failed with {response.Type}.");
            }

            throw new InvalidOperationException($"Unexpected TURN response type {response.Type}.");
        }

        throw new InvalidOperationException("TURN request retry limit exceeded.");
    }

    private StunMessage CreateAuthenticatedRequest(StunMessageType type)
    {
        var request = new StunMessage
        {
            Type = type,
            TransactionId = Guid.NewGuid().ToByteArray().AsSpan(0, 12).ToArray()
        };
        request.Attributes.Add(new StunAttribute { Type = StunAttributeType.Username, Value = System.Text.Encoding.UTF8.GetBytes(Username) });
        request.Attributes.Add(new StunAttribute { Type = StunAttributeType.Realm, Value = Realm });
        request.Attributes.Add(new StunAttribute { Type = StunAttributeType.Nonce, Value = Nonce });
        request.Attributes.Add(new StunAttribute { Type = StunAttributeType.MessageIntegrity });
        request.Attributes.Add(new StunAttribute { Type = StunAttributeType.Fingerprint });
        return request;
    }

    private bool TryUpdateAuthState(StunMessage response)
    {
        var nonce = response.Attributes.FirstOrDefault(attribute => attribute.Type == StunAttributeType.Nonce)?.Value;
        if (nonce == null)
        {
            return false;
        }

        var realm = response.Attributes.FirstOrDefault(attribute => attribute.Type == StunAttributeType.Realm)?.Value;
        if (realm != null)
        {
            Realm = realm;
        }

        Nonce = nonce;
        return true;
    }

    private StunMessage CloneAuthenticatedRequest(StunMessage original)
    {
        var cloned = CreateAuthenticatedRequest(original.Type);
        foreach (var attribute in original.Attributes)
        {
            if (attribute.Type is StunAttributeType.Username
                or StunAttributeType.Realm
                or StunAttributeType.Nonce
                or StunAttributeType.MessageIntegrity
                or StunAttributeType.Fingerprint)
            {
                continue;
            }

            cloned.Attributes.Add(new StunAttribute
            {
                Type = attribute.Type,
                Value = [.. attribute.Value]
            });
        }

        return cloned;
    }

    private static string GetPeerKey(IPEndPoint peerEndPoint) => $"{peerEndPoint.Address}:{peerEndPoint.Port}";

    private static byte[] CreateChannelDataPacket(ushort channelNumber, byte[] data)
    {
        int paddedLength = (data.Length + 3) & ~3;
        byte[] packet = new byte[4 + paddedLength];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), channelNumber);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)data.Length);
        data.CopyTo(packet.AsSpan(4));
        return packet;
    }

    private static bool TryParseChannelData(ReadOnlySpan<byte> packet, out ushort channelNumber, out byte[] payload)
    {
        channelNumber = 0;
        payload = [];

        if (packet.Length < 4)
        {
            return false;
        }

        byte firstByte = packet[0];
        if ((firstByte & 0b1100_0000) != 0b0100_0000)
        {
            return false;
        }

        channelNumber = BinaryPrimitives.ReadUInt16BigEndian(packet[..2]);
        int length = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        if (packet.Length < 4 + length)
        {
            return false;
        }

        payload = packet.Slice(4, length).ToArray();
        return true;
    }

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Max(MinimumRefreshInterval.TotalSeconds, AllocationLifetime.TotalSeconds / 2));
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                await RefreshAllocationAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during allocation disposal.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class TurnPeerBinding
    {
        public TurnPeerBinding(IPEndPoint peerEndPoint, ushort channelNumber)
        {
            PeerEndPoint = peerEndPoint;
            ChannelNumber = channelNumber;
        }

        public IPEndPoint PeerEndPoint { get; }
        public ushort ChannelNumber { get; }
    }
}
