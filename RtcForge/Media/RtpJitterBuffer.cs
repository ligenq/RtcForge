using RtcForge.Rtp;

namespace RtcForge.Media;

/// <summary>
/// A basic jitter buffer to reorder RTP packets and handle network jitter with a timeout for missing packets.
/// </summary>
public class RtpJitterBuffer
{
    private readonly SortedDictionary<ushort, RtpPacket> _buffer = new();
    private readonly int _maxPackets;
    private ushort? _lastPoppedSeq;
    private readonly object _lock = new();
    private DateTime? _waitingSince;
    private readonly TimeSpan _maxWaitTime;

    public int Count { get { lock (_lock) { return _buffer.Count; } } }

    public RtpJitterBuffer(int maxPackets = 50, int maxWaitTimeMs = 100)
    {
        _maxPackets = maxPackets;
        _maxWaitTime = TimeSpan.FromMilliseconds(maxWaitTimeMs);
    }

    public void Push(RtpPacket packet)
    {
        lock (_lock)
        {
            if (_lastPoppedSeq.HasValue && !IsNewer(packet.SequenceNumber, _lastPoppedSeq.Value))
            {
                // Packet is too old, ignore
                return;
            }

            if (_buffer.ContainsKey(packet.SequenceNumber))
            {
                // Duplicate packet
                return;
            }

            _buffer[packet.SequenceNumber] = packet;

            // Evict oldest packets if over capacity
            while (_buffer.Count > _maxPackets)
            {
                var oldestKey = _buffer.Keys.First();
                _buffer.Remove(oldestKey);
                if (_lastPoppedSeq == null || !IsNewer(_lastPoppedSeq.Value, oldestKey))
                {
                    _lastPoppedSeq = oldestKey;
                }
            }
        }
    }

    public RtpPacket? Pop()
    {
        lock (_lock)
        {
            if (_buffer.Count == 0)
            {
                return null;
            }

            ushort nextSeq = _lastPoppedSeq.HasValue ? (ushort)(_lastPoppedSeq.Value + 1) : _buffer.Keys.First();

            if (_buffer.TryGetValue(nextSeq, out var packet))
            {
                _buffer.Remove(nextSeq);
                _lastPoppedSeq = nextSeq;
                _waitingSince = null;
                return packet;
            }

            if (_waitingSince == null)
            {
                _waitingSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - _waitingSince.Value > _maxWaitTime)
            {
                // Timeout reached, skip missing packets and yield the next available one
                ushort firstKey = _buffer.Keys.First();
                packet = _buffer[firstKey];
                _buffer.Remove(firstKey);
                _lastPoppedSeq = firstKey;
                _waitingSince = null;
                return packet;
            }

            return null;
        }
    }

    private static bool IsNewer(ushort seq, ushort last)
    {
        return (seq != last) && ((ushort)(seq - last) < 32768);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
            _lastPoppedSeq = null;
            _waitingSince = null;
        }
    }
}
