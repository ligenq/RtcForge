namespace RtcForge.Ice;

public enum IceCandidateType
{
    Host,
    Srflx,
    Prflx,
    Relay
}

public class IceCandidate
{
    public string Foundation { get; set; } = string.Empty;
    public uint Component { get; set; } = 1; // 1 for RTP, 2 for RTCP (though WebRTC usually bundles)
    public string Protocol { get; set; } = "udp";
    public uint Priority { get; set; }
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; }
    public IceCandidateType Type { get; set; }
    public string? RelatedAddress { get; set; }
    public int? RelatedPort { get; set; }

    public override string ToString()
    {
        string s = $"candidate:{Foundation} {Component} {Protocol} {Priority} {Address} {Port} typ {Type.ToString().ToLower()}";
        if (RelatedAddress != null && RelatedPort != null)
        {
            s += $" raddr {RelatedAddress} rport {RelatedPort}";
        }
        return s;
    }

    public static IceCandidate Parse(string candidateLine)
    {
        if (candidateLine.StartsWith("candidate:"))
        {
            candidateLine = candidateLine[10..];
        }

        var parts = candidateLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidate = new IceCandidate
        {
            Foundation = parts[0],
            Component = uint.Parse(parts[1]),
            Protocol = parts[2],
            Priority = uint.Parse(parts[3]),
            Address = parts[4],
            Port = int.Parse(parts[5])
        };

        for (int i = 6; i < parts.Length; i++)
        {
            if (parts[i] == "typ")
            {
                candidate.Type = Enum.Parse<IceCandidateType>(parts[i + 1], true);
                i++;
            }
            else if (parts[i] == "raddr")
            {
                candidate.RelatedAddress = parts[i + 1];
                i++;
            }
            else if (parts[i] == "rport")
            {
                candidate.RelatedPort = int.Parse(parts[i + 1]);
                i++;
            }
        }

        return candidate;
    }
}
