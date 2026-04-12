using RtcForge.Media;
using RtcForge.Rtp;

namespace RtcForge.Tests.Media;

public class RtpNackTests
{
    [Fact]
    public async Task Receiver_OnGap_SendsNack()
    {
        // Arrange
        RtcpPacket? sentRtcp = null;
        var receiver = new RTCRtpReceiver(new AudioStreamTrack(), async (rtcp) => {
            sentRtcp = rtcp;
            await Task.CompletedTask;
        });

        // Act: Receive SN 1 then SN 3 (SN 2 is missing)
        await receiver.HandleRtpPacketAsync(new RtpPacket { SequenceNumber = 1, Ssrc = 123 });
        await receiver.HandleRtpPacketAsync(new RtpPacket { SequenceNumber = 3, Ssrc = 123 });

        // Assert
        Assert.NotNull(sentRtcp);
        Assert.IsType<RtcpNackPacket>(sentRtcp);
        var nack = (RtcpNackPacket)sentRtcp;
        Assert.Contains((ushort)2, nack.LostSequenceNumbers);
    }

    [Fact]
    public async Task Sender_OnNack_Retransmits()
    {
        // Arrange
        int sendCount = 0;
        var sender = new RTCRtpSender(new AudioStreamTrack(), async (rtp) => {
            sendCount++;
            await Task.CompletedTask;
        });

        var packet = new RtpPacket { SequenceNumber = 10, PayloadType = 111 };
        await sender.SendRtpAsync(packet);
        Assert.Equal(1, sendCount);

        // Act: Handle NACK for SN 10
        var nack = new RtcpNackPacket();
        nack.LostSequenceNumbers.Add(10);
        await sender.HandleNackAsync(nack);

        // Assert
        Assert.Equal(2, sendCount);
    }
}
