using Microsoft.Extensions.Logging;

namespace RtcForge.Sctp;

internal class SctpStream
{
    public ushort StreamId { get; }
    private readonly Dictionary<ushort, List<SctpDataChunk>> _reassemblyBuffer = [];
    private readonly Action<uint, byte[]> _onMessage;
    private readonly ILogger? _logger;

    public SctpStream(ushort streamId, Action<uint, byte[]> onMessage, ILogger? logger = null)
    {
        StreamId = streamId;
        _onMessage = onMessage;
        _logger = logger;
    }

    public void HandleChunk(SctpDataChunk chunk)
    {
        bool begin = (chunk.Flags & 0x02) != 0;
        bool end = (chunk.Flags & 0x01) != 0;
        _logger?.LogDebug("SctpStream {Sid} chunk tsn={Tsn} ssn={Ssn} flags=0x{Flags:X2} B={B} E={E} ppid={Ppid} bytes={Bytes}",
            StreamId, chunk.Tsn, chunk.StreamSequenceNumber, chunk.Flags, begin, end, chunk.PayloadProtocolId, chunk.UserData?.Length ?? 0);

        if (!_reassemblyBuffer.TryGetValue(chunk.StreamSequenceNumber, out var fragments))
        {
            if (_reassemblyBuffer.Count > 100)
            {
                var oldest = _reassemblyBuffer.Keys.First();
                _reassemblyBuffer.Remove(oldest);
                _logger?.LogDebug("SctpStream {Sid} evicted oldest ssn={Ssn} — reassembly buffer full", StreamId, oldest);
            }

            fragments = [];
            _reassemblyBuffer[chunk.StreamSequenceNumber] = fragments;
        }

        if (fragments.Count > 1000)
        {
            _logger?.LogDebug("SctpStream {Sid} dropping ssn={Ssn} — too many fragments", StreamId, chunk.StreamSequenceNumber);
            _reassemblyBuffer.Remove(chunk.StreamSequenceNumber);
            return;
        }

        if (fragments.Any(f => f.Tsn == chunk.Tsn))
        {
            _logger?.LogDebug("SctpStream {Sid} duplicate tsn={Tsn} ignored", StreamId, chunk.Tsn);
            return;
        }

        fragments.Add(chunk);

        bool hasBegin = fragments.Any(f => (f.Flags & 0x02) != 0);
        bool hasEnd = fragments.Any(f => (f.Flags & 0x01) != 0);

        _logger?.LogDebug("SctpStream {Sid} ssn={Ssn} fragCount={Count} hasB={HasB} hasE={HasE}",
            StreamId, chunk.StreamSequenceNumber, fragments.Count, hasBegin, hasEnd);

        if (hasBegin && hasEnd)
        {
            fragments.Sort(static (left, right) => left.Tsn.CompareTo(right.Tsn));
            if ((fragments[0].Flags & 0x02) == 0 || (fragments[^1].Flags & 0x01) == 0)
            {
                return;
            }

            for (int i = 1; i < fragments.Count; i++)
            {
                if (fragments[i].Tsn != fragments[i - 1].Tsn + 1)
                {
                    return;
                }
            }

            int totalLen = fragments.Sum(static fragment => fragment.UserData.Length);

            byte[] messageData = new byte[totalLen];
            int offset = 0;
            foreach (var fragment in fragments)
            {
                Buffer.BlockCopy(fragment.UserData, 0, messageData, offset, fragment.UserData.Length);
                offset += fragment.UserData.Length;
            }

            try
            {
                _onMessage(fragments[0].PayloadProtocolId, messageData);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "SctpStream {Sid} _onMessage callback threw", StreamId);
            }
            _reassemblyBuffer.Remove(chunk.StreamSequenceNumber);
        }
    }
}
