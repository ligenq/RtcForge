namespace RtcForge.Dtls;

public enum DtlsState
{
    New,
    Connecting,
    Connected,
    Closed,
    Failed
}

public interface IDtlsTransport : IDisposable
{
    DtlsState State { get; }
    event EventHandler<DtlsState>? OnStateChange;
    event EventHandler<byte[]>? OnData;

    Task StartAsync(bool isClient);
    Task SendAsync(byte[] data);
    void SetRemoteFingerprint(string algorithm, string fingerprint);
    SrtpKeys? GetSrtpKeys();
}

public class SrtpKeys
{
    public byte[] MasterKey { get; set; } = [];
    public byte[] MasterSalt { get; set; } = [];
    public ushort ProtectionProfile { get; set; }
}
