using RtcForge.Sctp;

namespace RtcForge.Tests.Sctp;

public class SctpRetransmissionTests
{
    [Fact]
    public async Task SendData_WithDroppedPacket_Retransmits()
    {
        // Arrange
        int sendCount = 0;
        byte[]? lastSentData = null;

        SctpAssociation assoc = new SctpAssociation(5000, 5000, async (data) => {
            sendCount++;
            lastSentData = data;
            await Task.CompletedTask;
        });

        // Hack: manually set state to established to allow sending
        typeof(SctpAssociation).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(assoc, SctpAssociationState.Established);
        typeof(SctpAssociation).GetField("_peerVerificationTag", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(assoc, 12345u);

        // Set a very short RTO for the test
        typeof(SctpAssociation).GetField("_rto", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(assoc, 200);

        // Act
        await assoc.StartAsync(false);
        await assoc.SendDataAsync(1, 51, System.Text.Encoding.UTF8.GetBytes("Test"));

        // sendCount should be 1 now
        Assert.Equal(1, sendCount);

        // Wait for more than RTO
        await Task.Delay(500);

        // Assert: sendCount should have increased due to retransmission
        Assert.True(sendCount > 1, $"Packet should have been retransmitted. Count: {sendCount}");

        assoc.Dispose();
    }

    [Fact]
    public async Task Sack_RemovesFromOutboundQueue()
    {
        // Arrange
        SctpAssociation assoc = new SctpAssociation(5000, 5000, (data) => Task.CompletedTask);
        typeof(SctpAssociation).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(assoc, SctpAssociationState.Established);

        await assoc.StartAsync(false);
        await assoc.SendDataAsync(1, 51, new byte[] { 1, 2, 3 });

        var outboundQueue = (Dictionary<uint, SctpOutboundChunk>)typeof(SctpAssociation).GetField("_outboundQueue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(assoc)!;
        Assert.Single(outboundQueue);
        uint tsn = outboundQueue.Keys.First();

        // Act: Receive SACK for this TSN
        var sack = new SctpSackChunk { CumulativeTsnAck = tsn };
        var packet = new SctpPacket { VerificationTag = 0 }; // Tag logic is simplified in HandleSackChunk currently
        packet.Chunks.Add(sack);

        byte[] sackBuffer = new byte[packet.GetSerializedLength()];
        packet.Serialize(sackBuffer);
        await assoc.HandlePacketAsync(sackBuffer);

        // Wait for process loop
        await Task.Delay(100);

        // Assert
        Assert.Empty(outboundQueue);
        assoc.Dispose();
    }
}
