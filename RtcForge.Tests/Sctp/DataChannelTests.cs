using RtcForge.Sctp;

namespace RtcForge.Tests.Sctp;

public class DataChannelTests
{
    [Fact]
    public async Task DataChannel_Loopback_SendsAndReceivesData()
    {
        // Arrange
        SctpAssociation assocA = null!;
        SctpAssociation assocB = null!;

        assocA = new SctpAssociation(5000, 5000, async (data) => await assocB.HandlePacketAsync(data));
        assocB = new SctpAssociation(5000, 5000, async (data) => await assocA.HandlePacketAsync(data));

        var dcA = new RTCDataChannel("test", 1, assocA);
        var dcB = new RTCDataChannel("test", 1, assocB);

        assocA.RegisterDataChannel(dcA);
        assocB.RegisterDataChannel(dcB);

        await assocB.StartAsync(false);
        await assocA.StartAsync(true);

        // Wait for handshake
        for (int i = 0; i < 100 && assocA.State != SctpAssociationState.Established; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System);
        }

        dcA.SetOpen();
        dcB.SetOpen();

        string receivedMessage = null!;
        dcB.OnMessage += (s, m) => receivedMessage = m;

        // Act
        await dcA.SendAsync("Hello WebRTC!");

        // Wait for message
        for (int i = 0; i < 100 && receivedMessage == null; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System);
        }

        // Assert
        Assert.Equal("Hello WebRTC!", receivedMessage);
    }
}
