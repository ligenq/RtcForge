using RtcForge.Dtls;

namespace RtcForge.Media;

public enum RTCDtlsTransportState
{
    New,
    Connecting,
    Connected,
    Closed,
    Failed
}

public sealed class RTCDtlsTransport
{
    public RTCDtlsTransportState State { get; private set; } = RTCDtlsTransportState.New;
    public RTCIceTransport IceTransport { get; }

    public event EventHandler<RTCDtlsTransportState>? OnStateChange;

    internal RTCDtlsTransport(IDtlsTransport internalTransport, RTCIceTransport iceTransport)
    {
        IceTransport = iceTransport;
        internalTransport.OnStateChange += HandleInternalStateChange;
    }

    private void HandleInternalStateChange(object? sender, DtlsState state)
    {
        State = state switch
        {
            DtlsState.New => RTCDtlsTransportState.New,
            DtlsState.Connecting => RTCDtlsTransportState.Connecting,
            DtlsState.Connected => RTCDtlsTransportState.Connected,
            DtlsState.Closed => RTCDtlsTransportState.Closed,
            DtlsState.Failed => RTCDtlsTransportState.Failed,
            _ => RTCDtlsTransportState.Failed
        };
        OnStateChange?.Invoke(this, State);
    }
}

public sealed class RTCIceTransport
{
    internal RTCIceTransport()
    {
    }
}
