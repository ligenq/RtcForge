using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace RtcForge.Sctp;

public enum SctpAssociationState
{
    Closed,
    CookieWait,
    CookieEchoed,
    Established,
    ShutdownPending,
    ShutdownSent,
    ShutdownReceived,
    ShutdownAckSent
}

public partial class SctpAssociation : IDisposable
{
    private volatile SctpAssociationState _state = SctpAssociationState.Closed;
    private readonly ushort _sourcePort;
    private readonly ushort _destinationPort;
    private readonly uint _myVerificationTag;
    private uint _peerVerificationTag;
    private readonly uint _myInitialTsn;
    private uint _peerInitialTsn;
    private uint _myTsn;
    private uint _cumulativeTsnAckPoint;
    private byte[]? _stateCookie;
    private readonly Lock _tsnLock = new();

    private readonly Channel<SctpPacket> _inputChannel = Channel.CreateBounded<SctpPacket>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.Wait });
    private readonly Func<byte[], Task> _sendFunc;
    private readonly ConcurrentDictionary<ushort, RTCDataChannel> _dataChannels = new();
    private readonly ConcurrentDictionary<ushort, SctpStream> _streams = new();
    private readonly Lock _receiveLock = new();
    private readonly SortedSet<uint> _receivedTsns = [];
    private readonly ConcurrentDictionary<ushort, ushort> _outboundSsns = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<SctpAssociation>? _logger;

    public SctpAssociationState State => _state;

    public SctpAssociation(ushort sourcePort, ushort destinationPort, Func<byte[], Task> sendFunc, ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<SctpAssociation>();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sourcePort = sourcePort;
        _destinationPort = destinationPort;
        _sendFunc = sendFunc;
        _myVerificationTag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
        _myInitialTsn = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
        _myTsn = _myInitialTsn;
    }

    internal void RegisterDataChannel(RTCDataChannel channel)
    {
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            _logger.LogDebug("SCTP registering data channel label={Label} id={Id}", channel.Label, channel.Id);
        }
        _dataChannels.AddOrUpdate(channel.Id, channel, (_, _) => channel);
        var stream = new SctpStream(channel.Id, (ppid, msg) => DispatchStreamMessage(channel.Id, ppid, msg), _loggerFactory?.CreateLogger("RtcForge.Sctp.SctpStream"));
        _streams.AddOrUpdate(channel.Id, stream, (_, _) => stream);
    }

    private void DispatchStreamMessage(ushort streamId, uint ppid, byte[] msg)
    {
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            _logger.LogDebug("SCTP stream reassembled streamId={Stream} ppid={Ppid} bytes={Bytes}", streamId, ppid, msg.Length);
        }
        if (ppid == 50)
        {
            HandleDcepMessage(streamId, msg);
        }
        else if (_dataChannels.TryGetValue(streamId, out var channel))
        {
            channel.HandleIncomingData(ppid, msg);
        }
        else
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger.LogDebug("SCTP dropping message - no channel registered for streamId={Stream} ppid={Ppid}", streamId, ppid);
            }
        }
    }

    public async Task StartAsync(bool isClient)
    {
        if (_logger?.IsEnabled(LogLevel.Information) == true)
        {
            _logger.LogInformation("SCTP start role={Role}", isClient ? "client" : "server");
        }
        if (isClient)
        {
            await SendInitAsync();
            _state = SctpAssociationState.CookieWait;
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger.LogDebug("SCTP state={State}", _state);
            }
        }

        Task.Run(() => ProcessPacketsAsync(), _cts.Token).FireAndForget();
        Task.Run(() => CheckRetransmissionsAsync(), _cts.Token).FireAndForget();
    }

    public const int MaxMessageSize = 262144;

    public async Task SendDataAsync(ushort streamId, uint ppid, byte[] data)
    {
        if (_state != SctpAssociationState.Established)
        {
            throw new InvalidOperationException("Association not established");
        }

        if (data.Length > MaxMessageSize)
        {
            throw new ArgumentException($"Message size {data.Length} exceeds maximum {MaxMessageSize}.", nameof(data));
        }

        const int maxChunkSize = 1200;
        int offset = 0;
        ushort ssn;
        lock (_tsnLock)
        {
            _outboundSsns.TryGetValue(streamId, out ssn);
            _outboundSsns[streamId] = (ushort)(ssn + 1);
        }

        while (offset < data.Length)
        {
            int remaining = data.Length - offset;
            int chunkSize = Math.Min(remaining, maxChunkSize);
            byte[] chunkData = new byte[chunkSize];
            Buffer.BlockCopy(data, offset, chunkData, 0, chunkSize);

            byte flags = 0;
            if (offset == 0)
            {
                flags |= 0x02;
            }

            if (offset + chunkSize == data.Length)
            {
                flags |= 0x01;
            }

            uint tsn;
            lock (_tsnLock) { tsn = _myTsn++; }

            var chunk = new SctpDataChunk
            {
                Flags = flags,
                Tsn = tsn,
                StreamId = streamId,
                StreamSequenceNumber = ssn,
                PayloadProtocolId = ppid,
                UserData = chunkData
            };

            lock (_outboundLock)
            {
                _outboundQueue[chunk.Tsn] = new SctpOutboundChunk(chunk, _timeProvider.GetUtcNow());
                _outstandingBytes += (uint)chunk.GetSerializedLength();
            }

            await SendChunkInternalAsync(chunk);
            offset += chunkSize;
        }
    }

    public async Task HandlePacketAsync(byte[] data)
    {
        if (SctpPacket.TryParse(data, out var packet))
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                var types = string.Join(",", packet.Chunks.Select(c => c.Type.ToString()));
                _logger.LogDebug("SCTP packet rx bytes={Bytes} chunks=[{Types}]", data.Length, types);
            }
            await _inputChannel.Writer.WriteAsync(packet);
        }
        else
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger.LogDebug("SCTP packet parse failed bytes={Bytes}", data.Length);
            }
        }
    }

    private async Task ProcessPacketsAsync()
    {
        try
        {
            while (await _inputChannel.Reader.WaitToReadAsync(_cts.Token))
            {
                while (_inputChannel.Reader.TryRead(out var packet))
                {
                    await HandleIncomingPacketInternal(packet);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during association disposal.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SCTP association packet processing failed");
        }
    }

    private async Task HandleIncomingPacketInternal(SctpPacket packet)
    {
        if (!IsVerificationTagValid(packet))
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger.LogDebug("SCTP dropping packet with invalid verification tag tag={Tag}", packet.VerificationTag);
            }
            return;
        }

        foreach (var chunk in packet.Chunks)
        {
            switch (chunk.Type)
            {
                case SctpChunkType.Init: await HandleInit((SctpInitChunk)chunk); break;
                case SctpChunkType.InitAck: await HandleInitAck((SctpInitChunk)chunk); break;
                case SctpChunkType.CookieEcho: await HandleCookieEcho(chunk); break;
                case SctpChunkType.CookieAck:
                    _state = SctpAssociationState.Established;
                    if (_logger?.IsEnabled(LogLevel.Information) == true)
                    {
                        _logger.LogInformation("SCTP state={State} after CookieAck", _state);
                    }
                    OnEstablished?.Invoke(this, EventArgs.Empty);
                    break;
                case SctpChunkType.Data: await HandleDataChunk((SctpDataChunk)chunk); break;
                case SctpChunkType.Sack: HandleSackChunk((SctpSackChunk)chunk); break;
                case SctpChunkType.Shutdown: await HandleShutdown(); break;
                case SctpChunkType.ShutdownAck: await HandleShutdownAck(); break;
                case SctpChunkType.ShutdownComplete: _state = SctpAssociationState.Closed; break;
            }
        }
    }

    private async Task HandleDataChunk(SctpDataChunk data)
    {
        bool isNew;
        lock (_receiveLock)
        {
            isNew = _receivedTsns.Add(data.Tsn);
            if (isNew)
            {
                while (_receivedTsns.Contains(_peerInitialTsn))
                {
                    _cumulativeTsnAckPoint = _peerInitialTsn;
                    _peerInitialTsn++;
                }
            }
        }

        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            _logger.LogDebug("SCTP DATA chunk tsn={Tsn} streamId={Stream} ppid={Ppid} payloadBytes={Bytes} isNew={New}",
                data.Tsn, data.StreamId, data.PayloadProtocolId, data.UserData?.Length ?? 0, isNew);
        }

        if (isNew)
        {
            var stream = _streams.GetOrAdd(data.StreamId, id => new SctpStream(id, (ppid, msg) => DispatchStreamMessage(id, ppid, msg), _loggerFactory?.CreateLogger("RtcForge.Sctp.SctpStream")));
            stream.HandleChunk(data);
        }
        await SendSackAsync();
    }

    public event EventHandler<RTCDataChannel>? OnRemoteDataChannel;

    private void HandleDcepMessage(ushort streamId, byte[] msg)
    {
        var dcep = DcepMessage.Parse(msg);
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            _logger.LogDebug("DCEP message type={Type} streamId={StreamId}", dcep.Type, streamId);
        }
        if (dcep.Type == DcepMessageType.DataChannelOpen)
        {
            var channel = new RTCDataChannel(dcep.Label, streamId, this);
            RegisterDataChannel(channel);
            channel.SetOpen();
            SendDataAsync(streamId, 50, new DcepMessage { Type = DcepMessageType.DataChannelAck }.Serialize()).FireAndForget();
            OnRemoteDataChannel?.Invoke(this, channel);
        }
        else if (dcep.Type == DcepMessageType.DataChannelAck
            && _dataChannels.TryGetValue(streamId, out var channel))
        {
            if (_logger?.IsEnabled(LogLevel.Information) == true)
            {
                _logger.LogInformation("DCEP ack received for data channel label={Label} id={Id}", channel.Label, channel.Id);
            }
            channel.SetOpen();
        }
    }


    private void HandleSackChunk(SctpSackChunk sack)
    {
        lock (_outboundLock)
        {
            List<uint> ackedTsns = [];
            foreach (uint tsn in _outboundQueue.Keys)
            {
                if (tsn <= sack.CumulativeTsnAck)
                {
                    ackedTsns.Add(tsn);
                }
            }

            foreach (var tsn in ackedTsns)
            {
                if (_outboundQueue.TryGetValue(tsn, out var item))
                {
                    int rtt = (int)(_timeProvider.GetUtcNow() - item.SentTime).TotalMilliseconds;
                    UpdateRto(rtt);
                    _outstandingBytes -= (uint)item.Chunk.GetSerializedLength();
                    _outboundQueue.Remove(tsn);
                    if (_cwnd <= _ssthresh)
                    {
                        _cwnd += 1200;
                    }
                    else
                    {
                        _cwnd += (1200 * 1200) / _cwnd;
                    }
                }
            }

            foreach (var (startOffset, endOffset) in sack.GapAckBlocks)
            {
                uint start = sack.CumulativeTsnAck + startOffset;
                uint end = sack.CumulativeTsnAck + endOffset;
                for (uint tsn = start; tsn <= end; tsn++)
                {
                    if (_outboundQueue.TryGetValue(tsn, out var item) && !item.Acked)
                    {
                        item.Acked = true;
                        _outstandingBytes -= (uint)item.Chunk.GetSerializedLength();
                    }
                }
            }
        }
    }

    private async Task SendSackAsync()
    {
        var sack = new SctpSackChunk { CumulativeTsnAck = _cumulativeTsnAckPoint, AdvertisedReceiverWindowCredit = 1024 * 1024 };
        await SendChunkInternalAsync(sack);
    }

    private async Task SendInitAsync()
    {
        _logger?.LogDebug("SCTP sending Init");
        var init = new SctpInitChunk(SctpChunkType.Init) { InitiateTag = _myVerificationTag, AdvertisedReceiverWindowCredit = 1024 * 1024, NumberOfInboundStreams = 2048, NumberOfOutboundStreams = 2048, InitialTsn = _myInitialTsn };
        var packet = new SctpPacket { SourcePort = _sourcePort, DestinationPort = _destinationPort, VerificationTag = 0 };
        packet.Chunks.Add(init);
        byte[] buffer = new byte[packet.GetSerializedLength()];
        packet.Serialize(buffer);
        await _sendFunc(buffer);
    }

    private async Task HandleInit(SctpInitChunk init)
    {
        _logger?.LogDebug("SCTP received Init");
        _peerVerificationTag = init.InitiateTag;
        _peerInitialTsn = init.InitialTsn;
        _cumulativeTsnAckPoint = _peerInitialTsn - 1;
        _stateCookie ??= CreateStateCookie(init);
        var response = new SctpInitChunk(SctpChunkType.InitAck)
        {
            InitiateTag = _myVerificationTag,
            AdvertisedReceiverWindowCredit = 1024 * 1024,
            NumberOfInboundStreams = 2048,
            NumberOfOutboundStreams = 2048,
            InitialTsn = _myInitialTsn,
            StateCookie = _stateCookie
        };
        await SendChunkInternalAsync(response);
    }

    private bool IsVerificationTagValid(SctpPacket packet)
    {
        if (packet.Chunks.Any(c => c.Type == SctpChunkType.Init))
        {
            return packet.VerificationTag == 0;
        }

        return packet.VerificationTag == _myVerificationTag;
    }

    private async Task HandleInitAck(SctpInitChunk initAck)
    {
        _logger?.LogDebug("SCTP received InitAck");
        _peerVerificationTag = initAck.InitiateTag;
        _peerInitialTsn = initAck.InitialTsn;
        _cumulativeTsnAckPoint = _peerInitialTsn - 1;
        if (initAck.StateCookie is not { Length: > 0 })
        {
            _logger?.LogDebug("SCTP InitAck missing State Cookie");
            return;
        }

        await SendChunkInternalAsync(new SctpCookieEchoChunk { Cookie = initAck.StateCookie });
        _state = SctpAssociationState.CookieEchoed;
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            _logger.LogDebug("SCTP state={State}", _state);
        }
    }

    public event EventHandler? OnEstablished;

    private async Task HandleCookieEcho(SctpChunk chunk)
    {
        _logger?.LogDebug("SCTP received CookieEcho");
        if (chunk is not SctpCookieEchoChunk cookieEcho
            || cookieEcho.Cookie.Length == 0
            || _stateCookie == null
            || !cookieEcho.Cookie.AsSpan().SequenceEqual(_stateCookie))
        {
            _logger?.LogDebug("SCTP dropping CookieEcho with invalid state cookie");
            return;
        }

        await SendChunkInternalAsync(new SctpSimpleChunk { Type = SctpChunkType.CookieAck, Length = 4 });
        _state = SctpAssociationState.Established;
        if (_logger?.IsEnabled(LogLevel.Information) == true)
        {
            _logger.LogInformation("SCTP state={State} after CookieEcho", _state);
        }
        OnEstablished?.Invoke(this, EventArgs.Empty);
    }

    private byte[] CreateStateCookie(SctpInitChunk init)
    {
        byte[] cookie = new byte[32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(0, 4), init.InitiateTag);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(4, 4), init.InitialTsn);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(8, 4), _myVerificationTag);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(12, 4), _myInitialTsn);
        System.Security.Cryptography.RandomNumberGenerator.Fill(cookie.AsSpan(16));
        return cookie;
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

        _state = SctpAssociationState.Closed;
        if (!disposing)
        {
            return;
        }

        _cts.Cancel();
        _inputChannel.Writer.TryComplete();
        foreach (var channel in _dataChannels.Values)
        {
            channel.SetClosed();
        }
        _cts.Dispose();
    }
}
