using System.Collections.Concurrent;
using RtcForge.Dtls;

namespace RtcForge.Tests.Dtls;

public class DtlsHandshakeTests
{
    [Fact]
    public async Task DtlsLoopback_ClientAndServer_Handshake()
    {
        var cert1 = DtlsCertificate.Generate();
        var cert2 = DtlsCertificate.Generate();

        DtlsTransport? client = null;
        DtlsTransport? server = null;

        // Wire up: client sends → server receives, server sends → client receives
        client = new DtlsTransport(async data => server?.HandleIncomingPacket(data), cert1);
        server = new DtlsTransport(async data => client?.HandleIncomingPacket(data), cert2);

        client.SetRemoteFingerprint("sha-256", cert2.Fingerprint);
        server.SetRemoteFingerprint("sha-256", cert1.Fingerprint);

        var clientState = new TaskCompletionSource<DtlsState>();
        var serverState = new TaskCompletionSource<DtlsState>();

        client.OnStateChange += (_, s) => { if (s == DtlsState.Connected || s == DtlsState.Failed) clientState.TrySetResult(s); };
        server.OnStateChange += (_, s) => { if (s == DtlsState.Connected || s == DtlsState.Failed) serverState.TrySetResult(s); };

        // Start server first, then client
        var serverTask = server.StartAsync(isClient: false);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System); // Let server start listening
        var clientTask = client.StartAsync(isClient: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        cts.Token.Register(() => { clientState.TrySetResult(DtlsState.Failed); serverState.TrySetResult(DtlsState.Failed); });

        var clientResult = await clientState.Task;
        var serverResult = await serverState.Task;

        Assert.Equal(DtlsState.Connected, serverResult);
        Assert.Equal(DtlsState.Connected, clientResult);

        // Verify SRTP keys were exported
        Assert.NotNull(client.GetSrtpKeys());
        Assert.NotNull(server.GetSrtpKeys());

        client.Dispose();
        server.Dispose();
    }
}
