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
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System); // Simulate network
            await peerB.HandlePacketAsync(data);
        });

        peerB = new SctpAssociation(5000, 5000, async (data) => {
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System);
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

            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System);
        }

        // Assert
        Assert.Equal(SctpAssociationState.Established, peerA.State);
        Assert.Equal(SctpAssociationState.Established, peerB.State);
    }
}
