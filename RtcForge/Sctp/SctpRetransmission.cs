namespace RtcForge.Sctp;

internal class SctpOutboundChunk
{
    public SctpDataChunk Chunk { get; }
    public DateTimeOffset SentTime { get; set; }
    public int Retransmissions { get; set; }
    public bool Acked { get; set; }

    public SctpOutboundChunk(SctpDataChunk chunk, DateTimeOffset sentTime)
    {
        Chunk = chunk;
        SentTime = sentTime;
    }
}

public partial class SctpAssociation
{
    private readonly Dictionary<uint, SctpOutboundChunk> _outboundQueue = new();
    private readonly object _outboundLock = new();
    private int _rto = 1000; // Retransmission Timeout in ms
    private int _srtt = -1;  // Smoothed Round Trip Time
    private int _rttvar = -1; // RTT Variation
    private uint _cwnd = 3000;
    private uint _ssthresh = 65535;
    private uint _outstandingBytes = 0;

    private async Task CheckRetransmissionsAsync()
    {
        while (_state != SctpAssociationState.Closed && !_cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(100), _timeProvider, _cts.Token); }
            catch (OperationCanceledException) { return; }
            List<SctpOutboundChunk> toRetransmit = new();

            lock (_outboundLock)
            {
                var now = _timeProvider.GetUtcNow();
                foreach (var item in _outboundQueue.Values.Where(v => !v.Acked))
                {
                    if ((now - item.SentTime).TotalMilliseconds > _rto)
                    {
                        toRetransmit.Add(item);
                    }
                }
            }

            foreach (var item in toRetransmit)
            {
                item.Retransmissions++;
                item.SentTime = _timeProvider.GetUtcNow();

                // Congestion Control on timeout
                _ssthresh = Math.Max(_cwnd / 2, 2 * 1200);
                _cwnd = 1200;

                await SendChunkInternalAsync(item.Chunk);
            }
        }
    }

    private void UpdateRto(int rtt)
    {
        // RFC 4960 Section 6.3.1 (Kar-Jacobson)
        if (_srtt == -1)
        {
            _srtt = rtt;
            _rttvar = rtt / 2;
        }
        else
        {
            _rttvar = (int)(((1 - 0.25) * _rttvar) + (0.25 * Math.Abs(_srtt - rtt)));
            _srtt = (int)(((1 - 0.125) * _srtt) + (0.125 * rtt));
        }
        _rto = _srtt + (4 * _rttvar);
        _rto = Math.Clamp(_rto, 1000, 60000); // RTO.Min = 1s, RTO.Max = 60s
    }

    private async Task SendChunkInternalAsync(SctpChunk chunk)
    {
        var packet = new SctpPacket
        {
            SourcePort = _sourcePort,
            DestinationPort = _destinationPort,
            VerificationTag = _peerVerificationTag
        };
        packet.Chunks.Add(chunk);

        byte[] buffer = new byte[packet.GetSerializedLength()];
        packet.Serialize(buffer);
        await _sendFunc(buffer);
    }

    public async Task ShutdownAsync()
    {
        if (_state != SctpAssociationState.Established)
        {
            return;
        }

        _state = SctpAssociationState.ShutdownPending;

        // Wait for outbound queue to clear
        while (!_cts.IsCancellationRequested)
        {
            lock (_outboundLock) { if (_outboundQueue.Count == 0)
                {
                    break;
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), _timeProvider, _cts.Token);
        }

        _state = SctpAssociationState.ShutdownSent;
        await SendChunkInternalAsync(new SctpShutdownChunk { CumulativeTsnAck = _cumulativeTsnAckPoint });
    }

    private async Task HandleShutdown(SctpShutdownChunk shutdown)
    {
        // RFC 4960 Section 9.2
        _state = SctpAssociationState.ShutdownReceived;
        await SendChunkInternalAsync(new SctpSimpleChunk { Type = SctpChunkType.ShutdownAck });
        _state = SctpAssociationState.ShutdownAckSent;
    }

    private async Task HandleShutdownAck()
    {
        await SendChunkInternalAsync(new SctpSimpleChunk { Type = SctpChunkType.ShutdownComplete });
        _state = SctpAssociationState.Closed;
    }
}
