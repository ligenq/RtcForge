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
        _dataChannels.AddOrUpdate(channel.Id, channel, (_, _) => channel);
        _streams.AddOrUpdate(channel.Id, new SctpStream(channel.Id, channel.HandleIncomingData), (_, _) => new SctpStream(channel.Id, channel.HandleIncomingData));
    }

    public async Task StartAsync(bool isClient)
    {
        if (isClient)
        {
            await SendInitAsync();
            _state = SctpAssociationState.CookieWait;
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
            await _inputChannel.Writer.WriteAsync(packet);
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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SCTP association packet processing failed");
        }
    }

    private async Task HandleIncomingPacketInternal(SctpPacket packet)
    {
        foreach (var chunk in packet.Chunks)
        {
            switch (chunk.Type)
            {
                case SctpChunkType.Init: await HandleInit(packet, (SctpInitChunk)chunk); break;
                case SctpChunkType.InitAck: await HandleInitAck(packet, (SctpInitChunk)chunk); break;
                case SctpChunkType.CookieEcho: await HandleCookieEcho(packet, chunk); break;
                case SctpChunkType.CookieAck:
                    _state = SctpAssociationState.Established;
                    OnEstablished?.Invoke(this, EventArgs.Empty);
                    break;
                case SctpChunkType.Data: await HandleDataChunk((SctpDataChunk)chunk); break;
                case SctpChunkType.Sack: HandleSackChunk((SctpSackChunk)chunk); break;
                case SctpChunkType.Shutdown: await HandleShutdown((SctpShutdownChunk)chunk); break;
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

        if (isNew)
        {
            var stream = _streams.GetOrAdd(data.StreamId, id => new SctpStream(id, (ppid, msg) =>
            {
                if (_dataChannels.TryGetValue(id, out var channel))
                {
                    channel.HandleIncomingData(ppid, msg);
                }
                else if (ppid == 50)
                {
                    HandleDcepMessage(id, msg);
                }
            }));
            stream.HandleChunk(data);
        }
        await SendSackAsync();
    }

    public event EventHandler<RTCDataChannel>? OnRemoteDataChannel;

    private void HandleDcepMessage(ushort streamId, byte[] msg)
    {
        var dcep = DcepMessage.Parse(msg);
        if (dcep.Type == DcepMessageType.DataChannelOpen)
        {
            var channel = new RTCDataChannel(dcep.Label, streamId, this);
            RegisterDataChannel(channel);
            channel.SetOpen();
            SendDataAsync(streamId, 50, new DcepMessage { Type = DcepMessageType.DataChannelAck }.Serialize()).FireAndForget();
            OnRemoteDataChannel?.Invoke(this, channel);
        }
        else if (dcep.Type == DcepMessageType.DataChannelAck)
        {
            if (_dataChannels.TryGetValue(streamId, out var channel))
            {
                channel.SetOpen();
            }
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

            foreach (var block in sack.GapAckBlocks)
            {
                uint start = sack.CumulativeTsnAck + block.Start;
                uint end = sack.CumulativeTsnAck + block.End;
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
        var init = new SctpInitChunk(SctpChunkType.Init) { InitiateTag = _myVerificationTag, AdvertisedReceiverWindowCredit = 1024 * 1024, NumberOfInboundStreams = 2048, NumberOfOutboundStreams = 2048, InitialTsn = _myInitialTsn };
        var packet = new SctpPacket { SourcePort = _sourcePort, DestinationPort = _destinationPort, VerificationTag = 0 };
        packet.Chunks.Add(init);
        byte[] buffer = new byte[packet.GetSerializedLength()];
        packet.Serialize(buffer);
        await _sendFunc(buffer);
    }

    private async Task HandleInit(SctpPacket _, SctpInitChunk init)
    {
        _peerVerificationTag = init.InitiateTag;
        _peerInitialTsn = init.InitialTsn;
        _cumulativeTsnAckPoint = _peerInitialTsn - 1;
        var response = new SctpInitChunk(SctpChunkType.InitAck) { InitiateTag = _myVerificationTag, AdvertisedReceiverWindowCredit = 1024 * 1024, NumberOfInboundStreams = 2048, NumberOfOutboundStreams = 2048, InitialTsn = _myInitialTsn };
        await SendChunkInternalAsync(response);
    }

    private async Task HandleInitAck(SctpPacket _, SctpInitChunk initAck)
    {
        _peerVerificationTag = initAck.InitiateTag;
        _peerInitialTsn = initAck.InitialTsn;
        _cumulativeTsnAckPoint = _peerInitialTsn - 1;
        await SendChunkInternalAsync(new SctpSimpleChunk { Type = SctpChunkType.CookieEcho, Length = 4 });
        _state = SctpAssociationState.CookieEchoed;
    }

    public event EventHandler? OnEstablished;

    private async Task HandleCookieEcho(SctpPacket _, SctpChunk _1)
    {
        await SendChunkInternalAsync(new SctpSimpleChunk { Type = SctpChunkType.CookieAck, Length = 4 });
        _state = SctpAssociationState.Established;
        OnEstablished?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _state = SctpAssociationState.Closed;
        _cts.Cancel();
        _inputChannel.Writer.TryComplete();
        foreach (var channel in _dataChannels.Values)
        {
            channel.SetClosed();
        }
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
