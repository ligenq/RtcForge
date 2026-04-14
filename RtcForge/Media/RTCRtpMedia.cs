using RtcForge.Rtp;

namespace RtcForge.Media;

public enum RTCRtpTransceiverDirection
{
    SendRecv,
    SendOnly,
    RecvOnly,
    Inactive
}

public class RTCRtpSender
{
    public MediaStreamTrack? Track { get; private set; }
    public RTCDtlsTransport? Transport { get; internal set; }
    private readonly Func<RtpPacket, Task> _sendRtpFunc;
    private readonly Dictionary<ushort, RtpPacket> _packetHistory = new();
    private readonly Lock _historyLock = new();

    public event EventHandler? OnPictureLoss;

    internal RTCRtpSender(MediaStreamTrack? track, Func<RtpPacket, Task> sendRtpFunc)
    {
        Track = track;
        _sendRtpFunc = sendRtpFunc;
    }

    public async Task SendRtpAsync(RtpPacket packet)
    {
        lock (_historyLock)
        {
            _packetHistory[packet.SequenceNumber] = packet;
            if (_packetHistory.Count > 100)
            {
                _packetHistory.Remove(_packetHistory.Keys.Min());
            }
        }
        await _sendRtpFunc(packet);
    }

    internal async Task HandleNackAsync(RtcpNackPacket nack)
    {
        foreach (var sn in nack.LostSequenceNumbers)
        {
            RtpPacket? packet;
            lock (_historyLock)
            {
                _packetHistory.TryGetValue(sn, out packet);
            }
            if (packet != null)
            {
                await _sendRtpFunc(packet);
            }
        }
    }

    internal async Task HandlePliAsync(RtcpPliPacket pli)
    {
        OnPictureLoss?.Invoke(this, EventArgs.Empty);
    }

    public Task ReplaceTrackAsync(MediaStreamTrack? withTrack)
    {
        Track = withTrack;
        return Task.CompletedTask;
    }

    public void ReplaceTrack(MediaStreamTrack? withTrack)
    {
        Track = withTrack;
    }
}

public class RTCRtpReceiver
{
    public MediaStreamTrack Track { get; }
    public RTCDtlsTransport? Transport { get; internal set; }
    private ushort _lastSeq = 0;
    private bool _firstPacket = true;
    private readonly Func<RtcpPacket, Task> _sendRtcpFunc;
    private readonly RtpJitterBuffer _jitterBuffer = new();

    internal RTCRtpReceiver(MediaStreamTrack track, Func<RtcpPacket, Task> sendRtcpFunc)
    {
        Track = track;
        _sendRtcpFunc = sendRtcpFunc;
    }

    public RtpPacket? GetNextPacket() => _jitterBuffer.Pop();

    public async Task RequestKeyFrameAsync()
    {
        await _sendRtcpFunc(new RtcpPliPacket { SenderSsrc = 0, MediaSsrc = 0 }); // SSRC should be set properly
    }

    internal async Task HandleRtpPacketAsync(RtpPacket packet)
    {
        _jitterBuffer.Push(packet);

        if (_firstPacket)
        {
            _lastSeq = packet.SequenceNumber;
            _firstPacket = false;
            return;
        }

        ushort expected = (ushort)(_lastSeq + 1);
        if (packet.SequenceNumber != expected && IsNewer(packet.SequenceNumber, _lastSeq))
        {
            var nack = new RtcpNackPacket { MediaSsrc = packet.Ssrc };
            for (ushort i = expected; i != packet.SequenceNumber; i++)
            {
                nack.LostSequenceNumbers.Add(i);
            }
            await _sendRtcpFunc(nack);
        }

        if (IsNewer(packet.SequenceNumber, _lastSeq))
        {
            _lastSeq = packet.SequenceNumber;
        }
    }

    private bool IsNewer(ushort seq, ushort last)
    {
        return (seq != last) && ((ushort)(seq - last) < 32768);
    }
}

public class RTCRtpTransceiver
{
    public string Mid { get; internal set; } = string.Empty;
    public RTCRtpSender Sender { get; }
    public RTCRtpReceiver Receiver { get; }
    public RTCRtpTransceiverDirection Direction { get; set; } = RTCRtpTransceiverDirection.SendRecv;
    public RTCRtpTransceiverDirection? CurrentDirection { get; internal set; }
    internal RTCRtpTransceiverDirection? RemoteDirection { get; set; }
    internal List<NegotiatedCodec> NegotiatedCodecs { get; set; } = new();

    internal RTCRtpTransceiver(RTCRtpSender sender, RTCRtpReceiver receiver)
    {
        Sender = sender;
        Receiver = receiver;
    }

    public void Stop()
    {
        Direction = RTCRtpTransceiverDirection.Inactive;
    }
}

public abstract class MediaStreamTrack
{
    public string Kind { get; }
    public string Id { get; } = Guid.NewGuid().ToString();
    public bool Enabled { get; set; } = true;

    protected MediaStreamTrack(string kind)
    {
        Kind = kind;
    }
}

public class AudioStreamTrack : MediaStreamTrack
{
    public AudioStreamTrack() : base("audio") { }
}

public class VideoStreamTrack : MediaStreamTrack
{
    public VideoStreamTrack() : base("video") { }
}

internal record NegotiatedCodec(byte PayloadType, string Name, int ClockRate, int? Channels = null);
