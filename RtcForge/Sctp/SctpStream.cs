namespace RtcForge.Sctp;

internal class SctpStream
{
    public ushort StreamId { get; }
    private readonly Dictionary<ushort, List<SctpDataChunk>> _reassemblyBuffer = new();
    private readonly Action<uint, byte[]> _onMessage;

    public SctpStream(ushort streamId, Action<uint, byte[]> onMessage)
    {
        StreamId = streamId;
        _onMessage = onMessage;
    }

    public void HandleChunk(SctpDataChunk chunk)
    {
        if (!_reassemblyBuffer.TryGetValue(chunk.StreamSequenceNumber, out var fragments))
        {
            if (_reassemblyBuffer.Count > 100)
            {
                var oldest = _reassemblyBuffer.Keys.First();
                _reassemblyBuffer.Remove(oldest);
            }

            fragments = new List<SctpDataChunk>();
            _reassemblyBuffer[chunk.StreamSequenceNumber] = fragments;
        }

        if (fragments.Count > 1000)
        {
            _reassemblyBuffer.Remove(chunk.StreamSequenceNumber);
            return;
        }

        if (fragments.Any(f => f.Tsn == chunk.Tsn))
        {
            return; // Duplicate
        }

        fragments.Add(chunk);

        bool hasBegin = fragments.Any(f => (f.Flags & 0x02) != 0);
        bool hasEnd = fragments.Any(f => (f.Flags & 0x01) != 0);

        if (hasBegin && hasEnd)
        {
            var orderedFragments = fragments.OrderBy(f => f.Tsn).ToList();
            int totalLen = orderedFragments.Sum(f => f.UserData.Length);
            byte[] messageData = new byte[totalLen];
            int offset = 0;
            foreach (var f in orderedFragments)
            {
                Buffer.BlockCopy(f.UserData, 0, messageData, offset, f.UserData.Length);
                offset += f.UserData.Length;
            }

            _onMessage(orderedFragments[0].PayloadProtocolId, messageData);
            _reassemblyBuffer.Remove(chunk.StreamSequenceNumber);
        }
    }
}
