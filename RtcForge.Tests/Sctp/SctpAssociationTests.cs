using RtcForge.Sctp;

namespace RtcForge.Tests.Sctp;

public class SctpAssociationTests
{
    [Fact]
    public async Task Handshake_Loopback_EstablishesConnection()
    {
        // Arrange
        SctpAssociation peerA = null!;
        SctpAssociation peerB = null!;

        peerA = new SctpAssociation(5000, 5000, async (data) => {
            await Task.Delay(1); // Simulate network
            await peerB.HandlePacketAsync(data);
        });

        peerB = new SctpAssociation(5000, 5000, async (data) => {
            await Task.Delay(1);
            await peerA.HandlePacketAsync(data);
        });

        // Act
        await peerB.StartAsync(false); // Server
        await peerA.StartAsync(true);  // Client

        // Wait for handshake
        for (int i = 0; i < 100; i++)
        {
            if (peerA.State == SctpAssociationState.Established && peerB.State == SctpAssociationState.Established)
            {
                break;
            }

            await Task.Delay(10);
        }

        // Assert
        Assert.Equal(SctpAssociationState.Established, peerA.State);
        Assert.Equal(SctpAssociationState.Established, peerB.State);
    }
}
