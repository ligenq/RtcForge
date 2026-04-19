using RtcForge.Dtls;

namespace RtcForge.Tests.Dtls;

public class DtlsEarlyPacketTests
{
    [Fact]
    public async Task HandleIncomingPacket_BeforeStart_BuffersAndDelivers()
    {
        // Arrange: two DTLS transports wired together for loopback
        var cert1 = DtlsCertificate.Generate();
        var cert2 = DtlsCertificate.Generate();

        DtlsTransport? client = null;
        DtlsTransport? server = null;

        client = new DtlsTransport(async data => server?.HandleIncomingPacket(data), cert1);
        server = new DtlsTransport(async data => client?.HandleIncomingPacket(data), cert2);

        client.SetRemoteFingerprint("sha-256", cert2.Fingerprint);
        server.SetRemoteFingerprint("sha-256", cert1.Fingerprint);

        var clientState = new TaskCompletionSource<DtlsState>();
        var serverState = new TaskCompletionSource<DtlsState>();

        client.OnStateChange += (_, s) => { if (s is DtlsState.Connected or DtlsState.Failed) clientState.TrySetResult(s); };
        server.OnStateChange += (_, s) => { if (s is DtlsState.Connected or DtlsState.Failed) serverState.TrySetResult(s); };

        // Act: start the client FIRST (it will send ClientHello to server before server.StartAsync).
        // This simulates the race condition where the remote peer sends DTLS packets before the
        // local side has called StartAsync on its DtlsTransport.
        var clientTask = client.StartAsync(isClient: true);
        // Give the client time to send the ClientHello before starting the server.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        var serverTask = server.StartAsync(isClient: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        cts.Token.Register(() => { clientState.TrySetResult(DtlsState.Failed); serverState.TrySetResult(DtlsState.Failed); });

        var clientResult = await clientState.Task;
        var serverResult = await serverState.Task;

        // Assert: handshake succeeds despite the server receiving ClientHello before StartAsync
        Assert.Equal(DtlsState.Connected, clientResult);
        Assert.Equal(DtlsState.Connected, serverResult);

        client.Dispose();
        server.Dispose();
    }

    [Fact]
    public async Task HandleIncomingPacket_AfterStart_DeliveredDirectly()
    {
        // Arrange: normal order — both sides start, then exchange packets.
        // This is the baseline test to confirm normal operation still works.
        var cert1 = DtlsCertificate.Generate();
        var cert2 = DtlsCertificate.Generate();

        DtlsTransport? client = null;
        DtlsTransport? server = null;

        client = new DtlsTransport(async data => server?.HandleIncomingPacket(data), cert1);
        server = new DtlsTransport(async data => client?.HandleIncomingPacket(data), cert2);

        client.SetRemoteFingerprint("sha-256", cert2.Fingerprint);
        server.SetRemoteFingerprint("sha-256", cert1.Fingerprint);

        var clientState = new TaskCompletionSource<DtlsState>();
        var serverState = new TaskCompletionSource<DtlsState>();

        client.OnStateChange += (_, s) => { if (s is DtlsState.Connected or DtlsState.Failed) clientState.TrySetResult(s); };
        server.OnStateChange += (_, s) => { if (s is DtlsState.Connected or DtlsState.Failed) serverState.TrySetResult(s); };

        // Act: start server first, then client (normal order)
        var serverTask = server.StartAsync(isClient: false);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        var clientTask = client.StartAsync(isClient: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        cts.Token.Register(() => { clientState.TrySetResult(DtlsState.Failed); serverState.TrySetResult(DtlsState.Failed); });

        var clientResult = await clientState.Task;
        var serverResult = await serverState.Task;

        Assert.Equal(DtlsState.Connected, clientResult);
        Assert.Equal(DtlsState.Connected, serverResult);

        client.Dispose();
        server.Dispose();
    }

    [Fact]
    public async Task HandleIncomingPacket_DataExchangeAfterEarlyPacketHandshake()
    {
        // Arrange: handshake with early packets, then verify application data flows
        var cert1 = DtlsCertificate.Generate();
        var cert2 = DtlsCertificate.Generate();

        DtlsTransport? client = null;
        DtlsTransport? server = null;

        client = new DtlsTransport(async data => server?.HandleIncomingPacket(data), cert1);
        server = new DtlsTransport(async data => client?.HandleIncomingPacket(data), cert2);

        client.SetRemoteFingerprint("sha-256", cert2.Fingerprint);
        server.SetRemoteFingerprint("sha-256", cert1.Fingerprint);

        var clientConnected = new TaskCompletionSource<DtlsState>();
        var serverConnected = new TaskCompletionSource<DtlsState>();
        var receivedData = new TaskCompletionSource<byte[]>();

        client.OnStateChange += (_, s) => { if (s is DtlsState.Connected or DtlsState.Failed) clientConnected.TrySetResult(s); };
        server.OnStateChange += (_, s) => { if (s is DtlsState.Connected or DtlsState.Failed) serverConnected.TrySetResult(s); };
        server.OnData += (_, data) => receivedData.TrySetResult(data);

        // Start client first to exercise early packet buffering
        var clientTask = client.StartAsync(isClient: true);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        var serverTask = server.StartAsync(isClient: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        cts.Token.Register(() =>
        {
            clientConnected.TrySetResult(DtlsState.Failed);
            serverConnected.TrySetResult(DtlsState.Failed);
            receivedData.TrySetCanceled();
        });

        Assert.Equal(DtlsState.Connected, await clientConnected.Task);
        Assert.Equal(DtlsState.Connected, await serverConnected.Task);

        // Act: send data from client to server over the established DTLS connection
        byte[] testPayload = [0xDE, 0xAD, 0xBE, 0xEF];
        await client.SendAsync(testPayload);

        var received = await receivedData.Task;

        // Assert
        Assert.Equal(testPayload, received);

        client.Dispose();
        server.Dispose();
    }
}
