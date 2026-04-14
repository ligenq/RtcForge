using Microsoft.Extensions.Logging;

namespace RtcForge.Media;

public class RTCConfiguration
{
    public List<RTCIceServer> IceServers { get; set; } = new();
    public RTCIceTransportPolicy IceTransportPolicy { get; set; } = RTCIceTransportPolicy.All;
    public ILoggerFactory? LoggerFactory { get; set; }
    public TimeProvider? TimeProvider { get; set; }
}

public class RTCIceServer
{
    public List<string> Urls { get; set; } = new();
    public string? Username { get; set; }
    public string? Credential { get; set; }
}

public enum RTCIceTransportPolicy
{
    All,
    Relay
}
