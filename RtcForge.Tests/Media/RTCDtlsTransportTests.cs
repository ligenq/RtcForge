using RtcForge.Dtls;
using RtcForge.Media;

namespace RtcForge.Tests.Media;

public class RTCDtlsTransportTests
{
    [Theory]
    [InlineData(DtlsState.New, RTCDtlsTransportState.New)]
    [InlineData(DtlsState.Connecting, RTCDtlsTransportState.Connecting)]
    [InlineData(DtlsState.Connected, RTCDtlsTransportState.Connected)]
    [InlineData(DtlsState.Closed, RTCDtlsTransportState.Closed)]
    [InlineData(DtlsState.Failed, RTCDtlsTransportState.Failed)]
    [InlineData((DtlsState)999, RTCDtlsTransportState.Failed)]
    public void InternalStateChange_MapsToPublicState(DtlsState internalState, RTCDtlsTransportState expectedPublicState)
    {
        var internalTransport = new FakeDtlsTransport();
        var transport = new RTCDtlsTransport(internalTransport, new RTCIceTransport());
        var observed = new List<RTCDtlsTransportState>();
        transport.OnStateChange += (_, state) => observed.Add(state);

        internalTransport.RaiseState(internalState);

        Assert.Equal(expectedPublicState, transport.State);
        Assert.Equal(expectedPublicState, Assert.Single(observed));
    }

    [Fact]
    public void Constructor_ExposesIceTransport()
    {
        var internalTransport = new FakeDtlsTransport();
        var iceTransport = new RTCIceTransport();

        var transport = new RTCDtlsTransport(internalTransport, iceTransport);

        Assert.Same(iceTransport, transport.IceTransport);
    }

    private sealed class FakeDtlsTransport : IDtlsTransport
    {
        public DtlsState State { get; private set; } = DtlsState.New;
        public event EventHandler<DtlsState>? OnStateChange;
        public event EventHandler<byte[]>? OnData;

        public Task StartAsync(bool isClient) => Task.CompletedTask;

        public Task SendAsync(byte[] data)
        {
            OnData?.Invoke(this, data);
            return Task.CompletedTask;
        }

        public void SetRemoteFingerprint(string algorithm, string fingerprint)
        {
        }

        public SrtpKeys? GetSrtpKeys() => null;

        public void Dispose()
        {
        }

        public void RaiseState(DtlsState state)
        {
            State = state;
            OnStateChange?.Invoke(this, state);
        }
    }
}
