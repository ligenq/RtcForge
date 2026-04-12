using RtcForge.Sctp;

namespace RtcForge.Tests.Sctp;

public class SctpShutdownTests
{
    [Fact]
    public async Task Shutdown_Handshake_ClosesAssociation()
    {
        // Arrange
        SctpAssociation peerA = null!;
        SctpAssociation peerB = null!;

        peerA = new SctpAssociation(5000, 5000, async (data) => await peerB.HandlePacketAsync(data));
        peerB = new SctpAssociation(5000, 5000, async (data) => await peerA.HandlePacketAsync(data));

        await peerB.StartAsync(false);
        await peerA.StartAsync(true);

        // Wait for establishment
        for (int i = 0; i < 50 && peerA.State != SctpAssociationState.Established; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(SctpAssociationState.Established, peerA.State);

        // Act: Trigger Shutdown
        await peerA.ShutdownAsync();

        // Wait for shutdown handshake
        for (int i = 0; i < 50 && peerA.State != SctpAssociationState.Closed; i++)
        {
            await Task.Delay(50);
        }

        // Assert
        Assert.Equal(SctpAssociationState.Closed, peerA.State);
        Assert.Equal(SctpAssociationState.Closed, peerB.State);
    }
}

public class SctpRtoTests
{
    [Fact]
    public void UpdateRto_KarJacobson_AdjustsCorrectly()
    {
        // Arrange
        var assoc = new SctpAssociation(5000, 5000, (d) => Task.CompletedTask);
        var updateRtoMethod = typeof(SctpAssociation).GetMethod("UpdateRto", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act 1: Initial RTT
        updateRtoMethod!.Invoke(assoc, new object[] { 200 });
        int rto1 = (int)typeof(SctpAssociation).GetField("_rto", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(assoc)!;

        // Expected initial: SRTT=200, RTTVAR=100 -> RTO = 200 + 400 = 600. 
        // But we clamped RTO to min 1000.
        Assert.Equal(1000, rto1);

        // Act 2: High RTT
        updateRtoMethod!.Invoke(assoc, new object[] { 500 });
        int rto2 = (int)typeof(SctpAssociation).GetField("_rto", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(assoc)!;

        // Expected: SRTT = (0.875 * 200) + (0.125 * 500) = 175 + 62.5 = 237
        // RTTVAR = (0.75 * 100) + (0.25 * |200-500|) = 75 + 75 = 150
        // RTO = 237 + 4*150 = 237 + 600 = 837. Clamped to 1000.
        Assert.Equal(1000, rto2);
    }
}
