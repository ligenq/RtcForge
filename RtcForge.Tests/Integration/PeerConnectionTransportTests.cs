using System.Reflection;
using RtcForge.Sctp;

namespace RtcForge.Tests.Integration;

public class PeerConnectionTransportTests
{
    [Fact]
    public async Task PeerConnection_DtlsPayloads_AreForwardedToSctpAssociation()
    {
        using var pc = new RTCPeerConnection();

        SctpAssociation assocA = null!;
        SctpAssociation assocB = null!;

        assocA = new SctpAssociation(5000, 5000, async data => await assocB.HandlePacketAsync(data));
        assocB = new SctpAssociation(5000, 5000, async data => await InvokeDtlsPayloadHandlerAsync(pc, data));

        typeof(RTCPeerConnection)
            .GetField("_sctpAssociation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(pc, assocA);

        var dcA = new RTCDataChannel("test", 1, assocA);
        var dcB = new RTCDataChannel("test", 1, assocB);
        assocA.RegisterDataChannel(dcA);
        assocB.RegisterDataChannel(dcB);

        await assocA.StartAsync(false);
        await assocB.StartAsync(true);

        for (int i = 0; i < 100; i++)
        {
            if (assocA.State == SctpAssociationState.Established && assocB.State == SctpAssociationState.Established)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System);
        }

        Assert.Equal(SctpAssociationState.Established, assocA.State);
        Assert.Equal(SctpAssociationState.Established, assocB.State);

        dcA.SetOpen();
        dcB.SetOpen();

        string? receivedMessage = null;
        dcA.OnMessage += (s, message) => receivedMessage = message;

        await dcB.SendAsync("hello over dtls");

        for (int i = 0; i < 100 && receivedMessage == null; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System);
        }

        Assert.Equal("hello over dtls", receivedMessage);
    }

    private static Task InvokeDtlsPayloadHandlerAsync(RTCPeerConnection pc, byte[] data)
    {
        var method = typeof(RTCPeerConnection).GetMethod("HandleIncomingDtlsApplicationDataAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(pc, [data])!;
    }
}
