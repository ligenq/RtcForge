using Microsoft.Extensions.Logging;

namespace RtcForge.Media;

/// <summary>
/// Configures a low-level <see cref="RTCPeerConnection"/>.
/// </summary>
public class RTCConfiguration
{
    /// <summary>
    /// Gets or sets the ICE servers used for server-reflexive or relayed connectivity.
    /// </summary>
    public List<RTCIceServer> IceServers { get; set; } = [];

    /// <summary>
    /// Gets or sets which ICE candidate types may be used.
    /// </summary>
    public RTCIceTransportPolicy IceTransportPolicy { get; set; } = RTCIceTransportPolicy.All;

    /// <summary>
    /// Gets or sets the logger factory used by the peer connection and protocol components.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// Gets or sets the time provider used for timers, delays, certificate validity, and timeouts.
    /// </summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>
    /// Gets or sets the default timeout used by <see cref="RTCPeerConnection.ConnectAsync(CancellationToken)"/>.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Describes a STUN or TURN server used by ICE.
/// </summary>
public class RTCIceServer
{
    /// <summary>
    /// Gets or sets the STUN or TURN server URLs.
    /// </summary>
    public List<string> Urls { get; set; } = [];

    /// <summary>
    /// Gets or sets the TURN username, when required.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Gets or sets the TURN credential, when required.
    /// </summary>
    public string? Credential { get; set; }
}

/// <summary>
/// Controls which ICE candidate types may be gathered and used.
/// </summary>
public enum RTCIceTransportPolicy
{
    /// <summary>
    /// Allows host, server-reflexive, and relayed candidates.
    /// </summary>
    All,

    /// <summary>
    /// Allows only relayed TURN candidates.
    /// </summary>
    Relay
}
