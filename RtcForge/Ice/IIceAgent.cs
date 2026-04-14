using RtcForge.Media;

namespace RtcForge.Ice;

public enum IceState
{
    New,
    Gathering,
    Complete,
    Checking,
    Connected,
    Completed,
    Failed,
    Disconnected,
    Closed
}

public interface IIceAgent : IDisposable
{
    IceState State { get; }
    string LocalUfrag { get; }
    string LocalPassword { get; }
    event EventHandler<IceCandidate>? OnLocalCandidate;
    event EventHandler<IceState>? OnStateChange;

    Task StartGatheringAsync();
    void AddRemoteCandidate(IceCandidate candidate);
    void SetRemoteCredentials(string ufrag, string password);
    void SetIceServers(IEnumerable<RTCIceServer> servers);
    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);
}
